#Requires -Version 7.0
<# Installs already downloaded, pinned native assets; downloads nothing and never restarts the app. #>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SourceDirectory,
    [Parameter(Mandatory)][string]$InstallDirectory,
    [string]$AppDataPath = (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'PttDictation'),
    [switch]$StageOnly,
    [switch]$ValidateOnly
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-LocalPath([string]$Value) {
    if (-not [IO.Path]::IsPathFullyQualified($Value) -or $Value.StartsWith('\\') -or $Value.StartsWith('//')) {
        throw 'Native Qwen paths must be absolute local paths.'
    }
    $full = [IO.Path]::GetFullPath($Value)
    # Reject directory junctions/symlinks in every existing ancestor, including
    # the destination parent, so staging/cleanup cannot escape the named tree.
    $current = $full
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            if ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw 'Native Qwen paths must not traverse reparse points.'
            }
        }
        $current = [IO.Path]::GetDirectoryName($current)
    }
    return $full
}

$SourceDirectory = Get-LocalPath $SourceDirectory
$InstallDirectory = Get-LocalPath $InstallDirectory
$AppDataPath = Get-LocalPath $AppDataPath
$plan = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\tools\qwen-installer\native-assets.json') -Raw | ConvertFrom-Json
if ($plan.schema -ne 1) { throw 'Unsupported native asset plan.' }

function Assert-Assets([string]$Directory) {
    foreach ($asset in $plan.files) {
        if ($asset.path -notmatch '^(bin/)?[A-Za-z0-9_.-]+$') { throw 'Unsafe native asset path.' }
        $path = Get-LocalPath (Join-Path $Directory $asset.path)
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required native asset is missing: $($asset.path)" }
        $file = Get-Item -LiteralPath $path
        if ($file.Length -ne $asset.size -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $asset.sha256) {
            throw "Native asset failed pinned size/SHA256 validation: $($asset.path)"
        }
    }
    # An extra DLL could be loaded by the native backend discovery mechanism.
    $expected = @($plan.files | ForEach-Object { $_.path.Replace('/', [IO.Path]::DirectorySeparatorChar) })
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $Directory 'bin') -File) {
        $relative = [IO.Path]::GetRelativePath($Directory, $file.FullName)
        if ($relative -notin $expected -and $file.Extension -eq '.dll') { throw "Unvalidated DLL in native runtime: $($file.Name)" }
    }
}

Assert-Assets $SourceDirectory
if ($ValidateOnly) { Write-Output 'All native source assets match the pinned sizes and SHA256 hashes.'; return }

$parent = [IO.Path]::GetDirectoryName($InstallDirectory)
if (-not $parent -or $InstallDirectory -eq [IO.Path]::GetPathRoot($InstallDirectory)) { throw 'Choose a dedicated native installation directory.' }
[IO.Directory]::CreateDirectory($parent) | Out-Null
$pending = Join-Path $parent ('.qwen-native-pending-' + [Guid]::NewGuid().ToString('N'))
try {
    if (Test-Path -LiteralPath $InstallDirectory) {
        Assert-Assets $InstallDirectory
    } else {
        [IO.Directory]::CreateDirectory((Join-Path $pending 'bin')) | Out-Null
        foreach ($asset in $plan.files) {
            Copy-Item -LiteralPath (Join-Path $SourceDirectory $asset.path) -Destination (Join-Path $pending $asset.path)
        }
        Assert-Assets $pending
        Move-Item -LiteralPath $pending -Destination $InstallDirectory
    }
} finally {
    if (Test-Path -LiteralPath $pending) {
        $resolvedPending = Get-LocalPath $pending
        $resolvedParent = (Get-LocalPath $parent).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        if (-not $resolvedPending.StartsWith($resolvedParent, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe native staging cleanup path.' }
        Remove-Item -LiteralPath $resolvedPending -Recurse -Force
    }
}
if ($StageOnly) { Write-Output "Verified native assets staged at $InstallDirectory; registration unchanged."; return }

# Keep the existing Hugging Face registration and its CPU/long-input fallback.
$hfManifest = Get-LocalPath (Join-Path $AppDataPath 'qwen-installation.json')
$hf = Get-Content -LiteralPath $hfManifest -Raw | ConvertFrom-Json
if ($hf.schema -ne 1 -or -not (Test-Path -LiteralPath (Get-LocalPath $hf.pythonPath) -PathType Leaf)) { throw 'Register a valid existing Qwen Python installation first.' }
$hfModel = Get-LocalPath $hf.modelPath
$hfConfig = Get-Content -LiteralPath (Join-Path $hfModel 'config.json') -Raw | ConvertFrom-Json
if ($hfConfig.model_type -ne 'qwen3_asr' -or $hfConfig.text_config.hidden_size -ne 2048) { throw 'The fallback must be Qwen3-ASR 1.7B.' }
if (-not (Test-Path -LiteralPath (Join-Path $hfModel 'model.safetensors')) -and -not (Test-Path -LiteralPath (Join-Path $hfModel 'model.safetensors.index.json'))) { throw 'The existing fallback model weights are missing.' }
$registration = [ordered]@{
    schema = 1; serverPath = Join-Path $InstallDirectory 'bin\llama-server.exe'
    modelPath = Join-Path $InstallDirectory 'Qwen3-ASR-1.7B-bf16.gguf'
    projectorPath = Join-Path $InstallDirectory 'mmproj-Qwen3-ASR-1.7B-bf16.gguf'
    modelId = 'Qwen/Qwen3-ASR-1.7B-hf'; llamaCommit = $plan.llamaCommit; modelRevision = $plan.modelRevision
    registeredAtUtc = [DateTime]::UtcNow.ToString('O')
}
$target = Get-LocalPath (Join-Path $AppDataPath 'qwen-native-installation.json')
$pendingManifest = Join-Path $AppDataPath ('qwen-native-installation-' + [Guid]::NewGuid().ToString('N') + '.tmp')
try {
    [IO.File]::WriteAllText($pendingManifest, ($registration | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    if (Test-Path -LiteralPath $target) {
        Copy-Item -LiteralPath $target -Destination ($target + '.backup-' + [DateTime]::UtcNow.ToString('yyyyMMddHHmmssfffffff'))
    }
    Move-Item -LiteralPath $pendingManifest -Destination $target -Force
} finally {
    if (Test-Path -LiteralPath $pendingManifest) { Remove-Item -LiteralPath $pendingManifest -Force }
}
Write-Output "Registered optional native Qwen: $target"
Write-Output 'Restart the canonical app to use native inference. Rename the optional manifest to disable it; the existing Hugging Face registration remains unchanged.'
