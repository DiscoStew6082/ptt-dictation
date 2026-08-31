param(
    [ValidateSet("cpu", "cuda", "both")]
    [string]$Runtime = "cpu",

    [int]$Iterations = 3,

    [string]$ResultPath = ""
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$smokeRoot = Join-Path $repoRoot "smoke"
$wav = Join-Path $smokeRoot "sample.wav"
$modelCache = Join-Path $env:LOCALAPPDATA "PttDictation\models"
$baselineModel = Join-Path $smokeRoot "tdt_ctc-110m-f16.gguf"
$streamingModels = @(
    [pscustomobject]@{
        Name = "realtime_eou_120m-v1-q8_0.gguf"
        Quantization = "q8_0"
        Sha256 = "62616b914d6f5a683a5dea672df055b57de5c49dddf871b8b44b9c814dc3d896"
        MinimumBytes = 176001472
    },
    [pscustomobject]@{
        Name = "realtime_eou_120m-v1-f16.gguf"
        Quantization = "f16"
        Sha256 = "d1a2b12f12b8a096a57499c9111ed13b442a2b786e17a292c168be45088f0edc"
        MinimumBytes = 266517952
    }
)

if ([string]::IsNullOrWhiteSpace($ResultPath)) {
    $ResultPath = Join-Path $smokeRoot "streaming-smoke-results.md"
}

function Ensure-StreamingModels {
    New-Item -ItemType Directory -Force -Path $modelCache | Out-Null
    $missing = $streamingModels |
        Where-Object { -not (Test-ModelFile $_) }

    if ($missing.Count -eq 0) {
        return
    }

    $hf = Get-Command hf -ErrorAction SilentlyContinue
    if ($null -eq $hf) {
        $names = ($missing | ForEach-Object { $_.Name }) -join ", "
        throw "Missing streaming models ($names), and hf is not on PATH."
    }

    $files = $streamingModels | ForEach-Object { $_.Name }
    & hf download mudler/parakeet-cpp-gguf @files --local-dir $modelCache | Out-Host

    foreach ($model in $streamingModels) {
        if (-not (Test-ModelFile $model)) {
            throw "Downloaded model failed verification: $($model.Name)"
        }
    }
}

function Test-ModelFile {
    param([object]$Model)

    $path = Join-Path $modelCache $Model.Name
    if (-not (Test-Path -LiteralPath $path)) {
        return $false
    }

    $item = Get-Item -LiteralPath $path
    if ($item.Length -lt $Model.MinimumBytes) {
        return $false
    }

    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    return $hash -eq $Model.Sha256
}

function Get-CliContext {
    param([string]$Kind)

    if ($Kind -eq "cpu") {
        return [pscustomobject]@{
            Name = "cpu"
            Cli = Join-Path $smokeRoot "runtime-cpu\parakeet-v0.4.0-bin-win-cpu-x64\parakeet-cli.exe"
            ExtraPath = @()
        }
    }

    return [pscustomobject]@{
        Name = "cuda"
        Cli = Join-Path $smokeRoot "runtime\parakeet-v0.4.0-bin-win-cuda-x64\parakeet-cli.exe"
        ExtraPath = @(
            Join-Path $env:LOCALAPPDATA "PttDictation\runtimes\win-cuda-x64\cudart-parakeet-bin-win-cuda-x64"
        )
    }
}

function Read-TranscriptText {
    param(
        [string]$Output,
        [string]$Mode
    )

    if ($Mode -eq "stream") {
        $line = ($Output -split "`n" | Where-Object { $_ -match "^\[stream:final\]" } | Select-Object -Last 1)
        if ([string]::IsNullOrWhiteSpace($line)) {
            throw "Streaming output did not include a [stream:final] line.`n$Output"
        }

        return ($line -replace "^\[stream:final\]\s*", "").Trim()
    }

    $json = ($Output -split "`n" | Where-Object { $_ -match "^\s*\{" } | Select-Object -Last 1)
    if ([string]::IsNullOrWhiteSpace($json)) {
        throw "Batch output did not include a JSON transcript line.`n$Output"
    }

    return ($json | ConvertFrom-Json).text
}

function Invoke-Transcription {
    param(
        [object]$Context,
        [string]$ModelPath,
        [string]$Mode
    )

    $oldPath = $env:PATH
    try {
        $pathParts = @($Context.ExtraPath + @((Split-Path $Context.Cli), $oldPath)) |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
        $env:PATH = $pathParts -join [IO.Path]::PathSeparator

        $args = @("transcribe", "--model", $ModelPath, "--input", $wav)
        if ($Mode -eq "stream") {
            $args += "--stream"
            $args += "--timestamps"
        } else {
            $args += "--json"
        }

        $previousErrorActionPreference = $ErrorActionPreference
        $ErrorActionPreference = "Continue"
        try {
            $sw = [Diagnostics.Stopwatch]::StartNew()
            $output = & $Context.Cli @args 2>&1
            $exitCode = $LASTEXITCODE
            $sw.Stop()
        }
        finally {
            $ErrorActionPreference = $previousErrorActionPreference
        }

        $rawOutput = $output -join "`n"
        if ($exitCode -ne 0) {
            throw "parakeet-cli failed with exit code $exitCode for $ModelPath`n$rawOutput"
        }

        $text = Read-TranscriptText -Output $rawOutput -Mode $Mode
        return [pscustomobject]@{
            ExitCode = $exitCode
            WallMs = [int]$sw.ElapsedMilliseconds
            Transcript = $text
        }
    }
    finally {
        $env:PATH = $oldPath
    }
}

function Measure-Case {
    param(
        [object]$Context,
        [string]$Model,
        [string]$Quantization,
        [string]$Mode,
        [string]$ModelPath
    )

    $runs = for ($i = 1; $i -le $Iterations; $i++) {
        Invoke-Transcription -Context $Context -ModelPath $ModelPath -Mode $Mode
    }

    $times = $runs | ForEach-Object { $_.WallMs }
    return [pscustomobject]@{
        Runtime = $Context.Name
        Model = $Model
        Quantization = $Quantization
        Mode = $Mode
        Iterations = $Iterations
        MinMs = ($times | Measure-Object -Minimum).Minimum
        AvgMs = [math]::Round(($times | Measure-Object -Average).Average, 1)
        MaxMs = ($times | Measure-Object -Maximum).Maximum
        Transcript = ($runs | Select-Object -Last 1).Transcript
    }
}

Ensure-StreamingModels

$runtimeKinds = if ($Runtime -eq "both") { @("cpu", "cuda") } else { @($Runtime) }
$contexts = $runtimeKinds | ForEach-Object { Get-CliContext $_ }
$results = foreach ($context in $contexts) {
    if (-not (Test-Path -LiteralPath $context.Cli)) {
        Write-Warning "Skipping $($context.Name); missing $($context.Cli)."
        continue
    }

    Measure-Case -Context $context -Model "tdt_ctc-110m-f16.gguf" -Quantization "f16" -Mode "batch" -ModelPath $baselineModel
    foreach ($model in $streamingModels) {
        Measure-Case `
            -Context $context `
            -Model $model.Name `
            -Quantization $model.Quantization `
            -Mode "stream" `
            -ModelPath (Join-Path $modelCache $model.Name)
    }
}

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("# Parakeet Realtime Streaming Smoke")
$lines.Add("")
$lines.Add("Date: $(Get-Date -Format o)")
$lines.Add("Audio: ``$wav``")
$lines.Add("Iterations per row: ``$Iterations``")
$lines.Add("")

foreach ($group in $results | Group-Object Model, Quantization) {
    $first = $group.Group | Select-Object -First 1
    $lines.Add("Model: ``$($first.Model)`` (``$($first.Quantization)``)")
    $lines.Add("")
    $lines.Add("| Runtime | Mode | Context | Wall time min | Wall time avg | Wall time max | Transcript |")
    $lines.Add("| --- | --- | ---: | ---: | ---: | ---: | --- |")
    foreach ($row in $group.Group) {
        $transcript = $row.Transcript.Replace("|", "\|")
        $lines.Add("| $($row.Runtime) | $($row.Mode) | $($row.Iterations) runs | $($row.MinMs) ms | $($row.AvgMs) ms | $($row.MaxMs) ms | $transcript |")
    }
    $lines.Add("")
}

$lines.Add("Note: ``parakeet-cli v0.4.0`` rejects ``--json`` with ``--stream``; streaming rows parse ``[stream:final]`` text output.")
$lines | Set-Content -Path $ResultPath -Encoding UTF8

Write-Host "Wrote $ResultPath"
Get-Content -Path $ResultPath
