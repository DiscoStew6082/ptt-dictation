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
| `focus_guard.event` | During pending capture, records the native event kind, object/child IDs, occurrence timestamp, delivery tick, age, and whether it predates capture. No textbox text is read. |
| `target.legacy_capture_detail` | Explains MSAA fallback rejection: missing root/focus, cycle/depth limit, wrong process/role/state, changed identity/context, or provider exception type. |
| `target.unsupported_provider` / `target.unsupported_selection*` | For rejected Document/Window targets, records provider framework, focus, selection count and read-only state, and up to eight enclosing ancestors. Never authorizes insertion or logs text/name/value content. |
| `target.unavailable` | The captured UI Automation reference stopped working; this alone does not prove the visible textbox disappeared. |
| `target.provider_operation_failed` | Names the exact failing native operation, such as `focus.original_element`, `capture.text_pattern`, or `snapshot.selection`, with exception type and HRESULT only. |
| `target.provider_refresh_rejected` | Explains why refreshing the original element was refused, including lost focus or unavailable capabilities. |
| `target.selection_requery_requested` / `target.selection_requery_observed` | Compares the unchanged pre-selection snapshot with a read after re-querying the original element's text pattern. Only lengths, equality checks, and timing are recorded. |
| `selection_acknowledgement_timeout` | The requested selection was not observed before the deadline; compare `documentMatches`, `snapshotMatchesBeforeRequest`, and focus-generation metadata. |
| `document_partition_or_surroundings_changed` | The observed text no longer matched the range the app owned and its surroundings. |
| `clipboard.paste_failed` | Inspect stage/sequence metadata to distinguish an unsent read failure from an uncertain submitted paste. |

The existing detailed timeline can contain dictated text and exception messages. The error index does not copy ordinary preview/transcription events, but exception messages may contain sensitive information. Target metadata excludes window titles, textbox contents, and clipboard contents. Keep raw diagnostic files local; do not commit or include them in release packages. This update does not add audio retention.

Recognition and insertion are separate: a `qwen.final_completed` event followed by a workflow failure can mean recognition succeeded but insertion failed. A saved transcript is not evidence that the textbox was updated.

`qwen.preload_started`, `qwen.preload_completed`, `qwen.preload_cancelled`, and `qwen.preload_failed` identify background startup loading and its elapsed time. Compare worker readiness with recording start and Stop to distinguish cold loading from final inference latency.

`recognition.preview_empty_warning` is also retained in the error index. It means at least two successful, non-cancelled preview responses were all empty or whitespace, no assembled preview contained text, and final recognition returned nonempty text. Failed previews, incomplete worker settlement, cancelled final recognition, empty final text, and insufficient preview samples do not trigger this comparison. The event contains counts, audio/processing durations, and final character count only. It changes no notifications, recognition, retries, insertion, or audio retention. It proves a preview/final discrepancy, not its acoustic or model cause.

The [controlled empty-preview investigation](research/parakeet-empty-preview-2026-09-23.md) records the observed incident and the limits of replay evidence.

## October 9 initial-capture investigation

The running October 1 package recorded repeated initial-capture failures. Firefox
exposed read-only Document or Window elements and its MSAA fallback was rejected.
A ChatGPT attempt exposed a Pane without a text pattern, then its focus generation
changed during the first recovery wait. Subsequent attempts exposed Edit elements
immediately and completed insertion. A successful retry did not resolve the
recurring failure, and the old metadata cannot identify the source of that change
or the reason MSAA rejected the Firefox target.

A reproducible guard defect was isolated: out-of-context Windows accessibility
events can be delivered after capture even when they were generated before it.
The guard discarded the native event timestamp and treated delivery as a new
change. Regression tests reproduced the resulting false rejection. The guard now
compares event occurrence against capture start, resolving the native 32-bit tick
against the 64-bit delivery tick. Only unambiguously older events are ignored;
same-tick, later, future/ambiguous events and changed native focus remain rejected.
Both worker recovery and deferred UI confirmation use the same generation.

Code-path tests cover delayed delivery, timestamp wraparound, genuine focus and
selection changes, switching away and back, container recovery, and deferred
confirmation followed by paste. They do not establish that the historic incident
was caused by a stale event or that all recurring browser failures are fixed.
The added event and MSAA metadata are needed to distinguish any next failure.
Agent-driven Firefox interaction could not be exercised because Windows Computer
Use access was denied. User-operated physical-hotkey attempts on the installed
repair still failed. The new trace established unchanged capture focus, native
Gecko Document focus, a read-only document, zero selected ranges, and an MSAA focus
chain ending at ROLE_SYSTEM_DOCUMENT (15), rejected as `role_not_text`. The user
confirmed ordinary keyboard input works in the same ChatGPT web composer.
The recorded Firefox failure is therefore not repaired by the timestamp change.

The saved Firefox profile said caret browsing was enabled, but the user's live F7
comparison offered to turn it on: that saved preference was stale and is not
evidence that caret browsing caused the failure. Likewise, use host-live log reads
when reconciling user actions; a stale sandbox file view initially omitted the
newest failure. Do not diagnose a missing hotkey from that snapshot.

The subsequent Chrome/Bing reproduction at 19:37:32 UTC captured a Pane without
TextPattern. Native caret and focus events for the same child arrived 63 ms after
capture began, so recovery and deferred confirmation correctly rejected the
changed generation. At 19:38:43 UTC, a physical-hotkey attempt captured the editor
and completed insertion, without another app update. Browser-tool inspection
between those attempts had accessed the accessibility tree. This supports cold
accessibility initialization as a cause, but does not prove the Firefox case has
the same cause or rule out a real focus change in the earlier Chrome attempt.

PTT now maintains a UIA focus subscription on a dedicated MTA worker and requests
the foreground browser's focused TextPattern while idle, before recording. Native
foreground/focus changes and UIA focus changes enqueue coalesced warmup requests;
a one-second fallback covers cold tabs without notifications, and coarse providers
get at most three reads per cycle. Reads never inspect textbox text or selection,
change focus, or supply a cached target to insertion. The existing hotkey capture,
focus-generation, selection, and clipboard checks remain authoritative. Warmup
does not wait on the UI/hotkey thread and stops starting reads during dictation.

Startup now takes an exclusive provider lease before capture, waiting for an
in-flight warmup read to finish. A bounded five-second preparation phase waits
for a focused writable field with a verifiable selection, then takes a fresh
capture under the existing strict guards. The lease covers deferred capture
confirmation. No warmed target is reused for insertion. An initially missing
UIA focus can become available; after its first identity is pinned, preparation
cannot switch to an unrelated editor. Once writable readiness is stable across
the provider read, that specific editor remains an identity constraint through
fresh capture, including scripted focus changes with no physical input. Native foreground, control, process and
input-generation checks reject window changes, typing, clicks or scrolling during
preparation. Delayed native events predating preparation do not invalidate it.

The non-activating Preparing textbox card makes clear that audio has not started.
Releasing hold-to-talk, toggling again, or cancelling retires a pending start;
it cannot start recording later. Timeout also leaves the microphone untouched.
If a native provider read outlives cancellation, lease release is queued behind
that read so another warmup cannot overlap it. `inline.preparation_ready` marks
the handoff to fresh capture. These changes close the known warmup/capture race;
they cannot make a hung or inaccessible external provider writable.

`accessibility.listening_started` confirms the subscription; the deduplicated
`accessibility.browser_warmed` records control type, pattern availability, and
process ID, without text. `accessibility.warmup_failed` records operation and
exception type. Subscription removal runs on its owning MTA without blocking Quit.

The regression model reproduces a cold provider's caret/focus notifications before
versus during capture. Warmup makes the former pass while genuine post-boundary
changes still reject. Lifecycle tests cover disposal during blocked registration,
coalescing, retry limits, active-session suppression, and recovery after a provider
failure. These are code-path evidence; exact physical-hotkey browser acceptance is
separate. Browser automation could inspect Bing but its simulated Right Ctrl did
not reach PTT's native hook.

Additional regressions force an in-flight warmup read and verify that neither
capture nor the microphone starts until it returns, including deferred capture
confirmation, blocked-read cancellation, timeout recovery, input redirection,
missing initial focus, and hold/toggle cancellation. The forced-overlap test was
red before the preparation lease and green afterward. On October 9, the prior
warmup-only build also passed the user's first physical attempt in a newly
started Chrome process on Bing; that successful baseline is not a live red
reproduction of the remaining race.

The first post-repair cold Chrome attempt then failed before microphone startup:
showing the Preparing overlay emitted `EVENT_SYSTEM_FOREGROUND` naming the
overlay even though Windows retained the editor's native foreground and focus.
An isolated native overlay regression reproduced this rejection. The guard now
ignores only an event naming PTT's exact known non-activating overlay when that
window is not actually foreground. Actual overlay activation, other windows,
and genuine switch-away-and-back events remain invalidating. Metadata-only
`focus_guard.preparation_rejected` distinguishes window, input-counter and
foreground-generation changes; no typed input is retained.

Upstream rationale: Chromium's [fragment root](https://raw.githubusercontent.com/chromium/chromium/main/ui/accessibility/platform/ax_fragment_root_win.cc)
signals a UIA client, while [TextPattern requests](https://raw.githubusercontent.com/chromium/chromium/main/ui/accessibility/platform/ax_platform_node_win.cc)
activate web-text capabilities through [accessibility mode selection](https://raw.githubusercontent.com/chromium/chromium/main/content/browser/accessibility/browser_accessibility_state_impl.cc).
Firefox's [UIA event bridge](https://raw.githubusercontent.com/mozilla-firefox/firefox/main/accessible/windows/uia/uiaRawElmProvider.cpp)
checks whether UIA clients are listening. These current upstream sources explain
the design, not the exact installed browser builds. Subscription threading follows
[Microsoft's guidance](https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-threading).

## September 23 investigation

Three distinct sequences were present in the local trace; they should not be treated as one generic "textbox changed" failure:

- An unusable target was rejected while microphone startup was still finishing. Automatic Stop followed Start by roughly 3 ms, then timed out after five seconds. NAudio 3.1.0 can overwrite its early `Stopping` state with `Capturing` in its startup thread. The recorder now repeats Stop if that state reappears, within the existing five-second deadline. It uses the same handshake during disposal. This does not add a fixed startup delay or change target eligibility.
- A captured UI Automation reference became unavailable while the final Qwen request was running. The trace proves a provider-reference failure, not why the visible editor became inaccessible. The transcript was preserved; adopting a newly focused textbox would not be safe.
- A replacement selection was not acknowledged within two seconds. The observed document and focus generation were unchanged, but the selection was still the original caret. The provider may have ignored Select or returned stale selection data. This evidence does not justify pasting anyway, extending the timeout, or automatically retrying the selection.

If automatic finalization also fails after a textbox error, the workflow now reports the original insertion failure followed by the finalization failure. Previously the later microphone error could conceal the first cause.

Deterministic regression tests cover the native state overwrite and primary-error preservation. Real recorder probes are separate from exact hotkey/textbox interaction acceptance. The selection-acknowledgement and unavailable-reference cases remain under diagnosis; the safety checks still refuse uncertain writes. NAudio's disposal can still block on a genuinely hung native capture thread, which is a separate inherited limitation from the repaired startup race.

The real microphone probe reproduced the old state sequence: Start returned `Starting`, the single Stop timed out after 5,002 ms, and the recorder remained `Capturing`. The new handshake rescued that baseline trial. Ten immediate production Stop trials then completed in 31-50 ms; three immediate Dispose trials completed in 33-38 ms; a normal 250 ms recording produced audio and stopped in 23 ms. All temporary probe audio was deleted. The pinned [NAudio capture implementation](https://github.com/naudio/NAudio/blob/0aaef29d04bec9567bdf2f669036fabecc33a2e2/src/NAudio.Wasapi/WasapiRecorder.cs) contains the startup overwrite.

To repeat this microphone lifecycle check deliberately, build `tools/PttDictation.Replay` in Release and run its executable with `--capture-stop-probe <ignored-output-directory>`. Unlike the default fixture replay, this explicit probe briefly opens the microphone. It uses a child process with a 60-second deadline, removes its temporary WAVs, and retains only diagnostic metadata in the output directory. It does not use speech recognition, clipboard access, or the running app.

## Bounded selection recovery and remaining evidence

If the first read after Select still reports exactly the pre-request caret, the app can re-query TextPattern on the same captured AutomationElement once for that selection request. It does not issue another Select, discover a replacement textbox, or extend the two-second acknowledgement deadline. The re-read must still match the complete original document partition, exact owned text, and original focus before paste is authorized. A changed document or different selection is a conflict; focus loss pauses insertion. A native read cannot be cancelled mid-call, but recovery returned after the original deadline does not authorize a paste. The existing insertion watchdog also remains in place.

The new trace records whether the re-query was supported, rejected, or returned a pattern, and the resulting snapshot comparisons. Success means a validated selection was observed after re-query; it cannot prove whether the old wrapper was stale or the provider simply finished acknowledging Select during that time. Failed or ignored selections still produce a safe refusal.

Unavailable references keep their existing single same-reference recovery attempt per recording. Provider-stage diagnostics now identify which required operation failed without adding diagnostic-only UIA reads. The app never treats an accessible window handle, identical text, or a reused runtime ID as proof that a replacement editor is the original target.

Headless regressions cover these recovery boundaries. The original intermittent editor failures have not been reproduced under the new build; these tests establish the bounded recovery behavior, not a claim that both real provider failures are eliminated. Ordinary use will supply the relevant provider-stage evidence if either recurs.
