#Requires -Version 5.1
<# Real archive, checksum, copy, and rollback operations in disposable fixtures.
   Only process, shortcut, and network boundaries are simulated. This does not
   replace acceptance of the downloaded installer against the real app. #>
[CmdletBinding()]
param(
    [string[]]$Scenario = @(),
    [string]$InstallerPath = ''
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2
if ([string]::IsNullOrEmpty($InstallerPath)) { $InstallerPath = Join-Path $PSScriptRoot 'Install-PttDictation.ps1' }
$InstallerPath = [IO.Path]::GetFullPath($InstallerPath)
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('ptt-release-installer-tests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null

function Assert($Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}
function New-Package([string]$Path, [string]$Version) {
    New-Item -ItemType Directory -Path (Join-Path $Path 'nested') -Force | Out-Null
    foreach ($name in @('PttDictation.exe', 'PttDictation.dll', 'PttDictation.Core.dll', 'System.Private.CoreLib.dll')) {
        [IO.File]::WriteAllText((Join-Path $Path $name), "$Version $name")
    }
    [IO.File]::WriteAllText((Join-Path $Path 'nested\asset.dat'), "$Version nested")
    [IO.File]::WriteAllText((Join-Path $Path "$Version-only.dat"), $Version)
}
function Get-TestHashes([string]$Path) {
    $hashes = @{}
    foreach ($file in Get-ChildItem -LiteralPath $Path -File -Recurse) {
        $hashes[$file.FullName.Substring($Path.Length + 1)] = (Get-FileHash -LiteralPath $file.FullName).Hash
    }
    return $hashes
}
function Assert-Hashes([string]$Path, $Expected) {
    $actual = Get-TestHashes $Path
    Assert ($actual.Count -eq $Expected.Count) 'Old installation file count changed.'
    foreach ($name in $Expected.Keys) {
        Assert ($actual[$name] -eq $Expected[$name]) "Old installation changed: $name"
    }
}

function Invoke-Fixture([string]$Case) {
    $caseRoot = Join-Path $testRoot $Case
    $live = Join-Path $caseRoot 'chosen-installation'
    $package = Join-Path $caseRoot 'package'
    $firstInstall = $Case -in @('first-install', 'first-install-copy-failure')
    if (-not $firstInstall) { New-Package $live 'old' }
    New-Package $package 'new'
    if (-not $firstInstall) {
        [IO.File]::WriteAllText((Join-Path $live 'deployment-receipt.json'), '{"oldReceipt":true}')
        $oldHashes = Get-TestHashes $live
    }
    else { $oldHashes = @{} }
    $archive = Join-Path $caseRoot 'PttDictation-win-x64.zip'
    $checksum = "$archive.sha256"
    Compress-Archive -Path (Join-Path $package '*') -DestinationPath $archive
    $archiveHash = (Get-FileHash -LiteralPath $archive).Hash
    [IO.File]::WriteAllText($checksum, "$archiveHash  PttDictation-win-x64.zip")
    $expectedExe = Join-Path $live 'PttDictation.exe'
    $state = @{
        Running = @([pscustomobject]@{ ProcessId = 101; ExecutablePath = $expectedExe;
            CommandLine = '"' + $expectedExe + '"'; CreationDate = [datetime]'2026-01-01' })
        Workers = @(); Stops = @(); Starts = @(); Injected = $false; NetworkCalls = 0; MutationCopies = 0
    }
    if ($Case -eq 'partial-backup' -or $firstInstall) { $state.Running = @() }
    if ($Case -eq 'unrelated-process') { $state.Running[0].ExecutablePath = Join-Path $caseRoot 'unrelated\PttDictation.exe' }
    if ($Case -eq 'checksum-mismatch') { [IO.File]::WriteAllText($checksum, ('0' * 64)) }
    if ($Case -in @('owned-workers', 'worker-stop-failure')) {
        $state.Workers = @(
            [pscustomobject]@{ ProcessId = 1101; ParentProcessId = 101; Name = 'parakeet-server.exe'; CreationDate = [datetime]'2026-01-02'; ExecutablePath = 'C:\runtime\parakeet-server.exe' },
            [pscustomobject]@{ ProcessId = 1102; ParentProcessId = 101; Name = 'parakeet-server.exe'; CreationDate = [datetime]'2025-01-01'; ExecutablePath = 'C:\runtime\parakeet-server.exe' },
            [pscustomobject]@{ ProcessId = 1103; ParentProcessId = 999; Name = 'parakeet-server.exe'; CreationDate = [datetime]'2026-01-02'; ExecutablePath = 'C:\runtime\parakeet-server.exe' },
            [pscustomobject]@{ ProcessId = 1104; ParentProcessId = 101; Name = 'parakeet-server.exe'; CreationDate = [datetime]'2026-01-02'; ExecutablePath = 'C:\runtime\parakeet-server.exe' })
    }
    function Get-Process {
        param($Name, $Id, $ErrorAction)
        if ($null -ne $Name) { Assert ($Name -eq 'PttDictation') 'Unexpected process query.' }
        foreach ($process in @($state.Running)) {
            if ($null -ne $Id -and $process.ProcessId -ne $Id) { continue }
            [pscustomobject]@{ Id = $process.ProcessId; Path = $process.ExecutablePath; HasExited = $false }
        }
    }
    function Get-CimInstance {
        param($ClassName, $Filter)
        Assert ($ClassName -eq 'Win32_Process') 'Unexpected CIM class.'
        if ($Filter -match '^ParentProcessId = (\d+)') {
            return @($state.Workers | Where-Object ParentProcessId -eq ([int]$Matches[1]) | ForEach-Object { $_.PSObject.Copy() })
        }
        if ($Filter -match '^ProcessId = (\d+)$') {
            $queriedId = [int]$Matches[1]
            return @(@($state.Running) + @($state.Workers) | Where-Object ProcessId -eq $queriedId | ForEach-Object { $_.PSObject.Copy() })
        }
        throw "Unexpected CIM filter: $Filter"
    }
    function Stop-Process {
        param($Id, $ErrorAction)
        Assert ($Case -ne 'unrelated-process') 'An unrelated app was stopped.'
        Assert ($Id -notin @(1102,1103,1104)) 'An unrelated, old, or reused worker PID was stopped.'
        $state.Stops += $Id
        if ($Case -eq 'stop-failure') { throw 'Injected app stop failure.' }
        if ($Case -eq 'unconfirmed-stop') { return }
        if ($Case -eq 'worker-stop-failure' -and $Id -eq 1101) { throw 'Injected worker stop failure.' }
        $state.Running = @($state.Running | Where-Object ProcessId -ne $Id)
        $state.Workers = @($state.Workers | Where-Object ProcessId -ne $Id)
        if ($Id -eq 101) {
            # A reused child PID must not be killed after the parent exits.
            foreach ($worker in $state.Workers) {
                if ($worker.ProcessId -eq 1104) { $worker.CreationDate = [datetime]'2026-02-01' }
            }
        }
    }
    function Wait-Process { param($Id, $Timeout, $ErrorAction) }
    function Start-Sleep { param($Seconds, $Milliseconds) }
    function Invoke-FixtureLaunch {
        param($FilePath, $Arguments, $WorkingDirectory, $Verb, $Show)
        Assert ($FilePath -eq $expectedExe -and $WorkingDirectory -eq $live) 'Launch moved the installation.'
        Assert ($Arguments -ceq '' -and $Verb -eq 'open' -and $Show -eq 1) 'Expected a normal launch with no arguments.'
        $state.Starts += (Get-FileHash -LiteralPath (Join-Path $live 'PttDictation.dll')).Hash
        if ($Case -eq 'launch-failure' -and $state.Starts.Count -eq 1) { throw 'Injected launch failure.' }
        $newId = 200 + $state.Starts.Count
        $state.Running += [pscustomobject]@{ ProcessId = $newId; ExecutablePath = $FilePath;
            CommandLine = '"' + $FilePath + '"'; CreationDate = [datetime]::Now }
    }
    function Start-Process { throw 'Installer must launch through the Explorer desktop.' }
    function New-Object {
        param($ComObject)
        Assert ($ComObject -eq 'Shell.Application') 'Shortcut writes are forbidden in installer fixtures.'
        $application = [pscustomobject]@{}
        $application | Add-Member ScriptMethod ShellExecute {
            param($FilePath, $Arguments, $WorkingDirectory, $Verb, $Show)
            Invoke-FixtureLaunch $FilePath $Arguments $WorkingDirectory $Verb $Show
        }
        $windows = [pscustomobject]@{ Desktop = [pscustomobject]@{ Document = [pscustomobject]@{ Application = $application } } }
        $windows | Add-Member ScriptMethod FindWindowSW {
            param($Location, $Root, $Class, $Handle, $Options)
            return $this.Desktop
        }
        $shell = [pscustomobject]@{ ShellWindows = $windows }
        $shell | Add-Member ScriptMethod Windows { return $this.ShellWindows }
        return $shell
    }
    function Move-Item { throw 'Installation must preserve its selected directory.' }
    function Copy-Item {
        param([Parameter(ValueFromPipeline)]$InputObject, $Destination, [switch]$Recurse, [switch]$Force)
        process {
            $InputObject | Microsoft.PowerShell.Management\Copy-Item -Destination $Destination -Recurse:$Recurse -Force:$Force
            if ($Destination -eq $live) { $state.MutationCopies++ }
            if ($Case -eq 'partial-backup' -and (Split-Path $Destination -Leaf) -like '.backup-*' -and -not $state.Injected) {
                $state.Injected = $true
                throw 'Injected incomplete backup copy.'
            }
            if ($Case -in @('copy-failure', 'copy-failure-no-launch', 'first-install-copy-failure') -and $Destination -eq $live -and -not $state.Injected) {
                $state.Injected = $true
                throw 'Injected installation copy failure.'
            }
            if ($Case -eq 'corrupt-backup' -and (Split-Path $Destination -Leaf) -like '.backup-*' -and $InputObject.Name -eq 'PttDictation.dll') {
                [IO.File]::WriteAllText((Join-Path $Destination 'PttDictation.dll'), 'incomplete backup bytes')
            }
        }
    }
    function Invoke-RestMethod {
        param($Uri, $Headers)
        Assert ($Case -eq 'online-success' -and $Uri -eq 'https://api.github.com/repos/DiscoStew6082/ptt-dictation/releases/latest') 'Unexpected network access.'
        $state.NetworkCalls++
        return [pscustomobject]@{ draft = $false; prerelease = $false; tag_name = 'v-test'; assets = @(
            [pscustomobject]@{ name = 'PttDictation-win-x64.zip'; browser_download_url = 'https://fixture.invalid/package' },
            [pscustomobject]@{ name = 'PttDictation-win-x64.zip.sha256'; browser_download_url = 'https://fixture.invalid/checksum' }) }
    }
    function Invoke-WebRequest {
        param($Uri, $Headers, $OutFile, [switch]$UseBasicParsing)
        Assert ($Case -eq 'online-success') 'Unexpected download.'
        $state.NetworkCalls++
        $source = if ($Uri -eq 'https://fixture.invalid/package') { $archive } elseif ($Uri -eq 'https://fixture.invalid/checksum') { $checksum } else { throw 'Unexpected asset URI.' }
        [IO.File]::Copy($source, $OutFile)
    }
    $arguments = @{ PackagePath = $archive; ChecksumPath = $checksum; InstallDirectory = $live; NoShortcut = $true }
    if ($Case -eq 'online-success') { $arguments.Remove('PackagePath'); $arguments.Remove('ChecksumPath') }
    if ($Case -in @('no-launch', 'copy-failure-no-launch')) { $arguments.NoLaunch = $true }
    if ($Case -eq 'relative-path') { $arguments.InstallDirectory = '.' }
    if ($Case -eq 'drive-relative-path') { $arguments.InstallDirectory = [IO.Path]::GetPathRoot($live).Substring(0,2) + 'relative-folder' }
    $caught = $null
    $previousDirectory = [Environment]::CurrentDirectory
    $invocationDirectory = if ($firstInstall) { $caseRoot } else { $live }
    Push-Location $invocationDirectory
    try {
        [Environment]::CurrentDirectory = $invocationDirectory
        $resolvedTarget = [IO.Path]::GetFullPath($arguments.InstallDirectory)
        Assert ($resolvedTarget.StartsWith($caseRoot + '\', [StringComparison]::OrdinalIgnoreCase)) 'Fixture target escaped its directory.'
        & $InstallerPath @arguments | Out-Null
    }
    catch { $caught = $_ }
    finally { [Environment]::CurrentDirectory = $previousDirectory; Pop-Location }
    $success = $Case -in @('success', 'online-success', 'no-launch', 'owned-workers', 'first-install')
    if ($success) {
        Assert ($null -eq $caught) "Unexpected install failure: $caught"
        $newHashes = Get-TestHashes $package
        $installed = Get-TestHashes $live
        $installed.Remove('deployment-receipt.json')
        Assert ($installed.Count -eq $newHashes.Count) 'Installed package file count changed.'
        foreach ($name in $newHashes.Keys) { Assert ($installed[$name] -eq $newHashes[$name]) "New package file missing or changed: $name" }
        Assert (-not (Test-Path -LiteralPath (Join-Path $live 'old-only.dat'))) 'Obsolete file retained.'
        $receipt = Get-Content -LiteralPath (Join-Path $live 'deployment-receipt.json') -Raw | ConvertFrom-Json
        Assert ($receipt.ExecutablePath -eq $expectedExe -and $receipt.ArchiveSha256 -eq $archiveHash) 'Receipt has wrong destination or archive hash.'
        if ($firstInstall) { Assert ($null -eq $receipt.BackupDirectory) 'Fresh installation claimed an old backup.' }
        else { Assert-Hashes $receipt.BackupDirectory $oldHashes }
        $expectedStarts = if ($Case -eq 'no-launch') { 0 } else { 1 }
        Assert ($state.Starts.Count -eq $expectedStarts -and $state.Running.Count -eq $expectedStarts) 'Wrong launch behavior.'
        if ($Case -eq 'owned-workers') {
            Assert (1101 -in $state.Stops -and $state.Workers.Count -eq 3) 'Owned worker survived or unrelated worker changed.'
        }
        if ($Case -eq 'online-success') { Assert ($state.NetworkCalls -eq 3 -and $receipt.ReleaseTag -eq 'v-test') 'Latest-release lookup/download was not exercised.' }
        if ($Case -eq 'success') {
            $verified = & $InstallerPath -VerifyOnly -InstallDirectory $live
            Assert ($verified.DeploymentReceiptVerified -and $verified.FilesHashed -eq $newHashes.Count) 'PowerShell 5.1 could not verify the installed receipt.'
            Assert ($state.Starts.Count -eq 1 -and $state.Stops.Count -eq 1) 'Read-only verification changed process state.'
        }
    }
    else {
        Assert ($null -ne $caught) "Expected $Case to fail."
        $expectedError = switch ($Case) {
            'partial-backup' { '*Injected incomplete backup*' }
            'corrupt-backup' { '*hash mismatch*' }
            { $_ -in @('copy-failure', 'copy-failure-no-launch', 'first-install-copy-failure') } { '*Injected installation copy failure*' }
            'launch-failure' { '*Injected launch failure*' }
            'checksum-mismatch' { '*SHA-256 verification failed*' }
            'unrelated-process' { '*Another PTT Dictation executable*' }
            'stop-failure' { '*Injected app stop failure*' }
            'unconfirmed-stop' { '*did not stop*' }
            'worker-stop-failure' { '*Injected worker stop failure*' }
            { $_ -in @('relative-path', 'drive-relative-path') } { '*fully qualified local directory*' }
        }
        Assert ("$caught" -like $expectedError) "Unexpected $Case error: $caught"
        if ($firstInstall) {
            Assert (-not (Test-Path -LiteralPath $live)) 'Failed first installation left a partial package.'
            Assert ($state.Starts.Count -eq 0 -and $state.Stops.Count -eq 0) 'Failed first installation changed process state.'
        }
        else { Assert-Hashes $live $oldHashes }
        if ($Case -eq 'copy-failure-no-launch') {
            Assert ($state.Starts.Count -eq 0 -and $state.Running.Count -eq 0) 'NoLaunch rollback launched an app.'
        }
        elseif ($Case -in @('copy-failure', 'launch-failure', 'worker-stop-failure')) {
            Assert ($state.Running.Count -eq 1) 'Recovery left the app stopped.'
            Assert ($state.Starts[-1] -eq $oldHashes['PttDictation.dll']) 'Recovery launched the failed package.'
        }
        elseif (-not $firstInstall) {
            Assert ($state.Starts.Count -eq 0 -and $state.MutationCopies -eq 0) 'Pre-install failure changed the installation or relaunched an untouched app.'
        }
    }
    Write-Host "PASS $Case"
}

$cases = @(if ($Scenario.Count -gt 0) { $Scenario } else { @(
    'partial-backup', 'corrupt-backup', 'relative-path', 'drive-relative-path', 'checksum-mismatch',
    'success', 'online-success', 'first-install', 'first-install-copy-failure', 'copy-failure', 'copy-failure-no-launch', 'launch-failure', 'no-launch', 'unrelated-process',
    'stop-failure', 'unconfirmed-stop', 'owned-workers', 'worker-stop-failure') })
$failures = @()
try {
    foreach ($case in $cases) {
        try { Invoke-Fixture $case }
        catch { $failures += "$case`: $_"; Write-Host "FAIL $case`: $_" }
    }
}
finally {
    $resolvedRoot = [IO.Path]::GetFullPath($testRoot)
    $tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $resolvedRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        (Split-Path $resolvedRoot -Leaf) -notlike 'ptt-release-installer-tests-*') { throw 'Unsafe fixture cleanup path.' }
    Remove-Item -LiteralPath $resolvedRoot -Recurse -Force
}
if ($failures.Count -gt 0) { throw ($failures -join [Environment]::NewLine) }
Write-Host "Release installer: $($cases.Count) scenarios passed."
