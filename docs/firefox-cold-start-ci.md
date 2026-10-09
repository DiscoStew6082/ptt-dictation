# Firefox cold-start CI

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
cancellation remain responsive. A fifteen-second operation ceiling is checked
by the timer and again before writing or acknowledging a late result. Initial
capture/preparation and focus-return limits remain five seconds. These are
separate phase limits, not a total dictation deadline.

Preparation retains the exact editor and selection obtained while the cold
provider becomes ready. After refinement, a strict native focus/input guard,
fresh validation of that same editor's selection, and queued native-event
confirmation all finish before microphone startup. The recording adopts that
prepared target without repeating provider capability discovery. The original
preparation deadline and intent checks remain active through this handoff;
changed identity, selection, or cancellation cannot select a replacement editor.

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
