#Requires -Version 7.0
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# This test operates a disposable runner desktop. Never opt a user's machine in.
if (-not $IsWindows -or $env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'Firefox cold-start integration tests require a disposable GitHub-hosted Windows runner.'
}

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$artifactDirectory = Join-Path $repositoryRoot 'artifacts/firefox-cold-start'
[IO.Directory]::CreateDirectory($artifactDirectory) | Out-Null
$firefoxPath = Join-Path $env:ProgramFiles 'Mozilla Firefox/firefox.exe'
if (-not (Test-Path -LiteralPath $firefoxPath -PathType Leaf)) {
    throw "The GitHub runner does not have Firefox at $firefoxPath. No browser test was performed."
}

$firefoxFile = Get-Item -LiteralPath $firefoxPath
@{
    firefoxPath = $firefoxFile.FullName
    firefoxVersion = $firefoxFile.VersionInfo.ProductVersion
    runnerImage = $env:ImageOS
    runnerImageVersion = $env:ImageVersion
    commit = $env:GITHUB_SHA
    runId = $env:GITHUB_RUN_ID
    startedUtc = [DateTimeOffset]::UtcNow.ToString('O')
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $artifactDirectory 'runner.json') -Encoding utf8

$env:PTT_FIREFOX_PATH = $firefoxFile.FullName
$env:PTT_FIREFOX_ARTIFACTS = $artifactDirectory
$env:PTT_RUN_FIREFOX_COLD_START_TESTS = '1'
$resultName = 'firefox-cold-start.trx'
$resultPath = Join-Path $artifactDirectory $resultName
if (Test-Path -LiteralPath $resultPath) {
    throw "Refusing to reuse an existing test result: $resultPath"
}

& dotnet test (Join-Path $repositoryRoot 'tests/PttDictation.Tests/PttDictation.Tests.csproj') `
    --configuration Release --no-restore --filter 'TestCategory=FirefoxColdStart' `
    --results-directory $artifactDirectory --logger "trx;LogFileName=$resultName"
$testExitCode = $LASTEXITCODE
if ($testExitCode -ne 0) {
    throw "Firefox cold-start tests failed (dotnet exit code $testExitCode). See the uploaded TRX and browser evidence."
}
if (-not (Test-Path -LiteralPath $resultPath -PathType Leaf)) {
    throw 'Firefox cold-start tests produced no TRX result. A missing result cannot pass.'
}

[xml]$testResults = Get-Content -LiteralPath $resultPath -Raw
$namespace = [Xml.XmlNamespaceManager]::new($testResults.NameTable)
$namespace.AddNamespace('t', 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010')
$cases = @($testResults.SelectNodes('/t:TestRun/t:Results/t:UnitTestResult', $namespace))
$counters = $testResults.SelectSingleNode('/t:TestRun/t:ResultSummary/t:Counters', $namespace)
if ($cases.Count -ne 2 -or @($cases | Where-Object { $_.outcome -ne 'Passed' }).Count -ne 0 -or
    $null -eq $counters -or [int]$counters.total -ne 2 -or [int]$counters.executed -ne 2 -or
    [int]$counters.passed -ne 2) {
    throw 'Expected exactly two passed Firefox cases (textarea and contenteditable), with no skipped or missing tests.'
}
Write-Output 'Passed both real-Firefox cases: three cold launches per editor, two dictations per launch, no skipped tests.'
