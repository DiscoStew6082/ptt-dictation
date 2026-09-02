using PttDictation.Core;

namespace PttDictation.Tests;

[TestClass]
public sealed class DictationWorkflowTests
{
    [TestMethod]
    public async Task HoldWorkflowPublishesSemanticStatesAndPastes()
    {
        var states = new List<DictationWorkflowState>();
        var history = new SessionHistory();
        var paster = new FakeClipboardPaster();
        var workflow = new DictationWorkflow(
            new FakeAudioRecorder("utterance.wav"),
            new FakeTranscriber("hello workflow"),
            paster,
            history);
        workflow.StateChanged += states.Add;

        await workflow.HandleAsync(DictationIntent.BeginHold, CancellationToken.None);
        await workflow.HandleAsync(DictationIntent.EndHold, CancellationToken.None);

        CollectionAssert.AreEqual(
            new[]
            {
                DictationWorkflowPhase.Recording,
                DictationWorkflowPhase.Processing,
                DictationWorkflowPhase.Processing,
                DictationWorkflowPhase.Pasted
            },
            states.Select(state => state.Phase).ToArray());
        Assert.AreEqual(DictationTriggerMode.Hold, states[0].TriggerMode);
        Assert.AreEqual("Hello workflow.", workflow.CurrentState.Transcript);
        Assert.AreEqual("Hello workflow.", paster.PastedText);
        CollectionAssert.AreEqual(new[] { "Hello workflow." }, history.Items.ToArray());
    }

    [TestMethod]
    public async Task ToggleWorkflowOwnsStartAndFinishSemantics()
    {
        var recorder = new FakeAudioRecorder("utterance.wav");
        var workflow = new DictationWorkflow(
            recorder,
            new FakeTranscriber("toggle text"),
            new FakeClipboardPaster(),
            new SessionHistory());

        await workflow.HandleAsync(DictationIntent.Toggle, CancellationToken.None);
        await workflow.HandleAsync(DictationIntent.EndHold, CancellationToken.None);

        Assert.AreEqual(DictationWorkflowPhase.Recording, workflow.CurrentState.Phase);
        Assert.AreEqual(DictationTriggerMode.Toggle, workflow.CurrentState.TriggerMode);
        Assert.AreEqual(0, recorder.StopCount);

        await workflow.HandleAsync(DictationIntent.Toggle, CancellationToken.None);

        Assert.AreEqual(DictationWorkflowPhase.Pasted, workflow.CurrentState.Phase);
        Assert.AreEqual(1, recorder.StopCount);
    }

    [TestMethod]
    public async Task CancelDuringRecordingDiscardsPreviewAudioPasteAndHistory()
    {
        var session = new FakeDictationSession("must not be transcribed");
        var history = new SessionHistory();
        var paster = new FakeClipboardPaster();
        var workflow = new DictationWorkflow(
            new FakeDictationSessionFactory(session),
            paster,
            history);

        await workflow.HandleAsync(DictationIntent.BeginHold, CancellationToken.None);
        session.PublishPartial("this preview looks wrong");
        Assert.AreEqual("this preview looks wrong", workflow.CurrentState.Transcript);
        Assert.IsTrue(workflow.CurrentState.CanCancel);

        await workflow.HandleAsync(DictationIntent.Cancel, CancellationToken.None);
        await workflow.HandleAsync(DictationIntent.EndHold, CancellationToken.None);

        Assert.AreEqual(DictationWorkflowPhase.Cancelled, workflow.CurrentState.Phase);
        Assert.AreEqual(string.Empty, workflow.CurrentState.Transcript);
        Assert.AreEqual(1, session.CancelCount);
        Assert.AreEqual(0, session.StopCount);
        Assert.IsNull(paster.PastedText);
        Assert.AreEqual(0, history.Items.Count);
    }

    [TestMethod]
    public async Task CancelDuringProcessingStopsFinalizationWithoutPasteOrHistory()
    {
        var session = new BlockingStopDictationSession();
        var history = new SessionHistory();
        var paster = new FakeClipboardPaster();
        var workflow = new DictationWorkflow(
            new SingleDictationSessionFactory(session),
            paster,
            history);

        await workflow.HandleAsync(DictationIntent.BeginHold, CancellationToken.None);
        var finish = workflow.HandleAsync(DictationIntent.EndHold, CancellationToken.None);
        await session.StopStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await workflow.HandleAsync(DictationIntent.Cancel, CancellationToken.None);
        await finish;

        Assert.AreEqual(DictationWorkflowPhase.Cancelled, workflow.CurrentState.Phase);
        Assert.IsNull(paster.PastedText);
        Assert.AreEqual(0, history.Items.Count);
    }
}

internal sealed class SingleDictationSessionFactory(IDictationSession session) : IDictationSessionFactory
{
    public IDictationSession CreateSession() => session;
}

internal sealed class BlockingStopDictationSession : IDictationSession
{
    public event Action<TranscriptUpdate>? TranscriptUpdated
    {
        add { }
        remove { }
    }

    public TaskCompletionSource StopStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task<DictationSessionResult> StopAsync(CancellationToken cancellationToken)
    {
        StopStarted.SetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        throw new InvalidOperationException("Unreachable");
    }

    public Task CancelAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
