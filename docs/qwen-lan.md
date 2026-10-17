# Android dictation through the PC's Qwen model

The optional LAN endpoint lets an Android voice keyboard send recordings to the Qwen3-ASR 1.7B model already used by PTT Dictation. Both callers share the same resident worker and serialization gate. Phone results return to Android; they never enter the desktop paste workflow or Session History. The tray app must be running, and the PC must be awake.

This is complete-recording English transcription, not live streaming. One phone request is accepted at a time; another receives HTTP 429. Desktop and phone recognition run sequentially. Cancelling a queued request does not cancel the active caller. Cancelling active Qwen inference retires its worker; the next request reloads it, as with desktop cancellation.

## Enable locally

The feature is off unless `%LOCALAPPDATA%\PttDictation\qwen-lan.json` exists. Keep this file private and out of Git; it contains the access key. Example structure (replace every placeholder):

```json
{
  "address": "192.168.1.20",
  "port": 8765,
  "apiKey": "REPLACE_WITH_AT_LEAST_32_RANDOM_URL_SAFE_CHARACTERS",
  "ffmpegPath": "C:\\Tools\\ffmpeg\\bin\\ffmpeg.exe"
}
```

Use the PC's explicit private IPv4 address. Wildcard and public bind addresses are rejected. Allow inbound TCP on that port only for the installed PTT executable, the intended network interface/local address, and the local subnet. The app does not create a firewall rule or change the Windows network profile. Restart PTT normally after changing configuration. Remove the configuration and restart to disable the endpoint; remove its firewall rule when no longer needed.

HTTP is unencrypted: use only a trusted home network, with no router port forwarding. The bearer key is required for every endpoint. This version does not provide TLS or remote-internet access. A DHCP address change requires updating the configuration, firewall scope, and phone URL.

FFmpeg must already be installed at the configured absolute local path. WAV, MP3, FLAC, AAC/M4A, Ogg/Opus, and WebM are decoded to PCM16 mono 16 kHz. Network protocols and playlist demuxers are disabled. Uploads are limited to 20 MiB including multipart overhead and five minutes of decoded audio; longer recordings are rejected, not silently truncated. Temporary audio is deleted after each request; startup cleans the service's recognized residue files after an interrupted run. Phone transcript text is omitted from Qwen completion diagnostics.

## Dictate Keyboard

In [Dictate Keyboard](https://github.com/DevEmperor/DictateKeyboard), choose a dark theme and its voice-only layout if desired. Add your own server under Settings / AI providers:

- Base URL: `http://PC_ADDRESS:8765/v1/`
- API key: the local configuration's `apiKey`
- Transcription model: `qwen3-asr-1.7b`
- Language: English
- Real-time streaming and optional AI rewording: off for this setup

Enable Dictate as an Android keyboard, select it in a normal text field, record, and stop. Text should return to that Android field. The API format is OpenAI-compatible but audio goes to the specified PC, not OpenAI. The native app supports LAN HTTP; a browser microphone page is not needed.

`GET /v1/models` and `GET /v1/audio/models` list the model. `POST /v1/audio/transcriptions` accepts multipart fields `file`, `model`, optional `language=en`, and `response_format=json` or `text`. Arbitrary language selection, streaming, timestamps, translation, and prompt-based rewriting are not implemented.

## Verification boundary

Automated tests cover authentication, multipart handling, response formats, invalid requests, request contention, cancellation, cleanup, and shared desktop/phone worker ownership. Test the live endpoint with real compressed audio after deployment. Neither that test nor package/process checks establish S25 keyboard acceptance: verify recording and text insertion on the phone separately. Source changes do not imply publication to GitHub.

## Local verification on October 17, 2026

- Release suite: 631 passed, 39 interactive tests skipped. After the final startup-cleanup addition, all eight LAN/shared-worker tests passed.
- Staged self-contained package installed through the repository updater at the existing installation path; all 604 package files verified, with the previous package retained for recovery.
- The live authenticated endpoint transcribed one existing public LibriSpeech sample from WAV, M4A/AAC, and Ogg/Opus, with identical text. Request times were 3.03, 1.85, and 1.93 seconds respectively; these are single-machine observations, not latency guarantees.
- Missing authorization returned 401; malformed audio returned 400; no temporary request directories remained. Process ancestry showed one desktop-owned Qwen worker (Python launcher plus runtime child), not a second model service.
- Phone-to-PC network reachability, S25 keyboard insertion, and desktop hotkey interaction after this deployment remain pending. The Windows firewall permission prompt was still pending at this record's creation.
