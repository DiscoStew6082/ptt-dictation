#Requires -Version 7.0
# Exercises real file installation and rollback in a disposable fixture. Only the
# process boundary is simulated; this is not live application acceptance evidence.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('ptt-deployment-tests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
$installer = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Update-LocalApp.ps1') -Raw
$fixedPath = 'C:\Users\stewart\projects\par-win-ptt\publish\ptt-dictation-win-x64'
$fixedSettingsInitialization = '$settingsPath = Join-Path $env:LOCALAPPDATA ''PttDictation\settings.json'''
if ([regex]::Matches($installer, [regex]::Escape($fixedPath)).Count -ne 1 -or
    [regex]::Matches($installer, [regex]::Escape($fixedSettingsInitialization)).Count -ne 1) {
    throw 'Expected exactly one fixed installation path and one fixed settings initialization in the production script.'
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
    $global:pttTestSettingsPath = Join-Path $caseRoot 'appdata\PttDictation\settings.json'
    $global:pttTestSettingsSource = Join-Path $caseRoot 'requested-settings.json'
    $oldSettingsBytes = [Text.Encoding]::Unicode.GetPreamble() + [Text.Encoding]::Unicode.GetBytes(
        "{  ""FinalTranscriptionBackend"": ""Parakeet"", ""note"": ""café old"" }" + [char]13 + [char]10)
    $newSettingsBytes = [Text.Encoding]::UTF8.GetPreamble() + [Text.Encoding]::UTF8.GetBytes(
        "{ ""FinalTranscriptionBackend"": ""Qwen"", ""note"": ""café new"" }" + [char]10)
    $hadSettings = $Scenario -notin @('settings-first-install', 'settings-missing-rollback')
    if ($hadSettings) {
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($global:pttTestSettingsPath)) | Out-Null
        [IO.File]::WriteAllBytes($global:pttTestSettingsPath, $oldSettingsBytes)
    }
    [IO.File]::WriteAllBytes($global:pttTestSettingsSource, $newSettingsBytes)
    $oldSettingsSnapshot = if ($hadSettings) { [Convert]::ToBase64String($oldSettingsBytes) } else { '<missing>' }
    $newSettingsSnapshot = [Convert]::ToBase64String($newSettingsBytes)

    # Both production destinations are replaced before the test script can run.
    # Refuse the fixture if either real installation or appdata access remains.
    $testScript = Join-Path $caseRoot 'installer.ps1'
    $settingsInitialization = '$settingsPath = ''' + $global:pttTestSettingsPath.Replace("'", "''") + ''''
    $isolatedInstaller = $installer.Replace($fixedPath, $live).
        Replace($fixedSettingsInitialization, $settingsInitialization).
        Replace('Local\PttDictation-PermanentDeployment', "Local\PttTest-$Scenario")
    Assert (-not $isolatedInstaller.Contains($fixedPath) -and
        -not $isolatedInstaller.Contains('$env:LOCALAPPDATA')) 'Fixture could access a real installation or appdata.'
    [IO.File]::WriteAllText($testScript, $isolatedInstaller, [Text.UTF8Encoding]::new($false))
    $global:pttTestExpectedExe = Join-Path $live 'PttDictation.exe'
    $global:pttTestRunning = @([pscustomobject]@{
        ProcessId = 101; ExecutablePath = $global:pttTestExpectedExe; CommandLine = '"' + $global:pttTestExpectedExe + '"'; CreationDate = [datetime]'2026-01-01'
    })
    $global:pttTestWorkers = @()
    if ($Scenario -eq 'owned-worker') {
        $global:pttTestWorkers = @(101, 999 | ForEach-Object {
            [pscustomobject]@{ ProcessId = $_ + 1000; ParentProcessId = $_; Name = 'parakeet-server.exe';
                ExecutablePath = (Join-Path $caseRoot 'runtime\parakeet-server.exe'); CreationDate = [datetime]'2026-01-01' }
        })
        $global:pttTestWorkers += [pscustomobject]@{ ProcessId = 1102; ParentProcessId = 101; Name = 'parakeet-server.exe';
            ExecutablePath = (Join-Path $caseRoot 'runtime\parakeet-server.exe'); CreationDate = [datetime]'2025-01-01' }
    }
    $global:pttTestStartCount = 0
    $global:pttTestStopCount = 0
    $global:pttTestLaunches = @()
    $global:pttTestSettingsAtStops = @()
    $global:pttTestFailure = $Scenario
    $oldHash = (Get-FileHash -LiteralPath (Join-Path $live 'PttDictation.dll')).Hash
    $newHash = (Get-FileHash -LiteralPath (Join-Path $stage 'PttDictation.dll')).Hash

    function Get-SettingsSnapshot {
        if (-not [IO.File]::Exists($global:pttTestSettingsPath)) { return '<missing>' }
        return [Convert]::ToBase64String([IO.File]::ReadAllBytes($global:pttTestSettingsPath))
    }
    function Get-CimInstance {
        param($ClassName, $Filter)
        if ($Filter -eq "Name = 'PttDictation.exe'") { return $global:pttTestRunning }
        if ($Filter -match '^ParentProcessId = (\d+)') {
            $parentId = [int]$Matches[1]
            return @($global:pttTestWorkers | Where-Object ParentProcessId -eq $parentId)
        }
        if ($Filter -match '^ProcessId = (\d+)$') {
            $processId = [int]$Matches[1]
            return @(@($global:pttTestRunning) + @($global:pttTestWorkers) | Where-Object ProcessId -eq $processId)
        }
        throw "Unexpected process query: $Filter"
    }
    function Stop-Process {
        param($Id, $ErrorAction)
        Assert ($Id -in @($global:pttTestRunning | ForEach-Object ProcessId) -or $Id -eq 1101) 'Attempted to stop an unrelated PID.'
        $global:pttTestStopCount++
        $global:pttTestSettingsAtStops += Get-SettingsSnapshot
        if ($global:pttTestFailure -eq 'settings-unconfirmed-stop') { return }
        $global:pttTestRunning = @($global:pttTestRunning | Where-Object ProcessId -ne $Id)
        $global:pttTestWorkers = @($global:pttTestWorkers | Where-Object ProcessId -ne $Id)
        if ($global:pttTestFailure -eq 'settings-frozen-source') {
            [IO.File]::WriteAllText($global:pttTestSettingsSource, '{ changed after preflight')
        }
    }
    function Wait-Process { param($Id, $Timeout, $ErrorAction) }
    function Start-Sleep { param($Seconds) }
    function Move-Item { throw 'Do not rename the live executable or directory; preserve pinned shortcut targets.' }
    function Start-Process {
        param($FilePath, $WorkingDirectory, [switch]$PassThru)
        Assert ($FilePath -eq $global:pttTestExpectedExe) 'Launched outside the permanent location.'
        Assert ($WorkingDirectory -eq (Split-Path $global:pttTestExpectedExe -Parent)) 'Wrong working directory.'
        $global:pttTestStartCount++
        $global:pttTestLaunches += [pscustomobject]@{
            Settings = Get-SettingsSnapshot
            PackageHash = (Get-FileHash -LiteralPath (Join-Path $WorkingDirectory 'PttDictation.dll')).Hash
        }
        $newId = 200 + $global:pttTestStartCount
        if ($global:pttTestFailure -notin @('startup-failure', 'settings-startup-failure', 'settings-missing-rollback') -or
            $global:pttTestStartCount -gt 1) {
            $global:pttTestRunning = @([pscustomobject]@{
                ProcessId = $newId; ExecutablePath = $FilePath; CommandLine = '"' + $FilePath + '"'; CreationDate = [datetime]'2026-01-02'
            })
        }
        return [pscustomobject]@{ Id = $newId }
    }
    function Copy-Item {
        param([Parameter(ValueFromPipeline)]$InputObject, $Destination, [switch]$Recurse, [switch]$Force)
        process {
            $InputObject | Microsoft.PowerShell.Management\Copy-Item -Destination $Destination -Recurse:$Recurse -Force:$Force
            if ($Destination -eq (Split-Path $global:pttTestExpectedExe -Parent) -and
                $InputObject.Name -eq 'PttDictation.dll' -and
                $InputObject.Directory.Name.StartsWith('.install-')) {
                if ($global:pttTestFailure -eq 'corrupt-install') {
                    Set-Content -LiteralPath (Join-Path $Destination 'PttDictation.dll') -Value 'corrupted'
                }
                if ($global:pttTestFailure -eq 'settings-copy-failure') { throw 'Injected package copy failure.' }
            }
        }
    }

    $arguments = @{ StagedPath = $stage }
    if ($Scenario.StartsWith('settings-')) { $arguments.SettingsSource = $global:pttTestSettingsSource }
    switch ($Scenario) {
        'missing-file' { Remove-Item -LiteralPath (Join-Path $stage 'PttDictation.Core.dll') }
        'live-as-source' { $arguments.StagedPath = $live }
        'ancestor-as-source' { $arguments.StagedPath = $caseRoot }
        'unexpected-arguments' { $global:pttTestRunning[0].CommandLine += ' --settings' }
        'other-location' { $global:pttTestRunning[0].ExecutablePath = Join-Path $stage 'PttDictation.exe' }
        'verify-existing' { $arguments = @{ VerifyOnly = $true } }
        'settings-malformed-json' { [IO.File]::WriteAllText($global:pttTestSettingsSource, '{"invalid":') }
        'settings-invalid-root' { [IO.File]::WriteAllText($global:pttTestSettingsSource, '[]') }
        'settings-unc-source' { $arguments.SettingsSource = '\\unreachable.invalid\share\settings.json' }
        'settings-slash-unc-source' { $arguments.SettingsSource = '//unreachable.invalid/share/settings.json' }
        'settings-missing-source' { [IO.File]::Delete($global:pttTestSettingsSource) }
        'settings-verify-rejected' { $arguments = @{ VerifyOnly = $true; SettingsSource = $global:pttTestSettingsSource } }
    }
    $caught = $null
    try { $result = & $testScript @arguments }
    catch { $caught = $_ }

    $successScenarios = @('success', 'owned-worker', 'settings-success', 'settings-first-install', 'settings-frozen-source')
    $rollbackScenarios = @('startup-failure', 'corrupt-install', 'settings-startup-failure', 'settings-copy-failure', 'settings-missing-rollback')
    if ($Scenario -in $successScenarios) {
        Assert ($null -eq $caught) "Install failed: $caught"
        $expectedStops = if ($Scenario -eq 'owned-worker') { 2 } else { 1 }
        Assert ($global:pttTestStartCount -eq 1 -and $global:pttTestStopCount -eq $expectedStops) 'Expected the app and its owned worker to stop, followed by one normal start.'
        if ($Scenario -eq 'owned-worker') {
            Assert ($global:pttTestWorkers.Count -eq 2 -and
                1999 -in @($global:pttTestWorkers | ForEach-Object ProcessId) -and
                1102 -in @($global:pttTestWorkers | ForEach-Object ProcessId)) 'Owned worker survived or an unrelated/older worker was stopped.'
        }
        Assert ((Get-FileHash -LiteralPath (Join-Path $live 'PttDictation.dll')).Hash -eq $newHash) 'New package was not installed.'
        $verified = & $testScript -VerifyOnly
        Assert $verified.DeploymentReceiptVerified 'Receipt verification failed.'
        Assert ($verified.FilesHashed -eq 6) 'Nested package files were not verified.'
        Assert (-not (Test-Path -LiteralPath (Join-Path $live 'old-only.dat'))) 'Obsolete package file was not removed.'
        Set-Content -LiteralPath (Join-Path $live 'nested\asset.dat') -Value 'tampered'
        $tamperError = $null
        try { $null = & $testScript -VerifyOnly } catch { $tamperError = $_ }
        Assert ($null -ne $tamperError -and "$tamperError" -like '*hash mismatch*') 'Tampered asset was not rejected.'
        Assert ($global:pttTestStartCount -eq 1 -and $global:pttTestStopCount -eq $expectedStops) 'VerifyOnly changed process state.'
    }
    elseif ($Scenario -eq 'verify-existing') {
        Assert ($null -eq $caught) "Read-only verification failed: $caught"
        Assert (-not $result.DeploymentReceiptVerified) 'An existing installation falsely claimed deployment verification.'
        Assert ($global:pttTestStartCount -eq 0 -and $global:pttTestStopCount -eq 0) 'Read-only verification changed process state.'
    }
    else {
        Assert ($null -ne $caught) "Expected $Scenario to fail."
        $expectedError = switch ($Scenario) {
            { $_ -in @('startup-failure', 'settings-startup-failure', 'settings-missing-rollback') } { '*Expected exactly one new process*'; break }
            'corrupt-install' { '*Installed file hash mismatch*' }
            'settings-copy-failure' { '*Injected package copy failure*' }
            'missing-file' { '*Incomplete self-contained package*' }
            'live-as-source' { '*Stage a separate package*' }
            'ancestor-as-source' { '*Stage a separate package*' }
            'unexpected-arguments' { '*Unexpected arguments*' }
            'other-location' { '*Another PTT executable*' }
            { $_ -in @('settings-malformed-json', 'settings-invalid-root') } { '*Invalid settings JSON*'; break }
            'settings-missing-source' { '*does not exist*' }
            { $_ -in @('settings-unc-source', 'settings-slash-unc-source') } { '*Settings source must be a local JSON file*'; break }
            'settings-unconfirmed-stop' { '*canonical app is still running*' }
            'settings-verify-rejected' { '*Parameter set cannot be resolved*' }
        }
        Assert ("$caught" -like $expectedError) "Unexpected failure in $($Scenario): $caught"
        Assert ((Get-FileHash -LiteralPath (Join-Path $live 'PttDictation.dll')).Hash -eq $oldHash) 'Old package was not preserved/restored.'
        if ($Scenario -in $rollbackScenarios) {
            Assert ($global:pttTestRunning.Count -eq 1) 'Rollback left the application stopped.'
            Assert ($global:pttTestRunning[0].ExecutablePath -eq $global:pttTestExpectedExe) 'Rollback launched the wrong path.'
            Assert ($global:pttTestStartCount -ge 1) 'Rollback did not restart the previous package.'
            Assert (-not (Test-Path -LiteralPath (Join-Path $live 'new-only.dat'))) 'Rollback left files from the failed package.'
            Assert (Test-Path -LiteralPath (Join-Path $live 'old-only.dat')) 'Rollback omitted an old package file.'
            Assert ($global:pttTestLaunches[-1].Settings -ceq $oldSettingsSnapshot -and
                $global:pttTestLaunches[-1].PackageHash -eq $oldHash) 'Recovered launch did not see the exact previous settings/package pair.'
        }
        elseif ($Scenario -eq 'settings-unconfirmed-stop') {
            Assert ($global:pttTestStartCount -eq 0 -and $global:pttTestStopCount -eq 1 -and
                $global:pttTestRunning.Count -eq 1) 'Unconfirmed shutdown should leave the existing app/package/settings alone.'
        }
        else {
            Assert ($global:pttTestStartCount -eq 0 -and $global:pttTestStopCount -eq 0) 'Preflight failure disturbed the live app.'
        }
    }

    if ($global:pttTestSettingsAtStops.Count -gt 0) {
        Assert ($global:pttTestSettingsAtStops[0] -ceq $oldSettingsSnapshot) 'Settings changed before the app was stopped.'
    }
    $successfulSettingsChange = $Scenario -in @('settings-success', 'settings-first-install', 'settings-frozen-source')
    $expectedFinalSettings = if ($successfulSettingsChange) { $newSettingsSnapshot } else { $oldSettingsSnapshot }
    Assert ((Get-SettingsSnapshot) -ceq $expectedFinalSettings) 'Final settings bytes/existence were not preserved or correctly installed.'
    if ($successfulSettingsChange -or $Scenario -in @('settings-startup-failure', 'settings-missing-rollback')) {
        Assert ($global:pttTestLaunches[0].Settings -ceq $newSettingsSnapshot -and
            $global:pttTestLaunches[0].PackageHash -eq $newHash) 'New launch did not see the requested frozen settings bytes with the new package.'
    }
    if (-not $Scenario.StartsWith('settings-')) {
        foreach ($launch in $global:pttTestLaunches) {
            Assert ($launch.Settings -ceq $oldSettingsSnapshot) 'An install without SettingsSource changed settings at launch.'
        }
    }
    $settingsDirectory = [IO.Path]::GetDirectoryName($global:pttTestSettingsPath)
    if ([IO.Directory]::Exists($settingsDirectory)) {
        Assert (@(Get-ChildItem -LiteralPath $settingsDirectory -Filter '.settings-*.tmp').Count -eq 0) 'An atomic settings temporary file was left behind.'
    }
    Write-Host "PASS: $Scenario"
}

try {
    $scenarios = @('owned-worker', 'success', 'startup-failure', 'corrupt-install', 'missing-file', 'live-as-source',
        'ancestor-as-source', 'unexpected-arguments', 'other-location', 'verify-existing',
        'settings-success', 'settings-first-install', 'settings-startup-failure', 'settings-copy-failure',
        'settings-missing-rollback', 'settings-malformed-json', 'settings-invalid-root', 'settings-missing-source',
        'settings-unconfirmed-stop', 'settings-verify-rejected', 'settings-frozen-source', 'settings-unc-source', 'settings-slash-unc-source')
    foreach ($scenario in $scenarios) { Invoke-Fixture $scenario }
    Write-Host "PASS: all $($scenarios.Count) deployment scenarios (including settings transactions and receipt tampering detection)."
}
finally {
    $resolved = [IO.Path]::GetFullPath($testRoot)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
        -not (Split-Path $resolved -Leaf).StartsWith('ptt-deployment-tests-')) {
        throw "Unsafe test cleanup target: $resolved"
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
    Remove-Variable -Scope Global -Name pttTestExpectedExe,pttTestRunning,pttTestWorkers,pttTestStartCount,pttTestStopCount,pttTestFailure,
        pttTestSettingsPath,pttTestSettingsSource,pttTestLaunches,pttTestSettingsAtStops -ErrorAction SilentlyContinue
}
