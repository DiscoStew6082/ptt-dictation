# Experimental dictation diagnostics — 2026-09-07

## Current state after the 2026-09-07 afternoon retry

The user explicitly requested restoring live transcription in the selected destination textbox and rejected an upper-right overlay. The morning experiment had been rolled back in full after a clipboard validation failure. The historical process/staging details below describe earlier runs and are not the current deployment.

- Source consolidation on 2026-09-08: the live textbox source was brought into the primary checkout for the requested Git checkpoint, preserving the correction-editor and recording-capture work. Use the primary checkout for subsequent builds. `publish/experimental-tracing-source` remains a preserved historical worktree. This consolidation did not replace or restart the deployed executable.
- Current staging: `publish/textbox-live-recovery-20260907`. Deployed to `publish/ptt-dictation-win-x64` and verified running as one process, PID 114656, with no arguments. App DLL SHA256 `99AE99C8EB37D276C4E5227DC9D388E093B5C52C89F09DAE6504E667FEE777E0`; Core DLL SHA256 `BCBAEC36C832062BF24E0975A1DA4A23B6DBCB2D245588200C66B9D10C80F1EB`. Startup build `experimental-live-insertion-7288e0fb-d370-4698-a8d1-a76db990477b`.
- Exact normal launch: `Start-Process -FilePath 'C:\Users\stewa\projects\par-win-ptt\publish\ptt-dictation-win-x64\PttDictation.exe' -WorkingDirectory 'C:\Users\stewa\projects\par-win-ptt\publish\ptt-dictation-win-x64'`.
- Live workflow recording/processing no longer opens a floating overlay, including during asynchronous target capture. Existing transition sounds and tray cancellation remain. Source-only fallback overlay tests are not the live workflow's presentation policy.
- Saved failure trace: `publish/diagnostic-cases/codex-clipboard-20260907-161933/events.jsonl`. At 20.109 seconds clipboard validation returned false before input; restoration then confirmed the same owned clipboard sequence. Recognition continued, but the old target permanently conflicted. The trace does not identify which clipboard read failed. New diagnostics distinguish sequence changes, unavailable text, and text mismatch without logging prior clipboard contents.
- Fix: only an unsent clipboard read failure with the same owned sequence and still-valid target may retry on the existing 200 ms pump. The exact selected snapshot must remain unchanged and the existing two-second deadline bounds recovery. Clipboard replacement, selection changes, ambiguous input failures, and acknowledgement failures do not gain retries. Clipboard restoration remains 750 ms.
- Regression evidence: three red cases (overlay plus initial/subsequent update recovery) before the fix; all 253 release tests pass afterward. Guard cases cover actual clipboard replacement, moved caret, persistent read failure, and failure after input.
- Native insertion evidence: `publish/textbox-recovery-native-01`. The saved 28-update sequence ran through production Windows UIA/clipboard workers in a new empty Notepad tab, with one deliberately injected clipboard read failure. It recovered and completed; the accessibility document exactly matched the expected 120-character final transcript. This is controlled insertion evidence, not Codex hotkey acceptance.
- Live Codex hotkey acceptance remains unverified. The user stopped Computer Use with physical Escape during the post-deployment UI check; no further UI input was attempted. The refreshed live app remains running. The original `.backup-before-live-dictation` and earlier experimental stages remain retained.

## Historical investigation

## Running and separate artifacts

- Canonical app: `publish/ptt-dictation-win-x64/PttDictation.exe`, now running cumulative revisable previews as PID 126532 when verified. Normal launch, no arguments. Earlier experimental PIDs 140428 and 23940, and restored PID 14884, were replaced after validation. Do not rebuild the working rollback from current HEAD: PDB analysis identified three actual source mismatches with that binary.
- Current stage: `publish/experimental-cumulative-fixed`. App DLL SHA256 `70A7B8EE93AA7B6E98F9BEF7E4DA75194639DF98B5188B87ED249251D0D5B5EA`; Core DLL SHA256 `BCBAEC36C832062BF24E0975A1DA4A23B6DBCB2D245588200C66B9D10C80F1EB`; startup trace build ID `experimental-live-insertion-2b944a44-98e8-4b1f-812e-d762cba0b984`. All three deployed binary hashes match staging; capture helper remains PID 142356.
- Previous context stage: `publish/experimental-context-fixed`, retained for rollback. App DLL SHA256 `5972C4620C4A6A6FFB106BB4959D6F96A310A7FBC24069995E9BEF9CE4AFE969`.
- Keep rollback backup: `publish/.backup-before-live-dictation`.
- Background audio helper: `publish/ptt-dictation-capture-win-x64/PttDictation.Capture.exe`, PID 142356 when started. This is a separate console process, launched hidden. It observes full WAV files; it does not record the microphone, control input, change settings, or restart the app. No login/startup registration was added.
- Experimental source: isolated git worktree `publish/experimental-tracing-source`. Includes the archived failed changes plus tracing, fixes, tests, and replay harness. Its fixed GUI is running at the canonical path.
- Previous fixed stage: `publish/experimental-placeholder-fixed`. App DLL SHA256 `5AFCE6F1B80DC1118A2CE20E69CA71709B491191D7230E268EF92C498A2A39C9`; Core DLL SHA256 `937412842CE65EA7601F88DCAD65A7C6D6DF573425FFAF2364930D8189A0AC3B`. Retained as an intermediate rollback.
- Experimental package: `publish/experimental-tracing-app`. Do not substitute this for the daily app without a deliberate experimental test. It shares the app's single-instance guard and settings when run interactively.
- Replay package: `publish/experimental-replay-win-x64`.
- Failed experiment archive: `publish/failed-live-insertion-source`.

The primary checkout's existing Settings/correction editor changes were preserved. New primary code is the shared diagnostic writer, external audio observer, helper tool, and their tests. Experimental workflow/input changes remain in the isolated worktree.

## Local data

Capture root: `C:/Users/stewa/AppData/Local/PttDictation/diagnostics/capture`.

Completed recordings appear as `recording-<timestamp>-<id>/audio.wav`. The writer retains exactly the newest three audio recordings and removes older owned audio files. It skips stale WAV files already present at helper startup. File observation is best effort: a file created and deleted before the observer opens it can be missed. Canceled recordings with no final WAV cannot be captured. Capture errors are logged. No audio is uploaded.

Experimental GUI event root, when deliberately run: `C:/Users/stewa/AppData/Local/PttDictation/diagnostics/experimental`.

Each process uses a separate event directory. `events.jsonl` and `events.previous.jsonl` are bounded to 4 MiB each. Logs include dictation text, word timestamps, correction stages, model/runtime choices, recording/chunk IDs, elapsed time, and full exception details. Original clipboard contents and surrounding textbox text are excluded. Disk/serialization failures are isolated from dictation; queue pressure is reported as dropped-event counts. Log rotation is independent of the three-recording audio retention.

## Replay

From PowerShell, with existing local model assets:

```powershell
& 'C:/Users/stewa/projects/par-win-ptt/publish/experimental-replay-win-x64/PttDictation.Replay.exe' '<retained-audio.wav>' '<unique-output-directory>' '<existing-runtime.exe>' '<existing-model.gguf>'
```

Replay uses the experimental `PcmChunkBuffer`, two-second chunks with 1.2-second overlap, original publication cadence, `ChunkedTranscribingDictationSession`, selected runtime implementation, correction dictionary, normalizer, and workflow. Preview and final output go to `comparison.json` and event logs. It never selects a Windows textbox or accesses the clipboard. It reads correction settings but does not save them. It requires installed runtime/model files and does not download assets. Its transient audio/chunk copies are removed on exit; the retained input remains owned by capture retention.

This harness can reproduce recognition, overlap assembly, and correction changes. It does not reproduce native target focus/selection/paste behavior, hotkeys, microphone capture, or automatic CUDA-to-CPU retry. Those paths have instrumentation in the separate experimental GUI and code-path tests; exact native user-interaction acceptance remains unverified.

## Evidence

- Primary source suite: 178/178 passed.
- Experimental source suite after acknowledgement and cumulative-preview fixes: 244/244 passed.
- Published helper smoke: four real WAVs written/finalized/deleted; four retention events; exactly WAVs 2/3/4 kept with matching SHA256; duplicate helper exited under mutex. Evidence: `smoke/baseline-verify/helper-run-78fa075ea93e4bfabd11e3ac8f7e301f`.
- Deterministic fixture replay: three previews plus final output, production chunk/workflow paths; `publish/replay-fixture-evidence`.
- Installed CPU model replay on locally synthesized speech: 13 previews and final result, no exceptions; `publish/replay-speech-evidence`. No user speech/retyping was needed.
- Actual deployment check: daily executable hashes match restored backup, original PID unchanged, one helper process, helper startup/configuration logs present.

The speech replay exposed a specific assembly defect: raw chunk 11 ended with `available so no`; raw chunk 12 recognized `available so nobody has to`; the assembled preview became `available so no nobody has to`. Full-recording recognition produced `available so nobody has to`. Preview dictionary correction did not introduce the duplicate. This is observed in the experimental audio pipeline, not proof of the cause of the reported chunking error and not proof it was introduced by the live-insertion change. The reported failure itself has not reproduced yet.

### Follow-up investigation

- The helper captured the user's next natural dictation, `So now what.`. Replaying that retained WAV through the experimental audio workflow produced three previews and the correct final output with no exception. Evidence: `publish/replay-user-evidence-20260907-1243`.
- Windows Computer Use became available. An empty new Notepad tab was created, preserving its restored existing tab. The diagnostic `--notepad-probe` mode calls the unchanged experimental `LiveClipboardPaster` with its actual MTA/STA workers, focus guard, timer, UI Automation surface, clipboard backend, and restoration queue. It replaced `Probe early words` with `Probe revised words`, then `Probe final text.`. Logs confirm all three writes acknowledged and clipboard restoration applied; the actual Notepad screenshot shows only the final 17 characters. Evidence: `publish/native-insertion-evidence-01`.
- This is native insertion-path evidence from a standalone probe. It does not exercise the complete experimental GUI, microphone, or the user's hotkey gesture. The daily app was not replaced or restarted. No new functional fix has been made.
- The user subsequently identified Codex as the failing target. A Notepad tab containing the harmless probe text remains open.

### Captured Codex failure — authorized live experiment

The user explicitly authorized the temporary switch and required saving the recording to avoid repeating a minute-long test. The canonical executable was switched to the traced package (PID 142524), launched normally without arguments, and startup tracing plus the external capture helper were verified. The user dictated in Codex using the usual gesture.

The complete approximately 56-second WAV was retained before rollback. Its size is 1,788,462 bytes and SHA256 is `940CA9FE5B0D1071AD9ADEB601B4B5C323E93CF365E512337615F038988F10BA`. In response to the explicit save/reuse request, this particular failed recording is also preserved in `publish/diagnostic-cases/codex-20260907-125057/audio.wav`, outside the rolling three-recording buffer. That case directory contains `events.jsonl` and the recovered full `transcript.txt`. Later normal dictation cannot rotate this case away. It remains local and ignored by git.

The actual failure is now reproduced: `target.conflict` reports `acknowledgement_timeout` two seconds after the first paste, then `workflow.finish_failed` reports `The original textbox changed, so dictation stopped replacing text.` Recognition continued and final transcription completed. No recognition/chunk exception caused this recorded failure. Initial UIA snapshot: document length 12, prefix 0, selection 0, suffix 12. The first 13-character preview was sent with clipboard and focus checks passing; no write was acknowledged afterward.

The working app was restored immediately after verifying the saved audio; backup/live EXE, App DLL, and Core DLL hashes match. No functional fix has been deployed.

Read-only inspection of the installed Codex package found the `Do anything` placeholder and CSS `content:attr(data-placeholder)` on a `:before` pseudo-element in `webview/assets/app-primary-7fe7c6486695.css`. The saved Codex trace does not contain the actual surrounding text, so identifying its 12 characters as the placeholder remains an inference.

### Reproduced placeholder mechanism and source fix

The controlled local Chromium fixture at `publish/placeholder-fixture.html` reproduced the acknowledgement timeout using the unchanged production native insertion path. Evidence: `publish/placeholder-red-02/events.jsonl`. Before insertion, UIA exposed the 12-character placeholder/newline. After insertion, the entire document was exactly the 17-character probe payload and the caret was at its end. The old code continued expecting the placeholder as surrounding content and failed. ValuePattern also exposed the placeholder, so it was not a separate reliable source of actual content.

Experimental `WindowsTextTarget` now acknowledges this transition only on the first write, from an originally empty selection, when the entire current document is exactly the pending payload with a collapsed end caret. It then treats the initial surrounding decoration as gone. Subsequent writes retain document, selection, and focus guards; timeout and clipboard timing are unchanged. Two placeholder regression cases failed before the fix and pass afterward. Negative cases cover unexpected content, selected text, a nonempty original selection, and surroundings disappearing after a previously acknowledged write.

Native green validation was blocked when Computer Use stopped the turn because it could not determine the current browser URL confidently enough to enforce policy. No further computer input was attempted after that stop. The fix is not deployed to the daily app and full Codex hotkey acceptance remains unverified. The pinned recording and trace remain available for continued validation without repeated speech.

### Native follow-through after resuming

The short Chromium probe passed with the placeholder fix: `publish/placeholder-green-01/events.jsonl` records the disappearing decoration and all three acknowledged writes, and the native document showed exactly `Probe final text.`.

The diagnostic replay tool now accepts `--saved-insertion-probe <events.jsonl> <output-directory>`. It replays saved dictionary-corrected previews and normalized final text at their recorded cadence through the actual insertion path, requiring the controlled fixture's `editor` automation ID before writing. This is insertion evidence using saved recognition output, not another model run or full hotkey acceptance.

The full saved sequence exposed another failure: `publish/placeholder-saved-traced-02/events.jsonl` records `target.selection_mismatch` with document length 118, prefix 118, selection 0, suffix 0, while the requested selection length was 118. The document matched exactly. A later native observation showed the complete requested selection. Chromium had returned the old caret immediately after accepting Select.

The experimental target now waits for that selection acknowledgement only when the observed snapshot is exactly the pre-request snapshot. It does not reselect while waiting, rejects other changes, and times out after two seconds without pasting. Three regression tests failed before this fix and pass afterward. Clipboard and paste acknowledgement timing are unchanged.

The full saved sequence passed in `publish/placeholder-saved-green-03`: 69 updates over 56 seconds, no conflicts, final expected length 486. Native accessibility document text compared exactly equal to `expected.txt`. The delayed-selection path was exercised repeatedly and recovered. This run began with the prior fixture output selected; the earlier empty-placeholder probe separately passed. Independent review found no deployment blocker. The review-added assertion that polling never reselects also passed.

The tested package was copied to the canonical directory after stopping only its verified previous process. Exact normal launch: `Start-Process -FilePath 'C:\Users\stewa\projects\par-win-ptt\publish\ptt-dictation-win-x64\PttDictation.exe' -WorkingDirectory 'C:\Users\stewa\projects\par-win-ptt\publish\ptt-dictation-win-x64'`. One canonical process, matching binary hashes, startup trace, and capture helper PID 142356 were verified. This proves deployment and the controlled native insertion path; the user's Codex hotkey interaction remains the final manual acceptance gate. The saved failure WAV remains pinned with its original hash.

## Launch record

### Revisable previews after the user's 13:29 reply

The next natural Codex dictation completed with zero insertion conflicts and its ending was present before stop, but the preview contained `mon monkeys` and `bur burger`. The full recognition did not contain those duplicated fragments. This case is pinned at `publish/diagnostic-cases/codex-preview-20260907-132926`.

The microphone preview path now emits cumulative audio snapshots, beginning at two seconds and publishing every 0.8 seconds. Recognition results replace the prior preview directly so earlier guesses can change; the legacy overlapped-chunk path remains available for noncumulative inputs. The serial worker retains one active inference and only the newest pending cumulative snapshot, releasing superseded audio immediately. It rejects older/equal-duration snapshots because WAV publication tasks can arrive out of capture order. Snapshot PCM size grows with recording length; queuing is bounded rather than accumulating obsolete snapshots.

Three behavior tests cover correction of earlier fragments, coalescing and cleanup, out-of-order delivery while a newer snapshot is pending/active, and cancellation without late output. They exercise the actual WAV publication boundary. Full suite: 244/244 passed; the three tests passed again after extending them through that boundary. Independent review found the out-of-order race during implementation; the watermark guard and regressions address it.

Saved-audio evidence: `publish/preview-cumulative-replay` produced the final recognition wording in the live preview at 17.074 seconds, before the original stop at 18.998 seconds. Its first incorrect fragments were revised instead of appended. `publish/tail-cumulative-replay` recovered `at all` at 10.420 seconds, before the original stop at 14.5 seconds. These are local production recognition/workflow replays, not new user recordings or full native hotkey acceptance. Model misrecognitions can remain in both preview and final text; this change does not silently guess the intended wording.

The long cumulative replay completed with 48 previews, 18 obsolete snapshots superseded, maximum two owned audio snapshots, and no unexpected failures (`publish/long-cumulative-replay`). Average inference was 913 ms, maximum 2203 ms; newest-pending queue wait peaked at 782 ms. Thus publication still starts at two seconds and offers updates every 0.8 seconds, but long-recording recognition can take about two seconds per visible revision. No growing inference backlog was observed.

The cumulative stage was deployed normally and its process, all three binary hashes, startup tracing, and continuing capture helper were verified. Only the prior canonical app and its verified child recognition process were stopped. Both the prior experimental stage and original daily rollback remain available. No new spoken test is required for development; the user's ordinary subsequent use supplies live observations.

### Missing live tail after successful Codex insertion

The user's 13:20:09 UTC test completed without any target conflicts, but live raw two-second recognition omitted `at all`. Chunks 12 through 16 were empty; the full-recording pass recovered the phrase after stop at 14.5 seconds. The recording and isolated trace are pinned in `publish/diagnostic-cases/codex-tail-20260907-132009`; audio SHA256 `CFB4C3EBFDA61EE84D6B62600C4D7537637DE5963E1FD010326E5CB7C61DBE34`.

`publish/tail-context-comparison/context-results.json` compares the same saved audio with two-, four-, six-second and full-prefix context using the installed local model. Six-second context recovered the phrase by the ten-second audio boundary. Production replay with the changed buffer then emitted `at all` in `replay.preview_output` at 10.257 seconds, before stopping: `publish/tail-six-second-replay`. Recognition averaged 194 ms, maximum 250 ms, with zero queue delay for that replay.

The source change grows recognition lookback up to six seconds while preserving the two-second first publication and 0.8-second publication step. Actual overlap metadata grows with the window. PCM copies are bounded at 192 KB per chunk and the full recording remains intact. Existing overlap assembly is unchanged; preview errors such as `wor works` remain. This slice fixes the reproduced missing tail and does not claim general transcription accuracy is solved.

The original long recording also replayed successfully in `publish/long-six-second-replay`: 68 previews, average chunk processing 229 ms, maximum 297 ms, maximum queue wait 16 ms. Independent review found no actionable defect. The full suite passed 240/240 on rerun; the prior run had one transient file-sharing error in the existing security-test helper (`WaitForFileTextAsync`), with the other 239 tests passing. That helper was not changed.

The context fix is deployed with one canonical PID 140428, normal launch using the same exact command recorded above, no arguments, matching EXE/App/Core hashes, and verified startup tracing. Its recovery of the phrase is proven by saved-audio production replay; the user's next natural dictation is the remaining live observation. No additional recording was requested for development.

Helper was started with the following exact PowerShell command (hidden applies only to the background helper):

```powershell
Start-Process -FilePath 'C:/Users/stewa/projects/par-win-ptt/publish/ptt-dictation-capture-win-x64/PttDictation.Capture.exe' -ArgumentList '"C:\Users\stewa\AppData\Local\PttDictation"','"C:\Users\stewa\AppData\Local\PttDictation\diagnostics\capture"','"C:\Users\stewa\projects\par-win-ptt\publish\ptt-dictation-win-x64\PttDictation.exe"' -WorkingDirectory 'C:/Users/stewa/projects/par-win-ptt/publish/ptt-dictation-capture-win-x64' -WindowStyle Hidden
```

Do not launch PTT Dictation itself with `-WindowStyle Hidden`. To stop the helper, first identify its exact executable path and current PID, then stop only that process. PID values above are a snapshot, not reusable process identifiers.
