using PttDictation.App;
using PttDictation.Core;

namespace PttDictation.Tests;

[TestClass]
public sealed class PreparedTargetHandoffTests
{
    [TestMethod]
    public void ReadinessCaptureUsesPinnedIdentityEvenWhenGlobalFocusChangesDuringProbe()
    {
        var original = new object();
        var transient = new object();
        object focused = original;
        var target = new Target();
        var captures = 0;
        var readiness = BrowserCaptureReadiness.CreatePrepared(() => focused, ReferenceEquals, identity =>
        {
            focused = transient;
            Assert.AreSame(original, identity, "Capture must use the passed identity, never reacquire global focus.");
            captures++;
            focused = original;
            return target;
        }, ReferenceEquals);
        Assert.IsTrue(readiness.Probe());
        Assert.AreSame(target, readiness.Target);
        Assert.IsTrue(readiness.Probe());
        Assert.AreEqual(1, captures);
        focused = transient;
        Assert.Throws<InvalidOperationException>(() => readiness.Probe());
    }

    [TestMethod]
    public async Task PreparationConfirmsOriginalTargetBeforeMicrophoneAndNeverBootstrapsItAgain()
    {
        var target = new Target();
        var captureCalls = 0;
        Action? confirm = null;
        var confirmationReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var worker = new ApartmentWorker(ApartmentState.MTA, "Prepared handoff test");
        using var output = new LiveClipboardPaster(() => { captureCalls++; throw new AssertFailedException("Duplicate target bootstrap."); },
            (_, _) => { }, acquireCapture: _ => Task.FromResult<IDisposable>(new Lease()),
            automationWorker: worker,
            createPreparedReadiness: () => new(() => true, () => target),
            confirmCapture: action => { confirm = action; confirmationReady.TrySetResult(); });
        var session = new Recording();
        var workflow = new DictationWorkflow(session, output, new SessionHistory());
        var beginning = workflow.HandleAsync(DictationIntent.BeginHold, CancellationToken.None);
        await confirmationReady.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(0, session.Starts);
        Assert.IsFalse(beginning.IsCompleted);
        Assert.AreEqual(1, target.SelectionChecks);
        Assert.AreEqual(ApartmentState.MTA, target.ValidationApartment);
        confirm!();
        await beginning.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(1, session.Starts);
        Assert.AreEqual(0, captureCalls);
        output.UpdatePreview("words");
        output.Pump();
        await worker.InvokeAsync(() => { });
        Assert.AreEqual(1, target.Replacements, "Use the exact prepared object for insertion.");
        await workflow.HandleAsync(DictationIntent.Cancel, CancellationToken.None);
    }

    [TestMethod]
    [DataRow("identity")]
    [DataRow("selection")]
    [DataRow("native-intent")]
    public async Task ChangedIdentitySelectionOrNativeIntentDuringFinalValidationRejectsPreparation(string change)
    {
        var target = new Target();
        var sameIdentity = true;
        var unchanged = true;
        using var output = new LiveClipboardPaster(() => throw new AssertFailedException("Must not recapture another target."),
            (_, _) => Assert.Fail("Preparation grants no paste permission."),
            acquireCapture: _ => Task.FromResult<IDisposable>(new Lease()),
            createPreparedReadiness: () => new(() => sameIdentity, () => target),
            beginCaptureGuard: () =>
            {
                if (change == "identity") sameIdentity = false;
                if (change == "selection") target.Prepared = false;
                if (change == "native-intent") unchanged = false;
                return () => unchanged;
            });
        await Assert.ThrowsAsync<InvalidOperationException>(() => output.PrepareCaptureAsync(CancellationToken.None));
        Assert.AreEqual(0, target.Replacements);
    }

    [TestMethod]
    public async Task CancellationBeforeQueuedConfirmationCannotPublishPreparedTarget()
    {
        Action? confirm = null;
        using var cancellation = new CancellationTokenSource();
        using var output = new LiveClipboardPaster(() => throw new AssertFailedException(), (_, _) => { },
            acquireCapture: _ => Task.FromResult<IDisposable>(new Lease()),
            createPreparedReadiness: () => new(() => true, () => new Target()),
            confirmCapture: action => confirm = action);
        var preparing = output.PrepareCaptureAsync(cancellation.Token);
        Assert.IsNotNull(confirm);
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => preparing);
        confirm();
        Assert.IsFalse(output.InlinePreview);
    }

    [TestMethod]
    public async Task NativeIntentChangeBetweenConfirmationAndAdoptionRejectsHandoff()
    {
        var unchanged = true;
        using var output = new LiveClipboardPaster(() => throw new AssertFailedException(), (_, _) => { },
            acquireCapture: _ => Task.FromResult<IDisposable>(new Lease()),
            createPreparedReadiness: () => new(() => true, () => new Target()),
            beginCaptureGuard: () => () => unchanged);
        await output.PrepareCaptureAsync(CancellationToken.None);
        unchanged = false;
        Assert.Throws<InvalidOperationException>(output.CaptureTarget);
    }

    private sealed class Lease : IDisposable { public void Dispose() { } }
    private sealed class Target : IWindowsTextTarget
    {
        public bool Prepared = true;
        public int SelectionChecks, Replacements;
        public ApartmentState ValidationApartment;
        public bool IsFocused => true;
        public bool SupportsReplacement => true;
        public bool CanPasteFallback => true;
        public bool IsPreparedSelectionCurrent
        {
            get { SelectionChecks++; ValidationApartment = Thread.CurrentThread.GetApartmentState(); return Prepared; }
        }
        public bool HasAttemptedWrite => false;
        public TextTargetUpdateResult TryReplace(string previous, string replacement, Action<string> paste)
        { Replacements++; return TextTargetUpdateResult.Success; }
    }
    private sealed class Recording : IDictationSession, IDictationSessionFactory
    {
        public event Action<TranscriptUpdate>? TranscriptUpdated { add { } remove { } }
        public int Starts;
        public IDictationSession CreateSession() => this;
        public Task StartAsync(CancellationToken cancellationToken) { Starts++; return Task.CompletedTask; }
        public Task CancelAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<DictationSessionResult> StopAsync(CancellationToken cancellationToken) => throw new AssertFailedException();
    }
}
