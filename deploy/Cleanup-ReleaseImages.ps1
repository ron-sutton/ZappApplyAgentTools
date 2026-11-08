[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)][ValidateSet('zappapply','zappapply-queue-worker','zappapply-agenttools')][string]$Repository,
    [Parameter(Mandatory)][ValidateSet('web-','release-')][string]$TagPrefix,
    [string[]]$KeepImages = @(),
    [ValidateRange(0,20)][int]$KeepUnused = 1
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
if (($Repository -eq 'zappapply' -and $TagPrefix -ne 'web-') -or
    ($Repository -ne 'zappapply' -and $TagPrefix -ne 'release-')) {
    throw 'Repository and release tag prefix do not match.'
}
function Read-Docker {
    param([Parameter(ValueFromRemainingArguments)][string[]]$Arguments)
    $output = @(& docker @Arguments)
    if ($LASTEXITCODE -ne 0) { throw 'Docker inventory could not be read; cleanup stopped.' }
    return $output
}
function Get-ContainerImages {
    $containers = @(Read-Docker container ls --all --quiet)
    if ($containers.Count -eq 0) { return @() }
    $images = @(Read-Docker container inspect --format '{{.Image}}' @containers)
    if (@($images | Where-Object { $_ -notmatch '^sha256:[a-f0-9]{64}$' }).Count) {
        throw 'Unexpected container image inventory; cleanup stopped.'
    }
    return $images
}
# Never use host-wide prune, force deletion, container removal or volume removal.
# Full IDs protect aliases of current/rollback/container images as well as their tags.
$protected = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($image in $KeepImages) {
    if ([string]::IsNullOrWhiteSpace($image)) { continue }
    $resolved = @(Read-Docker image inspect --format '{{.Id}}' $image)
    if ($resolved.Count -ne 1 -or $resolved[0] -notmatch '^sha256:[a-f0-9]{64}$') { throw 'A retained release could not be resolved; cleanup stopped.' }
    [void]$protected.Add($resolved[0])
}
foreach ($id in @(Get-ContainerImages)) { [void]$protected.Add($id) }
$rows = @(Read-Docker image ls --no-trunc --format '{{json .}}' | ForEach-Object { $_ | ConvertFrom-Json })
$releases = @($rows | Where-Object {
    $_.Repository -ceq $Repository -and $_.Tag -cmatch ('^' + [regex]::Escape($TagPrefix) + '[a-zA-Z0-9._-]+$')
})
if (@($releases | Where-Object { $_.ID -notmatch '^sha256:[a-f0-9]{64}$' }).Count) { throw 'Unexpected release image inventory; cleanup stopped.' }
if ($releases.Count -eq 0) { Write-Host "No release images to clean for $Repository."; return }
$ids = @($releases.ID | Select-Object -Unique)
# Standalone cleanup keeps the newest unused release for rollback. Deployment
# passes the exact successful current/previous IDs and KeepUnused=0 instead.
if ($KeepUnused -gt 0) {
    $metadata = @(Read-Docker image inspect --format '{"Id":"{{.Id}}","Created":"{{.Created}}"}' @ids |
        ForEach-Object { $_ | ConvertFrom-Json })
    if ($metadata.Count -ne $ids.Count) { throw 'Incomplete image dates; cleanup stopped.' }
    $unused = @($metadata | Where-Object { !$protected.Contains($_.Id) } |
        Sort-Object @{Expression = { [DateTimeOffset]::Parse($_.Created) }; Descending = $true}, Id |
        Select-Object -First $KeepUnused)
    foreach ($image in $unused) { [void]$protected.Add($image.Id) }
}
$removed = 0
foreach ($group in @($releases | Group-Object ID)) {
    if ($protected.Contains($group.Name)) { continue }
    # Recheck container references before each image group. Stopped containers
    # count too. Docker's non-force removal remains the final race safeguard.
    foreach ($id in @(Get-ContainerImages)) { [void]$protected.Add($id) }
    if ($protected.Contains($group.Name)) { continue }
    foreach ($release in $group.Group) {
        $reference = $release.Repository + ':' + $release.Tag
        $now = @(Read-Docker image inspect --format '{{.Id}}' $reference)
        if ($now.Count -ne 1 -or $now[0] -cne $release.ID) { throw 'A release tag changed during cleanup; cleanup stopped.' }
        if ($PSCmdlet.ShouldProcess($reference, 'Remove unused old release image tag')) {
            & docker image rm $reference
            if ($LASTEXITCODE -ne 0) { throw "Image $reference was retained because Docker refused removal. Cleanup stopped." }
            $removed++
        }
    }
}
Write-Host "Release cleanup for ${Repository}: removed $removed old tags; kept active/container and retained rollback images."
