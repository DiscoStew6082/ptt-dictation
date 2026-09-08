# Security Policy

PTT Dictation is a local Windows tray application. It records temporary audio only while dictation is active and uses local runtime/model assets for transcription.

## Supported Versions

Security fixes are intended for the latest release only. Supported builds target Windows 10/11 x64 and .NET 10 LTS, which is supported until November 14, 2028.

## Trust Boundaries

- Clipboard insertion is a local OS boundary. Interim and final transcripts are temporarily placed on the clipboard; restoration is best-effort. Other local applications may observe them. Destination editors receive real text during live recording, so their normal autosave and text-processing behavior applies.
- UI Automation reads the original editable field's text and selection to validate replacement of only this recording's text. These surrounding editor snapshots are transient. Known password, disabled, and read-only fields are excluded; uncertain writes stop instead of falling back to a duplicate whole-transcript paste.
- Recognition-stage comparisons and recovery transcripts remain in session-only history. This development checkpoint additionally enables local diagnostic logging of dictated text, recognition stages, timing, and errors under `%LOCALAPPDATA%\PttDictation\diagnostics\experimental`. The log rotates at 4 MiB and retains up to three complete recordings. Diagnostic files may contain sensitive speech and remain local; they are not automatically uploaded. Original clipboard contents and surrounding editor text are excluded.
- Runtime and model path overrides trust the selected local files. Do not point the app at untrusted executables or models.
- The low-level keyboard hook is used to detect the push-to-talk hotkey while the app is running.
- Runtime/model downloads contact upstream hosts on first use unless local paths are configured.

Release artifacts should publish SHA-256 checksums at minimum. The repository release workflow also produces a CycloneDX SBOM, and public tag builds produce GitHub artifact attestations. Code signing is recommended for broad public distribution.

## Reporting a Vulnerability

Please report security concerns privately before opening a public issue.

Use GitHub private vulnerability reporting if it is enabled for this repository. If that channel is not available, contact the maintainer out of band and do not include exploit details, sensitive audio, transcripts, credentials, or proof-of-concept payloads in a public issue.

Include:

- A short description of the issue.
- Steps to reproduce it.
- Any logs, screenshots, or proof-of-concept details that are safe to share.

Please do not include sensitive personal audio, transcripts, or credentials in reports.
