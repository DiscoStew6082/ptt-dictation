# Error tracing

The running app writes local diagnostics under `%LOCALAPPDATA%\PttDictation\diagnostics\experimental`.
The directory name is historical; the normal app configures it at startup.

- `events.jsonl` and `events.previous.jsonl` contain the detailed timeline.
- `errors.jsonl` and `errors.previous.jsonl` independently retain exceptions, rejected operations, timeouts, target conflicts, and reported warnings/errors. Routine cancellation exceptions are excluded. One incident can produce several entries as it propagates through the target, insertion, workflow, and notification layers.
- Each file is bounded to 4 MiB. Errors therefore survive ordinary preview-log rotation, but retention is not unlimited.

Use the recording ID, timestamp, process ID, build ID, and event sequence to connect an error to its timeline. New recordings include the captured foreground process ID and native window handles; process-name lookup runs separately from capture and can be unavailable if the process exits. Control type is recorded with the existing capability check. These diagnostics never adopt a different textbox or alter selection/paste decisions.

Tray warnings and errors are recorded even when notifications are disabled. Settings validation, save/load, model download, Qwen setup, UI action failures, and the existing unhandled-exception paths are traced. This is best-effort application diagnostics, not a guarantee against OS termination, power loss, unavailable disk, or queue exhaustion. `diagnostics.loss` reports observed queue/sink losses; logging failure must not block dictation.

For textbox incidents, inspect the first specific event rather than only the final generic message:

| Event/reason | Meaning |
| --- | --- |
| `target.surface_capabilities` / `inline.capture_result` | Whether Windows exposed a usable editable target at capture time. |
| `target.unavailable` | The captured UI Automation reference stopped working; this alone does not prove the visible textbox disappeared. |
| `selection_acknowledgement_timeout` | The requested selection was not observed before the deadline; compare `documentMatches`, `snapshotMatchesBeforeRequest`, and focus-generation metadata. |
| `document_partition_or_surroundings_changed` | The observed text no longer matched the range the app owned and its surroundings. |
| `clipboard.paste_failed` | Inspect stage/sequence metadata to distinguish an unsent read failure from an uncertain submitted paste. |

The existing detailed timeline can contain dictated text and exception messages. The error index does not copy ordinary preview/transcription events, but exception messages may contain sensitive information. Target metadata excludes window titles, textbox contents, and clipboard contents. Keep raw diagnostic files local; do not commit or include them in release packages. This update does not add audio retention.

Recognition and insertion are separate: a `qwen.final_completed` event followed by a workflow failure can mean recognition succeeded but insertion failed. A saved transcript is not evidence that the textbox was updated.
