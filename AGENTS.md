# PTT Dictation agent instructions

- Default to dark mode first.
- For Discord/paste-friendly benchmark result displays, put model identity and quantization as text above the table, then keep the table columns focused on metrics such as context, wall time, prompt TPS, and generation TPS.

## Supported interaction and acceptance evidence

- PTT Dictation is tray-first. Do not use command-line arguments, a second app launch, single-instance activation, direct method calls, or `ToolStripMenuItem.PerformClick()` as acceptance evidence for a tray-menu bug unless the user explicitly asks to test that route.
- For a tray, menu, hotkey, or window defect, reproduce and accept the fix through the user's exact gesture on the canonical live executable. For Settings, right-click the actual PTT Dictation notification-area icon, click **Open Settings**, and verify exactly one visible, responsive Settings window.
- Lower-level tests may support diagnosis, but they do not replace the exact live interaction. Label evidence separately as code-path, layout, deployment, presentation, or user-interaction evidence.
- If available automation cannot perform the exact Windows interaction, report live acceptance as blocked. Finish safe source tests and deployment checks, then request one explicit manual verification. Never substitute a CLI or process/window check and claim the UI defect is fixed.
- Start the interactive app normally. Never use PowerShell `-WindowStyle Hidden` for PTT Dictation. Record the exact launch command and verify there is one canonical process running from `publish\ptt-dictation-win-x64` with no unexpected arguments.
- After a reported regression or a previous failed fix, get a red reproduction before changing code. Test from a clean app restart, then exercise both the first and second **Open Settings** invocation. Compare the working **Session History** tray item with **Open Settings** to isolate menu wiring from form lifetime.
- A successful build, test run, publish, hash comparison, process restart, screenshot, or `MainWindowHandle` check is not proof that the reported interaction works.
- Do not change unrelated timing, launch behavior, or UX while investigating unless the user authorizes it.

## Live Settings acceptance gate

Before saying the tray Settings defect is fixed, verify all of the following or explicitly state which item is blocked:

1. The exact executable path and process identity are known.
2. The app was launched normally, with no hidden-window option or unexpected arguments.
3. The actual notification-area icon was right-clicked.
4. The exact **Open Settings** item was clicked on both the first and second invocation, with exactly one visible, responsive Settings window each time.
5. Any unavailable observation is reported as blocked rather than replaced with proxy evidence.
