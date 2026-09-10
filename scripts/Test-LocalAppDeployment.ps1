#Requires -Version 7.0
# Exercises real file installation and rollback in a disposable fixture. Only the
# process boundary is simulated; this is not live application acceptance evidence.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('ptt-deployment-tests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
$installer = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Update-LocalApp.ps1') -Raw
$fixedPath = 'C:\Users\stewa\projects\par-win-ptt\publish\ptt-dictation-win-x64'
if ([regex]::Matches($installer, [regex]::Escape($fixedPath)).Count -ne 1) {
    throw 'Expected exactly one fixed installation path in the production script.'
}

function Assert($Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function New-Package([string]$Path, [string]$Version) {
    New-Item -ItemType Directory -Path $Path -Force | Out-Null
    foreach ($name in @('PttDictation.exe', 'PttDictation.dll', 'PttDictation.Core.dll', 'System.Private.CoreLib.dll')) {
        Set-Content -LiteralPath (Join-Path $Path $name) -Value "$Version $name"
    }
    New-Item -ItemType Directory -Path (Join-Path $Path 'nested') | Out-Null
    Set-Content -LiteralPath (Join-Path $Path 'nested\asset.dat') -Value "$Version nested asset"
    Set-Content -LiteralPath (Join-Path $Path "$Version-only.dat") -Value $Version
}

function Invoke-Fixture([string]$Scenario) {
    $caseRoot = Join-Path $testRoot $Scenario
    $live = Join-Path $caseRoot 'publish\ptt-dictation-win-x64'
    $stage = Join-Path $caseRoot 'stage'
    New-Package $live 'old'
    New-Package $stage 'new'
    # The test-only copy cannot reach the real installation, even if a mock fails.
    $testScript = Join-Path $caseRoot 'installer.ps1'
    $installer.Replace($fixedPath, $live).Replace('Local\PttDictation-PermanentDeployment', "Local\PttTest-$Scenario") |
        Set-Content -LiteralPath $testScript
    $global:pttTestExpectedExe = Join-Path $live 'PttDictation.exe'
    $global:pttTestRunning = @([pscustomobject]@{
        ProcessId = 101; ExecutablePath = $global:pttTestExpectedExe; CommandLine = '"' + $global:pttTestExpectedExe + '"'
    })
    $global:pttTestStartCount = 0
    $global:pttTestStopCount = 0
    $global:pttTestFailure = $Scenario
    $oldHash = (Get-FileHash -LiteralPath (Join-Path $live 'PttDictation.dll')).Hash

    function Get-CimInstance { param($ClassName, $Filter) return $global:pttTestRunning }
    function Stop-Process {
        param($Id, $ErrorAction)
        Assert ($Id -in @($global:pttTestRunning | ForEach-Object ProcessId)) 'Attempted to stop an unrelated PID.'
        $global:pttTestStopCount++
        $global:pttTestRunning = @()
    }
    function Wait-Process { param($Id, $Timeout, $ErrorAction) }
    function Start-Sleep { param($Seconds) }
    function Move-Item { throw 'Do not rename the live executable or directory; preserve pinned shortcut targets.' }
    function Start-Process {
        param($FilePath, $WorkingDirectory, [switch]$PassThru)
        Assert ($FilePath -eq $global:pttTestExpectedExe) 'Launched outside the permanent location.'
        Assert ($WorkingDirectory -eq (Split-Path $global:pttTestExpectedExe -Parent)) 'Wrong working directory.'
        $global:pttTestStartCount++
        $newId = 200 + $global:pttTestStartCount
        if ($global:pttTestFailure -ne 'startup-failure' -or $global:pttTestStartCount -gt 1) {
            $global:pttTestRunning = @([pscustomobject]@{
                ProcessId = $newId; ExecutablePath = $FilePath; CommandLine = '"' + $FilePath + '"'
            })
        }
        return [pscustomobject]@{ Id = $newId }
    }
    function Copy-Item {
        param([Parameter(ValueFromPipeline)]$InputObject, $Destination, [switch]$Recurse, [switch]$Force)
        process {
            $InputObject | Microsoft.PowerShell.Management\Copy-Item -Destination $Destination -Recurse:$Recurse -Force:$Force
            if ($global:pttTestFailure -eq 'corrupt-install' -and
                $Destination -eq (Split-Path $global:pttTestExpectedExe -Parent) -and
                $InputObject.Name -eq 'PttDictation.dll' -and
                $InputObject.Directory.Name.StartsWith('.install-')) {
                Set-Content -LiteralPath (Join-Path $Destination 'PttDictation.dll') -Value 'corrupted'
            }
        }
    }

    $arguments = @{ StagedPath = $stage }
    switch ($Scenario) {
        'missing-file' { Remove-Item -LiteralPath (Join-Path $stage 'PttDictation.Core.dll') }
        'live-as-source' { $arguments.StagedPath = $live }
        'ancestor-as-source' { $arguments.StagedPath = $caseRoot }
        'unexpected-arguments' { $global:pttTestRunning[0].CommandLine += ' --settings' }
        'other-location' { $global:pttTestRunning[0].ExecutablePath = Join-Path $stage 'PttDictation.exe' }
        'verify-existing' { $arguments = @{ VerifyOnly = $true } }
    }
    $caught = $null
    try { $result = & $testScript @arguments }
    catch { $caught = $_ }

    if ($Scenario -eq 'success') {
        Assert ($null -eq $caught) "Install failed: $caught"
        Assert ($global:pttTestStartCount -eq 1 -and $global:pttTestStopCount -eq 1) 'Expected one stop and one normal start.'
        Assert ((Get-FileHash -LiteralPath (Join-Path $live 'PttDictation.dll')).Hash -eq
            (Get-FileHash -LiteralPath (Join-Path $stage 'PttDictation.dll')).Hash) 'New package was not installed.'
        $verified = & $testScript -VerifyOnly
        Assert $verified.DeploymentReceiptVerified 'Receipt verification failed.'
        Assert ($verified.FilesHashed -eq 6) 'Nested package files were not verified.'
        Assert (-not (Test-Path -LiteralPath (Join-Path $live 'old-only.dat'))) 'Obsolete package file was not removed.'
        Set-Content -LiteralPath (Join-Path $live 'nested\asset.dat') -Value 'tampered'
        $tamperError = $null
        try { $null = & $testScript -VerifyOnly } catch { $tamperError = $_ }
        Assert ($null -ne $tamperError -and "$tamperError" -like '*hash mismatch*') 'Tampered asset was not rejected.'
        Assert ($global:pttTestStartCount -eq 1 -and $global:pttTestStopCount -eq 1) 'VerifyOnly changed process state.'
    }
    elseif ($Scenario -eq 'verify-existing') {
        Assert ($null -eq $caught) "Read-only verification failed: $caught"
        Assert (-not $result.DeploymentReceiptVerified) 'An existing installation falsely claimed deployment verification.'
        Assert ($global:pttTestStartCount -eq 0 -and $global:pttTestStopCount -eq 0) 'Read-only verification changed process state.'
    }
    else {
        Assert ($null -ne $caught) "Expected $Scenario to fail."
        $expectedError = switch ($Scenario) {
            'startup-failure' { '*Expected exactly one new process*' }
            'corrupt-install' { '*Installed file hash mismatch*' }
            'missing-file' { '*Incomplete self-contained package*' }
            'live-as-source' { '*Stage a separate package*' }
            'ancestor-as-source' { '*Stage a separate package*' }
            'unexpected-arguments' { '*Unexpected arguments*' }
            'other-location' { '*Another PTT executable*' }
        }
        Assert ("$caught" -like $expectedError) "Unexpected failure in ${Scenario}: $caught"
        Assert ((Get-FileHash -LiteralPath (Join-Path $live 'PttDictation.dll')).Hash -eq $oldHash) 'Old package was not preserved/restored.'
        if ($Scenario -in @('startup-failure', 'corrupt-install')) {
            Assert ($global:pttTestRunning.Count -eq 1) 'Rollback left the application stopped.'
            Assert ($global:pttTestRunning[0].ExecutablePath -eq $global:pttTestExpectedExe) 'Rollback launched the wrong path.'
            Assert ($global:pttTestStartCount -ge 1) 'Rollback did not restart the previous package.'
            Assert (-not (Test-Path -LiteralPath (Join-Path $live 'new-only.dat'))) 'Rollback left files from the failed package.'
            Assert (Test-Path -LiteralPath (Join-Path $live 'old-only.dat')) 'Rollback omitted an old package file.'
        }
        else {
            Assert ($global:pttTestStartCount -eq 0 -and $global:pttTestStopCount -eq 0) 'Preflight failure disturbed the live app.'
        }
    }
    Write-Host "PASS: $Scenario"
}

try {
    foreach ($scenario in @('success', 'startup-failure', 'corrupt-install', 'missing-file', 'live-as-source',
        'ancestor-as-source', 'unexpected-arguments', 'other-location', 'verify-existing')) {
        Invoke-Fixture $scenario
    }
    Write-Host 'PASS: all 9 deployment scenarios (including receipt tampering detection).'
}
finally {
    $resolved = [IO.Path]::GetFullPath($testRoot)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
        -not (Split-Path $resolved -Leaf).StartsWith('ptt-deployment-tests-')) {
        throw "Unsafe test cleanup target: $resolved"
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
    Remove-Variable -Scope Global -Name pttTestExpectedExe,pttTestRunning,pttTestStartCount,pttTestStopCount,pttTestFailure -ErrorAction SilentlyContinue
}
