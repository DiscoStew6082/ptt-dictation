#Requires -Version 7.0
[CmdletBinding()]
param([switch]$PrepareOnly)

$ErrorActionPreference = 'Stop'
if (-not $IsWindows -or $env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'Firefox native dump analysis is restricted to disposable GitHub-hosted Windows runners.'
}

$artifactRoot = Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts/firefox-cold-start'
$dumps = @(Get-ChildItem -LiteralPath $artifactRoot -Filter '*.dmp' -File -Recurse -ErrorAction SilentlyContinue)
if ($dumps.Count -eq 0 -and -not $PrepareOnly) { Write-Output 'No Firefox failure dumps were produced.'; return }

$debugger = 'C:\Program Files (x86)\Windows Kits\10\Debuggers\x64\cdb.exe'
if (-not (Test-Path -LiteralPath $debugger)) {
    # Microsoft documents this debugger-only SDK installation in
    # microsoft/ebpf-for-windows/.github/skills/debug-crash-dump/SKILL.md.
    $setup = Join-Path $env:RUNNER_TEMP 'ptt-debugger-sdk-setup.exe'
    Invoke-WebRequest 'https://go.microsoft.com/fwlink/?linkid=2272610' -OutFile $setup
    $signature = Get-AuthenticodeSignature -LiteralPath $setup
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') {
        throw 'The debugger SDK installer does not have a valid Microsoft signature.'
    }
    $install = Start-Process -FilePath $setup -ArgumentList '/features OptionId.WindowsDesktopDebuggers /quiet /norestart' -WindowStyle Hidden -PassThru
    if (-not $install.WaitForExit(300000)) {
        $install.Kill($true)
        throw 'Debugger-only SDK installation timed out.'
    }
    if ($install.ExitCode -notin @(0, 3010) -or -not (Test-Path -LiteralPath $debugger)) {
        throw "Debugger-only SDK installation failed: $($install.ExitCode)"
    }
}

if ($PrepareOnly) { Write-Output "Prepared native debugger: $debugger"; return }

$symbolCache = Join-Path $env:RUNNER_TEMP 'ptt-debugger-symbols'
$localSymbols = Join-Path $env:RUNNER_TEMP 'ptt-debugger-local-symbols'
[IO.Directory]::CreateDirectory($localSymbols) | Out-Null
$symbols = "srv*$symbolCache*https://msdl.microsoft.com/download/symbols;srv*$symbolCache*https://symbols.mozilla.org/"
foreach ($dump in $dumps) {
    # Always retain module/offset stacks even if remote symbols are unavailable.
    foreach ($pass in @(
        @{Name='raw'; Symbols=$localSymbols; Timeout=30000},
        @{Name='symbols'; Symbols=$symbols; Timeout=150000}
    )) {
        $output = $dump.FullName + ".$($pass.Name).stacks.txt"
        $errors = $dump.FullName + ".$($pass.Name).debugger-errors.txt"
        # Thread IDs and module identities correlate both snapshots with the
        # pending provider call. Lock queries retain missing-memory/type errors
        # rather than inferring an owner from a function name alone.
        $commands = 'vertarget; .time; lm; lmvm xul; lmvm UIAutomationCore; ~* kv 0x40; !locks; !cs -l -o; q'
        $arguments = '-z "{0}" -y "{1}" -i "C:\Program Files\Mozilla Firefox" -c "{2}"' -f $dump.FullName, $pass.Symbols, $commands
        $analysis = Start-Process -FilePath $debugger -ArgumentList $arguments -WindowStyle Hidden -PassThru -RedirectStandardOutput $output -RedirectStandardError $errors
        if (-not $analysis.WaitForExit($pass.Timeout)) {
            $analysis.Kill($true)
            Add-Content -LiteralPath $errors 'Native stack analysis timed out; partial output retained.'
        }
        Write-Output "Native stack evidence: $output"
    }
}
