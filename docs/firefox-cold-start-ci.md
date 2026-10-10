# Firefox cold-start CI

Chrome now exercises the same insertion contract in a separate CI job. See
[browser cold-start CI](browser-cold-start-ci.md) for the shared coverage and
entry points; this document retains the Firefox-specific diagnosis.

The **Firefox cold start** job in `.github/workflows/ci.yml` runs on a disposable
GitHub-hosted Windows desktop. It launches the runner's actual Mozilla Firefox
executable with a new process and a fresh, isolated profile for each iteration.
It does not use a WinForms editor or a simulated accessibility provider.

The two test cases exercise a textarea and a contenteditable browser editor.
Each case performs three cold launches and two dictations per launch. The first
attempt exercises initial accessibility capture; the second checks reuse after
the first insertion. PTT's accessibility client starts before any Firefox process
and remains alive across all three browser restarts, matching cold-browser use
with PTT already running. No test code queries Firefox's accessibility provider
before the first dictation. Deterministic transcript updates drive the production
dictation workflow, UI Automation target capture and selection, clipboard paste,
and Windows input path. Right Ctrl is sent through the real low-level keyboard
hook. Independent DOM reports must match the exact resulting text and record
three trusted paste events per dictation. The second dictation must preserve
the first one's text through preview and final replacement. The actual
nonactivating preparation overlay participates in each recording.

Each replacement has a fixed 47-second CI acceptance bound. It was originally
derived from three 15-second response stages plus observation time, and remains
unchanged after adding bounded native-selection recovery. It is stricter than
the sum of every maximum product response stage, not an end-to-end latency
guarantee. The whole trial still has its
90-second deadline, initial capture still has ten seconds, and a failed workflow
fails the test immediately. Each preview boundary requires exactly one new
trusted paste and exact DOM text. Evidence records capture, preview, revision
and final latency, plus whether the provider-wait state occurred. That state
metadata is separate from waiting-window presentation, which the regular
visible UI tests verify. The former eight-second preview limit could expire
while PTT was correctly waiting for Firefox and then delivering the text once.

Live replacement keeps its clipboard payload until exact target readback
acknowledges the insertion, then restores the original clipboard only if its
sequence is still owned. Cancellation or failure after input submission leaves
the dictation payload in place: delivery is uncertain, and restoring unrelated
clipboard data beneath a queued paste would be unsafe. The next acknowledged
write can restore the saved original; a newer user copy always takes precedence.
No uncertain paste is resubmitted. Firefox's disconnected text-range error can
refresh the pattern on the original captured element once per recording; all
existing focus, text-partition, selection, and deadline checks still apply.
Acknowledgement reads the original document without asking permission to paste
again. Focus and held-modifier checks gate new writes, not readback of submitted
input. Native paste handles caret visibility; an additional accessibility
focus/scroll request is not part of acknowledgement, where it could block the
browser from processing the paste being observed.
Passive selection and write acknowledgement allow fifteen seconds for the
provider to respond. The write budget starts when paste submission returns,
after clipboard preparation and target revalidation. Polls never reset either
phase's deadline. Active selection recovery and unsent clipboard-read retries
retain their two-second limits. Exact document and selection checks remain
mandatory, and uncertain paste input is never resent.

Native stacks from the isolated runner showed Firefox's main thread blocked in
`StartupCache::GetBuffer` while a background `StartupCache::WriteToDisk` held the
same mutex during disk I/O. Accessibility snapshots could remain stale briefly
after that pause ended. An already captured target now shows a nonactivating
waiting status after a single operation blocks for five seconds; recording and
cancellation remain responsive. The normal fifteen-second operation ceiling is
checked by the timer and again before writing or acknowledging a late result.
A later cold-run failure recorded a single native selection request taking
25.828 seconds. The initial recovery change allowed a selection request begun
during the current ready-target operation and spanning its original
fifteen-second deadline to enter one
recovery window, bounded at thirty seconds from that
operation's original start. Its explicit progress state survives return, so a
late timer cannot bypass the ceiling. The request is never replayed by this
recovery, and its allowance cannot carry into a later operation or extend passive
acknowledgement polling. Native input generation, original focus, full document
partition and exact selection must still match before paste; input during the
wait or final validation revokes write permission. Thirty seconds is a finite
response policy informed by the observed stall, not a guarantee that arbitrary
provider hangs can recover. The CI observation limits remain 47 seconds per
replacement and 90 seconds per trial. Initial
cold readiness and focus-return limits remain five seconds. After an exact
prepared editor becomes ready within that first limit, final validation has one
five-second phase, with an absolute ten-second preparation ceiling. These are
separate phase limits, not a total dictation deadline.

The October 10 post-merge run at `49e40a5` exposed a second recovery gap:
[Firefox's textarea test failed](https://github.com/DiscoStew6082/ptt-dictation/actions/runs/38083297753/attempts/1)
after selection requery produced the exact owned range in 19 milliseconds, but
the original element's `HasKeyboardFocus` read then blocked for 19.368 seconds.
The watchdog expired at 15.047 seconds. This was a focus-property read, not a
focus-changing request, and no profile-cleanup lock error occurred. The retry
passed with the same source, including slower focus reads of roughly one to
three seconds; that establishes intermittence, not a repair. Failure-only
native workers exhausted their eight-second lifetimes without producing dumps,
so these artifacts do not establish the browser-side cause of this particular
stall. Earlier startup-cache stacks remain evidence for their earlier runs.

Original-element focus reads now qualify for the same thirty-second absolute
ready-operation ceiling only when explicit monotonic progress shows a fresh
read spanning that operation's fifteen-second deadline. Completion evidence is
retained so a delayed watchdog cannot reject a qualifying returned read or let
a result beyond thirty seconds write. The allowance does not reset, carry into
the next operation, or extend a generic provider hang or passive write
acknowledgement. A native input baseline is captured before ready-operation
provider work and checked again before paste, so input during the blocked read
cannot be adopted as new insertion intent.

Passive selection acknowledgement ends when the exact document partition and
owned range are observed within its existing fifteen-second deadline. A later
focus read cannot retroactively expire that already completed phase. Clipboard
submission still requires a fresh read of the original prepared range, fresh
original-element focus verification, unchanged native input and a current
uncancelled session within the absolute operation ceiling. No identity is
inferred from cached UIA runtime IDs, and no uncertain paste is replayed.

Diagnostic workers flush bootstrap stages before helper compilation. Prepared
workers can capture a slow read while it is occurring; failure-only workers
retain their bounded lifetimes and now reveal whether native capture ever
began. Diagnostic collection never changes a failed test to a passing one.

### October 10 live mutex investigation

[Investigation run 38086883821](https://github.com/DiscoStew6082/ptt-dictation/actions/runs/38086883821)
tested commit `2a8408a` on top of `666bccb` with four independent Windows
runners and Firefox 156.0.1. All 24 fresh launches and 48 dictations passed.
Ordinary [CI](https://github.com/DiscoStew6082/ptt-dictation/actions/runs/38086883573)
and [CodeQL](https://github.com/DiscoStew6082/ptt-dictation/actions/runs/38086883598)
also passed on attempt one. Passing tests alone do not identify the wait's cause;
the live captures do.

Each runner captured one original-element `HasKeyboardFocus` read for each
editor. The call was already pending for at least one second before capture.
Every one of the eight captures retained the same pending call across two
paired testhost/Firefox snapshots; all 32 dumps succeeded. Metadata samples
before and after each dump preserve operation name, managed thread ID and
monotonic inferred start time, so a subsequent same-name call cannot stand in
for the triggering call.

All 16 Firefox snapshots show the main thread in
`StartupCache::GetBuffer -> MutexImpl::lock -> FutexImpl::wait -> WaitOnAddress`.
In each pair, the same background writer thread remains in
`StartupCache::WriteToDisk -> PR_Write -> WriteFile -> NtWriteFile`.
All 16 testhost snapshots show the named dictation automation thread in
`UiaGetPropertyValue -> CrossProcess_GetPropertyValues -> WaitForResponse`.
Wait-chain inspection additionally connects testhost COM dispatch to the exact
recorded Firefox main thread in the textarea captures from runners two and
three. These are observed live waits, not a cause inferred from an expired timer.

The [matching 156.0.1 release source](https://github.com/mozilla-firefox/firefox/blob/19a90052d7869f7b194ff27d316520366ad1d592/startupcache/StartupCache.cpp)
acquires `mTableLock` in `GetBuffer` and holds that lock around the background
`WriteToDisk` call. Its lambda at line 795 matches the source location in the
decoded writer stacks. Thus the shared startup-cache lock relationship is
supported by the matching source and repeated stacks. The minimal dumps do
not contain a directly readable mutex owner field: Gecko uses a futex-backed
mutex, and `!locks`/`!cs` cannot establish its owner from an RTL critical-section
list. Keep that source-based ownership inference distinct from the directly
observed wait and writer stacks. Mozilla's
[upstream optimization discussion](https://bugzilla.mozilla.org/show_bug.cgi?id=2048221)
also proposes separating table locking from disk I/O; its attempted patch was
backed out and is not an app-side fix.

The calls recovered and all insertions completed. This establishes startup-cache
mutex contention during the captured focus reads, not a permanent deadlock,
and it does not retroactively prove the cause of the earlier uncaptured
19.368-second read or the preparation deadline collision. Native dump collection
can pause the target; the first testhost dumps took up to 2.72 seconds, so these
instrumented call durations are not baseline latency measurements. No
profile-cleanup lock error occurred in this investigation.

[The repeat run at `2ddb48d`](https://github.com/DiscoStew6082/ptt-dictation/actions/runs/38087298611)
passed three diagnostic samples and failed one during final preparation.
Ordinary CI and CodeQL passed on the same head. The failed sample's first
testhost dump ran from 21:23:19.679 to 21:23:24.803 UTC, taking 5.124 seconds;
the five-second final-validation deadline expired during that pause, and the
failure was logged as the dump finished. The focused-element query was already
pending for 1.434 seconds before that dump. Both Firefox snapshots still show
the startup-cache mutex wait and background writer, but the resulting
10.745-second query duration cannot be classified as uninstrumented latency.
The diagnostic failure remains in the run history; it does not justify raising
the app's preparation deadline.

The prepared collector now executes its first native testhost dump before
Firefox launch, in addition to preparing the helper and loading `dbghelp`.
This uses the exact owned testhost identity, records warmup timing, and removes
the warmup dump before publishing readiness. This addresses the observed cold
dump cost outside the timed browser experiment; it does not eliminate every
possible observation effect. Trial dump durations remain recorded.

[The warmup verification at `d5e2823`](https://github.com/DiscoStew6082/ptt-dictation/actions/runs/38087714280)
passed three diagnostic samples and failed one at the next editor's cold-process
gate, after all three textarea trials passed. That editor's first gate recorded
no Firefox processes at 21:29:25.559 UTC; its launch gate found three processes
at 21:29:33.544, after automation initialization and collector warmup but before
launching its own browser. This is not evidence that the three processes belonged
to the preceding browser job, and no profile-deletion lock error was recorded.
The strict gate refused to proceed rather than terminate unowned processes.
Ordinary CI and CodeQL passed on the same head. The failure remains available
for comparison with subsequent ownership diagnostics.

Browser teardown now records exact job membership, process start time, image
path and creator PID before terminating the owned job. It retains handles to
those verified members and requires both their exit signals and zero active
job processes within the existing shared five-second cleanup limit. Cold-process
gate observations also retain process identity and creator PID; metadata errors
do not relax the gate. These checks harden exit evidence without claiming that
the earlier unexplained processes were owned, extending deadlines, or killing
unrelated browsers. A native parent/child regression verifies that both owned
processes exit while the testhost remains outside the job.

The **Firefox lock investigation** workflow is manually available after merge;
its four-sample matrix also runs on the investigation PR branch. It prepares
the debugger and collector before browser launch, watches only relevant focus
and preparation calls, saves the caller first, takes a second pair after a
bounded interval, and records pending-call timing before optional wait-chain
inspection. Exact PID, start time and process name are checked before every
dump. Raw and decoded artifacts are retained for 14 days. This opt-in workflow
does not change runtime policy or ordinary browser acceptance criteria.

### Preparation handoff

Preparation retains the exact editor and selection obtained while the cold
provider becomes ready. After refinement, a strict native focus/input guard,
fresh validation of that same editor's selection, and queued native-event
confirmation all finish before microphone startup. The recording adopts that
prepared target without repeating provider capability discovery. The original
preparation phase deadline and intent checks remain active through this handoff;
changed identity, selection, or cancellation cannot select a replacement editor.

[The post-merge run at `b47a84d`](https://github.com/DiscoStew6082/ptt-dictation/actions/runs/38085154591)
exposed a separate preparation deadline collision. The textarea target and
consistent selection were captured at 295 milliseconds; the strict native
capture boundary began at 1.090 seconds. Final provider validation returned at
5.048 seconds, after the shared five-second timer had already rejected startup.
The browser's DOM stayed focused and untouched, and neither recording nor
paste began. These artifacts identify the phase and elapsed time, but not
which individual provider call accounted for the interval.

The two preparation phases now have explicit monotonic deadlines. Only timely
readiness of a retained exact target permits the one final-validation phase;
unready polling, a missing target, or expired readiness cannot renew it.
The final checks retain the pinned readiness identity, original complete
selection and native intent. Late timers and queued UI confirmations still
respect the phase deadline and the ten-second absolute ceiling. Preparation
traces include phase boundaries and readiness provider calls to identify future
stalls without treating a timeout as proof of a particular Firefox lock.

UI Automation identity capture and target validation run on the serialized,
windowless MTA worker, following
[Microsoft's UI Automation threading guidance](https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-threading).
Clipboard API calls run on a separate STA with a message loop. The clipboard
thread never calls back into a textbox provider while the automation worker
waits for it. The original native focus guard still starts at the hotkey boundary.

The browser is cold at the process and profile level. This is not a reboot or a
claim that the operating system file cache is empty. The test uses an isolated
local page, not a signed-in ChatGPT account. Recognition output is deterministic;
the microphone, speech models, and external recognition services are outside
this test. Production components run inside the test host, so app startup,
configuration loading and tray wiring are outside this gate. This provides
real Firefox integration evidence, not acceptance of
the user's Firefox profile, extensions, website state, or physical keyboard.
Simultaneously starting PTT and Firefox is also outside this cold-browser gate;
earlier harness runs exposed client-initialization delays in that different
scenario. Those failed runs remain available in the CI history.

The disposable profile suppresses first-run onboarding using
`browser.preonboarding.enabled=false`, matching
[Mozilla's common test profile](https://github.com/mozilla-firefox/firefox/blob/main/testing/profiles/common/user.js).
It does not force accessibility on or attach a browser automation protocol before
the first dictation. The first CI execution correctly failed on Firefox's welcome
modal before this profile setting was added; its saved screenshot identified the
setup failure rather than allowing a skipped browser check.

The entry point is `scripts/Test-FirefoxColdStart.ps1`. It refuses to run outside
a GitHub-hosted Windows runner. The test additionally requires explicit opt-in
and a valid Firefox executable. An ordinary local test run skips this category
without opt-in; explicit opt-in on a non-runner fails before any browser action.
The ordinary CI test job excludes the
`FirefoxColdStart` category so an opt-out or skipped integration test cannot
masquerade as Firefox coverage. The dedicated job requires exactly two passed
test cases, zero skipped cases, and a new TRX result. Missing Firefox, absent
results, failed focus, or a test timeout fails the job.

Every attempt uploads `firefox-cold-start-evidence-<attempt>` for 14 days, including its TRX
and the available browser evidence. `runner.json` records the Firefox version,
runner image, source commit, and run/attempt identity. Failed trials also retain
the browser for ten seconds to collect DOM observations and bounded Windows
wait-chain diagnostics before teardown. Native diagnostics run in a separate,
bounded process and save minimal Firefox/testhost stack dumps before querying
wait chains. A failure-only CI step installs Microsoft's debugger tools on the
disposable runner and resolves stacks using Microsoft and Mozilla symbols;
raw dumps and partial analysis remain available if symbols time out. This
observation never retries a dictation or changes a failed result. Firefox tracks the GitHub runner
image instead of a pinned browser version; version metadata makes a runner or
browser update visible when investigating a regression. The job runs on pushes
and pull requests targeting `master` or `main`.

Do not run the script locally by forging its CI environment variables. The
runner-only boundary keeps this automation away from a user's browsing session.
