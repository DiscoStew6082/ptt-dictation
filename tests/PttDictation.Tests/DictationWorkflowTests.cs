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

internal sealed class LegacyDictationControllerHarness
{
    private readonly DictationWorkflow _workflow;

    public LegacyDictationControllerHarness(
        IAudioRecorder recorder,
        ITranscriber transcriber,
        IClipboardPaster clipboardPaster,
        SessionHistory history,
        Action<string>? finalTranscriptReady = null,
        Action<string>? cleanupWarningReady = null,
        Func<IReadOnlyList<TranscriptCorrection>>? getTranscriptCorrections = null)
        : this(
            new BatchDictationSessionFactoryForTest(recorder, transcriber),
            clipboardPaster,
            history,
            finalTranscriptReady,
            cleanupWarningReady,
            getTranscriptCorrections: getTranscriptCorrections)
    {
    }

    public LegacyDictationControllerHarness(
        IDictationSessionFactory sessionFactory,
        IClipboardPaster clipboardPaster,
        SessionHistory history,
        Action<string>? finalTranscriptReady = null,
        Action<string>? cleanupWarningReady = null,
        Action<TranscriptUpdate>? transcriptUpdateReady = null,
        Func<IReadOnlyList<TranscriptCorrection>>? getTranscriptCorrections = null)
    {
        _workflow = new DictationWorkflow(
            sessionFactory,
            clipboardPaster,
            history,
            getTranscriptCorrections);
        var previous = _workflow.CurrentState;
        _workflow.StateChanged += state =>
        {
            if (state.Phase == DictationWorkflowPhase.Recording
                && !string.IsNullOrWhiteSpace(state.Transcript))
            {
                transcriptUpdateReady?.Invoke(new TranscriptUpdate(
                    TranscriptUpdateKind.Partial,
                    state.Transcript));
            }
            else if (state.Phase == DictationWorkflowPhase.Processing
                     && previous.Phase == DictationWorkflowPhase.Processing
                     && !string.IsNullOrWhiteSpace(state.Transcript))
            {
                finalTranscriptReady?.Invoke(state.Transcript);
                transcriptUpdateReady?.Invoke(new TranscriptUpdate(
                    TranscriptUpdateKind.Final,
                    state.Transcript));
            }

            if (!string.IsNullOrWhiteSpace(state.CleanupWarningPath))
            {
                cleanupWarningReady?.Invoke(state.CleanupWarningPath);
            }

            previous = state;
        };
    }

    public async Task<bool> HandleHotkeyDownAsync(CancellationToken cancellationToken)
    {
        var previous = _workflow.CurrentState.Phase;
        await _workflow.HandleAsync(DictationIntent.BeginHold, cancellationToken);
        if (_workflow.CurrentState.Phase == DictationWorkflowPhase.Failed)
        {
            throw new InvalidOperationException(_workflow.CurrentState.ErrorMessage);
        }

        return previous != DictationWorkflowPhase.Recording
            && _workflow.CurrentState.Phase == DictationWorkflowPhase.Recording;
    }

    public async Task<DictationOutcome> HandleHotkeyUpAsync(CancellationToken cancellationToken)
    {
        if (_workflow.CurrentState.Phase != DictationWorkflowPhase.Recording)
        {
            return DictationOutcome.NotRecording;
        }

        await _workflow.HandleAsync(DictationIntent.EndHold, cancellationToken);
        return _workflow.CurrentState.Phase switch
        {
            DictationWorkflowPhase.Pasted => DictationOutcome.Pasted,
            DictationWorkflowPhase.Empty => DictationOutcome.EmptyTranscript,
            DictationWorkflowPhase.Failed => throw new InvalidOperationException(_workflow.CurrentState.ErrorMessage),
            _ => DictationOutcome.NotRecording
        };
    }
}

internal enum DictationOutcome
{
    NotRecording,
    EmptyTranscript,
    Pasted
}

internal sealed class BatchDictationSessionFactoryForTest(
    IAudioRecorder recorder,
    ITranscriber transcriber) : IDictationSessionFactory
{
    public IDictationSession CreateSession()
    {
        return new BatchDictationSessionForTest(recorder, transcriber);
    }
}

internal sealed class BatchDictationSessionForTest(
    IAudioRecorder recorder,
    ITranscriber transcriber) : IDictationSession
{
    public event Action<TranscriptUpdate>? TranscriptUpdated
    {
        add { }
        remove { }
    }

    public Task StartAsync(CancellationToken cancellationToken) => recorder.StartAsync(cancellationToken);

    public async Task<DictationSessionResult> StopAsync(CancellationToken cancellationToken)
    {
        var audio = await recorder.StopAsync(cancellationToken);
        var transcript = await transcriber.TranscribeAsync(audio.Path, cancellationToken);
        return new DictationSessionResult(transcript, audio);
    }

    public async Task CancelAsync(CancellationToken cancellationToken)
    {
        var audio = await recorder.StopAsync(cancellationToken);
        if (audio.DeleteAfterUse && File.Exists(audio.Path))
        {
            File.Delete(audio.Path);
        }
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
