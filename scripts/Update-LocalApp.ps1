#Requires -Version 7.0
<#
.SYNOPSIS
Installs a tested package at the permanent Start-menu shortcut target.
.EXAMPLE
pwsh -File scripts/Update-LocalApp.ps1 -StagedPath publish/next-build
.EXAMPLE
pwsh -File scripts/Update-LocalApp.ps1 -VerifyOnly
#>
[CmdletBinding(DefaultParameterSetName = 'Install')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Install')]
    [string]$StagedPath,
    [Parameter(Mandatory, ParameterSetName = 'Verify')]
    [switch]$VerifyOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# This is deliberately independent of cwd and worktrees. Do not add a destination
# override: an intentional relocation also requires migrating the user's shortcuts.
$liveDirectory = 'C:\Users\stewa\projects\par-win-ptt\publish\ptt-dictation-win-x64'
$publishDirectory = Split-Path $liveDirectory -Parent
$liveExe = Join-Path $liveDirectory 'PttDictation.exe'
$receiptName = 'deployment-receipt.json'

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
    $all = @(Get-CimInstance Win32_Process -Filter "Name = 'PttDictation.exe'")
    foreach ($process in $all) {
        if ($process.ExecutablePath -ne $liveExe) {
            throw "Another PTT executable is running (PID $($process.ProcessId)); close it before updating."
        }
        if ($process.CommandLine.Trim() -notin @($liveExe, ('"' + $liveExe + '"'))) {
            throw "Unexpected arguments on PTT process $($process.ProcessId)."
        }
    }
    return $all
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
    $stopped = $false
    $installStarted = $false
    try {
        foreach ($process in $running) {
            Stop-Process -Id $process.ProcessId -ErrorAction Stop
            $stopped = $true
            Wait-Process -Id $process.ProcessId -Timeout 15 -ErrorAction SilentlyContinue
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
            if ($installStarted) {
                foreach ($process in @(Get-LiveProcesses)) {
                    Stop-Process -Id $process.ProcessId -ErrorAction Stop
                    Wait-Process -Id $process.ProcessId -Timeout 15 -ErrorAction SilentlyContinue
                }
                Copy-PublishPackage $backup $liveDirectory
                Assert-PackageHashes $liveDirectory $oldHashes
            }
            if (($stopped -or $installStarted) -and @(Get-LiveProcesses).Count -eq 0) {
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
