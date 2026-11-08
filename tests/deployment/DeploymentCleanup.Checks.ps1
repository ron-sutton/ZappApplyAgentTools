[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$DeployScript,
    [Parameter(Mandatory)][string]$Repository,
    [Parameter(Mandatory)][string]$TagPrefix,
    [Parameter(Mandatory)][string]$EnvironmentVariable
)
$ErrorActionPreference='Stop';$script:checks=0
function Check([string]$Name,[bool]$Ok){if(!$Ok){throw "FAIL $Name"};$script:checks++;Write-Host "PASS $Name"}
function Id([char]$Letter){'sha256:'+([string]$Letter*64)}
$global:deploymentCleanupFixture=@{}
function global:docker {
    param([Parameter(ValueFromRemainingArguments)][string[]]$Arguments)
    $f=$global:deploymentCleanupFixture;$f.Calls.Add($Arguments);$global:LASTEXITCODE=0
    if($Arguments[0] -eq 'info'){return 'linux'}
    if($Arguments[0] -eq 'compose' -and $Arguments[1] -eq 'version'){return '2.35.0'}
    if($Arguments[0] -eq 'network'){return 'zappapply-queue_default'}
    if($Arguments -contains 'ps'){return 'fictional-previous-container'}
    if($Arguments[0] -eq 'inspect'){return (Id 'b')}
    if($Arguments[0] -eq 'build' -and $f.Mode -eq 'build-failure'){$global:LASTEXITCODE=1;return}
    if($Arguments -contains 'up'){$f.Ups++;if($f.Mode -eq 'health-failure' -and $f.Ups -eq 1){$global:LASTEXITCODE=1};return}
    if($Arguments[0] -eq 'container' -and $Arguments[1] -eq 'ls'){return 'fictional-current-container'}
    if($Arguments[0] -eq 'container' -and $Arguments[1] -eq 'inspect'){return (Id 'a')}
    if($Arguments[0] -eq 'image' -and $Arguments[1] -eq 'ls'){
        if($f.Mode -eq 'cleanup-failure'){$global:LASTEXITCODE=1;return}
        foreach($pair in @(@('a','current'),@('b','previous'),@('c','old'))){@{Repository=$f.Repository;Tag=$f.Prefix+$pair[1];ID=(Id $pair[0])}|ConvertTo-Json -Compress};return
    }
    if($Arguments[0] -eq 'image' -and $Arguments[1] -eq 'inspect'){
        if($Arguments[3] -like '*execution-ledger*'){return '1'}
        if($Arguments[3] -like '*prompt-protocol*'){return '2'}
        if($Arguments[3] -like '*queue-contract*'){return '3'}
        if($Arguments[-1] -match '^sha256:'){return $Arguments[-1]}
        if($Arguments[-1] -eq ($f.Repository+':'+$f.Prefix+'old')){return (Id 'c')}
        return (Id 'a')
    }
    if($Arguments[0] -eq 'image' -and $Arguments[1] -eq 'rm'){$f.Removed.Add($Arguments[2]);return}
}
function Scenario([string]$Mode){
    $global:deploymentCleanupFixture=@{Mode=$Mode;Repository=$Repository;Prefix=$TagPrefix;Calls=[Collections.Generic.List[object]]::new();Removed=[Collections.Generic.List[string]]::new();Ups=0}
    $failed=$false;try{& $DeployScript -Image ($Repository+':'+$TagPrefix+'current')|Out-Null}catch{$failed=$true};return $failed
}
$settings=[IO.Path]::GetTempFileName();$previous=[Environment]::GetEnvironmentVariable($EnvironmentVariable);$summary=$env:GITHUB_STEP_SUMMARY
try{
    [Environment]::SetEnvironmentVariable($EnvironmentVariable,$settings);$env:GITHUB_STEP_SUMMARY=$null
    Check 'Healthy deployment removes only older releases' (!(Scenario 'success') -and $global:deploymentCleanupFixture.Removed.Count -eq 1 -and $global:deploymentCleanupFixture.Removed[0] -eq ($Repository+':'+$TagPrefix+'old'))
    $calls=$global:deploymentCleanupFixture.Calls
    $up=$calls.FindIndex([Predicate[object]]{param($a)$a -contains 'up'})
    $cleanup=$calls.FindIndex([Predicate[object]]{param($a)$a[0] -eq 'image' -and $a[1] -eq 'ls'})
    Check 'Cleanup runs only after readiness succeeds' ($up -ge 0 -and $cleanup -gt $up)
    Check 'Build failure never cleans rollback images' ((Scenario 'build-failure') -and $global:deploymentCleanupFixture.Removed.Count -eq 0 -and @($global:deploymentCleanupFixture.Calls|Where-Object {$_[0] -eq 'image' -and $_[1] -eq 'ls'}).Count -eq 0)
    Check 'Readiness failure and rollback never run cleanup' ((Scenario 'health-failure') -and $global:deploymentCleanupFixture.Removed.Count -eq 0 -and @($global:deploymentCleanupFixture.Calls|Where-Object {$_[0] -eq 'image' -and $_[1] -eq 'ls'}).Count -eq 0)
    Check 'Cleanup failure does not fail or roll back a healthy deployment' (!(Scenario 'cleanup-failure') -and $global:deploymentCleanupFixture.Ups -eq 1)
    Write-Host "$script:checks deployment cleanup checks passed; no real Docker calls."
}finally{
    [Environment]::SetEnvironmentVariable($EnvironmentVariable,$previous);$env:GITHUB_STEP_SUMMARY=$summary
    Remove-Item -LiteralPath $settings;Remove-Item Function:\docker;Remove-Variable deploymentCleanupFixture -Scope Global
}
