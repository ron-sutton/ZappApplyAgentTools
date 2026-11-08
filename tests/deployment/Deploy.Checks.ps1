# Uses a fake Docker function. Never connects to Docker, SQL, Hermes or a server.
$ErrorActionPreference = 'Stop'
$deploy = Join-Path $PSScriptRoot '../../deploy/Deploy.ps1'
$script:count = 0
function Check([string]$Name, [bool]$Valid) {
    if (!$Valid) { throw "FAIL $Name" }
    $script:count++
    Write-Host "PASS $Name"
}
$global:agentToolsDeployFixture = @{ Mode = ''; Calls = [Collections.Generic.List[object]]::new(); Ups = 0 }
function global:docker {
    param([Parameter(ValueFromRemainingArguments)][string[]]$Arguments)
    $global:agentToolsDeployFixture.Calls.Add([pscustomobject]@{ Arguments = $Arguments; Image = $env:ZAPPAPPLY_AGENTTOOLS_IMAGE })
    $global:LASTEXITCODE = 0
    if ($Arguments[0] -eq 'info') { return 'linux' }
    if ($Arguments[0] -eq 'compose' -and $Arguments[1] -eq 'version') { return '2.35.0' }
    if ($Arguments[0] -eq 'network') { return 'zappapply-queue_default' }
    if ($Arguments -contains 'ps') { if ($global:agentToolsDeployFixture.Mode -ne 'first-failure') { return 'fictional-container' }; return }
    if ($Arguments[0] -eq 'inspect') { return 'sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa' }
    if ($Arguments[0] -eq 'build' -and $global:agentToolsDeployFixture.Mode -eq 'build-failure') { $global:LASTEXITCODE = 1; return }
    if ($Arguments -contains '--initialize-store' -and $global:agentToolsDeployFixture.Mode -eq 'init-failure') { $global:LASTEXITCODE = 1; return }
    if ($Arguments -contains 'up') {
        $global:agentToolsDeployFixture.Ups++
        if ($global:agentToolsDeployFixture.Mode -in 'startup-failure','first-failure' -and $global:agentToolsDeployFixture.Ups -eq 1) { $global:LASTEXITCODE = 1 }
    }
}
function Scenario([string]$Mode, [bool]$Initialize = $false) {
    $global:agentToolsDeployFixture.Mode = $Mode; $global:agentToolsDeployFixture.Calls.Clear(); $global:agentToolsDeployFixture.Ups = 0
    $failed = $false
    try { & $deploy -Image 'zappapply-agenttools:release-fictional-123' -InitializeStore:$Initialize | Out-Null }
    catch { Write-Host "Scenario $Mode caught: $($_.Exception.Message)"; $failed = $true }
    return $failed
}
$settings = [IO.Path]::GetTempFileName()
$saved = @{}
foreach ($name in 'ZAPPAPPLY_AGENTTOOLS_ENV_FILE','ZAPPAPPLY_AGENTTOOLS_IMAGE','ZAPPAPPLY_QUEUE_NETWORK','GITHUB_STEP_SUMMARY') {
    $saved[$name] = [Environment]::GetEnvironmentVariable($name)
}
try {
    $env:ZAPPAPPLY_AGENTTOOLS_ENV_FILE = $settings
    $env:ZAPPAPPLY_QUEUE_NETWORK = 'zappapply-queue_default'
    $env:GITHUB_STEP_SUMMARY = $null
    Check 'Successful deployment completes' (!(Scenario 'success'))
    Check 'Regular deployment never initializes SQL' (@($global:agentToolsDeployFixture.Calls | Where-Object { $_.Arguments -contains '--initialize-store' }).Count -eq 0)
    Check 'Deployment waits for health' (@($global:agentToolsDeployFixture.Calls | Where-Object { $_.Arguments -contains 'up' -and $_.Arguments -contains '--wait' }).Count -eq 1)
    Check 'Only agenttools service is targeted' (@($global:agentToolsDeployFixture.Calls | Where-Object { $_.Arguments -contains 'up' -and $_.Arguments[-1] -eq 'agenttools' }).Count -eq 1)
    Check 'First-time SQL initialization is opt-in and succeeds' (!(Scenario 'success' $true))
    Check 'SQL initialization runs exactly once before replacement' (@($global:agentToolsDeployFixture.Calls | Where-Object { $_.Arguments -contains '--initialize-store' }).Count -eq 1 -and
        ($global:agentToolsDeployFixture.Calls.FindIndex([Predicate[object]]{param($c) $c.Arguments -contains '--initialize-store'})) -lt
        ($global:agentToolsDeployFixture.Calls.FindIndex([Predicate[object]]{param($c) $c.Arguments -contains 'up'})))
    Check 'Build failure fails deployment' (Scenario 'build-failure')
    Check 'Build failure leaves existing container untouched' ($global:agentToolsDeployFixture.Ups -eq 0 -and @($global:agentToolsDeployFixture.Calls | Where-Object { $_.Arguments -contains 'stop' }).Count -eq 0)
    Check 'SQL initialization failure fails deployment' (Scenario 'init-failure' $true)
    Check 'Initialization failure leaves existing container untouched' ($global:agentToolsDeployFixture.Ups -eq 0)
    Check 'Readiness failure remains a failed action after rollback' (Scenario 'startup-failure')
    $upCalls = @($global:agentToolsDeployFixture.Calls | Where-Object { $_.Arguments -contains 'up' })
    Check 'Rollback uses the previous immutable image' ($upCalls.Count -eq 2 -and $upCalls[1].Image -eq 'sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa')
    Check 'First deployment failure fails action' (Scenario 'first-failure')
    Check 'First deployment failure stops only agenttools' (@($global:agentToolsDeployFixture.Calls | Where-Object { $_.Arguments -contains 'stop' -and $_.Arguments[-1] -eq 'agenttools' }).Count -eq 1)
    $env:ZAPPAPPLY_AGENTTOOLS_ENV_FILE = Join-Path $PSScriptRoot '../../deploy/service.env.example'
    Check 'Configuration inside checkout is rejected' (Scenario 'success')
    Check 'Invalid configuration fails before Docker' ($global:agentToolsDeployFixture.Calls.Count -eq 0)
    Write-Host "$script:count deployment checks passed. No real Docker calls."
}
finally {
    foreach ($entry in $saved.GetEnumerator()) { [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value) }
    Remove-Item -LiteralPath $settings -Force
    Remove-Item Function:\docker
    Remove-Variable -Name agentToolsDeployFixture -Scope Global
}
