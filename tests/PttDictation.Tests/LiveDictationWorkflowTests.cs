using PttDictation.Core;

namespace PttDictation.Tests;

[TestClass]
public sealed class LiveDictationWorkflowTests
{
    [TestMethod]
    public async Task LivePreviewAndFinalRecognitionRemainSeparatelyInspectable()
    {
        var session = new FakeDictationSession("the final textbook is different");
        var output = new CapturingLiveOutput();
        var history = new SessionHistory();
        var workflow = new DictationWorkflow(new FakeDictationSessionFactory(session), output, history,
            () => [new TranscriptCorrection("textbook", "textbox")]);

        await workflow.HandleAsync(DictationIntent.BeginHold, CancellationToken.None);
        session.PublishPartial("the preview textbook");
        Assert.AreEqual("the preview textbox", output.Preview);
        await workflow.HandleAsync(DictationIntent.EndHold, CancellationToken.None);

        var comparison = history.Entries.Single().Comparison!;
        Assert.AreEqual("the preview textbook", comparison.RawPreview);
        Assert.AreEqual("the preview textbox", comparison.LastPreview);
        Assert.AreEqual("the final textbook is different", comparison.RawFinal);
        Assert.AreEqual("the final textbox is different", comparison.CorrectedFinal);
        Assert.AreEqual("The final textbox is different.", comparison.FinalText);
        Assert.AreEqual(comparison.FinalText, output.Final);
        Assert.IsTrue(output.Ended);
    }

    [TestMethod]
    public async Task FailedInsertionStillKeepsCompletedRecognitionForRecovery()
    {
        var output = new CapturingLiveOutput { FailInsertion = true };
        var history = new SessionHistory();
        var workflow = new DictationWorkflow(new FakeAudioRecorder("recording.wav"),
            new FakeTranscriber("a whole minute of speech must survive"), output, history);
        await workflow.HandleAsync(DictationIntent.BeginHold, CancellationToken.None);
        await workflow.HandleAsync(DictationIntent.EndHold, CancellationToken.None);

        Assert.AreEqual(DictationWorkflowPhase.Failed, workflow.CurrentState.Phase);
        Assert.AreEqual("A whole minute of speech must survive.", history.Items.Single());
        Assert.AreEqual(history.Items.Single(), history.Entries.Single().Comparison!.FinalText);
        Assert.IsTrue(output.Ended);
    }

    [TestMethod]
    public async Task EndingRecordingStopsAcceptingLatePreviewUpdates()
    {
        var session = new FakeDictationSession("final");
        var output = new CapturingLiveOutput();
        var workflow = new DictationWorkflow(new FakeDictationSessionFactory(session), output, new SessionHistory());
        await workflow.HandleAsync(DictationIntent.BeginHold, CancellationToken.None);
        session.PublishPartial("preview");
        await workflow.HandleAsync(DictationIntent.EndHold, CancellationToken.None);
        session.PublishPartial("stale preview");
        Assert.AreEqual("preview", output.Preview);
        Assert.AreEqual("Final.", output.Final);
    }

    [TestMethod]
    public async Task EmptyFinalRecognitionPreservesNonemptyPreviewForRecovery()
    {
        var session = new FakeDictationSession("");
        var output = new CapturingLiveOutput();
        var history = new SessionHistory();
        var workflow = new DictationWorkflow(new FakeDictationSessionFactory(session), output, history);
        await workflow.HandleAsync(DictationIntent.BeginHold, CancellationToken.None);
        session.PublishPartial("speech that must survive");
        await workflow.HandleAsync(DictationIntent.EndHold, CancellationToken.None);
        Assert.AreEqual("speech that must survive", history.Items.Single());
        Assert.AreEqual("", history.Entries.Single().Comparison!.RawFinal);
        Assert.AreEqual(DictationWorkflowPhase.Failed, workflow.CurrentState.Phase);
        Assert.IsNull(output.Final);
        Assert.IsTrue(output.Ended);
    }

    [TestMethod]
    public async Task CancelRevokesInsertionBeforeWaitingForRecorderCleanup()
    {
        var session = new SlowCancelSession();
        var output = new CapturingLiveOutput();
        var workflow = new DictationWorkflow(new SingleDictationSessionFactory(session), output, new SessionHistory());
        await workflow.HandleAsync(DictationIntent.BeginHold, CancellationToken.None);
        var cancel = workflow.HandleAsync(DictationIntent.Cancel, CancellationToken.None);
        await session.Cancelling.Task;
        Assert.IsTrue(output.Ended);
        Assert.IsFalse(cancel.IsCompleted);
        session.Release.TrySetResult();
        await cancel;
    }

    private sealed class SlowCancelSession : IDictationSession
    {
        public event Action<TranscriptUpdate>? TranscriptUpdated { add { } remove { } }
        public TaskCompletionSource Cancelling { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<DictationSessionResult> StopAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public async Task CancelAsync(CancellationToken cancellationToken)
        {
            Cancelling.TrySetResult();
            await Release.Task;
        }
    }

    private sealed class CapturingLiveOutput : ILiveClipboardPaster
    {
        public string? Preview { get; private set; }
        public string? Final { get; private set; }
        public bool Ended { get; private set; }
        public bool FailInsertion { get; init; }
        public void CaptureTarget() { }
        public void UpdatePreview(string text) => Preview = text;
        public void EndSession() => Ended = true;
        public Task PasteAsync(string text, CancellationToken cancellationToken)
        {
            if (FailInsertion) throw new InvalidOperationException("The original textbox changed.");
            Final = text;
            return Task.CompletedTask;
        }
    }
}
