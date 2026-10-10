#Requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][ValidateSet('Firefox', 'Chrome')][string]$Browser)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# This test operates a disposable runner desktop. Never opt a user's machine in.
if (-not $IsWindows -or $env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'Browser cold-start integration tests require a disposable GitHub-hosted Windows runner.'
}

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$browserKey = $Browser.ToLowerInvariant()
$artifactDirectory = Join-Path $repositoryRoot "artifacts/$browserKey-cold-start"
[IO.Directory]::CreateDirectory($artifactDirectory) | Out-Null
$browserRelativePath = if ($Browser -eq 'Firefox') { 'Mozilla Firefox/firefox.exe' } else { 'Google/Chrome/Application/chrome.exe' }
$browserPath = Join-Path $env:ProgramFiles $browserRelativePath
if ($Browser -eq 'Chrome') {
    # The runner's signed Chrome MSI records its executable in App Paths.
    $chromeKey = Get-Item -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe' -ErrorAction SilentlyContinue
    $registeredChrome = if ($null -ne $chromeKey) { $chromeKey.GetValue('') } else { $null }
    if ($registeredChrome -and [IO.Path]::IsPathFullyQualified($registeredChrome)) { $browserPath = $registeredChrome }
}
if (-not (Test-Path -LiteralPath $browserPath -PathType Leaf)) {
    throw "The GitHub runner does not have $Browser at $browserPath. No browser test was performed."
}

$browserFile = Get-Item -LiteralPath $browserPath
@{
    browser = $Browser
    "$($browserKey)Path" = $browserFile.FullName
    "$($browserKey)Version" = $browserFile.VersionInfo.ProductVersion
    runnerImage = $env:ImageOS
    runnerImageVersion = $env:ImageVersion
    commit = $env:GITHUB_SHA
    runId = $env:GITHUB_RUN_ID
    runAttempt = $env:GITHUB_RUN_ATTEMPT
    startedUtc = [DateTimeOffset]::UtcNow.ToString('O')
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $artifactDirectory 'runner.json') -Encoding utf8

[Environment]::SetEnvironmentVariable("PTT_$($Browser.ToUpperInvariant())_PATH", $browserFile.FullName)
[Environment]::SetEnvironmentVariable("PTT_$($Browser.ToUpperInvariant())_ARTIFACTS", $artifactDirectory)
[Environment]::SetEnvironmentVariable("PTT_RUN_$($Browser.ToUpperInvariant())_COLD_START_TESTS", '1')
$resultName = "$browserKey-cold-start.trx"
$resultPath = Join-Path $artifactDirectory $resultName
if (Test-Path -LiteralPath $resultPath) {
    throw "Refusing to reuse an existing test result: $resultPath"
}

& dotnet test (Join-Path $repositoryRoot 'tests/PttDictation.Tests/PttDictation.Tests.csproj') `
    --configuration Release --no-restore --filter "TestCategory=$($Browser)ColdStart" `
    --results-directory $artifactDirectory --logger "trx;LogFileName=$resultName"
$testExitCode = $LASTEXITCODE
if ($testExitCode -ne 0) {
    throw "$Browser cold-start tests failed (dotnet exit code $testExitCode). See the uploaded TRX and browser evidence."
}
if (-not (Test-Path -LiteralPath $resultPath -PathType Leaf)) {
    throw "$Browser cold-start tests produced no TRX result. A missing result cannot pass."
}

[xml]$testResults = Get-Content -LiteralPath $resultPath -Raw
$namespace = [Xml.XmlNamespaceManager]::new($testResults.NameTable)
$namespace.AddNamespace('t', 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010')
$cases = @($testResults.SelectNodes('/t:TestRun/t:Results/t:UnitTestResult', $namespace))
$counters = $testResults.SelectSingleNode('/t:TestRun/t:ResultSummary/t:Counters', $namespace)
if ($cases.Count -ne 2 -or @($cases | Where-Object { $_.outcome -ne 'Passed' }).Count -ne 0 -or
    $null -eq $counters -or [int]$counters.total -ne 2 -or [int]$counters.executed -ne 2 -or
    [int]$counters.passed -ne 2) {
    throw "Expected exactly two passed $Browser cases (textarea and contenteditable), with no skipped or missing tests."
}
foreach ($editor in @('textarea', 'contenteditable')) {
    foreach ($trial in 1..3) {
        $passedPath = Join-Path $artifactDirectory "$editor/trial-$trial/passed.json"
        $observations = @(Get-Content -LiteralPath $passedPath -Raw | ConvertFrom-Json)
        if ($observations.Count -ne 2) { throw "Missing dictation evidence: $passedPath" }
        foreach ($dictation in 1..2) {
            $row = $observations[$dictation - 1]
            if ($row.dictation -ne $dictation -or $row.actual -ne $row.expected -or
                $row.TrustedPasteEvents -ne (3 * $dictation) -or $row.userAgent -notmatch "$Browser/") {
                throw "Invalid browser insertion evidence: $passedPath"
            }
        }
    }
}
Write-Output "Passed both real-$Browser cases: three cold launches per editor, two dictations per launch, no skipped tests."
