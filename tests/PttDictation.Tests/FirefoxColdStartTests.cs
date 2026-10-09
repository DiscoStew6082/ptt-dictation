using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using PttDictation.App;
using PttDictation.Core;

namespace PttDictation.Tests;

// Deliberately CI-only: never attaches to the user's Firefox or sends input on
// their desktop. The browser, UIA, hook, clipboard and paste are real. Only the
// speech recognizer is replaced with a deterministic transcript source.
[TestClass]
[DoNotParallelize]
public sealed class FirefoxColdStartTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [TestCategory("FirefoxColdStart")]
    [DataRow("textarea")]
    [DataRow("contenteditable")]
    public async Task FreshFirefoxAcceptsFirstAndSecondDictation(string editorKind)
    {
        if (Environment.GetEnvironmentVariable("PTT_RUN_FIREFOX_COLD_START_TESTS") != "1")
            Assert.Inconclusive("Firefox integration runs only in its dedicated GitHub-hosted Windows CI job.");
        Assert.AreEqual("true", Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "CI-only test.");
        Assert.AreEqual("github-hosted", Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT"), "Disposable runner required.");
        Assert.AreEqual("1", Environment.GetEnvironmentVariable("PTT_RUN_FIREFOX_COLD_START_TESTS"), "Explicit Firefox CI opt-in required.");
        var executable = Environment.GetEnvironmentVariable("PTT_FIREFOX_PATH");
        Assert.IsTrue(Path.IsPathFullyQualified(executable ?? "") && File.Exists(executable), "Installed Firefox executable is required.");
        var output = Path.Combine(Environment.GetEnvironmentVariable("PTT_FIREFOX_ARTIFACTS")
            ?? throw new InvalidOperationException("Artifact directory required."), editorKind);
        Directory.CreateDirectory(output);
        TestContext.WriteLine($"Firefox {FileVersionInfo.GetVersionInfo(executable!).ProductVersion}; executable={executable}");
        using var diagnostics = DiagnosticTrace.Configure(Path.Combine(output, "diagnostics"), "firefox-cold-start-ci");
        await OnStaAsync(async () =>
        {
            // PTT stays running while Firefox is closed and reopened, matching
            // the regression. No Firefox process exists during client startup.
            Assert.AreEqual(0, Process.GetProcessesByName("firefox").Length);
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
                Assert.IsLessThan(20000L, started.ElapsedMilliseconds, "PTT accessibility client startup failed before Firefox launch.");
                await Task.Delay(25);
            }
            // Preparation occurs before any Firefox process exists and performs
            // no accessibility or browser work. Opt-in diagnostics only.
            await using var slowCollector = await FirefoxSlowCallCollector.StartIfEnabledAsync(output);
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
        await using var fixture = FirefoxEditorFixture.Start(editorKind);
        using var browser = new OwnedFirefox(executable, fixture.Url, output);
        using var stopSlowMonitor = new CancellationTokenSource();
        Task? slowMonitor = null;
        var sessions = new KnownTranscripts();
        var history = new SessionHistory();
        var ui = SynchronizationContext.Current!;
        var workflow = new DictationWorkflow(sessions, paster, history, synchronizationContext: ui);
        using var hotkey = new GlobalHotkeySource(DictationHotkey.RightControl, DictationHotkey.F9);
        Exception? dispatchError = null;
        var presses = 0;
        var releases = 0;
        var keyDown = false;
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
        hotkey.Start();
        var observations = new List<object>();
        try
        {
            browser.Start();
            await Until(() => browser.TryActivate(), "Firefox native foreground window", 20000);
            // Only the page's DOM reports readiness. No test UIA query, WebDriver
            // attachment, provider probing, or arbitrary warmup delay precedes PTT.
            await Until(() => fixture.Latest is { Focused: true, Ready: true }, "Firefox editor DOM focus", 20000);
            browser.BindReadyWindow();
            slowMonitor = slowCollector?.WatchTrialAsync(browser.WaitChainTarget, GetCurrentThreadId(), output,
                stopSlowMonitor.Token);
            var clicksBefore = fixture.Latest!.TrustedClicks;
            browser.ClickEditor(fixture.Latest);
            await Until(() => fixture.Latest is { Focused: true } state && state.TrustedClicks > clicksBefore,
                "native click establishes Firefox caret", 5000);
            Assert.IsTrue(fixture.Latest!.UserAgent.Contains("Firefox/", StringComparison.Ordinal));
            Assert.AreEqual("", fixture.Latest.Text);
            for (var dictation = 1; dictation <= 2; dictation++)
            {
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
                sessions.Current!.Preview(early);
                await Until(() => fixture.Latest?.Text == prefix + early, "first real clipboard preview", 8000);
                sessions.Current.Preview(revised);
                await Until(() => fixture.Latest?.Text == prefix + revised, "real UIA preview selection/replacement", 8000);
                browser.RequireForeground();
                SendRightControl(release: true);
                keyDown = false;
                await Until(() => workflow.CurrentState.Phase == DictationWorkflowPhase.Pasted, "final replacement", 10000);
                await Until(() => fixture.Latest?.Text == prefix + final, "independent DOM final readback", 5000);
                Assert.AreEqual(dictation, Volatile.Read(ref releases));
                Assert.AreEqual(dictation, history.Items.Count);
                Assert.AreEqual(final.Trim(), history.Items[^1]);
                Assert.IsTrue(fixture.Latest!.Focused, "Firefox editor must retain focus.");
                Assert.AreEqual(pasteEventsBefore + 3, fixture.Latest.TrustedPasteEvents,
                    "Each preview, revision and final replacement must be a real trusted browser paste.");
                Assert.IsGreaterThan(inputEventsBefore, fixture.Latest.TrustedInputEvents,
                    "Firefox must emit trusted input events for the inserted text.");
                observations.Add(new { dictation, browser.ProcessId, captureMs, browserFocusAgeMs, expected = prefix + final,
                    actual = fixture.Latest.Text, presses, releases, userAgent = fixture.Latest.UserAgent,
                    fixture.Latest.TrustedPasteEvents, fixture.Latest.TrustedInputEvents });
                TestContext.WriteLine($"PASS {editorKind} {Path.GetFileName(output)} PID={browser.ProcessId} dictation={dictation} capture={captureMs:F0}ms exact DOM match");
            }
            File.WriteAllText(Path.Combine(output, "passed.json"), JsonSerializer.Serialize(observations, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
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
            stopSlowMonitor.Cancel();
            if (slowMonitor is not null)
            {
                try { await slowMonitor; }
                catch (Exception error) { TestContext.WriteLine($"Slow-call diagnostics unavailable: {error}"); }
            }
            if (keyDown) SendRightControl(release: true);
            await workflow.HandleAsync(DictationIntent.Cancel, CancellationToken.None);
            File.WriteAllText(Path.Combine(output, "dom-observations.json"), JsonSerializer.Serialize(fixture.Snapshots));
            await DiagnosticTrace.FlushAsync();
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
        }) { IsBackground = true, Name = "Firefox cold start CI" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        // The CI job/process has an additional hard timeout for uninterruptible UIA.
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(300));
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

    private sealed class OwnedFirefox(string executable, Uri url, string output) : IDisposable
    {
        private DateTime _started;
        private Process? _launcher;
        private IntPtr _window;
        private readonly string _profile = Path.Combine(Path.GetTempPath(), "ptt-firefox-" + Guid.NewGuid().ToString("N"));
        public int ProcessId { get; private set; }
        private DateTime _boundStartedUtc;
        private uint _boundThreadId;
        public FirefoxWaitChainDiagnostics.ProcessTarget WaitChainTarget =>
            new(ProcessId, _boundStartedUtc, _boundThreadId);

        public void Start()
        {
            Assert.AreEqual(0, Process.GetProcessesByName("firefox").Length,
                "Cold test requires no existing Firefox processes on its dedicated runner.");
            Directory.CreateDirectory(_profile);
            // Suppress first-run chrome only. Never force/enable accessibility.
            File.WriteAllText(Path.Combine(_profile, "user.js"), """
                user_pref("browser.aboutwelcome.enabled", false);
                user_pref("browser.preonboarding.enabled", false);
                user_pref("datareporting.policy.dataSubmissionPolicyBypassNotification", true);
                user_pref("browser.shell.checkDefaultBrowser", false);
                user_pref("browser.startup.homepage_override.mstone", "ignore");
                user_pref("ui.systemUsesDarkTheme", 1);
                """);
            var start = new ProcessStartInfo(executable) { UseShellExecute = false };
            foreach (var argument in new[] { "--no-remote", "--profile", _profile, "--new-window", url.AbsoluteUri })
                start.ArgumentList.Add(argument);
            _started = DateTime.UtcNow;
            _launcher = Process.Start(start) ?? throw new InvalidOperationException("Firefox did not start.");
            File.WriteAllText(Path.Combine(output, "launch.json"), JsonSerializer.Serialize(new
            {
                executable, version = FileVersionInfo.GetVersionInfo(executable).ProductVersion,
                arguments = start.ArgumentList, startedUtc = _started, launcherPid = _launcher.Id
            }));
        }

        public bool TryActivate()
        {
            foreach (var process in Process.GetProcessesByName("firefox"))
            {
                using (process)
                {
                    if (process.StartTime.ToUniversalTime() < _started || process.MainWindowHandle == IntPtr.Zero) continue;
                    _window = process.MainWindowHandle;
                    ProcessId = process.Id;
                    if (GetForegroundWindow() == _window) return true;
                    SetForegroundWindow(_window);
                    return GetForegroundWindow() == _window;
                }
            }
            return false;
        }

        public void RequireForeground()
        {
            Assert.AreNotEqual(IntPtr.Zero, _window);
            Assert.AreEqual(_window, GetForegroundWindow(), "Refusing input outside this trial's Firefox window.");
            GetWindowThreadProcessId(_window, out var owner);
            Assert.AreEqual((uint)ProcessId, owner);
        }

        public void BindReadyWindow()
        {
            // Firefox replaces its initial startup window. Bind the real window
            // once after our page has painted; never retarget during dictation.
            var window = GetForegroundWindow();
            var windowThreadId = GetWindowThreadProcessId(window, out var owner);
            using var process = Process.GetProcessById((int)owner);
            Assert.AreEqual("firefox", process.ProcessName);
            Assert.IsTrue(process.StartTime.ToUniversalTime() >= _started);
            var title = new System.Text.StringBuilder(512);
            GetWindowText(window, title, title.Capacity);
            StringAssert.Contains(title.ToString(), "PTT Firefox cold-start test");
            _window = window;
            ProcessId = process.Id;
            _boundStartedUtc = process.StartTime.ToUniversalTime();
            _boundThreadId = windowThreadId;
        }

        public void ClickEditor(FirefoxEditorFixture.State page)
        {
            RequireForeground();
            Assert.AreEqual(1d, page.PixelRatio, "CI click coordinates require the runner's normal 100% display scaling.");
            var point = new NativePoint { X = (int)Math.Round(page.ClickX), Y = (int)Math.Round(page.ClickY) };
            Assert.AreEqual(_window, GetAncestor(WindowFromPoint(point), 2), "Editor point must belong to this Firefox window.");
            Assert.IsTrue(SetCursorPos(point.X, point.Y));
            WindowsPasteInput.NativeInput[] inputs = [
                new() { Type = 0, Data = new WindowsPasteInput.InputUnion { Mouse = new WindowsPasteInput.MouseInput { Flags = 2 } } },
                new() { Type = 0, Data = new WindowsPasteInput.InputUnion { Mouse = new WindowsPasteInput.MouseInput { Flags = 4 } } }
            ];
            Assert.AreEqual(2u, SendInput(2, inputs, Marshal.SizeOf<WindowsPasteInput.NativeInput>()));
        }

        public void Dispose()
        {
            // No browser existed at Start. Only this CI trial can launch one;
            // never kill a process predating its start, even on a failed trial.
            if (_launcher is null) return;
            foreach (var process in Process.GetProcessesByName("firefox"))
            {
                using (process)
                {
                    try
                    {
                        if (process.StartTime.ToUniversalTime() < _started) continue;
                        process.Kill(entireProcessTree: true);
                        process.WaitForExit(5000);
                    }
                    catch (ArgumentException) { }
                    catch (InvalidOperationException) { }
                }
            }
            _launcher.Dispose();
            Assert.AreEqual(0, Process.GetProcessesByName("firefox").Length, "Firefox must fully exit before the next cold trial.");
            Directory.Delete(_profile, recursive: true);
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
