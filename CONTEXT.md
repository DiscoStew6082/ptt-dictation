# PTT Dictation

PTT Dictation turns a user-controlled microphone recording into locally transcribed text inserted into the selected textbox where recording began. Supported editors receive revisable live text followed by a final replacement. Recording and processing do not open a floating transcript overlay.

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

**Dictation presentation**:
The sounds, visible status, controls, and history feedback through which the current dictation state is conveyed to the user.
_Avoid_: UI state, workflow view

**Dictation cancellation**:
A user decision to abandon an active dictation workflow during recording or processing. Cancellation stops further insertion without undoing text already inserted or adding a history entry.
_Avoid_: Paste cancellation

**Correction draft**:
An unsaved phrase and replacement being added or used to edit a selected correction rule. A complete draft participates in the correction preview; saved rules become active for new dictations.
_Avoid_: Pending replacement, temporary rule
