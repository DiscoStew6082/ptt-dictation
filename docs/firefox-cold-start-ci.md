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

Every run uploads `firefox-cold-start-evidence` for 14 days, including its TRX
and the available browser evidence. `runner.json` records the Firefox version,
runner image, source commit, and run identity. Firefox tracks the GitHub runner
image instead of a pinned browser version; version metadata makes a runner or
browser update visible when investigating a regression. The job runs on pushes
and pull requests targeting `master` or `main`.

Do not run the script locally by forging its CI environment variables. The
runner-only boundary keeps this automation away from a user's browsing session.
