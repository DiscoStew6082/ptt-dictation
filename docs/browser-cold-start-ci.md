# Browser cold-start CI

The **Firefox cold start** and **Chrome cold start** jobs run the same insertion
contract on separate disposable GitHub-hosted Windows desktops. Each uses the
runner's installed browser, with a fresh process and isolated profile for each
of three launches per editor type. Both textarea and contenteditable editors
receive two dictations per launch: six cold launches and twelve dictations per
browser per CI run.

The shared harness exercises PTT's production capture, workflow, UI Automation,
native Right Ctrl hook, clipboard and Windows input path. Only microphone and
recognition are replaced with deterministic transcript updates. A dark local
page independently reports exact DOM text and trusted input/paste events. Early
preview, revised preview and final text must each cause exactly one paste; the
second dictation must preserve the first. No WebDriver, DevTools connection or
forced browser accessibility setting primes the browser before the first hold.

PTT's accessibility client starts before the browser and remains running across
the three restarts. Initial capture retains its ten-second test limit, each
replacement has a 47-second observation allowance, and the whole trial is
limited to ninety seconds. The 47-second limit is a strict CI acceptance bound,
not the sum of every maximum product response stage; it remains unchanged with
bounded native-selection recovery. A failed workflow fails the test immediately.
These are not latency guarantees. See the [Firefox diagnosis and response policy](firefox-cold-start-ci.md)
for the startup-stall failures that motivated these checks.

`scripts/Test-FirefoxColdStart.ps1` and `scripts/Test-ChromeColdStart.ps1` call
the shared `scripts/Test-BrowserColdStart.ps1` entry point. Both scripts and
the harness require explicit browser opt-in on a GitHub-hosted Windows runner.
Missing browsers, missing results, skipped cases, or incomplete per-launch
evidence fail the job. Do not forge the CI environment to run this on a user's
desktop. The ordinary and release test suites exclude both browser categories.

Each job uploads `<browser>-cold-start-evidence-<attempt>` for fourteen days.
It includes the browser version, runner image, source revision, TRX result,
launch identity, exact DOM observations, capture/replacement latencies and
provider-wait metadata. Failures retain available screenshots and diagnostics.
Firefox's optional native stack diagnostics remain Firefox-specific.

This covers actual browser processes, not signed-in ChatGPT/Bing sessions,
personal profiles, extensions, physical microphone input, app startup or tray
wiring. Cold means a fresh browser process and profile, not an OS reboot or an
empty filesystem cache. Runtime changes found by either job must also pass the
other browser's gate before deployment.
