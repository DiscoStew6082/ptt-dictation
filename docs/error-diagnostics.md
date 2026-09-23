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

## September 23 investigation

Three distinct sequences were present in the local trace; they should not be treated as one generic "textbox changed" failure:

- An unusable target was rejected while microphone startup was still finishing. Automatic Stop followed Start by roughly 3 ms, then timed out after five seconds. NAudio 3.1.0 can overwrite its early `Stopping` state with `Capturing` in its startup thread. The recorder now repeats Stop if that state reappears, within the existing five-second deadline. It uses the same handshake during disposal. This does not add a fixed startup delay or change target eligibility.
- A captured UI Automation reference became unavailable while the final Qwen request was running. The trace proves a provider-reference failure, not why the visible editor became inaccessible. The transcript was preserved; adopting a newly focused textbox would not be safe.
- A replacement selection was not acknowledged within two seconds. The observed document and focus generation were unchanged, but the selection was still the original caret. The provider may have ignored Select or returned stale selection data. This evidence does not justify pasting anyway, extending the timeout, or automatically retrying the selection.

If automatic finalization also fails after a textbox error, the workflow now reports the original insertion failure followed by the finalization failure. Previously the later microphone error could conceal the first cause.

Deterministic regression tests cover the native state overwrite and primary-error preservation. Real recorder probes are separate from exact hotkey/textbox interaction acceptance. The selection-acknowledgement and unavailable-reference cases remain under diagnosis; the safety checks still refuse uncertain writes. NAudio's disposal can still block on a genuinely hung native capture thread, which is a separate inherited limitation from the repaired startup race.

The real microphone probe reproduced the old state sequence: Start returned `Starting`, the single Stop timed out after 5,002 ms, and the recorder remained `Capturing`. The new handshake rescued that baseline trial. Ten immediate production Stop trials then completed in 31-50 ms; three immediate Dispose trials completed in 33-38 ms; a normal 250 ms recording produced audio and stopped in 23 ms. All temporary probe audio was deleted. The pinned [NAudio capture implementation](https://github.com/naudio/NAudio/blob/0aaef29d04bec9567bdf2f669036fabecc33a2e2/src/NAudio.Wasapi/WasapiRecorder.cs) contains the startup overwrite.

To repeat this microphone lifecycle check deliberately, build `tools/PttDictation.Replay` in Release and run its executable with `--capture-stop-probe <ignored-output-directory>`. Unlike the default fixture replay, this explicit probe briefly opens the microphone. It uses a child process with a 60-second deadline, removes its temporary WAVs, and retains only diagnostic metadata in the output directory. It does not use speech recognition, clipboard access, or the running app.
