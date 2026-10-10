using PttDictation.App;
using PttDictation.Core;

namespace PttDictation.Tests;

[TestClass]
public sealed class FinalCaptureValidationTests
{
    [TestMethod]
    [DataRow(1090L, 3957L, true)]
    [DataRow(4999L, 4999L, true)]
    [DataRow(1090L, 5000L, false)]
    [DataRow(4999L, 5001L, false)]
    [DataRow(5000L, 0L, false)]
    public async Task OnlyTimelyPinnedReadinessGetsOneBoundedFinalValidation(long readiness, long validation, bool succeeds)
    {
        using var fixture = new Fixture(readiness, validation);
        await fixture.Begin();
        Assert.AreEqual(succeeds ? DictationWorkflowPhase.Recording : DictationWorkflowPhase.Failed,
            fixture.Workflow.CurrentState.Phase);
        Assert.AreEqual(succeeds ? 1 : 0, fixture.Recording.Starts);
        Assert.AreEqual(readiness < 5000 ? 1 : 0, fixture.NativeGuards);
        Assert.AreEqual(readiness < 5000 ? 2 : 1, fixture.Probes,
            "Final validation must neither recapture nor retry provider work.");
        Assert.AreEqual(0, fixture.Captures);
        Assert.AreEqual(0, fixture.Pastes);
        await fixture.Cancel();
    }

    [TestMethod]
    [DataRow("identity")]
    [DataRow("selection")]
    [DataRow("focus")]
    [DataRow("input")]
    [DataRow("native-intent")]
    [DataRow("cancel")]
    [DataRow("end-session")]
    public async Task ChangesDuringRecoveredFinalValidationNeverStartMicrophone(string change)
    {
        using var fixture = new Fixture(1090, 3957);
        fixture.DuringFinalProbe = () =>
        {
            switch (change)
            {
                case "identity": fixture.SameIdentity = false; break;
                case "selection": fixture.Target.Prepared = false; break;
                case "focus": fixture.Target.Focused = false; break;
                case "input": fixture.PreparationUnchanged = false; break;
                case "native-intent": fixture.NativeIntentUnchanged = false; break;
                case "cancel": fixture.Cancellation.Cancel(); break;
                case "end-session": fixture.Output.EndSession(); break;
            }
        };
        await fixture.Begin();
        Assert.AreNotEqual(DictationWorkflowPhase.Recording, fixture.Workflow.CurrentState.Phase);
        Assert.AreEqual(0, fixture.Recording.Starts);
        Assert.AreEqual(0, fixture.Captures);
        Assert.AreEqual(0, fixture.Pastes);
    }

    [TestMethod]
    public async Task LateNativeConfirmationCannotPublishOrStartMicrophone()
    {
        using var fixture = new Fixture(1090, 3957) { DeferConfirmation = true };
        var beginning = fixture.Begin();
        Assert.IsNotNull(fixture.Confirm);
        Assert.AreEqual(0, fixture.Recording.Starts);
        fixture.Now = 6090; // Final phase expires; no timer delivery is needed.
        fixture.Confirm();
        await beginning;
        Assert.AreEqual(DictationWorkflowPhase.Failed, fixture.Workflow.CurrentState.Phase);
        Assert.AreEqual(0, fixture.Recording.Starts);
        Assert.IsFalse(fixture.Output.InlinePreview);
    }

    [TestMethod]
    public async Task CompletedPreparationStillRejectsExpiredHandoff()
    {
        using var fixture = new Fixture(1090, 3957);
        await fixture.Output.PrepareCaptureAsync(CancellationToken.None);
        fixture.Now = 6090;
        Assert.Throws<InvalidOperationException>(fixture.Output.CaptureTarget);
        Assert.AreEqual(0, fixture.Captures);
        Assert.IsFalse(fixture.Output.InlinePreview);
    }

    [TestMethod]
    public async Task NativeIntentGuardCrossingDeadlineCannotAdoptPreparedTarget()
    {
        long now = 0;
        var advanceDuringHandoff = false;
        var captures = 0;
        using var output = new LiveClipboardPaster(() => { captures++; throw new AssertFailedException("No recapture."); },
            (_, _) => Assert.Fail("Preparation permits no paste."), tickCount: () => now,
            acquireCapture: _ => Task.FromResult<IDisposable>(new Lease()),
            createPreparedReadiness: () => new(() => { now = 1090; return true; }, () => new Target()),
            beginCaptureGuard: () => () =>
            {
                if (advanceDuringHandoff) now = 6090;
                return true; // Identity and intent are unchanged, but validation is too late.
            });
        await output.PrepareCaptureAsync(CancellationToken.None);
        advanceDuringHandoff = true;
        Assert.Throws<InvalidOperationException>(output.CaptureTarget);
        Assert.AreEqual(0, captures);
        Assert.IsFalse(output.InlinePreview);
    }

    [TestMethod]
    [DataRow(3957L, true)]
    [DataRow(5000L, false)]
    public async Task FinalSnapshotReadMustAlsoReturnBeforeTheFinalPhaseDeadline(long duration, bool succeeds)
    {
        using var fixture = new Fixture(1090, 0);
        fixture.Target.DuringPreparedCheck = () => fixture.Now += duration;
        await fixture.Begin();
        Assert.AreEqual(succeeds ? 1 : 0, fixture.Recording.Starts);
        Assert.AreEqual(succeeds ? DictationWorkflowPhase.Recording : DictationWorkflowPhase.Failed,
            fixture.Workflow.CurrentState.Phase);
        Assert.AreEqual(0, fixture.Pastes);
        await fixture.Cancel();
    }

    [TestMethod]
    public async Task WarmupLeaseWaitCannotConsumeInitialBudgetAndThenReceiveAnotherPhase()
    {
        long now = 0;
        var nativeGuards = 0;
        using var output = new LiveClipboardPaster(() => throw new AssertFailedException(), (_, _) => Assert.Fail(),
            tickCount: () => now,
            acquireCapture: _ => { now = 5000; return Task.FromResult<IDisposable>(new Lease()); },
            createPreparedReadiness: () => new(() => true, () => new Target()),
            beginCaptureGuard: () => { nativeGuards++; return () => true; });
        await Assert.ThrowsAsync<InvalidOperationException>(() => output.PrepareCaptureAsync(CancellationToken.None));
        Assert.AreEqual(0, nativeGuards);
    }

    [TestMethod]
    public async Task ReadinessWithoutPreparedTargetKeepsOriginalDeadline()
    {
        long now = 0;
        using var output = new LiveClipboardPaster(() => throw new AssertFailedException(), (_, _) => Assert.Fail(),
            tickCount: () => now, acquireCapture: _ => Task.FromResult<IDisposable>(new Lease()),
            createReadiness: () => () => { now = 1090; return true; });
        await output.PrepareCaptureAsync(CancellationToken.None);
        now = 5000;
        Assert.Throws<InvalidOperationException>(output.CaptureTarget);
    }

    [TestMethod]
    public void FinalValidationCannotRestartOrReviveCancelledPreparation()
    {
        long now = 0;
        using var preparation = new CapturePreparation(CancellationToken.None, () => true, _ => { }, () => now);
        now = 1090;
        preparation.BeginFinalValidation();
        now = 5000;
        Assert.Throws<InvalidOperationException>(preparation.BeginFinalValidation);
        now = 6090;
        Assert.Throws<OperationCanceledException>(preparation.ThrowIfExpired);
        Assert.Throws<OperationCanceledException>(preparation.BeginFinalValidation);

        using var cancelled = new CancellationTokenSource();
        using var other = new CapturePreparation(cancelled.Token, () => true, _ => { }, () => now);
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(other.BeginFinalValidation);
    }

    [TestMethod]
    public async Task CancelledFinalNativeReadRetainsLeaseUntilWorkerReturns()
    {
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        using var worker = new ApartmentWorker(ApartmentState.MTA, "Final capture validation test");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probes = 0;
        using var output = new LiveClipboardPaster(() => throw new AssertFailedException(), (_, _) => Assert.Fail(),
            automationWorker: worker,
            acquireCapture: _ => Task.FromResult<IDisposable>(new Lease(() => released.TrySetResult())),
            createPreparedReadiness: () => new(() =>
            {
                if (++probes == 2) { entered.TrySetResult(); release.Wait(); }
                return true;
            }, () => new Target()));
        var preparing = output.PrepareCaptureAsync(cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => preparing.WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.IsFalse(released.Task.IsCompleted,
                "The cancelled native call still owns its provider lease until the serial MTA drains.");
            release.Set();
            await released.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally { release.Set(); }
    }

    private sealed class Fixture : IDisposable
    {
        public long Now;
        public int NativeGuards, Probes, Captures, Pastes;
        public bool SameIdentity = true, PreparationUnchanged = true, NativeIntentUnchanged = true;
        public bool DeferConfirmation;
        public Action? DuringFinalProbe, Confirm;
        public CancellationTokenSource Cancellation { get; } = new();
        public Target Target { get; } = new();
        public Recording Recording { get; } = new();
        public LiveClipboardPaster Output { get; }
        public DictationWorkflow Workflow { get; }
        public Fixture(long readiness, long validation)
        {
            Output = new LiveClipboardPaster(() => { Captures++; throw new AssertFailedException("No recapture."); },
                (_, _) => { Pastes++; Assert.Fail("Preparation permits no paste."); }, tickCount: () => Now,
                acquireCapture: _ => Task.FromResult<IDisposable>(new Lease()),
                beginPreparation: () => () => PreparationUnchanged,
                beginCaptureGuard: () => { NativeGuards++; return () => NativeIntentUnchanged; },
                createPreparedReadiness: () => new(() =>
                {
                    if (++Probes == 1) Now = readiness;
                    else { Now += validation; DuringFinalProbe?.Invoke(); }
                    return SameIdentity;
                }, () => Target),
                confirmCapture: action => { if (DeferConfirmation) Confirm = action; else action(); });
            Workflow = new DictationWorkflow(Recording, Output, new SessionHistory());
        }
        public Task Begin() => Workflow.HandleAsync(DictationIntent.BeginHold, Cancellation.Token);
        public Task Cancel() => Workflow.HandleAsync(DictationIntent.Cancel, CancellationToken.None);
        public void Dispose() { Output.Dispose(); Cancellation.Dispose(); }
    }

    private sealed class Lease(Action? released = null) : IDisposable
    { public void Dispose() => released?.Invoke(); }

    private sealed class Target : IWindowsTextTarget
    {
        public bool Prepared = true, Focused = true;
        public Action? DuringPreparedCheck;
        public bool IsFocused => Focused;
        public bool SupportsReplacement => true;
        public bool CanPasteFallback => true;
        public bool IsPreparedSelectionCurrent
        { get { DuringPreparedCheck?.Invoke(); return Prepared && Focused; } }
        public bool HasAttemptedWrite => false;
        public TextTargetUpdateResult TryReplace(string previous, string replacement, Action<string> paste)
            => throw new AssertFailedException("Preparation permits no write.");
    }

    private sealed class Recording : IDictationSession, IDictationSessionFactory
    {
        public int Starts;
        public event Action<TranscriptUpdate>? TranscriptUpdated { add { } remove { } }
        public IDictationSession CreateSession() => this;
        public Task StartAsync(CancellationToken cancellationToken) { Starts++; return Task.CompletedTask; }
        public Task CancelAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<DictationSessionResult> StopAsync(CancellationToken cancellationToken) => throw new AssertFailedException();
    }
}
