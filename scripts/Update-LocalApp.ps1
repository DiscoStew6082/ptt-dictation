#Requires -Version 7.0
<#
.SYNOPSIS
Installs a tested package at the permanent Start-menu shortcut target.
.EXAMPLE
pwsh -File scripts/Update-LocalApp.ps1 -StagedPath publish/next-build
.EXAMPLE
pwsh -File scripts/Update-LocalApp.ps1 -StagedPath publish/next-build -SettingsSource settings-snapshot.json
.EXAMPLE
pwsh -File scripts/Update-LocalApp.ps1 -VerifyOnly
#>
[CmdletBinding(DefaultParameterSetName = 'Install')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Install')]
    [string]$StagedPath,
    [Parameter(ParameterSetName = 'Install')]
    [ValidateNotNullOrEmpty()]
    [string]$SettingsSource,
    [Parameter(Mandatory, ParameterSetName = 'Verify')]
    [switch]$VerifyOnly,
    [ValidateNotNullOrEmpty()]
    [string]$InstallDirectory = (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs\PttDictation')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# The default is stable per user and independent of cwd and worktrees. An explicit
# directory supports portable and secondary-machine installations.
$liveDirectory = [IO.Path]::GetFullPath($InstallDirectory)
if (-not [IO.Path]::IsPathFullyQualified($InstallDirectory) -or
    $InstallDirectory.StartsWith('\\') -or $InstallDirectory.StartsWith('//') -or
    $liveDirectory -eq [IO.Path]::GetPathRoot($liveDirectory)) {
    throw 'InstallDirectory must be a fully qualified local directory below a drive root.'
}
$publishDirectory = Split-Path $liveDirectory -Parent
$liveExe = Join-Path $liveDirectory 'PttDictation.exe'
$receiptName = 'deployment-receipt.json'
# A supplied file provides bytes only, never an installation/settings destination.
$settingsPath = Join-Path $env:LOCALAPPDATA 'PttDictation\settings.json'

function Assert-NoLinks([string]$Path) {
    $item = Get-Item -LiteralPath $Path -Force
    while ($null -ne $item) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Refusing linked path: $($item.FullName)"
        }
        $item = $item.Parent
    }
    foreach ($child in Get-ChildItem -LiteralPath $Path -Recurse -Force) {
        if ($child.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Refusing linked package entry: $($child.FullName)"
        }
    }
}

function Get-PackageHashes([string]$Directory) {
    foreach ($required in @('PttDictation.exe', 'PttDictation.dll', 'PttDictation.Core.dll', 'System.Private.CoreLib.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $Directory $required) -PathType Leaf)) {
            throw "Incomplete self-contained package: missing $required in $Directory"
        }
    }
    $hashes = [ordered]@{}
    foreach ($file in Get-ChildItem -LiteralPath $Directory -Recurse -File -Force | Sort-Object FullName) {
        $relative = [IO.Path]::GetRelativePath($Directory, $file.FullName)
        if ($relative -ne $receiptName) {
            $hashes[$relative] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
        }
    }
    return $hashes
}

function Assert-PackageHashes([string]$Directory, $Expected) {
    $actual = Get-PackageHashes $Directory
    if ($actual.Count -ne $Expected.Count) { throw 'Installed package file count does not match.' }
    foreach ($name in $Expected.Keys) {
        if ($actual[$name] -ne $Expected[$name]) { throw "Installed file hash mismatch: $name" }
    }
}

function Get-LiveProcesses {
    $result = @()
    foreach ($nativeProcess in @(Get-Process -Name PttDictation -ErrorAction SilentlyContinue)) {
        try {
            if ($nativeProcess.HasExited) { continue }
            $nativePath = $nativeProcess.Path
        }
        catch { continue }
        if ($nativePath -ne $liveExe) {
            throw "Another PTT executable is running (PID $($nativeProcess.Id)); close it before updating."
        }
        $details = @(Get-CimInstance Win32_Process -Filter "ProcessId = $($nativeProcess.Id)")
        if ($details.Count -eq 0) {
            try { if ($nativeProcess.HasExited) { continue } }
            catch { continue }
        }
        if ($details.Count -ne 1) { throw "Could not verify PTT process $($nativeProcess.Id)." }
        if ($details[0].CommandLine.Trim() -notin @($liveExe, ('"' + $liveExe + '"'))) {
            throw "Unexpected arguments on PTT process $($nativeProcess.Id)."
        }
        $result += [pscustomobject]@{
            ProcessId = $nativeProcess.Id
            ExecutablePath = $nativePath
            CommandLine = $details[0].CommandLine
            CreationDate = $details[0].CreationDate
        }
    }
    return $result
}

function Start-AndVerifyLive {
    Write-Host "Launch: Start-Process -FilePath '$liveExe' -WorkingDirectory '$liveDirectory'"
    $started = Start-Process -FilePath $liveExe -WorkingDirectory $liveDirectory -PassThru
    Start-Sleep -Seconds 2
    $running = @(Get-LiveProcesses)
    if ($running.Count -ne 1 -or $running[0].ProcessId -ne $started.Id) {
        throw 'Expected exactly one new process at the permanent executable path.'
    }
    return $running[0]
}

function Stop-LiveProcess($Process) {
    # Snapshot only this app's direct transcription workers before its PID exits.
    # Recheck each identity afterward so PID reuse cannot stop unrelated work.
    $current = @(Get-CimInstance Win32_Process -Filter "ProcessId = $($Process.ProcessId)")
    if ($current.Count -eq 0) { return }
    if ($current[0].ExecutablePath -ne $liveExe -or $current[0].CreationDate -ne $Process.CreationDate) {
        throw 'The live process identity changed before shutdown.'
    }
    $workers = @(Get-CimInstance Win32_Process -Filter "ParentProcessId = $($Process.ProcessId) AND Name = 'parakeet-server.exe'" |
        Where-Object { $_.CreationDate -ge $Process.CreationDate })
    Stop-Process -Id $Process.ProcessId -ErrorAction Stop
    Wait-Process -Id $Process.ProcessId -Timeout 15 -ErrorAction SilentlyContinue
    foreach ($worker in $workers) {
        $remaining = @(Get-CimInstance Win32_Process -Filter "ProcessId = $($worker.ProcessId)")
        if ($remaining.Count -eq 1 -and
            $remaining[0].ParentProcessId -eq $Process.ProcessId -and
            $remaining[0].CreationDate -eq $worker.CreationDate -and
            $remaining[0].ExecutablePath -eq $worker.ExecutablePath) {
            Stop-Process -Id $worker.ProcessId -ErrorAction Stop
            Wait-Process -Id $worker.ProcessId -Timeout 15 -ErrorAction SilentlyContinue
        }
    }
}

function Copy-PublishPackage([string]$Source, [string]$Destination) {
    # Preserve the live directory and existing executable in place so shell
    # shortcuts cannot follow a renamed executable into a backup directory.
    foreach ($path in @($Source, $Destination)) {
        if ((Split-Path ([IO.Path]::GetFullPath($path)) -Parent) -ne $publishDirectory) {
            throw "Package copy outside the permanent publish root: $path"
        }
    }
    Assert-NoLinks $Source
    Assert-NoLinks $Destination
    $sourceFiles = @{}
    foreach ($file in Get-ChildItem -LiteralPath $Source -Recurse -File -Force) {
        $sourceFiles[[IO.Path]::GetRelativePath($Source, $file.FullName)] = $true
    }
    Get-ChildItem -LiteralPath $Source -Force | Copy-Item -Destination $Destination -Recurse -Force
    foreach ($file in Get-ChildItem -LiteralPath $Destination -Recurse -File -Force) {
        $relative = [IO.Path]::GetRelativePath($Destination, $file.FullName)
        if (-not $sourceFiles.ContainsKey($relative)) {
            # Only obsolete files under the validated destination are removed.
            Remove-Item -LiteralPath $file.FullName -Force
        }
    }
}

function Assert-SettingsFilePath([string]$Path) {
    $entry = [IO.Path]::GetFullPath($Path)
    if (-not [IO.Path]::IsPathFullyQualified($Path) -or
        $Path.StartsWith('\\') -or $Path.StartsWith('//') -or $entry.StartsWith('\\')) {
        throw 'Settings must use a fully qualified local file path.'
    }
    if (Test-Path -LiteralPath $entry -PathType Container) {
        throw "Settings path is a directory: $entry"
    }
    while (-not [string]::IsNullOrEmpty($entry)) {
        if (Test-Path -LiteralPath $entry) {
            $item = Get-Item -LiteralPath $entry -Force
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Refusing linked settings path: $entry"
            }
        }
        $entry = [IO.Path]::GetDirectoryName($entry)
    }
}

function Read-RequestedSettings([string]$Path) {
    # Reject network/provider paths lexically before Resolve-Path or file access.
    if ($Path.Contains('::') -or [IO.Path]::GetFullPath($Path.Replace('/', '\')).StartsWith('\\')) {
        throw 'Settings source must be a local JSON file.'
    }
    $resolved = Resolve-Path -LiteralPath $Path
    if ($resolved.Provider.Name -ne 'FileSystem') { throw 'Settings source must be a local JSON file.' }
    Assert-SettingsFilePath $resolved.ProviderPath
    [byte[]]$bytes = [IO.File]::ReadAllBytes($resolved.ProviderPath)
    $stream = [IO.MemoryStream]::new($bytes, $false)
    $reader = [IO.StreamReader]::new($stream, [Text.UTF8Encoding]::new($false, $true), $true)
    $document = $null
    try {
        # Validate the frozen bytes with the same strict JSON syntax as the app.
        $document = [Text.Json.JsonDocument]::Parse($reader.ReadToEnd())
        if ($document.RootElement.ValueKind -ne [Text.Json.JsonValueKind]::Object) {
            throw 'Settings JSON must contain an object.'
        }
    }
    catch { throw "Invalid settings JSON in '$Path': $_" }
    finally {
        if ($null -ne $document) { $document.Dispose() }
        $reader.Dispose()
        $stream.Dispose()
    }
    return ,$bytes
}

function Write-PermanentSettings([byte[]]$Bytes) {
    Assert-SettingsFilePath $settingsPath
    $directory = [IO.Path]::GetDirectoryName($settingsPath)
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    $temporary = Join-Path $directory ('.settings-' + [guid]::NewGuid().ToString('N') + '.tmp')
    try {
        [IO.File]::WriteAllBytes($temporary, $Bytes)
        if ([IO.File]::Exists($settingsPath)) {
            [IO.File]::Replace($temporary, $settingsPath, [NullString]::Value)
        }
        else {
            [IO.File]::Move($temporary, $settingsPath)
        }
        if ([Convert]::ToBase64String([IO.File]::ReadAllBytes($settingsPath)) -cne [Convert]::ToBase64String($Bytes)) {
            throw 'Installed settings bytes do not match the requested file.'
        }
    }
    finally {
        if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) }
    }
}

function Assert-LiveStopped {
    if (@(Get-LiveProcesses).Count -ne 0) {
        throw 'The canonical app is still running; refusing to change its package or settings.'
    }
}

# Serialize installs so two sessions cannot replace each other's package.
$mutex = [Threading.Mutex]::new($false, 'Local\PttDictation-PermanentDeployment')
$locked = $false
try {
    try { $locked = $mutex.WaitOne(0) }
    catch [Threading.AbandonedMutexException] { $locked = $true }
    if (-not $locked) { throw 'Another app deployment is already running.' }
    Assert-NoLinks $liveDirectory
    $oldHashes = Get-PackageHashes $liveDirectory
    $running = @(Get-LiveProcesses)
    if ($running.Count -gt 1) { throw 'Multiple canonical processes are running; close duplicates first.' }

    if ($VerifyOnly) {
        $receiptPath = Join-Path $liveDirectory $receiptName
        $receiptVerified = $false
        if (Test-Path -LiteralPath $receiptPath) {
            $receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json -AsHashtable
            if ($receipt.ExecutablePath -ne $liveExe) { throw 'Deployment receipt points at a different executable.' }
            Assert-PackageHashes $liveDirectory $receipt.Hashes
            $receiptVerified = $true
        }
        [pscustomobject]@{
            ExecutablePath = $liveExe
            ProcessIds = @($running | ForEach-Object ProcessId)
            FilesHashed = $oldHashes.Count
            DeploymentReceiptVerified = $receiptVerified
        }
        return
    }

    $changeSettings = $PSBoundParameters.ContainsKey('SettingsSource')
    $requestedSettings = $null
    if ($changeSettings) {
        $requestedSettings = Read-RequestedSettings $SettingsSource
        Assert-SettingsFilePath $settingsPath
    }

    $source = (Resolve-Path -LiteralPath $StagedPath).Path
    if ($source -eq $liveDirectory -or
        $source.StartsWith($liveDirectory + '\', [StringComparison]::OrdinalIgnoreCase) -or
        $liveDirectory.StartsWith($source.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Stage a separate package; the live directory cannot be the source.'
    }
    Assert-NoLinks $source
    $expected = Get-PackageHashes $source
    $installId = [guid]::NewGuid().ToString('N')
    $prepared = Join-Path $publishDirectory ".install-$installId"
    $backup = Join-Path $publishDirectory ".backup-$installId"
    New-Item -ItemType Directory -Path $prepared | Out-Null
    Get-ChildItem -LiteralPath $source -Force | Copy-Item -Destination $prepared -Recurse -Force
    Assert-PackageHashes $prepared $expected
    New-Item -ItemType Directory -Path $backup | Out-Null
    Copy-PublishPackage $liveDirectory $backup
    Assert-PackageHashes $backup $oldHashes
    # Check again immediately before stopping; preflight errors leave the app alone.
    $running = @(Get-LiveProcesses)
    if ($running.Count -gt 1) { throw 'Multiple canonical processes appeared during preparation.' }
    # Capture the previous existence and exact bytes before shutdown. With no
    # SettingsSource, settings are neither read nor written by the installer.
    $previousSettingsExisted = $false
    $previousSettings = $null
    if ($changeSettings) {
        Assert-SettingsFilePath $settingsPath
        $previousSettingsExisted = [IO.File]::Exists($settingsPath)
        if ($previousSettingsExisted) { $previousSettings = [IO.File]::ReadAllBytes($settingsPath) }
    }
    $stopped = $false
    $installStarted = $false
    $settingsAttempted = $false
    try {
        foreach ($process in $running) {
            $stopped = $true
            Stop-LiveProcess $process
        }
        Assert-LiveStopped
        if ($changeSettings) {
            # Mark the attempt before the atomic operation, so a verification
            # failure after replacement also restores the old bytes.
            $settingsAttempted = $true
            Write-PermanentSettings $requestedSettings
        }
        $installStarted = $true
        Copy-PublishPackage $prepared $liveDirectory
        Assert-PackageHashes $liveDirectory $expected
        $process = Start-AndVerifyLive
        $receipt = [ordered]@{
            InstalledAtUtc = [DateTime]::UtcNow.ToString('o')
            ExecutablePath = $liveExe
            ProcessIdAtInstall = $process.ProcessId
            BackupDirectory = $backup
            Hashes = $expected
        }
        $receipt | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $liveDirectory $receiptName) -Encoding utf8
        Write-Host "Verified $($expected.Count) package files and PID $($process.ProcessId) at $liveExe"
        Write-Host "Previous package retained at $backup"
    }
    catch {
        $installError = $_
        try {
            if ($installStarted -or $settingsAttempted) {
                foreach ($process in @(Get-LiveProcesses)) {
                    Stop-LiveProcess $process
                }
                Assert-LiveStopped
            }
            # Restore settings before package recovery and before any restart.
            # If this fails, leave the app stopped and report recovery failure.
            if ($settingsAttempted) {
                if ($previousSettingsExisted) {
                    Write-PermanentSettings $previousSettings
                }
                else {
                    Assert-SettingsFilePath $settingsPath
                    [IO.File]::Delete($settingsPath)
                }
            }
            if ($installStarted) {
                Copy-PublishPackage $backup $liveDirectory
                Assert-PackageHashes $liveDirectory $oldHashes
            }
            if (($stopped -or $installStarted -or $settingsAttempted) -and @(Get-LiveProcesses).Count -eq 0) {
                $null = Start-AndVerifyLive
                Write-Warning 'Update failed; the previous package was restored and restarted at the permanent path.'
            }
        }
        catch { throw "Update failed: $installError. Recovery also failed: $_. Previous package: $backup" }
        throw $installError
    }
    # Cleanup cannot invalidate a verified installation or trigger a rollback.
    try {
        if ((Split-Path ([IO.Path]::GetFullPath($prepared)) -Parent) -ne $publishDirectory -or
            (Split-Path $prepared -Leaf) -ne ".install-$installId") {
            throw "Unsafe staging cleanup target: $prepared"
        }
        Assert-NoLinks $prepared
        Remove-Item -LiteralPath $prepared -Recurse -Force
    }
    catch { Write-Warning "Installation succeeded; temporary package cleanup failed: $_" }
}
finally {
    if ($locked) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
