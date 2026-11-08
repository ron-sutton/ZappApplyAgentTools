[CmdletBinding()]
param([string]$CleanupScript = (Join-Path $PSScriptRoot '../../deploy/Cleanup-ReleaseImages.ps1'))
$ErrorActionPreference='Stop'
$script:checks=0
function Check([string]$Name,[bool]$Ok) { if(!$Ok){throw "FAIL $Name"};$script:checks++;Write-Host "PASS $Name" }
function Id([char]$Letter) { return 'sha256:' + ([string]$Letter * 64) }
$global:cleanupFixture=@{}
function Reset-Fixture {
    $global:cleanupFixture=@{
        Images=@(
            @{Repository='zappapply';Tag='web-current';ID=(Id 'a')},
            @{Repository='zappapply';Tag='web-current-alias';ID=(Id 'a')},
            @{Repository='zappapply';Tag='web-previous';ID=(Id 'b')},
            @{Repository='zappapply';Tag='web-old';ID=(Id 'c')},
            @{Repository='zappapply';Tag='web-old-alias';ID=(Id 'c')},
            @{Repository='zappapply';Tag='web-stopped';ID=(Id 'd')},
            @{Repository='zappapply';Tag='latest';ID=(Id 'e')},
            @{Repository='zappapply-agenttools';Tag='release-old';ID=(Id 'f')},
            @{Repository='hermes';Tag='old';ID=(Id 'e')},
            @{Repository='<none>';Tag='<none>';ID=(Id 'f')}
        )
        Removed=[Collections.Generic.List[string]]::new(); Calls=[Collections.Generic.List[object]]::new()
        ContainerReads=0; FailList=$false; FailContainers=$false; FailRemove=$false; Race=$false; TagRace=$false
    }
}
function global:docker {
    param([Parameter(ValueFromRemainingArguments)][string[]]$Arguments)
    $f=$global:cleanupFixture;$f.Calls.Add($Arguments);$global:LASTEXITCODE=0
    if($Arguments[0] -eq 'container' -and $Arguments[1] -eq 'ls') {
        $f.ContainerReads++
        if($f.FailContainers){$global:LASTEXITCODE=1;return}
        return 'fictional-running','fictional-stopped'
    }
    if($Arguments[0] -eq 'container' -and $Arguments[1] -eq 'inspect') {
        Id 'a'; Id 'd'
        if($f.Race -and $f.ContainerReads -gt 1){Id 'c'}
        return
    }
    if($Arguments[0] -eq 'image' -and $Arguments[1] -eq 'ls') {
        if($f.FailList){$global:LASTEXITCODE=1;return}
        $f.Images | ForEach-Object {$_ | ConvertTo-Json -Compress}; return
    }
    if($Arguments[0] -eq 'image' -and $Arguments[1] -eq 'inspect') {
        $format=$Arguments[3]
        foreach($reference in $Arguments[4..($Arguments.Count-1)]) {
            if($reference -match '^sha256:'){$id=$reference}
            else{$entry=@($f.Images | Where-Object {($_.Repository+':'+$_.Tag) -ceq $reference});if($entry.Count -ne 1){$global:LASTEXITCODE=1;return};$id=$entry[0].ID}
            if($format -eq '{{.Id}}') {if($f.TagRace -and $reference -eq 'zappapply:web-old'){Id 'f'}else{$id}}
            else{@{Id=$id;Created=$(if($id -eq (Id 'b')){'2026-11-08T00:00:00Z'}else{'2026-11-01T00:00:00Z'})} | ConvertTo-Json -Compress}
        };return
    }
    if($Arguments[0] -eq 'image' -and $Arguments[1] -eq 'rm') {
        if($f.FailRemove){$global:LASTEXITCODE=1;return}
        $f.Removed.Add($Arguments[2]);return
    }
    throw "Unexpected Docker operation: $Arguments"
}
try {
    Reset-Fixture
    & $CleanupScript -Repository zappapply -TagPrefix web- -KeepImages @('zappapply:web-current',(Id 'b')) -KeepUnused 0
    Check 'Only older release tags are removed' (($global:cleanupFixture.Removed | Sort-Object) -join ',' -eq 'zappapply:web-old,zappapply:web-old-alias')
    Check 'Current aliases and explicit rollback image are protected' (!$global:cleanupFixture.Removed.Contains('zappapply:web-current-alias') -and !$global:cleanupFixture.Removed.Contains('zappapply:web-previous'))
    Check 'Stopped containers protect their images' (!$global:cleanupFixture.Removed.Contains('zappapply:web-stopped'))
    Check 'No prune, force, volume or container deletion is used' (@($global:cleanupFixture.Calls | Where-Object {$_ -contains 'prune' -or $_ -contains '--force' -or $_ -contains 'volume' -or ($_[0] -eq 'container' -and $_[1] -eq 'rm')}).Count -eq 0)
    Reset-Fixture
    & $CleanupScript -Repository zappapply -TagPrefix web-
    Check 'Standalone cleanup keeps newest unused release for rollback' (!$global:cleanupFixture.Removed.Contains('zappapply:web-previous') -and $global:cleanupFixture.Removed.Count -eq 2)
    Reset-Fixture
    & $CleanupScript -Repository zappapply -TagPrefix web- -WhatIf
    Check 'WhatIf previews without deleting images' ($global:cleanupFixture.Removed.Count -eq 0)
    Reset-Fixture;$global:cleanupFixture.Race=$true
    & $CleanupScript -Repository zappapply -TagPrefix web- -KeepImages (Id 'b') -KeepUnused 0
    Check 'Newly referenced images are retained on inventory recheck' ($global:cleanupFixture.Removed.Count -eq 0)
    foreach($failure in 'FailList','FailContainers','TagRace','FailRemove') {
        Reset-Fixture;$global:cleanupFixture[$failure]=$true;$failed=$false
        try { & $CleanupScript -Repository zappapply -TagPrefix web- -KeepImages (Id 'b') -KeepUnused 0 }catch{$failed=$true}
        Check "$failure stops cleanup safely" ($failed -and $global:cleanupFixture.Removed.Count -eq 0)
    }
    Reset-Fixture;$failed=$false
    try{& $CleanupScript -Repository hermes -TagPrefix web-}catch{$failed=$true}
    Check 'Unrelated repository cannot be targeted' ($failed -and $global:cleanupFixture.Calls.Count -eq 0)
    Reset-Fixture;$failed=$false
    try{& $CleanupScript -Repository zappapply -TagPrefix release-}catch{$failed=$true}
    Check 'Wrong release prefix fails before Docker' ($failed -and $global:cleanupFixture.Calls.Count -eq 0)
    Write-Host "$script:checks cleanup checks passed; no real Docker calls."
}
finally {Remove-Item Function:\docker;Remove-Variable cleanupFixture -Scope Global}
