# Contributing

Thanks for helping improve PTT Dictation.

## Development Setup

Prerequisites:

- Windows 10 or later
- .NET SDK 10.0.400 or newer
- Visual Studio 2026 or another editor with .NET 10 SDK support

The app targets .NET 10 LTS, which is supported until November 14, 2028.

Run the CI-equivalent checks before opening a pull request:

```powershell
dotnet restore PttDictation.sln --locked-mode
dotnet test PttDictation.sln --configuration Release --no-restore
dotnet list PttDictation.sln package --vulnerable --include-transitive
```

For this machine's pinned local installation, publish a tested build to staging and use the deployment script (PowerShell 7):

```powershell
dotnet publish src\PttDictation.App\PttDictation.App.csproj -c Release -r win-x64 --self-contained true -o publish\next-build
pwsh -File scripts\Update-LocalApp.ps1 -StagedPath publish\next-build
```

The deployment script defaults to `%LOCALAPPDATA%\Programs\PttDictation`, independent of the current directory or worktree. Pass `-InstallDirectory` to update an installation created elsewhere, and keep shortcuts pointed at that selected path. It requires an existing installation, rejects incomplete or linked packages, checks every installed file's SHA-256, and verifies exactly one normally launched process at that path with no arguments. An installation failure triggers restoration and restart of the previous package; recovery failures are reported explicitly. The previous package is retained in a `.backup-*` directory for recovery, and the installed package receives `deployment-receipt.json` with hashes and the selected executable path.

To inspect the installation without replacing files or restarting the app:

```powershell
pwsh -File scripts\Update-LocalApp.ps1 -VerifyOnly [-InstallDirectory <existing-install-directory>]
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
