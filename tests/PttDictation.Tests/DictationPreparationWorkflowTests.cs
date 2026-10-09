using PttDictation.Core;

namespace PttDictation.Tests;

[TestClass]
public sealed class DictationPreparationWorkflowTests
{
    [TestMethod]
    public async Task ColdTextboxPreparationCompletesBeforeCaptureAndMicrophoneStart()
    {
        var calls = new List<string>();
        var paster = new Paster(calls);
        var session = new Session(calls);
        var workflow = Create(paster, session);
        var begin = workflow.HandleAsync(DictationIntent.BeginHold, CancellationToken.None);
        CollectionAssert.AreEqual(new[] { "prepare" }, calls);
        Assert.IsFalse(begin.IsCompleted);
        Assert.AreEqual(DictationWorkflowPhase.Preparing, workflow.CurrentState.Phase);
        Assert.IsTrue(workflow.CurrentState.CanCancel);

        paster.Prepared.SetResult();
        await begin.WaitAsync(TimeSpan.FromSeconds(2));
        CollectionAssert.AreEqual(new[] { "prepare", "capture", "start" }, calls);
        Assert.AreEqual(DictationWorkflowPhase.Recording, workflow.CurrentState.Phase);
        await workflow.HandleAsync(DictationIntent.Cancel, CancellationToken.None);
    }

    [TestMethod]
    [DataRow(DictationIntent.BeginHold, DictationIntent.EndHold)]
    [DataRow(DictationIntent.Toggle, DictationIntent.Toggle)]
    [DataRow(DictationIntent.BeginHold, DictationIntent.Cancel)]
    public async Task StoppingPendingStartNeverCapturesOrStartsAudioLater(DictationIntent start, DictationIntent stop)
    {
        var calls = new List<string>();
        var paster = new Paster(calls);
        var workflow = Create(paster, new Session(calls));
        var states = new List<DictationWorkflowPhase>();
        workflow.StateChanged += state => states.Add(state.Phase);
        var begin = workflow.HandleAsync(start, CancellationToken.None);
        await workflow.HandleAsync(stop, CancellationToken.None);
        await begin.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsFalse(paster.Prepared.Task.IsCompleted,
            "Cancellation must retire the pending workflow without waiting for an unresponsive provider.");
        paster.Prepared.SetResult();
        CollectionAssert.AreEqual(new[] { "prepare" }, calls);
        Assert.AreEqual(DictationWorkflowPhase.Cancelled, workflow.CurrentState.Phase);
        Assert.IsFalse(states.Contains(DictationWorkflowPhase.Recording));
    }

    [TestMethod]
    public async Task ExternalCancellationDuringPreparationNeverStartsAudio()
    {
        var calls = new List<string>();
        var paster = new Paster(calls);
        var workflow = Create(paster, new Session(calls));
        using var cancellation = new CancellationTokenSource();
        var begin = workflow.HandleAsync(DictationIntent.BeginHold, cancellation.Token);
        cancellation.Cancel();
        await begin.WaitAsync(TimeSpan.FromSeconds(2));
        paster.Prepared.SetResult();
        CollectionAssert.AreEqual(new[] { "prepare" }, calls);
        Assert.AreEqual(DictationWorkflowPhase.Cancelled, workflow.CurrentState.Phase);
    }

    [TestMethod]
    public async Task ReleasingHoldDuringMicrophoneStartupCancelsWithoutLateRecording()
    {
        var calls = new List<string>();
        var paster = new Paster(calls);
        paster.Prepared.SetResult();
        var session = new Session(calls) { BlockStart = true };
        var workflow = Create(paster, session);
        var states = new List<DictationWorkflowPhase>();
        workflow.StateChanged += state => states.Add(state.Phase);
        var begin = workflow.HandleAsync(DictationIntent.BeginHold, CancellationToken.None);
        await session.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await workflow.HandleAsync(DictationIntent.EndHold, CancellationToken.None);
        session.ReleaseStart.SetResult();
        await begin.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(1, session.CancelCount);
        Assert.AreEqual(DictationWorkflowPhase.Cancelled, workflow.CurrentState.Phase);
        Assert.IsFalse(states.Contains(DictationWorkflowPhase.Recording));
    }

    [TestMethod]
    public async Task PreparationFailureNeverStartsOrStopsUnstartedMicrophone()
    {
        var calls = new List<string>();
        var paster = new Paster(calls);
        var workflow = Create(paster, new Session(calls));
        var begin = workflow.HandleAsync(DictationIntent.BeginHold, CancellationToken.None);
        paster.Prepared.SetException(new InvalidOperationException("Textbox preparation failed."));
        await begin.WaitAsync(TimeSpan.FromSeconds(2));
        CollectionAssert.AreEqual(new[] { "prepare" }, calls);
        Assert.AreEqual(DictationWorkflowPhase.Failed, workflow.CurrentState.Phase);
        Assert.AreEqual("Textbox preparation failed.", workflow.CurrentState.ErrorMessage);
    }

    [TestMethod]
    public async Task HoldReleaseDoesNotCancelPendingToggle()
    {
        var calls = new List<string>();
        var paster = new Paster(calls);
        var workflow = Create(paster, new Session(calls));
        var begin = workflow.HandleAsync(DictationIntent.Toggle, CancellationToken.None);
        await workflow.HandleAsync(DictationIntent.EndHold, CancellationToken.None);
        paster.Prepared.SetResult();
        await begin.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(DictationWorkflowPhase.Recording, workflow.CurrentState.Phase);
        await workflow.HandleAsync(DictationIntent.Cancel, CancellationToken.None);
    }

    private static DictationWorkflow Create(Paster paster, Session session) =>
        new(new SingleDictationSessionFactory(session), paster, new SessionHistory());

    private sealed class Paster(List<string> calls) : IClipboardPaster
    {
        public TaskCompletionSource Prepared { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task PrepareCaptureAsync(CancellationToken cancellationToken)
        {
            calls.Add("prepare");
            return Prepared.Task;
        }
        public void CaptureTarget() => calls.Add("capture");
        public Task PasteAsync(string text, CancellationToken cancellationToken) => throw new AssertFailedException("No paste expected.");
    }

    private sealed class Session(List<string> calls) : IDictationSession
    {
        public bool BlockStart { get; init; }
        public int CancelCount { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseStart { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public event Action<TranscriptUpdate>? TranscriptUpdated { add { } remove { } }
        public Task StartAsync(CancellationToken cancellationToken)
        {
            calls.Add("start");
            Started.SetResult();
            return BlockStart ? ReleaseStart.Task : Task.CompletedTask;
        }
        public Task<DictationSessionResult> StopAsync(CancellationToken cancellationToken) => throw new AssertFailedException("No finalization expected.");
        public Task CancelAsync(CancellationToken cancellationToken)
        {
            calls.Add("cancel");
            CancelCount++;
            return Task.CompletedTask;
        }
    }
}
