# Project status

Last user-confirmed dictation baseline on October 26, 2026: [`e19dff4`](https://github.com/DiscoStew6082/ptt-dictation/commit/e19dff463fafb734ccfd06e71735ef6d0365f4bd). The later maintenance cleanup below preserves its active dictation behavior. Use this page to distinguish completed work from the dated investigation records elsewhere in `docs/` and `DIAGNOSTICS.md`. Historical test totals describe their named baseline.

## October 26 maintenance cleanup

- One installation engine in `scripts/Install-PttDictation.ps1` now handles public downloads, offline archives, staged local builds, verification, and recovery. `Update-LocalApp.ps1` is a compatibility wrapper. The existing installation destination is preserved when supplied with `-InstallDirectory`.
- Removed unused chunk-publishing/buffering code, unused deconstruction compatibility, unreachable presentation fallback, and their exclusive tests. Active dictation, cancellation, waiting feedback, history, and error presentation are retained.
- Repaired missing-event, Unicode, retention-capacity, and audio-ownership assertions; consolidated STA test helpers and bounded test cleanup.
- Retired historical benchmark/context experiments and the proactive Firefox lock investigation. Their last source is recoverable at `679eeb9`; ordinary browser regression checks and failure diagnostics remain.
- CI and release validation now run the shipped Qwen worker's 15 model-free Python tests. Superseded CI runs are cancelled; only documentation-only pull requests skip browser jobs. Main-branch pushes always validate browsers.
- Local source validation: 635 .NET tests passed, 34 visible-UI cases deferred to the disposable CI desktop, and all 15 Python worker tests passed. Synthetic replay also passed. Installer validation covers 32 staged-update cases, 18 archive/download cases, and five snapshot checks.

Source validation is separate from installation and microphone acceptance. The stable installation's `deployment-receipt.json` records its installed package hashes. The published `v0.1.3` assets below predate this cleanup; source changes do not replace an already published release.

## Completed and verified

- PRs [#49](https://github.com/DiscoStew6082/ptt-dictation/pull/49), [#50](https://github.com/DiscoStew6082/ptt-dictation/pull/50), and [#51](https://github.com/DiscoStew6082/ptt-dictation/pull/51) are merged. They cover audio snapshot ownership and single-use paste authorization, synchronized NAudio dependency locks, and the shared authenticated Qwen LAN service.
- The merged application was installed through `Update-LocalApp.ps1` at this machine's existing stable `publish/ptt-dictation-win-x64/PttDictation.exe` path. The deployment receipt, all 604 package hashes, and one normally launched process without arguments were verified. Other installations should preserve their own selected stable path.
- Local Release validation passed 641 tests; 39 visible/browser cases were deferred to the disposable CI desktop. [Post-merge CI](https://github.com/DiscoStew6082/ptt-dictation/actions/runs/40385481386) passed 676 Windows tests plus real Chrome/Firefox cold-start checks. [CodeQL](https://github.com/DiscoStew6082/ptt-dictation/actions/runs/40385481228) passed.
- All 16 real-microphone probe cases and 31 isolated deployment scenarios passed. The microphone probe checked cumulative preview/final-audio prefix equality across two recordings and stale snapshot rejection. The installed Qwen endpoint transcribed the public speech fixture correctly and left no request audio behind.
- After installation, the user confirmed two successive dictations and another dictation immediately after cancellation: all three inserted correctly, and cancellation produced no duplicate or late text. This closes ordinary desktop dictation/cancellation acceptance for that build. It does not certify every historical editor-specific edge case or phone interaction.
- The Firefox CI harness now allows transient unowned processes to exit naturally within a fixed twenty-second startup budget, then performs a fresh strict check immediately before launch. Persistent, unknown-state or late observations still fail; no unowned process is terminated and no failed dictation is retried. See [browser CI](browser-cold-start-ci.md). This is a test-harness change; the application baseline above is unchanged.
- Repository cleanup removed six obsolete remote branches (four matching merged trees, one merged branch and one superseded SDK update), two merged local branches, and the unpublished `v0.1.0` draft release. Published releases and deployment rollback backups were retained.
- [Release v0.1.3](https://github.com/DiscoStew6082/ptt-dictation/releases/tag/v0.1.3) publishes all these fixes from [`45333ff`](https://github.com/DiscoStew6082/ptt-dictation/commit/45333ff70a3e4972ea83e6ef2f13022769582b42), plus verified installer backup/rollback, fully qualified destination checks, and owned-worker cleanup. It is the latest stable release. [CI](https://github.com/DiscoStew6082/ptt-dictation/actions/runs/40390687561), [CodeQL](https://github.com/DiscoStew6082/ptt-dictation/actions/runs/40390687692), and the [release build](https://github.com/DiscoStew6082/ptt-dictation/actions/runs/40390730203) passed. The installer passed 18 failure/success scenarios under both Windows PowerShell 5.1 and PowerShell 7. The actual public latest-release downloads passed checksum, source revision, SBOM, package-content and signed-provenance verification; the ZIP contains 604 files and .NET 10.0.14.

## Remaining work and priority

| Item | Status and next step |
| --- | --- |
| Android/S25 voice keyboard | The PC service is implemented and deployed. A real phone recording followed by insertion into an Android text field remains unverified. Finish this only if phone dictation is still wanted; PC-local API success is not phone acceptance. |
| Launch at Windows sign-in | Check once at the next normal sign-in if automatic startup is needed. Normal installer restart and a valid shortcut do not prove sign-in behavior. |
| Code signing | The app and installer are unsigned. Consider signing for broad distribution; it is optional for the working local installation. |

## Parked investigations

- **Empty Parakeet previews:** the [September investigation](research/parakeet-empty-preview-2026-09-23.md) did not establish a reproducible cause. Reopen it on a new occurrence with targeted evidence. Ordinary successful dictation does not prove the historical cause was fixed, and no extra audio retention is implied.
- **Qwen live previews:** the [same-audio evaluation](research/qwen-chunking-evaluation-2026-09-23.md) supported the existing full-recording Qwen final pass, not replacing Parakeet previews with frequent Qwen retranscription. There is no approved preview rewrite outstanding.
- **Old validation notes:** retain their dated evidence, but do not treat every historical “pending” or “not deployed” statement as current unfinished implementation. Reproduce a specific recurring defect before reopening its investigation. A historical scenario without direct acceptance stays unverified; it does not automatically justify another implementation change.

Deployment backups, private diagnostic recordings, and ignored experiment output are recovery/evidence artifacts, not alternative launch locations or outstanding features. Keep them out of source control and public release packages.
