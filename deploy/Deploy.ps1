[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^zappapply-agenttools:release-[a-zA-Z0-9._-]+$')]
    [string]$Image,
    [switch]$InitializeStore
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
function Invoke-Docker {
    param([Parameter(ValueFromRemainingArguments)][string[]]$Arguments)
    & docker @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Docker command failed (exit $LASTEXITCODE)." }
}
$root = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$composeFile = Join-Path $root 'compose.production.yml'
if (!$env:ZAPPAPPLY_AGENTTOOLS_ENV_FILE -or ![IO.Path]::IsPathFullyQualified($env:ZAPPAPPLY_AGENTTOOLS_ENV_FILE) -or
    !(Test-Path -LiteralPath $env:ZAPPAPPLY_AGENTTOOLS_ENV_FILE -PathType Leaf)) {
    throw 'Set ZAPPAPPLY_AGENTTOOLS_ENV_FILE to an existing absolute server configuration file outside the checkout.'
}
$configPath = (Resolve-Path -LiteralPath $env:ZAPPAPPLY_AGENTTOOLS_ENV_FILE).Path
if ($configPath.StartsWith($root.TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Server configuration must be outside the checkout so checkout cleanup cannot delete it.'
}
if ((Invoke-Docker info --format '{{.OSType}}').Trim() -ne 'linux') { throw 'Docker must be running Linux containers.' }
$composeVersion = (Invoke-Docker compose version --short).Trim().TrimStart('v').Split('-')[0]
if ([version]$composeVersion -lt [version]'2.30.0') { throw 'Docker Compose 2.30.0 or later is required.' }
if (!$env:ZAPPAPPLY_QUEUE_NETWORK) { $env:ZAPPAPPLY_QUEUE_NETWORK = 'zappapply-queue_default' }
Invoke-Docker network inspect $env:ZAPPAPPLY_QUEUE_NETWORK --format '{{.Name}}'
$env:ZAPPAPPLY_AGENTTOOLS_IMAGE = $Image
$compose = @('compose', '--project-name', 'zappapply-agenttools', '--file', $composeFile)
Invoke-Docker @compose config --quiet
$previousContainer = @(Invoke-Docker @compose ps --all --quiet agenttools) | Select-Object -First 1
$previousImage = if ($previousContainer) { (Invoke-Docker inspect --format '{{.Image}}' $previousContainer).Trim() } else { $null }

# The final image depends on the verification stage: no SQL/Hermes calls are made by build checks.
# Keep the running container until the new image and optional additive SQL initialization succeed.
Invoke-Docker build --pull --tag $Image --file (Join-Path $root 'Dockerfile') $root
if ($InitializeStore) { Invoke-Docker @compose run --rm --no-deps agenttools --initialize-store }
try {
    Invoke-Docker @compose up --detach --no-deps --wait --wait-timeout 180 agenttools
    Write-Host "ZappApplyAgentTools deployed successfully: $Image"
    if ($env:GITHUB_STEP_SUMMARY) {
        Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value "Deployed agent-tools image: ``$Image``. SQL-backed health check passed. Website rollout and Hermes configuration were not changed."
    }
}
catch {
    $deploymentError = $_
    if ($previousImage) {
        Write-Warning 'New agent-tools container failed readiness. Restoring the previous image; SQL data is retained.'
        $env:ZAPPAPPLY_AGENTTOOLS_IMAGE = $previousImage
        try { Invoke-Docker @compose up --detach --no-deps --wait --wait-timeout 180 agenttools }
        catch { Write-Warning 'Previous image could not become ready. Inspect the service on the server.' }
    }
    else {
        & docker @compose stop agenttools
        Write-Warning 'First deployment failed readiness; the agent-tools container was stopped.'
    }
    throw $deploymentError
}

# Cleanup is deliberately outside deployment/rollback handling. A cleanup
# problem must never roll back an already healthy release.
try {
    & (Join-Path $PSScriptRoot 'Cleanup-ReleaseImages.ps1') -Repository 'zappapply-agenttools' -TagPrefix 'release-' -KeepImages @($Image, $previousImage) -KeepUnused 0
}
catch { Write-Warning ("Deployment succeeded, but old release image cleanup was skipped: " + $_.Exception.Message) }
