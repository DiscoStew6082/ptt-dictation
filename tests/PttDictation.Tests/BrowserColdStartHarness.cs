using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using PttDictation.App;
using PttDictation.Core;

namespace PttDictation.Tests;

// Deliberately CI-only: never attaches to the user's browser or sends input on
// their desktop. The browser, UIA, hook, clipboard and paste are real. Only the
// speech recognizer is replaced with a deterministic transcript source.
internal sealed class BrowserColdStartHarness(TestContext testContext, ColdStartBrowser browserKind)
{
    // Observe the bounded pre-send, selection and acknowledgement stages rather
    // than expiring before the application's own response policy. The unchanged
    // trial deadline still bounds the whole run; a workflow failure fails at once.
    private const int ReplacementObservationMilliseconds =
        (int)(3 * WindowsTextTarget.ResponseTimeoutMilliseconds) + 2000;

    private TestContext TestContext => testContext;
    private string BrowserName => browserKind.ToString();
    private string EnvironmentName => BrowserName.ToUpperInvariant();

    public async Task RunAsync(string editorKind)
    {
        if (Environment.GetEnvironmentVariable($"PTT_RUN_{EnvironmentName}_COLD_START_TESTS") != "1")
            Assert.Inconclusive($"{BrowserName} integration runs only in its dedicated GitHub-hosted Windows CI job.");
        Assert.AreEqual("true", Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "CI-only test.");
        Assert.AreEqual("github-hosted", Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT"), "Disposable runner required.");
        Assert.AreEqual("1", Environment.GetEnvironmentVariable($"PTT_RUN_{EnvironmentName}_COLD_START_TESTS"), "Explicit browser CI opt-in required.");
        var executable = Environment.GetEnvironmentVariable($"PTT_{EnvironmentName}_PATH");
        Assert.IsTrue(Path.IsPathFullyQualified(executable ?? "") && File.Exists(executable), "Installed browser executable is required.");
        var output = Path.Combine(Environment.GetEnvironmentVariable($"PTT_{EnvironmentName}_ARTIFACTS")
            ?? throw new InvalidOperationException("Artifact directory required."), editorKind);
        Directory.CreateDirectory(output);
        TestContext.WriteLine($"{BrowserName} {FileVersionInfo.GetVersionInfo(executable!).ProductVersion}; executable={executable}");
        using var diagnostics = DiagnosticTrace.Configure(Path.Combine(output, "diagnostics"), $"{BrowserName.ToLowerInvariant()}-cold-start-ci");
        await OnStaAsync(async () =>
        {
            // Start PTT before any process of the browser under test exists.
            // Transient Firefox background work may exit naturally; never kill
            // an unowned process or start the test while one is still running.
            await WaitForNoExistingBrowserAsync(browserKind, output, CancellationToken.None);
            using var overlay = new StatusOverlayForm();
            using var paster = new LiveClipboardPaster(() => overlay.IsHandleCreated ? overlay.Handle : IntPtr.Zero);
            var started = Stopwatch.StartNew();
            var eventsPath = Path.Combine(output, "diagnostics", "events.jsonl");
            while (true)
            {
                await DiagnosticTrace.FlushAsync();
                if (File.Exists(eventsPath))
                {
                    using var stream = new FileStream(eventsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(stream);
                    if ((await reader.ReadToEndAsync()).Contains("\"stage\":\"accessibility.listening_started\"", StringComparison.Ordinal)) break;
                }
                Assert.IsLessThan(20000L, started.ElapsedMilliseconds, "PTT accessibility client startup failed before browser launch.");
                await Task.Delay(25);
            }
            // Preparation occurs before the owned browser exists and performs
            // no accessibility or browser work. Opt-in diagnostics only.
            await using var slowCollector = browserKind == ColdStartBrowser.Firefox
                ? await FirefoxSlowCallCollector.StartIfEnabledAsync(output) : null;
            for (var trial = 1; trial <= 3; trial++)
            {
                var trialOutput = Path.Combine(output, $"trial-{trial}");
                Directory.CreateDirectory(trialOutput);
                await RunTrialAsync(executable!, editorKind, trialOutput, paster, overlay, slowCollector);
                await DiagnosticTrace.FlushAsync();
            }
        });
    }

    private async Task RunTrialAsync(string executable, string editorKind, string output,
        LiveClipboardPaster paster, StatusOverlayForm overlay, FirefoxSlowCallCollector? slowCollector)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = deadline.Token;
        var fixture = BrowserEditorFixture.Start(editorKind);
        var browser = new OwnedBrowser(browserKind, executable, fixture.Url, output);
        using var stopSlowMonitor = new CancellationTokenSource();
        Task? slowMonitor = null;
        var sessions = new KnownTranscripts();
        var history = new SessionHistory();
        var ui = SynchronizationContext.Current!;
        var workflow = new DictationWorkflow(sessions, paster, history, synchronizationContext: ui);
        var hotkey = new GlobalHotkeySource(DictationHotkey.RightControl, DictationHotkey.F9);
        Exception? dispatchError = null;
        var presses = 0;
        var releases = 0;
        var keyDown = false;
        var providerWaitObserved = 0;
        void ObserveProviderWait()
        {
            if (paster.WaitingForTargetResponse) Interlocked.Exchange(ref providerWaitObserved, 1);
        }
        paster.PresentationChanged += ObserveProviderWait;
        void Dispatch(DictationIntent intent) => ui.Post(async _ =>
        {
            try { await workflow.HandleAsync(intent, token); }
            catch (Exception error) { dispatchError = error; }
        }, null);
        hotkey.Pressed += () => { Interlocked.Increment(ref presses); Dispatch(DictationIntent.BeginHold); };
        hotkey.Released += () => { Interlocked.Increment(ref releases); Dispatch(DictationIntent.EndHold); };
        workflow.StateChanged += state =>
        {
            // Include the actual nonactivating preparation window that previously
            // generated a false foreground event during cold browser capture.
            if (state.Phase == DictationWorkflowPhase.Preparing)
                overlay.ShowStatus(DictationStatusCatalog.Preparing);
            else overlay.Hide();
        };
        var observations = new List<object>();
        Exception? trialFailure = null;
        try
        {
            hotkey.Start();
            await browser.StartAsync(token);
            await Until(() => browser.TryActivate(), "browser native foreground window", 20000);
            // Only the page's DOM reports readiness. No test UIA query, WebDriver
            // attachment, provider probing, or arbitrary warmup delay precedes PTT.
            await Until(() => fixture.Latest is { Focused: true, Ready: true }, "browser editor DOM focus", 20000);
            browser.BindReadyWindow();
            slowMonitor = slowCollector?.WatchTrialAsync(browser.WaitChainTarget, GetCurrentThreadId(), output,
                stopSlowMonitor.Token);
            var clicksBefore = fixture.Latest!.TrustedClicks;
            browser.ClickEditor(fixture.Latest);
            await Until(() => fixture.Latest is { Focused: true } state && state.TrustedClicks > clicksBefore,
                "native click establishes browser caret", 5000);
            Assert.IsTrue(fixture.Latest!.UserAgent.Contains(browserKind == ColdStartBrowser.Firefox ? "Firefox/" : "Chrome/", StringComparison.Ordinal));
            Assert.AreEqual("", fixture.Latest.Text);
            for (var dictation = 1; dictation <= 2; dictation++)
            {
                Interlocked.Exchange(ref providerWaitObserved, 0);
                var prefix = fixture.Latest!.Text;
                var pasteEventsBefore = fixture.Latest.TrustedPasteEvents;
                var inputEventsBefore = fixture.Latest.TrustedInputEvents;
                var early = $"dictation {dictation} early words";
                var revised = $"dictation {dictation} revised words";
                var final = $"Dictation {dictation} final sentence.";
                sessions.NextFinal = final;
                browser.RequireForeground();
                var browserFocusAgeMs = fixture.Latest!.Timestamp - fixture.Latest.FirstFocusedAt;
                var readyAt = Stopwatch.GetTimestamp();
                SendRightControl(release: false);
                keyDown = true;
                await Until(() => workflow.CurrentState.Phase == DictationWorkflowPhase.Recording && paster.InlinePreview,
                    $"dictation {dictation} first capture", 10000);
                Assert.AreEqual(dictation, Volatile.Read(ref presses), "Real keyboard hook must receive each hold exactly once.");
                var captureMs = Stopwatch.GetElapsedTime(readyAt).TotalMilliseconds;
                var replacementAt = Stopwatch.GetTimestamp();
                sessions.Current!.Preview(early);
                await Until(() => fixture.Latest?.Text == prefix + early, "first real clipboard preview", ReplacementObservationMilliseconds);
                var previewMs = Stopwatch.GetElapsedTime(replacementAt).TotalMilliseconds;
                Assert.AreEqual(pasteEventsBefore + 1, fixture.Latest!.TrustedPasteEvents, "The early preview must paste exactly once.");
                replacementAt = Stopwatch.GetTimestamp();
                sessions.Current.Preview(revised);
                await Until(() => fixture.Latest?.Text == prefix + revised, "real UIA preview selection/replacement", ReplacementObservationMilliseconds);
                var revisionMs = Stopwatch.GetElapsedTime(replacementAt).TotalMilliseconds;
                Assert.AreEqual(pasteEventsBefore + 2, fixture.Latest!.TrustedPasteEvents, "The revision must paste exactly once.");
                browser.RequireForeground();
                replacementAt = Stopwatch.GetTimestamp();
                SendRightControl(release: true);
                keyDown = false;
                await Until(() => workflow.CurrentState.Phase == DictationWorkflowPhase.Pasted, "final replacement", ReplacementObservationMilliseconds);
                await Until(() => fixture.Latest?.Text == prefix + final, "independent DOM final readback", 5000);
                var finalMs = Stopwatch.GetElapsedTime(replacementAt).TotalMilliseconds;
                Assert.AreEqual(dictation, Volatile.Read(ref releases));
                Assert.AreEqual(dictation, history.Items.Count);
                Assert.AreEqual(final.Trim(), history.Items[^1]);
                Assert.IsTrue(fixture.Latest!.Focused, "Browser editor must retain focus.");
                Assert.AreEqual(pasteEventsBefore + 3, fixture.Latest.TrustedPasteEvents,
                    "Each preview, revision and final replacement must be a real trusted browser paste.");
                Assert.IsGreaterThan(inputEventsBefore, fixture.Latest.TrustedInputEvents,
                    "Browser must emit trusted input events for the inserted text.");
                observations.Add(new { dictation, browser.ProcessId, captureMs, previewMs, revisionMs, finalMs,
                    providerWaitObserved = Volatile.Read(ref providerWaitObserved) != 0, browserFocusAgeMs, expected = prefix + final,
                    actual = fixture.Latest.Text, presses, releases, userAgent = fixture.Latest.UserAgent,
                    fixture.Latest.TrustedPasteEvents, fixture.Latest.TrustedInputEvents });
                TestContext.WriteLine($"PASS {editorKind} {Path.GetFileName(output)} PID={browser.ProcessId} dictation={dictation} capture={captureMs:F0}ms exact DOM match");
            }
            File.WriteAllText(Path.Combine(output, "passed.json"), JsonSerializer.Serialize(observations, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception failure)
        {
            trialFailure = failure;
            TestContext.WriteLine($"Primary trial failure: {failure}");
            try { File.WriteAllText(Path.Combine(output, "failure.txt"), failure.ToString()); }
            catch (Exception error) { TestContext.WriteLine($"Primary failure evidence unavailable: {error}"); }
            // Persist the browser history before any optional native diagnostics.
            try { File.WriteAllText(Path.Combine(output, "dom-at-failure.json"), JsonSerializer.Serialize(fixture.Snapshots)); }
            catch (Exception error) { TestContext.WriteLine($"Initial DOM evidence unavailable: {error}"); }
            // Evidence comes from the isolated CI desktop and generated page only.
            try
            {
                var bounds = Screen.PrimaryScreen!.Bounds;
                using var image = new Bitmap(bounds.Width, bounds.Height);
                using (var graphics = Graphics.FromImage(image)) graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
                image.Save(Path.Combine(output, "failure.png"));
            }
            catch (Exception error) { TestContext.WriteLine($"Screenshot unavailable: {error.GetType().Name}"); }
            // Preserve the failed outcome while observing whether the same
            // browser/provider recovers before teardown. No retry or input.
            if (browserKind == ColdStartBrowser.Chrome) throw;
            var observationWindow = Task.Delay(TimeSpan.FromSeconds(10));
            var uiThreadId = GetCurrentThreadId();
            await CaptureFailureEvidence("wait-chains-at-failure.json", dump: true);
            await observationWindow;
            await CaptureFailureEvidence("wait-chains-after-observation.json", dump: false);
            throw;

            async Task CaptureFailureEvidence(string name, bool dump)
            {
                try { await FirefoxWaitChainDiagnostics.CaptureAsync(browser.WaitChainTarget, uiThreadId,
                    Path.Combine(output, name), dumpFirefox: dump); }
                catch (Exception error) { TestContext.WriteLine($"Failure observation unavailable: {error}"); }
            }
        }
        finally
        {
            var cleanup = new ColdStartCleanup((stage, error) =>
            {
                TestContext.WriteLine($"Cleanup failure ({stage}): {error}");
                File.AppendAllText(Path.Combine(output, "cleanup-errors.txt"), $"{stage}: {error}{Environment.NewLine}");
            });
            paster.PresentationChanged -= ObserveProviderWait;
            stopSlowMonitor.Cancel();
            if (slowMonitor is not null)
            {
                try { await slowMonitor; }
                catch (Exception error) { TestContext.WriteLine($"Slow-call diagnostics unavailable: {error}"); }
            }
            if (keyDown) cleanup.Run("release-hotkey", () => SendRightControl(release: true));
            await cleanup.RunAsync("cancel-workflow", () => workflow.HandleAsync(DictationIntent.Cancel, CancellationToken.None));
            cleanup.Run("dom-evidence", () => File.WriteAllText(Path.Combine(output, "dom-observations.json"), JsonSerializer.Serialize(fixture.Snapshots)));
            await cleanup.RunAsync("flush-diagnostics", () => DiagnosticTrace.FlushAsync());
            cleanup.Run("hotkey", hotkey.Dispose);
            cleanup.Run("browser", browser.Dispose);
            await cleanup.RunAsync("fixture", () => fixture.DisposeAsync().AsTask());
            cleanup.ThrowIfNeeded(trialFailure);
        }

        async Task Until(Func<bool> predicate, string stage, int milliseconds)
        {
            var watch = Stopwatch.StartNew();
            while (!predicate())
            {
                token.ThrowIfCancellationRequested();
                if (dispatchError is not null) throw new InvalidOperationException($"Hotkey dispatch failed at {stage}.", dispatchError);
                Assert.AreNotEqual(DictationWorkflowPhase.Failed, workflow.CurrentState.Phase,
                    $"{stage}: {workflow.CurrentState.ErrorMessage}");
                Assert.IsLessThan(milliseconds, watch.ElapsedMilliseconds,
                    $"Timeout at {stage}; state={workflow.CurrentState.Phase}; DOM={JsonSerializer.Serialize(fixture.Latest)}");
                await Task.Delay(10, token);
            }
        }
    }

    private static async Task OnStaAsync(Func<Task> action)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using var context = new ApplicationContext();
                using var ui = new WindowsFormsSynchronizationContext();
                SynchronizationContext.SetSynchronizationContext(ui);
                Exception? failure = null;
                ui.Post(async _ =>
                {
                    try { await action(); }
                    catch (Exception error) { failure = error; }
                    finally { context.ExitThread(); }
                }, null);
                Application.Run(context);
                if (failure is not null) completed.TrySetException(failure);
                else completed.TrySetResult();
            }
            catch (Exception error) { completed.TrySetException(error); }
        }) { IsBackground = true, Name = "Browser cold start CI" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        // The CI job/process has an additional hard timeout for uninterruptible UIA.
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(300));
    }

    private static async Task WaitForNoExistingBrowserAsync(ColdStartBrowser kind, string output,
        CancellationToken cancellationToken)
    {
        if (kind != ColdStartBrowser.Firefox)
        {
            AssertNoExistingBrowser(kind, output);
            return;
        }
        var started = Stopwatch.StartNew();
        await ColdStartProcessGate.WaitUntilNoneRunningAsync(
            () => Process.GetProcessesByName("firefox"), kind.ToString(), observations =>
                File.AppendAllText(Path.Combine(output, "cold-process-precondition.jsonl"),
                    JsonSerializer.Serialize(new { observedUtc = DateTime.UtcNow, browser = kind.ToString(),
                        elapsedMilliseconds = started.ElapsedMilliseconds, observations }) + Environment.NewLine),
            TimeSpan.FromSeconds(20), cancellationToken);
    }

    private static void AssertNoExistingBrowser(ColdStartBrowser kind, string output)
    {
        var existing = Process.GetProcessesByName(kind.ToString().ToLowerInvariant());
        try
        {
            ColdStartProcessGate.AssertNoneRunning(existing, kind.ToString(), observations =>
                File.AppendAllText(Path.Combine(output, "cold-process-precondition.jsonl"),
                    JsonSerializer.Serialize(new { observedUtc = DateTime.UtcNow, browser = kind.ToString(), observations })
                    + Environment.NewLine));
        }
        finally { foreach (var process in existing) process.Dispose(); }
    }

    private sealed class KnownTranscripts : IDictationSessionFactory
    {
        public string NextFinal = "";
        public KnownSession? Current;
        public IDictationSession CreateSession() => Current = new KnownSession(NextFinal);
    }

    private sealed class KnownSession(string final) : IDictationSession
    {
        public event Action<TranscriptUpdate>? TranscriptUpdated;
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void Preview(string text) => TranscriptUpdated?.Invoke(new(TranscriptUpdateKind.Partial, text));
        public Task<DictationSessionResult> StopAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new DictationSessionResult(new TranscriptResult(final, TimeSpan.Zero, 1)));
        public Task CancelAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class OwnedBrowser(ColdStartBrowser kind, string executable, Uri url, string output) : IDisposable
    {
        private DateTime _started;
        private ColdStartBrowserJob? _job;
        private IntPtr _window;
        private readonly string _profile = Path.Combine(Path.GetTempPath(), "ptt-browser-" + Guid.NewGuid().ToString("N"));
        private string ProcessName => kind.ToString().ToLowerInvariant();
        public int ProcessId { get; private set; }
        private DateTime _boundStartedUtc;
        private uint _boundThreadId;
        public FirefoxWaitChainDiagnostics.ProcessTarget WaitChainTarget =>
            new(ProcessId, _boundStartedUtc, _boundThreadId);

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await WaitForNoExistingBrowserAsync(kind, output, cancellationToken);
            Directory.CreateDirectory(_profile);
            // Suppress first-run chrome only. Never force/enable accessibility.
            if (kind == ColdStartBrowser.Firefox) File.WriteAllText(Path.Combine(_profile, "user.js"), """
                user_pref("browser.aboutwelcome.enabled", false);
                user_pref("browser.preonboarding.enabled", false);
                // Firefox 154+ can trigger OS pinning prompts despite the onboarding prefs.
                // https://bugzilla.mozilla.org/show_bug.cgi?id=2065938
                user_pref("browser.bypassAutoTriggerActions", true);
                user_pref("datareporting.policy.dataSubmissionPolicyBypassNotification", true);
                user_pref("browser.shell.checkDefaultBrowser", false);
                user_pref("browser.startup.homepage_override.mstone", "ignore");
                user_pref("ui.systemUsesDarkTheme", 1);
                """);
            string[] arguments = kind == ColdStartBrowser.Firefox
                ? ["--no-remote", "--profile", _profile, "--new-window", url.AbsoluteUri]
                : [$"--user-data-dir={_profile}", "--no-first-run", "--no-default-browser-check", "--force-dark-mode", "--new-window", url.AbsoluteUri];
            cancellationToken.ThrowIfCancellationRequested();
            // Re-enumerate immediately before launch: a prior empty observation
            // cannot authorize attaching to a newly appeared browser process.
            AssertNoExistingBrowser(kind, output);
            _started = DateTime.UtcNow;
            _job = new ColdStartBrowserJob(executable, arguments, observation =>
                File.AppendAllText(Path.Combine(output, "owned-process-cleanup.jsonl"),
                    JsonSerializer.Serialize(observation) + Environment.NewLine));
            File.WriteAllText(Path.Combine(output, "launch.json"), JsonSerializer.Serialize(new
            {
                executable, version = FileVersionInfo.GetVersionInfo(executable).ProductVersion,
                arguments, startedUtc = _started, launcherPid = _job.LauncherId,
                profile = _profile, ownership = "isolated job assigned before process resume"
            }));
        }

        public bool TryActivate()
        {
            foreach (var process in Process.GetProcessesByName(ProcessName))
            {
                using (process)
                {
                    try
                    {
                        if (!IsOwned(process)) continue;
                        var window = process.MainWindowHandle;
                        if (window == IntPtr.Zero) continue;
                        GetWindowThreadProcessId(window, out var owner);
                        if (owner != (uint)process.Id) continue;
                        _window = window;
                        ProcessId = process.Id;
                        if (GetForegroundWindow() == _window) return true;
                        SetForegroundWindow(_window);
                        return GetForegroundWindow() == _window;
                    }
                    catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException)
                    {
                        File.AppendAllText(Path.Combine(output, "startup-process-observations.txt"),
                            $"Transient process/window observation: {error}{Environment.NewLine}");
                    }
                }
            }
            return false;
        }

        public void RequireForeground()
        {
            Assert.AreNotEqual(IntPtr.Zero, _window);
            Assert.AreEqual(_window, GetForegroundWindow(), "Refusing input outside this trial's browser window.");
            GetWindowThreadProcessId(_window, out var owner);
            Assert.AreEqual((uint)ProcessId, owner);
            using var process = Process.GetProcessById(ProcessId);
            Assert.IsTrue(IsOwned(process));
            Assert.AreEqual(_boundStartedUtc, process.StartTime.ToUniversalTime());
        }

        public void BindReadyWindow()
        {
            // Firefox replaces its initial startup window. Bind the real window
            // once after our page has painted; never retarget during dictation.
            var window = GetForegroundWindow();
            var windowThreadId = GetWindowThreadProcessId(window, out var owner);
            using var process = Process.GetProcessById((int)owner);
            Assert.IsTrue(IsOwned(process), "Foreground browser must belong to this isolated profile job.");
            var title = new System.Text.StringBuilder(512);
            GetWindowText(window, title, title.Capacity);
            StringAssert.Contains(title.ToString(), "PTT browser cold-start test");
            _window = window;
            ProcessId = process.Id;
            _boundStartedUtc = process.StartTime.ToUniversalTime();
            _boundThreadId = windowThreadId;
        }

        public void ClickEditor(BrowserEditorFixture.State page)
        {
            RequireForeground();
            Assert.AreEqual(1d, page.PixelRatio, "CI click coordinates require the runner's normal 100% display scaling.");
            var point = new NativePoint { X = (int)Math.Round(page.ClickX), Y = (int)Math.Round(page.ClickY) };
            Assert.AreEqual(_window, GetAncestor(WindowFromPoint(point), 2), "Editor point must belong to this browser window.");
            Assert.IsTrue(SetCursorPos(point.X, point.Y));
            WindowsPasteInput.NativeInput[] inputs = [
                new() { Type = 0, Data = new WindowsPasteInput.InputUnion { Mouse = new WindowsPasteInput.MouseInput { Flags = 2 } } },
                new() { Type = 0, Data = new WindowsPasteInput.InputUnion { Mouse = new WindowsPasteInput.MouseInput { Flags = 4 } } }
            ];
            Assert.AreEqual(2u, SendInput(2, inputs, Marshal.SizeOf<WindowsPasteInput.NativeInput>()));
        }

        public void Dispose()
        {
            // Job membership was fixed before process resume. Stop only this
            // profile's descendants, including children whose launcher exited.
            _job?.Dispose();
            ColdStartCleanup.DeleteProfile(_profile);
        }

        private bool IsOwned(Process process)
        {
            try
            {
                return _job is not null && _job.Contains(process)
                    && process.ProcessName.Equals(ProcessName, StringComparison.OrdinalIgnoreCase)
                    && process.StartTime.ToUniversalTime() >= _started
                    // A newly resumed Chrome process may not expose its module
                    // yet. Leave it unowned until its executable can be verified.
                    && process.MainModule is { FileName: var modulePath }
                    && string.Equals(Path.GetFullPath(modulePath), Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException)
            { return false; }
        }
    }

    private static void SendRightControl(bool release)
    {
        WindowsPasteInput.NativeInput[] input = [new()
        {
            Type = 1,
            Data = new WindowsPasteInput.InputUnion
            {
                Keyboard = new WindowsPasteInput.KeyboardInput
                {
                    VirtualKey = 0xA3, ScanCode = 0x1D, Flags = (uint)(1 | (release ? 2 : 0))
                    // Not PTT's paste marker: exercise the real hotkey hook.
                }
            }
        }];
        Assert.AreEqual(1u, SendInput(1, input, Marshal.SizeOf<WindowsPasteInput.NativeInput>()), "Windows rejected Right Ctrl input.");
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, System.Text.StringBuilder title, int length);
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, [In] WindowsPasteInput.NativeInput[] inputs, int size);
}
