#Requires -Version 7.0
$ErrorActionPreference = 'Stop'
$registration = Join-Path $PSScriptRoot 'Register-QwenInstallation.ps1'
$count = 0
foreach ($parameter in @('PythonPath', 'ModelPath', 'AppDataPath')) {
    foreach ($networkPath in @('//example.invalid/share/file', '\\example.invalid\share\file')) {
        $arguments = @{ PythonPath = 'C:\missing-python.exe'; ModelPath = 'C:\missing-model'; AppDataPath = 'C:\missing-appdata'; ValidateOnly = $true }
        $arguments[$parameter] = $networkPath
        $rejected = $false
        try { & $registration @arguments }
        catch {
            if ($_.Exception.Message -notlike '*absolute local paths*') { throw }
            $rejected = $true
        }
        if (-not $rejected) { throw "Network $parameter was accepted." }
        $count++
    }
}

$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('ptt-qwen-registration-' + [Guid]::NewGuid().ToString('N'))
$previousArgsFile = $env:PTT_QWEN_ARGS_FILE
try {
    $model = Join-Path $tempRoot 'model'
    $appData = Join-Path $tempRoot 'appdata'
    [IO.Directory]::CreateDirectory($model) | Out-Null
    [IO.Directory]::CreateDirectory($appData) | Out-Null
    [IO.File]::WriteAllText((Join-Path $model 'config.json'),
        '{"model_type":"qwen3_asr","text_config":{"hidden_size":2048}}')
    [IO.File]::WriteAllBytes((Join-Path $model 'model.safetensors'), [byte[]]@(0))
    $python = Join-Path $tempRoot 'fake-python.cmd'
    $argsFile = Join-Path $tempRoot 'python-arguments.txt'
    [IO.File]::WriteAllText($python, "@echo off`r`necho %*>`"%PTT_QWEN_ARGS_FILE%`"`r`nexit /b 0`r`n")
    $env:PTT_QWEN_ARGS_FILE = $argsFile
    & $registration -PythonPath $python -ModelPath $model -AppDataPath $appData -ValidateOnly
    $pythonArguments = [IO.File]::ReadAllText($argsFile)
    if (-not $pythonArguments.Contains($model, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Registration validation did not pass the selected model directory to Python.'
    }
    $count++
}
finally {
    $env:PTT_QWEN_ARGS_FILE = $previousArgsFile
    if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force }
}

Write-Output "Passed $count Qwen registration checks."
