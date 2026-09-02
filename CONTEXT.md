# PTT Dictation

PTT Dictation turns a user-controlled microphone recording into locally transcribed text pasted into the app where recording began.

## Language

**Dictation workflow**:
A single accepted recording attempt from start through one terminal outcome: pasted text, an empty transcript, cancellation, or failure. Hold-to-talk and toggle-to-talk are trigger modes for the same workflow.
_Avoid_: Hotkey flow, recording session

**Trigger mode**:
The way a user controls the start and finish of a dictation workflow: hold-to-talk or toggle-to-talk.
_Avoid_: Hotkey type

**Dictation state**:
The user-observable stage of a dictation workflow, from idle through recording and processing to a terminal outcome. Transcript and processing detail belong to the current state rather than to separate notices.
_Avoid_: UI status, controller state

**Dictation cancellation**:
A user decision to abandon an active dictation workflow during recording or processing. Cancellation discards the workflow without pasting text or adding it to history.
_Avoid_: Paste cancellation
