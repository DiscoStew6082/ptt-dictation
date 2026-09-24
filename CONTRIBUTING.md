# Contributing

Thanks for helping improve PTT Dictation.

## Development Setup

Prerequisites:

- Windows 10 or later
- .NET SDK 10.0.400 or newer
- Visual Studio 2026 or another editor with .NET 10 SDK support

The app targets .NET 10 LTS, which is supported until November 14, 2028.

Run the default local checks before opening a pull request:

```powershell
dotnet restore PttDictation.sln --locked-mode
dotnet test PttDictation.sln --configuration Release --no-restore
dotnet list PttDictation.sln package --vulnerable --include-transitive
```

The default test command is window-free. Tests that intentionally show Settings,
tray UI, history captures, or status overlays require explicit visible-UI opt-in.
Tests that save through Settings also require opt-in because a failed hotkey
validation can open a message box, even when the form began hidden.
They report **Skipped** by default, including when selected directly by test name;
no remembered exclusion filter is required. Hidden form construction, settings
logic, control layout, transcription, and other non-presenting tests still run.

Visible tests carry the `VisibleUi` category and call
`VisibleUiTestGate.RequireOptIn()` before creating or presenting UI. Put that guard
at the beginning of every new visible test, even when it calls a presentation
helper indirectly. Screenshot-only branches must use
`VisibleUiTestGate.ShouldCaptureScreenshot(path)` before showing a window: a
`PARAKEET_*_PREVIEW_PATH` or `PTT_*_PREVIEW_PATH` variable is not permission to show
windows. A requested screenshot without opt-in skips its test before presentation.

Only on a separate desktop explicitly approved for visible test windows, set the
process-scoped environment variable `PTT_RUN_VISIBLE_UI_TESTS` to exactly `1` and
run the desired tests. Remove the variable immediately afterward; do not persist
it in a user or machine environment. Values such as `true`, generic `CI` variables,
and screenshot paths do not enable visible tests. The GitHub Actions test step
opts in explicitly on its disposable `windows-latest` runner, retaining UI
coverage away from the user's desktop. Routine local verification must leave
visible-UI opt-in unset.

Test helpers that launch command-line child processes must use
`UseShellExecute = false` and `CreateNoWindow = true` (PowerShell `Start-Process`
helpers also need `-WindowStyle Hidden`). This applies to test helpers, not the
normally launched interactive PTT Dictation app or its tray acceptance workflow.

For this machine's pinned local installation, publish a tested build to staging and use the deployment script (PowerShell 7):

```powershell
dotnet publish src\PttDictation.App\PttDictation.App.csproj -c Release -r win-x64 --self-contained true -o publish\next-build
pwsh -File scripts\Update-LocalApp.ps1 -StagedPath publish\next-build
```

The deployment script deliberately fixes the destination to `C:\Users\stewa\projects\par-win-ptt\publish\ptt-dictation-win-x64\PttDictation.exe`, independent of the current directory or worktree. Keep Start-menu pins pointed there. It requires an existing installation, rejects incomplete or linked packages, checks every installed file's SHA-256, and verifies exactly one normally launched process at that path with no arguments. An installation failure triggers restoration and restart of the previous package; recovery failures are reported explicitly. The previous package is retained in a `.backup-*` directory for recovery, and the installed package receives `deployment-receipt.json` with hashes and the fixed executable path.

Launches during installation and recovery go through Explorer's desktop automation object. Direct `Start-Process` and a newly created `Shell.Application` object's own `ShellExecute` can inherit the updater host's Windows jobs, allowing host shutdown to terminate the daily app later. The desktop broker keeps the launch independent of that host. Deployment fails if the broker is unavailable rather than silently reverting to an inherited launch. Startup-folder sign-in behavior remains a separate acceptance check from successful installation and process lifetime.

To inspect the installation without replacing files or restarting the app:

```powershell
pwsh -File scripts\Update-LocalApp.ps1 -VerifyOnly
```

This reports process IDs (empty if stopped) and checks receipt hashes when a receipt exists. A package installed before this workflow has no receipt; its path and current hashes can be inspected, but are not proof of a verified deployment. Process and file checks do not replace tray/hotkey acceptance. For builds on other machines or CI packaging, publish into a separate artifact folder without using this machine-specific installer.

Run the deployment regression checks without touching the live installation:

```powershell
pwsh -File scripts\Test-LocalAppDeployment.ps1
```

Before publishing a release, verify the build and record a SHA-256 checksum for the zip:

```powershell
Get-FileHash publish\PttDictation-win-x64.zip -Algorithm SHA256
```

Public releases should also consider code signing, SBOM generation, and provenance attestations.

Generated output belongs under `publish\`, `bin\`, or `obj\` and should not be committed.

## Pull Requests

- Keep changes focused.
- Add or update tests for behavior changes.
- Prefer public-interface tests over implementation-detail tests.
- Update `README.md` when behavior, setup, privacy boundaries, downloaded assets, or publishing instructions change.
- Keep `SECURITY.md` aligned with any change to clipboard behavior, temporary audio retention, runtime/model downloads, or local path overrides.
