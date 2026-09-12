#Requires -Version 7.0
<# Restores a verified app-and-settings snapshot through the permanent-path updater. #>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SnapshotPath,
    [switch]$VerifyOnly,
    [ValidateNotNullOrEmpty()]
    [string]$InstallDirectory = (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs\PttDictation')
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$snapshotRoot = (Resolve-Path -LiteralPath $SnapshotPath).Path
$item = Get-Item -LiteralPath $snapshotRoot -Force
while ($null -ne $item) {
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Rollback snapshots must not use linked paths.' }
    $item = $item.Parent
}
foreach ($entry in Get-ChildItem -LiteralPath $snapshotRoot -Recurse -Force) {
    if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Rollback snapshot contains a linked entry.' }
}
$manifest = Get-Content -LiteralPath (Join-Path $snapshotRoot 'snapshot.json') -Raw | ConvertFrom-Json -AsHashtable
$permanentExe = Join-Path ([IO.Path]::GetFullPath($InstallDirectory)) 'PttDictation.exe'
if ($manifest.Schema -ne 1 -or $manifest.ExecutablePath -ne $permanentExe) {
    throw 'Snapshot is not for this permanent PTT installation.'
}
$hashes = @{}
foreach ($file in Get-ChildItem -LiteralPath $snapshotRoot -Recurse -File -Force) {
    $relative = [IO.Path]::GetRelativePath($snapshotRoot, $file.FullName)
    if ($relative -ne 'snapshot.json') { $hashes[$relative] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash }
}
if ($hashes.Count -ne $manifest.Files.Count) { throw 'Rollback snapshot file count differs from its manifest.' }
foreach ($name in $hashes.Keys) {
    if ($hashes[$name] -ne $manifest.Files[$name]) { throw "Rollback snapshot hash mismatch: $name" }
}
foreach ($required in @('app\PttDictation.exe', 'app\PttDictation.dll', 'app\PttDictation.Core.dll', 'app\System.Private.CoreLib.dll', 'settings.json')) {
    if (-not $hashes.ContainsKey($required)) { throw "Rollback snapshot missing required file: $required" }
}
$settingsSource = Join-Path $snapshotRoot 'settings.json'
$settings = Get-Content -LiteralPath $settingsSource -Raw | ConvertFrom-Json -AsHashtable
if ($settings -isnot [Collections.IDictionary]) { throw 'Snapshot settings must be a JSON object.' }
if ($VerifyOnly) {
    [pscustomobject]@{ SnapshotPath = $snapshotRoot; SourceCommit = $manifest.SourceCommit; FilesVerified = $hashes.Count; ExecutablePath = $permanentExe }
    return
}
$updater = Join-Path $PSScriptRoot 'Update-LocalApp.ps1'
& $updater -StagedPath (Join-Path $snapshotRoot 'app') -SettingsSource $settingsSource -InstallDirectory $InstallDirectory
& $updater -VerifyOnly -InstallDirectory $InstallDirectory
$settingsTarget = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'PttDictation\settings.json'
if ((Get-FileHash -LiteralPath $settingsTarget).Hash -ne $hashes['settings.json']) { throw 'Restored settings did not match the saved snapshot.' }
Write-Host 'Previous PTT Dictation app and settings restored. The app is running at its permanent shortcut path.'
