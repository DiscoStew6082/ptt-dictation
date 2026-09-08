# Live textbox checkpoint

The main checkout includes the live textbox implementation previously developed in a separate local worktree. Build and edit the main checkout for subsequent changes.

## Current behavior

- Cumulative recognition previews can revise earlier guesses. One active inference and only the latest pending snapshot bound the queue.
- UI Automation identifies the original editor and checks the selected range and surrounding text before each replacement. Focus changes pause insertion without forcing focus back.
- The final recognition replaces the dictated range. Failed insertion keeps available text in Session History for recovery.
- Recording and processing do not show a floating overlay. Existing status sounds and cancellation remain available.
- A clipboard read failure before keyboard input can retry only while its sequence and selected range remain valid. The existing two-second deadline bounds this recovery. Ambiguous submitted pastes never retry as a duplicate fallback.
- Clipboard restoration keeps its existing 750 ms delay.

## Validation and limits

Regression coverage exercises cumulative revisions, delayed selection and paste acknowledgements, placeholder disappearance, clipboard-read recovery, focus and selection changes, cancellation, history recovery, and overlay suppression.

The saved failing insertion sequence was replayed through the production Windows accessibility and clipboard workers in an empty Notepad test tab. One deliberately injected clipboard read failure recovered; all 28 updates completed, and the final 120-character document matched the expected transcript exactly. This is controlled native insertion evidence, not complete Codex hotkey acceptance.

The final live Codex check was interrupted by the user stopping Computer Use. Exact microphone/hotkey acceptance in Codex remains unverified. Test results and deployment hashes do not substitute for that interaction.

Local recordings, detailed diagnostic traces, deployment backups, and machine-specific investigation notes are excluded from this checkpoint. See the README and security policy for the development build's local diagnostic retention behavior.
