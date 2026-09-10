# Live textbox checkpoint

The main checkout includes the live textbox implementation previously developed in a separate local worktree. Build and edit the main checkout for subsequent changes.

## Current behavior

- Cumulative recognition previews can revise earlier guesses. One active inference and only the latest pending snapshot bound the queue.
- UI Automation identifies the original editor and checks the selected range and surrounding text before each replacement. Focus changes pause insertion without forcing focus back.
- The final recognition replaces the dictated range. Failed insertion keeps available text in Session History for recovery.
- A captured UI Automation element that is no longer available fails insertion instead of being treated as temporary focus loss. Finishing then leaves Processing, retains the final transcript, and releases the workflow for another recording. Temporary focus loss pauses insertion during recording; after stopping, focus has a five-second grace period before the workflow fails and retains the result in history.
- Trailing dictated text is located from the document end instead of relying on a prefix search across a pasted link. Provider character movement is checked against exact text, and the complete prefix/selection/suffix partition is verified before selecting. The existing post-selection and paste acknowledgement checks remain required.
- Recording and processing do not show a floating overlay. Existing status sounds and cancellation remain available.
- A clipboard read failure before keyboard input can retry only while its sequence and selected range remain valid. The existing two-second deadline bounds this recovery. Ambiguous submitted pastes never retry as a duplicate fallback.
- Clipboard restoration keeps its existing 750 ms delay.

## Validation and limits

Regression coverage exercises cumulative revisions, delayed selection and paste acknowledgements, placeholder disappearance, clipboard-read recovery, focus and selection changes, cancellation, history recovery, and overlay suppression.

The saved failing insertion sequence was replayed through the production Windows accessibility and clipboard workers in an empty Notepad test tab. One deliberately injected clipboard read failure recovered; all 28 updates completed, and the final 120-character document matched the expected transcript exactly. This is controlled native insertion evidence, not complete Codex hotkey acceptance.

On 2026-09-10, live Codex attempts reproduced one-character-short selection ranges after a pasted link and indefinite finalization when the captured textbox disappeared or remained unfocused. Regression tests failed before the corresponding fixes; all 269 Release tests pass afterward. Coverage includes preservation of the link across revisions, provider character units for emoji/combining marks/CRLF, refusal of unverifiable ranges, final-transcript retention, and the ability to start another recording after failure. The updated package was installed at the permanent path and all 464 package hashes were verified.

Final microphone/hotkey acceptance of the link fix in Codex is pending the user's live check. Test results and deployment hashes do not substitute for that interaction.

Local recordings, detailed diagnostic traces, deployment backups, and machine-specific investigation notes are excluded from this checkpoint. See the README and security policy for the development build's local diagnostic retention behavior.
