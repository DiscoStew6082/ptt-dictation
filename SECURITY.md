# Security Policy

PTT Dictation is a local Windows tray application. It records temporary audio only while dictation is active and uses local runtime/model assets for transcription.

## Supported Versions

Security fixes are intended for the latest release only. Supported builds target Windows 10/11 x64 and .NET 10 LTS, which is supported until November 14, 2028.

## Trust Boundaries

- Clipboard paste is a local OS boundary. Transcripts are temporarily placed on the clipboard and pasted into the active app; clipboard restoration is best-effort.
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
