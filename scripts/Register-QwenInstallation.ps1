#Requires -Version 7.0
<# Registers an existing local Qwen environment. Downloads nothing; does not switch the running app. #>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PythonPath,
    [Parameter(Mandatory)][string]$ModelPath,
    [string]$AppDataPath = (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'PttDictation'),
    [switch]$ValidateOnly
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
foreach ($value in @($PythonPath, $ModelPath, $AppDataPath)) {
    if (-not [IO.Path]::IsPathFullyQualified($value) -or ($value.StartsWith('\\') -or $value.StartsWith('//'))) {
        throw 'Qwen installation paths must be absolute local paths.'
    }
}
$pythonExe = (Resolve-Path -LiteralPath $PythonPath).Path
$modelDirectory = (Resolve-Path -LiteralPath $ModelPath).Path
$AppDataPath = [IO.Path]::GetFullPath($AppDataPath)
foreach ($resolvedPath in @($pythonExe, $modelDirectory, $AppDataPath)) {
    if ($resolvedPath.StartsWith('\\') -or $resolvedPath.StartsWith('//')) {
        throw 'Qwen installation paths must resolve to local paths.'
    }
}
if (-not (Test-Path -LiteralPath $pythonExe -PathType Leaf)) { throw 'Python executable missing.' }
if (-not (Test-Path -LiteralPath $modelDirectory -PathType Container)) { throw 'Model directory missing.' }
$modelConfig = Get-Content -LiteralPath (Join-Path $modelDirectory 'config.json') -Raw | ConvertFrom-Json
if ($modelConfig.model_type -ne 'qwen3_asr' -or $modelConfig.text_config.hidden_size -ne 2048) {
    throw 'This setup requires the native Qwen3-ASR-1.7B-hf model.'
}
if (-not (Test-Path -LiteralPath (Join-Path $modelDirectory 'model.safetensors')) -and
    -not (Test-Path -LiteralPath (Join-Path $modelDirectory 'model.safetensors.index.json'))) {
    throw 'Qwen model weights missing.'
}
$probe = 'import sys, torch, transformers; from transformers import AutoModelForMultimodalLM, AutoProcessor; assert sys.version_info >= (3,12), "Python 3.12 required"; assert torch.__version__ in ("2.11.0+cpu", "2.11.0+cu128"), "Use validated PyTorch 2.11.0 CPU or CUDA 12.8"; assert transformers.__version__ == "5.13.0", "Use validated Transformers 5.13.0"; AutoProcessor.from_pretrained(sys.argv[1], local_files_only=True, trust_remote_code=False); backend = "CUDA" if torch.cuda.is_available() and torch.cuda.is_bf16_supported() else "CPU"; print("Validated Qwen runtime; inference backend: " + backend)'
& $pythonExe -B -c $probe $modelDirectory
if ($LASTEXITCODE -ne 0) { throw 'Qwen environment validation failed; registration unchanged.' }
if ($ValidateOnly) { return }
[IO.Directory]::CreateDirectory($AppDataPath) | Out-Null
$manifestPath = Join-Path $AppDataPath 'qwen-installation.json'
$pendingPath = Join-Path $AppDataPath ('qwen-installation-' + [Guid]::NewGuid().ToString('N') + '.tmp')
$manifest = [ordered]@{ schema = 1; pythonPath = $pythonExe; modelPath = $modelDirectory;
    modelId = 'Qwen/Qwen3-ASR-1.7B-hf'; registeredAtUtc = [DateTime]::UtcNow.ToString('O') }
try {
    [IO.File]::WriteAllText($pendingPath, ($manifest | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $pendingPath -Destination $manifestPath -Force
} finally {
    if (Test-Path -LiteralPath $pendingPath) { Remove-Item -LiteralPath $pendingPath -Force }
}
Write-Output "Registered local Qwen installation: $manifestPath"
Write-Output 'Select Qwen3-ASR 1.7B as final recognition in Settings. Live preview continues using Parakeet.'
