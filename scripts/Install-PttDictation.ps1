#Requires -Version 5.1
<#
.SYNOPSIS
Installs or verifies PTT Dictation from a release download, archive, or staged build.
.EXAMPLE
powershell -File .\Install-PttDictation.ps1 -InstallDirectory D:\Apps\PttDictation
.EXAMPLE
powershell -File .\Install-PttDictation.ps1 -PackagePath .\PttDictation-win-x64.zip -ChecksumPath .\PttDictation-win-x64.zip.sha256
.EXAMPLE
pwsh -File .\Install-PttDictation.ps1 -StagedPath publish/next-build -NoShortcut
.EXAMPLE
pwsh -File .\Install-PttDictation.ps1 -VerifyOnly
#>
[CmdletBinding(DefaultParameterSetName = 'Online')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Offline')][string]$PackagePath,
    [Parameter(Mandatory, ParameterSetName = 'Offline')][string]$ChecksumPath,
    [Parameter(Mandatory, ParameterSetName = 'Staged')][string]$StagedPath,
    [Parameter(ParameterSetName = 'Staged')][ValidateNotNullOrEmpty()][string]$SettingsSource,
    [Parameter(Mandatory, ParameterSetName = 'Verify')][switch]$VerifyOnly,
    [ValidateNotNullOrEmpty()]
    [string]$InstallDirectory = (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs\PttDictation'),
    [switch]$NoShortcut,
    [switch]$NoLaunch
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2
$repository = 'DiscoStew6082/ptt-dictation'
$packageName = 'PttDictation-win-x64.zip'
$checksumName = "$packageName.sha256"
$receiptName = 'deployment-receipt.json'
$liveDirectory = [IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\')
$liveExe = Join-Path $liveDirectory 'PttDictation.exe'
$settingsPath = Join-Path $env:LOCALAPPDATA 'PttDictation\settings.json'

function Assert-SafeLocalDirectory([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    # Windows PowerShell 5.1 lacks Path.IsPathFullyQualified. A drive and root
    # separator reject relative and drive-relative inputs before normalization.
    if ($Path -notmatch '^[A-Za-z]:[\\/]' -or $Path.StartsWith('\\') -or $Path.StartsWith('//') -or
        $full -eq [IO.Path]::GetPathRoot($full)) {
        throw 'InstallDirectory must be a fully qualified local directory below a drive root.'
    }
    $entry = $full
    while (-not [string]::IsNullOrEmpty($entry)) {
        if (Test-Path -LiteralPath $entry) {
            $item = Get-Item -LiteralPath $entry -Force
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Refusing linked installation path: $entry"
            }
        }
        $entry = [IO.Path]::GetDirectoryName($entry)
    }
}

function Assert-NoLinks([string]$Path) {
    $item = Get-Item -LiteralPath $Path -Force
    while ($null -ne $item) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Refusing linked path: $Path" }
        $item = $item.Parent
    }
    foreach ($child in Get-ChildItem -LiteralPath $Path -Recurse -Force) {
        if ($child.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Refusing linked package entry: $($child.FullName)"
        }
    }
}

function Get-RelativeName([string]$Root, [string]$Path) {
    $prefix = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    $full = [IO.Path]::GetFullPath($Path)
    if (-not $full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path is outside package root: $full"
    }
    return $full.Substring($prefix.Length)
}

function Get-PackageHashes([string]$Directory, [switch]$Backup) {
    if (-not $Backup) {
        foreach ($required in @('PttDictation.exe', 'PttDictation.dll', 'PttDictation.Core.dll', 'System.Private.CoreLib.dll')) {
            if (-not (Test-Path -LiteralPath (Join-Path $Directory $required) -PathType Leaf)) {
                throw "Incomplete self-contained package: missing $required"
            }
        }
    }
    $hashes = [ordered]@{}
    foreach ($file in Get-ChildItem -LiteralPath $Directory -Recurse -File -Force | Sort-Object FullName) {
        $relative = Get-RelativeName $Directory $file.FullName
        if ($Backup -or $relative -ne $receiptName) {
            $hashes[$relative] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
        }
    }
    return $hashes
}

function Assert-PackageHashes([string]$Directory, $Expected, [switch]$Backup) {
    $actual = Get-PackageHashes $Directory -Backup:$Backup
    if ($actual.Count -ne $Expected.Count) { throw 'Installed package file count does not match.' }
    foreach ($name in $Expected.Keys) {
        if ($actual[$name] -ne $Expected[$name]) { throw "Installed file hash mismatch: $name" }
    }
}

function Copy-Package([string]$Source, [string]$Destination) {
    Assert-NoLinks $Source
    if (-not (Test-Path -LiteralPath $Destination)) {
        New-Item -ItemType Directory -Path $Destination | Out-Null
    }
    Assert-NoLinks $Destination
    $sourceFiles = @{}
    foreach ($file in Get-ChildItem -LiteralPath $Source -Recurse -File -Force) {
        $sourceFiles[(Get-RelativeName $Source $file.FullName)] = $true
    }
    Get-ChildItem -LiteralPath $Source -Force | Copy-Item -Destination $Destination -Recurse -Force
    foreach ($file in Get-ChildItem -LiteralPath $Destination -Recurse -File -Force) {
        $relative = Get-RelativeName $Destination $file.FullName
        if (-not $sourceFiles.ContainsKey($relative)) { Remove-Item -LiteralPath $file.FullName -Force }
    }
}

function Get-PttProcesses {
    $result = @()
    foreach ($nativeProcess in @(Get-Process -Name PttDictation -ErrorAction SilentlyContinue)) {
        try {
            if ($nativeProcess.HasExited) { continue }
            $nativePath = $nativeProcess.Path
        }
        catch { continue }
        $details = @(Get-CimInstance Win32_Process -Filter "ProcessId = $($nativeProcess.Id)")
        if ([string]::IsNullOrEmpty($nativePath) -and $details.Count -eq 1) {
            $nativePath = $details[0].ExecutablePath
        }
        if ([string]::IsNullOrEmpty($nativePath)) {
            if ($null -eq (Get-Process -Id $nativeProcess.Id -ErrorAction SilentlyContinue)) { continue }
            throw "Could not verify PTT Dictation process $($nativeProcess.Id)."
        }
        if ($nativePath -ne $liveExe) {
            throw "Another PTT Dictation executable is running at '$nativePath' (PID $($nativeProcess.Id)); close it before installing."
        }
        if ($details.Count -eq 0) {
            try { if ($nativeProcess.HasExited) { continue } } catch { continue }
        }
        if ($details.Count -ne 1) { throw "Could not verify PTT Dictation process $($nativeProcess.Id)." }
        $commandLine = if ($null -eq $details[0].CommandLine) { '' } else { $details[0].CommandLine.Trim() }
        if ($commandLine -notin @($liveExe, ('"' + $liveExe + '"'))) {
            throw "Unexpected arguments on PTT Dictation process $($nativeProcess.Id)."
        }
        $result += [pscustomobject]@{ ProcessId = $nativeProcess.Id; ExecutablePath = $nativePath;
            CommandLine = $commandLine; CreationDate = $details[0].CreationDate }
    }
    return $result
}

function Stop-PttProcesses {
    foreach ($process in @(Get-PttProcesses)) {
        $current = @(Get-CimInstance Win32_Process -Filter "ProcessId = $($process.ProcessId)")
        if ($current.Count -eq 0) { continue }
        if ($current[0].ExecutablePath -ne $liveExe -or $current[0].CreationDate -ne $process.CreationDate) {
            throw 'The live process identity changed before shutdown.'
        }
        # Snapshot only this app's workers. Recheck identity after the parent
        # exits so PID reuse cannot stop another app's transcription process.
        $workers = @(Get-CimInstance Win32_Process -Filter "ParentProcessId = $($process.ProcessId) AND Name = 'parakeet-server.exe'" |
            Where-Object { $_.CreationDate -ge $process.CreationDate })
        $script:stopAttempted = $true
        Stop-Process -Id $process.ProcessId -ErrorAction Stop
        Wait-Process -Id $process.ProcessId -Timeout 15 -ErrorAction SilentlyContinue
        $remainingApp = @(Get-CimInstance Win32_Process -Filter "ProcessId = $($process.ProcessId)")
        if ($remainingApp.Count -eq 1 -and $remainingApp[0].CreationDate -eq $process.CreationDate) {
            throw 'PTT Dictation did not stop before installation.'
        }
        foreach ($worker in $workers) {
            $remaining = @(Get-CimInstance Win32_Process -Filter "ProcessId = $($worker.ProcessId)")
            if ($remaining.Count -eq 1 -and $remaining[0].ParentProcessId -eq $process.ProcessId -and
                $remaining[0].CreationDate -eq $worker.CreationDate -and
                $remaining[0].ExecutablePath -eq $worker.ExecutablePath) {
                Stop-Process -Id $worker.ProcessId -ErrorAction Stop
                Wait-Process -Id $worker.ProcessId -Timeout 15 -ErrorAction SilentlyContinue
            }
        }
    }
    if (@(Get-PttProcesses).Count -ne 0) { throw 'PTT Dictation did not stop before installation.' }
}

function Get-ExplorerDesktopApplication {
    # Direct Start-Process and the root Shell.Application object can inherit the
    # deployment host's kill-on-close job. Dispatch from Explorer's desktop
    # automation object instead, so closing the updater cannot close dictation.
    $shell = New-Object -ComObject Shell.Application
    $windows = $shell.Windows()
    $desktopHandle = 0
    $desktop = $windows.FindWindowSW(0, 0, 8, [ref]$desktopHandle, 1)
    if ($null -eq $desktop -or $null -eq $desktop.Document -or $null -eq $desktop.Document.Application) {
        throw 'Explorer desktop is unavailable; cannot launch PTT independently of the deployment host.'
    }
    return $desktop.Document.Application
}

function Start-AndVerify {
    $desktopApplication = Get-ExplorerDesktopApplication
    Assert-LiveStopped
    $launchedAt = [DateTime]::Now
    Write-Host "Launch via Explorer desktop: ShellExecute('$liveExe', '', '$liveDirectory', 'open', 1)"
    $desktopApplication.ShellExecute($liveExe, '', $liveDirectory, 'open', 1)
    # Explorer dispatch is asynchronous and provides no PID. Accept only a fresh
    # canonical process, then confirm the same identity survives startup.
    $started = $null
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        $running = @(Get-PttProcesses)
        if ($running.Count -gt 1) { break }
        if ($running.Count -eq 1) {
            if ($running[0].CreationDate -ge $launchedAt) { $started = $running[0] }
            break
        }
        Start-Sleep -Milliseconds 250
    }
    if ($null -eq $started) {
        throw 'Expected exactly one new process at the permanent executable path.'
    }
    Start-Sleep -Seconds 2
    $running = @(Get-PttProcesses)
    if ($running.Count -ne 1 -or $running[0].ProcessId -ne $started.ProcessId -or
        $running[0].CreationDate -ne $started.CreationDate) {
        throw 'Expected exactly one new process at the permanent executable path.'
    }
    return $running[0]
}

function Write-StartMenuShortcut {
    $programs = [Environment]::GetFolderPath('Programs')
    $shortcutPath = Join-Path $programs 'PTT Dictation.lnk'
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $liveExe
    $shortcut.WorkingDirectory = $liveDirectory
    $shortcut.Description = 'PTT Dictation'
    $shortcut.IconLocation = "$liveExe,0"
    $shortcut.Save()
    $verified = $shell.CreateShortcut($shortcutPath)
    if ($verified.TargetPath -ne $liveExe) { throw 'Start-menu shortcut verification failed.' }
    return $shortcutPath
}

function Assert-SettingsFilePath([string]$Path) {
    $entry = [IO.Path]::GetFullPath($Path)
    if ($Path -notmatch '^[A-Za-z]:[\\/]' -or
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
    if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'Settings restoration requires PowerShell 7 (pwsh).' }
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
    if (@(Get-PttProcesses).Count -ne 0) {
        throw 'The canonical app is still running; refusing to change its package or settings.'
    }
}

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'PTT Dictation supports Windows only.' }
Assert-SafeLocalDirectory $InstallDirectory
# All entry points serialize changes to the same selected installation.
$sha = [Security.Cryptography.SHA256]::Create()
try { $identity = ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($liveDirectory.ToUpperInvariant())))).Replace('-', '') }
finally { $sha.Dispose() }
$mutex = [Threading.Mutex]::new($false, "Local\PttDictation-Install-$identity")
$locked = $false
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ('PttDictation-install-' + [Guid]::NewGuid().ToString('N'))
$stage = Join-Path $temporaryRoot 'stage'
$backup = $null
$backupVerified = $false
$oldHashes = $null
$hadLive = $false
$createdLive = $false
$installStarted = $false
$oldWasRunning = $false
$stopAttempted = $false
$settingsAttempted = $false
$previousSettingsExisted = $false
$previousSettings = $null
try {
    try { $locked = $mutex.WaitOne(0) }
    catch [Threading.AbandonedMutexException] { $locked = $true }
    if (-not $locked) { throw 'Another app deployment is already running.' }
    $running = @(Get-PttProcesses)
    if ($running.Count -gt 1) { throw 'Multiple canonical processes are running; close duplicates first.' }
    $oldWasRunning = $running.Count -eq 1
    $hadLive = Test-Path -LiteralPath $liveDirectory -PathType Container
    if ($hadLive) { Assert-NoLinks $liveDirectory }
    if ($VerifyOnly) {
        $hashes = Get-PackageHashes $liveDirectory
        $receiptPath = Join-Path $liveDirectory $receiptName
        $verified = $false
        if (Test-Path -LiteralPath $receiptPath) {
            $receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
            if ($receipt.ExecutablePath -ne $liveExe) { throw 'Deployment receipt points at a different executable.' }
            $expected = @{}
            foreach ($property in $receipt.Hashes.PSObject.Properties) { $expected[$property.Name] = $property.Value }
            Assert-PackageHashes $liveDirectory $expected
            $verified = $true
        }
        [pscustomobject]@{ ExecutablePath = $liveExe; ProcessIds = @($running | ForEach-Object ProcessId);
            FilesHashed = $hashes.Count; DeploymentReceiptVerified = $verified }
        return
    }
    $changeSettings = $PSBoundParameters.ContainsKey('SettingsSource')
    $requestedSettings = $null
    if ($changeSettings) {
        $requestedSettings = Read-RequestedSettings $SettingsSource
        Assert-SettingsFilePath $settingsPath
    }
    New-Item -ItemType Directory -Path $stage -Force | Out-Null
    $actualArchiveHash = $null
    $releaseTag = 'local-build'
    if ($PSCmdlet.ParameterSetName -eq 'Staged') {
        $source = (Resolve-Path -LiteralPath $StagedPath).Path.TrimEnd('\')
        if ($source -eq $liveDirectory -or
            $source.StartsWith($liveDirectory + '\', [StringComparison]::OrdinalIgnoreCase) -or
            $liveDirectory.StartsWith($source + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Stage a separate package; the live directory cannot be the source.'
        }
        Assert-NoLinks $source
        $expectedHashes = Get-PackageHashes $source
        Copy-Package $source $stage
        Assert-PackageHashes $stage $expectedHashes
    }
    else {
        $downloadedPackage = Join-Path $temporaryRoot $packageName
        $downloadedChecksum = Join-Path $temporaryRoot $checksumName
        if ($PSCmdlet.ParameterSetName -eq 'Online') {
            [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
            $headers = @{ 'User-Agent' = 'PttDictation-Installer'; 'Accept' = 'application/vnd.github+json' }
            $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$repository/releases/latest" -Headers $headers
            if ($release.draft -or $release.prerelease) { throw 'The latest GitHub release is not a stable public release.' }
            $packageAssets = @($release.assets | Where-Object name -eq $packageName)
            $checksumAssets = @($release.assets | Where-Object name -eq $checksumName)
            if ($packageAssets.Count -ne 1 -or $checksumAssets.Count -ne 1) { throw "Release $($release.tag_name) must contain exactly one package and checksum asset." }
            Invoke-WebRequest -Uri $packageAssets[0].browser_download_url -Headers $headers -OutFile $downloadedPackage -UseBasicParsing
            Invoke-WebRequest -Uri $checksumAssets[0].browser_download_url -Headers $headers -OutFile $downloadedChecksum -UseBasicParsing
            $releaseTag = $release.tag_name
        }
        else {
            $downloadedPackage = (Resolve-Path -LiteralPath $PackagePath).Path
            $downloadedChecksum = (Resolve-Path -LiteralPath $ChecksumPath).Path
            $releaseTag = 'local-package'
        }
        $match = [regex]::Match([IO.File]::ReadAllText($downloadedChecksum), '(?i)\b[0-9a-f]{64}\b')
        if (-not $match.Success) { throw 'Checksum file does not contain a SHA-256 value.' }
        $actualArchiveHash = (Get-FileHash -LiteralPath $downloadedPackage -Algorithm SHA256).Hash
        if ($actualArchiveHash -ne $match.Value.ToUpperInvariant()) { throw 'Release package SHA-256 verification failed.' }
        Expand-Archive -LiteralPath $downloadedPackage -DestinationPath $stage -Force
        Assert-NoLinks $stage
        $expectedHashes = Get-PackageHashes $stage
    }
    # Fail before stopping the current app if an independent desktop launch is unavailable.
    if (-not $NoLaunch) { $null = Get-ExplorerDesktopApplication }
    if ($hadLive) {
        $oldHashes = Get-PackageHashes $liveDirectory -Backup
        $backup = Join-Path (Split-Path $liveDirectory -Parent) ('.backup-' + [Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $backup | Out-Null
        Copy-Package $liveDirectory $backup
        Assert-PackageHashes $backup $oldHashes -Backup
        $backupVerified = $true
    }
    if ($changeSettings) {
        Assert-SettingsFilePath $settingsPath
        $previousSettingsExisted = [IO.File]::Exists($settingsPath)
        if ($previousSettingsExisted) { $previousSettings = [IO.File]::ReadAllBytes($settingsPath) }
    }
    $running = @(Get-PttProcesses)
    if ($running.Count -gt 1) { throw 'Multiple canonical processes appeared during preparation.' }
    $oldWasRunning = $running.Count -eq 1
    Stop-PttProcesses
    if (-not $hadLive) {
        New-Item -ItemType Directory -Path $liveDirectory -Force | Out-Null
        $createdLive = $true
    }
    if ($changeSettings) { $settingsAttempted = $true; Write-PermanentSettings $requestedSettings }
    $installStarted = $true
    Copy-Package $stage $liveDirectory
    Assert-PackageHashes $liveDirectory $expectedHashes
    $processId = $null
    if (-not $NoLaunch) { $processId = (Start-AndVerify).ProcessId }
    [ordered]@{
        InstalledAtUtc = [DateTime]::UtcNow.ToString('o'); ReleaseTag = $releaseTag;
        ExecutablePath = $liveExe; ProcessIdAtInstall = $processId; BackupDirectory = $backup;
        ArchiveSha256 = $actualArchiveHash; Hashes = $expectedHashes
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $liveDirectory $receiptName) -Encoding UTF8
    if (-not $NoShortcut) { Write-Host "Start-menu shortcut: $(Write-StartMenuShortcut)" }
    Write-Host "Installed and verified $($expectedHashes.Count) files at $liveExe (PID $processId)"
    if ($null -ne $backup) { Write-Host "Previous package retained at $backup" }
}
catch {
    $installError = $_
    try {
        if ($installStarted -or $settingsAttempted) { Stop-PttProcesses }
        if ($settingsAttempted) {
            if ($previousSettingsExisted) { Write-PermanentSettings $previousSettings }
            else { Assert-SettingsFilePath $settingsPath; [IO.File]::Delete($settingsPath) }
        }
        if ($installStarted -and $hadLive) {
            if (-not $backupVerified) { throw 'A verified backup is unavailable for recovery.' }
            Copy-Package $backup $liveDirectory
            Assert-PackageHashes $liveDirectory $oldHashes -Backup
        }
        if (-not $NoLaunch -and $hadLive -and
            (($oldWasRunning -and $stopAttempted) -or $installStarted -or $settingsAttempted) -and
            (Test-Path -LiteralPath $liveExe -PathType Leaf) -and @(Get-PttProcesses).Count -eq 0) {
            $null = Start-AndVerify
            Write-Warning 'Installation failed; the previous package was restored and restarted at its permanent path.'
        }
        if ($createdLive -and (Test-Path -LiteralPath $liveDirectory)) {
            Assert-SafeLocalDirectory $liveDirectory
            Remove-Item -LiteralPath $liveDirectory -Recurse -Force
        }
    }
    catch { throw "Installation failed: $installError. Recovery also failed: $_. Previous package: $backup" }
    throw $installError
}
finally {
    try {
        $resolvedTemporary = [IO.Path]::GetFullPath($temporaryRoot)
        $temporaryPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
        if ($resolvedTemporary.StartsWith($temporaryPrefix, [StringComparison]::OrdinalIgnoreCase) -and
            (Split-Path $resolvedTemporary -Leaf) -like 'PttDictation-install-*' -and (Test-Path -LiteralPath $resolvedTemporary)) {
            Assert-NoLinks $resolvedTemporary
            Remove-Item -LiteralPath $resolvedTemporary -Recurse -Force
        }
    }
    catch { Write-Warning "Temporary installer package cleanup failed: $_" }
    finally { if ($locked) { $mutex.ReleaseMutex() }; $mutex.Dispose() }
}
