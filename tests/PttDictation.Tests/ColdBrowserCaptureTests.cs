using PttDictation.App;
using PttDictation.Core;

namespace PttDictation.Tests;

[TestClass]
public sealed class ColdBrowserCaptureTests
{
    [TestMethod]
    public void ProviderRefinementDuringReadinessRetriesBeforePinning()
    {
        var container = new object();
        var editor = new object();
        object focused = container;
        var ready = BrowserCaptureReadiness.Create(() => focused, (_, _) => true,
            _ => { focused = editor; return true; }, ReferenceEquals);
        Assert.IsFalse(ready(), "A probe that changes provider identity is not yet stable.");
        Assert.IsTrue(ready());
        Assert.IsTrue(ready());
    }
    [TestMethod]
    public async Task ScriptedEditorChangeAfterPreparationCannotBecomeInsertionTarget()
    {
        object focused = new();
        using var warmup = new WindowsAccessibilityWarmup(_ => new Lease(), () => true);
        int captures = 0;
        Exception? failure = null;
        using var output = new LiveClipboardPaster(() => { captures++; return new Target(); }, (_, _) => { },
            acquireCapture: warmup.AcquireCaptureAsync,
            createReadiness: () => BrowserCaptureReadiness.Create(() => focused, ReferenceEquals, _ => true));
        output.InsertionFailed += error => failure = error;
        await output.PrepareCaptureAsync(CancellationToken.None);
        focused = new object(); // No native window or physical input change.
        output.CaptureTarget();
        Assert.IsNotNull(failure);
        Assert.AreEqual(0, captures);
    }

    [TestMethod]
    public void ReadyContainerRefinementPinsTheSpecificEditor()
    {
        object focused = new();
        var available = false;
        var ready = BrowserCaptureReadiness.Create(() => focused, (_, _) => true,
            _ => available, ReferenceEquals);
        Assert.IsFalse(ready());
        focused = new object();
        available = true;
        Assert.IsTrue(ready());
        Assert.IsTrue(ready());
        focused = new object();
        Assert.Throws<InvalidOperationException>(() => ready());
    }
    [TestMethod]
    public async Task UnreadyProviderTimesOutWithoutMicrophoneAndReleasesNextAttempt()
    {
        using var warmup = new WindowsAccessibilityWarmup(_ => new Lease(), () => true);
        using var output = new LiveClipboardPaster(() => new Target(), (_, _) => { },
            acquireCapture: warmup.AcquireCaptureAsync, createReadiness: () => () => false);
        var session = new Session();
        var workflow = new DictationWorkflow(session, output, new SessionHistory());
        await workflow.HandleAsync(DictationIntent.BeginHold, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(8));
        Assert.AreEqual(0, session.Starts);
        Assert.AreEqual(DictationWorkflowPhase.Failed, workflow.CurrentState.Phase);
        StringAssert.Contains(workflow.CurrentState.ErrorMessage!, "No recording was started");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        using var next = await warmup.AcquireCaptureAsync(timeout.Token);
    }
    [TestMethod]
    public void MissingInitialProviderCanBecomeReadyWithoutRetargetingLater()
    {
        object? focused = null;
        var editor = new object();
        var ready = BrowserCaptureReadiness.Create(() => focused, ReferenceEquals, _ => true);
        Assert.IsFalse(ready());
        focused = editor;
        Assert.IsTrue(ready());
        focused = null;
        Assert.IsFalse(ready());
        focused = new object();
        Assert.Throws<InvalidOperationException>(() => ready());
    }
    [TestMethod]
    public async Task ReadinessAndUserIntentAreRequiredBeforeCapture()
    {
        var ready = false;
        var unchanged = true;
        var reads = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var warmup = new WindowsAccessibilityWarmup(_ => new Lease(), () => true);
        int captures = 0;
        using var output = new LiveClipboardPaster(() => { captures++; return new Target(); }, (_, _) => { },
            acquireCapture: warmup.AcquireCaptureAsync, beginPreparation: () => () => unchanged,
            createReadiness: () => () => { reads.TrySetResult(); return ready; });
        var preparation = output.PrepareCaptureAsync(CancellationToken.None);
        await reads.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsFalse(preparation.IsCompleted);
        Assert.AreEqual(0, captures);
        unchanged = false;
        ready = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => preparation);
        Assert.AreEqual(0, captures);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var next = await warmup.AcquireCaptureAsync(deadline.Token);
    }

    [TestMethod]
    public async Task CancellationHoldsLeaseUntilUncooperativeReadHasActuallyReturned()
    {
        using var release = new ManualResetEvent(false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var warmup = new WindowsAccessibilityWarmup(_ => new Lease(), () => true);
        using var cancellation = new CancellationTokenSource();
        using var output = new LiveClipboardPaster(() => new Target(), (_, _) => { },
            acquireCapture: warmup.AcquireCaptureAsync,
            createReadiness: () => () => { entered.TrySetResult(); release.WaitOne(); return true; },
            automationWorker: new ApartmentWorker(ApartmentState.MTA, "Cold readiness test"));
        var preparation = output.PrepareCaptureAsync(cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => preparation.WaitAsync(TimeSpan.FromSeconds(1)));
            using var shortWait = new CancellationTokenSource(150);
            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            {
                using var unexpected = await warmup.AcquireCaptureAsync(shortWait.Token);
            });
            release.Set();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var lease = await warmup.AcquireCaptureAsync(timeout.Token);
        }
        finally { release.Set(); }
    }

    [TestMethod]
    public async Task IntentChangeAfterReadinessStillPreventsFreshCapture()
    {
        var unchanged = true;
        using var warmup = new WindowsAccessibilityWarmup(_ => new Lease(), () => true);
        int captures = 0;
        using var output = new LiveClipboardPaster(() => { captures++; return new Target(); }, (_, _) => { },
            acquireCapture: warmup.AcquireCaptureAsync, beginPreparation: () => () => unchanged);
        await output.PrepareCaptureAsync(CancellationToken.None);
        unchanged = false;
        Assert.Throws<InvalidOperationException>(output.CaptureTarget);
        Assert.AreEqual(0, captures);
    }

    [TestMethod]
    public void ReadOnlyOrUnverifiableTargetsDoNotCountAsReady()
    {
        Assert.IsTrue(BrowserCaptureReadiness.IsReady(new Target()));
        Assert.IsFalse(BrowserCaptureReadiness.IsReady(new Target { Writable = false }));
        Assert.IsFalse(BrowserCaptureReadiness.IsReady(new Target { Prepared = false }));
        Assert.IsFalse(BrowserCaptureReadiness.IsReady(new Target { Focused = false }));
    }
    [TestMethod]
    public async Task WorkflowCannotCaptureOrRecordWhileWarmupOwnsProvider()
    {
        using var release = new ManualResetEvent(false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probeCalls = 0;
        using var warmup = new WindowsAccessibilityWarmup(_ => new Lease(), () =>
        {
            Interlocked.Increment(ref probeCalls);
            entered.TrySetResult();
            release.WaitOne();
            return true;
        }, interval: TimeSpan.FromMinutes(1));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        int captures = 0, guards = 0;
        Action? confirm = null;
        using var output = new LiveClipboardPaster(() => { captures++; return new Target(); }, (_, _) => { },
            beginCaptureGuard: () => { guards++; return () => true; },
            confirmCapture: action => confirm = action, acquireCapture: warmup.AcquireCaptureAsync);
        var session = new Session();
        var workflow = new DictationWorkflow(session, output, new SessionHistory());
        var beginning = workflow.HandleAsync(DictationIntent.BeginHold, CancellationToken.None);
        try
        {
            Assert.AreEqual(0, guards, "Capture guard cannot start inside an in-flight warmup read.");
            Assert.AreEqual(0, captures);
            Assert.AreEqual(0, session.Starts);
            Assert.IsFalse(beginning.IsCompleted);
            release.Set();
            await beginning.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.AreEqual(1, guards);
            Assert.AreEqual(1, captures);
            Assert.AreEqual(1, session.Starts);
            warmup.Request();
            await Task.Delay(180);
            Assert.AreEqual(1, Volatile.Read(ref probeCalls), "Lease must cover deferred capture confirmation.");
            Assert.IsNotNull(confirm);
            confirm();
            await workflow.HandleAsync(DictationIntent.Cancel, CancellationToken.None);
        }
        finally { release.Set(); }
        warmup.Dispose();
        await warmup.Completion.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private sealed class Lease : IDisposable { public void Dispose() { } }
    private sealed class Target : IWindowsTextTarget
    {
        public bool Writable { get; init; } = true;
        public bool Prepared { get; init; } = true;
        public bool Focused { get; init; } = true;
        public bool IsFocused => Focused;
        public bool SupportsReplacement => Writable;
        public bool CanPasteFallback => Writable;
        public bool IsPreparedSelectionCurrent => Prepared;
        public bool HasAttemptedWrite => false;
        public TextTargetUpdateResult TryReplace(string previousText, string replacementText, Action<string> paste)
            => TextTargetUpdateResult.Success;
    }
    private sealed class Session : IDictationSession, IDictationSessionFactory
    {
        public event Action<TranscriptUpdate>? TranscriptUpdated { add { } remove { } }
        public int Starts;
        public IDictationSession CreateSession() => this;
        public Task StartAsync(CancellationToken cancellationToken) { Starts++; return Task.CompletedTask; }
        public Task CancelAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<DictationSessionResult> StopAsync(CancellationToken cancellationToken) => throw new AssertFailedException();
    }
}
