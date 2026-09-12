#Requires -Version 5.1
<#
.SYNOPSIS
Downloads, verifies, and installs the latest PTT Dictation release for the current user.
.EXAMPLE
powershell -ExecutionPolicy Bypass -File .\Install-PttDictation.ps1
.EXAMPLE
powershell -ExecutionPolicy Bypass -File .\Install-PttDictation.ps1 -InstallDirectory D:\Apps\PttDictation
.EXAMPLE
powershell -ExecutionPolicy Bypass -File .\Install-PttDictation.ps1 -PackagePath .\PttDictation-win-x64.zip -ChecksumPath .\PttDictation-win-x64.zip.sha256
#>
[CmdletBinding(DefaultParameterSetName = 'Online')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Offline')]
    [string]$PackagePath,
    [Parameter(Mandatory, ParameterSetName = 'Offline')]
    [string]$ChecksumPath,
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
$liveDirectory = [IO.Path]::GetFullPath($InstallDirectory)
$liveExe = Join-Path $liveDirectory 'PttDictation.exe'

function Assert-SafeLocalDirectory([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    if (-not [IO.Path]::IsPathRooted($Path) -or $Path.StartsWith('\\') -or $Path.StartsWith('//') -or
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
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Refusing linked path: $Path" }
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

function Get-PackageHashes([string]$Directory) {
    foreach ($required in @('PttDictation.exe', 'PttDictation.dll', 'PttDictation.Core.dll', 'System.Private.CoreLib.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $Directory $required) -PathType Leaf)) {
            throw "Incomplete self-contained package: missing $required"
        }
    }
    $hashes = [ordered]@{}
    foreach ($file in Get-ChildItem -LiteralPath $Directory -Recurse -File -Force | Sort-Object FullName) {
        $relative = Get-RelativeName $Directory $file.FullName
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
        if ($details.Count -ne 1) { throw "Could not verify PTT Dictation process $($nativeProcess.Id)." }
        $commandLine = if ($null -eq $details[0].CommandLine) { '' } else { $details[0].CommandLine.Trim() }
        if ($commandLine -notin @($liveExe, ('"' + $liveExe + '"'))) {
            throw "Unexpected arguments on PTT Dictation process $($nativeProcess.Id)."
        }
        $result += [pscustomobject]@{ ProcessId = $nativeProcess.Id; ExecutablePath = $nativePath; CommandLine = $commandLine }
    }
    return $result
}

function Stop-PttProcesses {
    foreach ($process in @(Get-PttProcesses)) {
        Stop-Process -Id $process.ProcessId -ErrorAction Stop
        Wait-Process -Id $process.ProcessId -Timeout 15 -ErrorAction SilentlyContinue
    }
    if (@(Get-PttProcesses).Count -ne 0) { throw 'PTT Dictation did not stop before installation.' }
}

function Start-AndVerify {
    Write-Host "Launch: Start-Process -FilePath '$liveExe' -WorkingDirectory '$liveDirectory'"
    $started = Start-Process -FilePath $liveExe -WorkingDirectory $liveDirectory -PassThru
    Start-Sleep -Seconds 2
    $running = @(Get-PttProcesses)
    if ($running.Count -ne 1 -or $running[0].ProcessId -ne $started.Id) {
        throw 'Expected exactly one normally launched process at the installed path.'
    }
    return $started.Id
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

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'PTT Dictation supports Windows only.' }
Assert-SafeLocalDirectory $liveDirectory

$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ('PttDictation-install-' + [Guid]::NewGuid().ToString('N'))
$stage = Join-Path $temporaryRoot 'stage'
$downloadedPackage = Join-Path $temporaryRoot $packageName
$downloadedChecksum = Join-Path $temporaryRoot $checksumName
$backup = $null
$hadLive = Test-Path -LiteralPath $liveDirectory -PathType Container
$installStarted = $false
$oldWasRunning = $false

try {
    New-Item -ItemType Directory -Path $stage -Force | Out-Null
    if ($PSCmdlet.ParameterSetName -eq 'Online') {
        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
        $headers = @{ 'User-Agent' = 'PttDictation-Installer'; 'Accept' = 'application/vnd.github+json' }
        $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$repository/releases/latest" -Headers $headers
        if ($release.draft -or $release.prerelease) { throw 'The latest GitHub release is not a stable public release.' }
        $packageAssets = @($release.assets | Where-Object name -eq $packageName)
        $checksumAssets = @($release.assets | Where-Object name -eq $checksumName)
        if ($packageAssets.Count -ne 1 -or $checksumAssets.Count -ne 1) {
            throw "Release $($release.tag_name) must contain exactly one package and checksum asset."
        }
        Invoke-WebRequest -Uri $packageAssets[0].browser_download_url -Headers $headers -OutFile $downloadedPackage -UseBasicParsing
        Invoke-WebRequest -Uri $checksumAssets[0].browser_download_url -Headers $headers -OutFile $downloadedChecksum -UseBasicParsing
        $releaseTag = $release.tag_name
    }
    else {
        $downloadedPackage = (Resolve-Path -LiteralPath $PackagePath).Path
        $downloadedChecksum = (Resolve-Path -LiteralPath $ChecksumPath).Path
        $releaseTag = 'local-package'
    }

    $checksumText = [IO.File]::ReadAllText($downloadedChecksum)
    $match = [regex]::Match($checksumText, '(?i)\b[0-9a-f]{64}\b')
    if (-not $match.Success) { throw 'Checksum file does not contain a SHA-256 value.' }
    $expectedArchiveHash = $match.Value.ToUpperInvariant()
    $actualArchiveHash = (Get-FileHash -LiteralPath $downloadedPackage -Algorithm SHA256).Hash
    if ($actualArchiveHash -ne $expectedArchiveHash) { throw 'Release package SHA-256 verification failed.' }

    Expand-Archive -LiteralPath $downloadedPackage -DestinationPath $stage -Force
    Assert-NoLinks $stage
    $expectedHashes = Get-PackageHashes $stage

    $running = @(Get-PttProcesses)
    $oldWasRunning = $running.Count -eq 1
    if ($hadLive) {
        Assert-NoLinks $liveDirectory
        $backup = Join-Path (Split-Path $liveDirectory -Parent) ('.backup-' + [Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $backup | Out-Null
        Get-ChildItem -LiteralPath $liveDirectory -Force | Copy-Item -Destination $backup -Recurse -Force
    }
    else {
        New-Item -ItemType Directory -Path (Split-Path $liveDirectory -Parent) -Force | Out-Null
        New-Item -ItemType Directory -Path $liveDirectory | Out-Null
    }

    Stop-PttProcesses
    $installStarted = $true
    Copy-Package $stage $liveDirectory
    Assert-PackageHashes $liveDirectory $expectedHashes

    $processId = $null
    if (-not $NoLaunch) { $processId = Start-AndVerify }
    $receipt = [ordered]@{
        InstalledAtUtc = [DateTime]::UtcNow.ToString('o')
        ReleaseTag = $releaseTag
        ExecutablePath = $liveExe
        ProcessIdAtInstall = $processId
        BackupDirectory = $backup
        ArchiveSha256 = $actualArchiveHash
        Hashes = $expectedHashes
    }
    $receipt | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $liveDirectory $receiptName) -Encoding UTF8
    $shortcutPath = $null
    if (-not $NoShortcut) { $shortcutPath = Write-StartMenuShortcut }
    Write-Host "Installed and verified $($expectedHashes.Count) files at $liveExe"
    if ($null -ne $shortcutPath) { Write-Host "Start-menu shortcut: $shortcutPath" }
    if ($null -ne $backup) { Write-Host "Previous package retained at $backup" }
}
catch {
    $installError = $_
    try {
        if ($installStarted) {
            foreach ($process in @(Get-PttProcesses)) {
                Stop-Process -Id $process.ProcessId -ErrorAction Stop
                Wait-Process -Id $process.ProcessId -Timeout 15 -ErrorAction SilentlyContinue
            }
        }
        if ($null -ne $backup -and (Test-Path -LiteralPath $backup)) {
            Copy-Package $backup $liveDirectory
            if ($oldWasRunning -and -not $NoLaunch) { $null = Start-AndVerify }
        }
        elseif (-not $hadLive -and (Test-Path -LiteralPath $liveDirectory)) {
            $resolvedLive = [IO.Path]::GetFullPath($liveDirectory)
            Assert-SafeLocalDirectory $resolvedLive
            Remove-Item -LiteralPath $resolvedLive -Recurse -Force
        }
    }
    catch { throw "Installation failed: $installError. Recovery also failed: $_" }
    throw $installError
}
finally {
    $resolvedTemporary = [IO.Path]::GetFullPath($temporaryRoot)
    $temporaryPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if ($resolvedTemporary.StartsWith($temporaryPrefix, [StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path $resolvedTemporary -Leaf) -like 'PttDictation-install-*' -and
        (Test-Path -LiteralPath $resolvedTemporary)) {
        Remove-Item -LiteralPath $resolvedTemporary -Recurse -Force
    }
}
