using PttDictation.App;
using PttDictation.Core;

namespace PttDictation.Tests;

[TestClass]
public sealed class InitialCaptureFailureTests
{
    [TestMethod]
    public async Task RejectionAfterUserReleasesHoldCancelsPendingFinalInferenceAndKeepsCaptureError()
    {
        var path = Path.GetTempFileName();
        var recorder = new Recorder(path);
        var model = new ColdModel();
        Action? confirm = null;
        using var output = new LiveClipboardPaster(() => new WindowsTextTarget(new Surface()),
            (_, _) => Assert.Fail("Rejected capture must not paste."), confirmCapture: action => confirm = action);
        var workflow = new DictationWorkflow(
            new ChunkedTranscribingDictationSessionFactory(recorder, model), output, new SessionHistory());
        await workflow.HandleAsync(DictationIntent.BeginHold, CancellationToken.None);
        var finish = workflow.HandleAsync(DictationIntent.EndHold, CancellationToken.None);
        try
        {
            Assert.AreEqual(1, model.Transcriptions);
            Assert.IsFalse(finish.IsCompleted);
            Assert.IsNotNull(confirm);
            confirm();
            await finish.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.AreEqual(DictationWorkflowPhase.Failed, workflow.CurrentState.Phase);
            Assert.AreEqual("This field cannot safely receive dictation.", workflow.CurrentState.ErrorMessage);
            Assert.AreEqual(1, recorder.Stops);
            Assert.IsFalse(recorder.Recording);
            Assert.IsFalse(File.Exists(path));
        }
        finally
        {
            model.Ready.TrySetResult();
            await finish.WaitAsync(TimeSpan.FromSeconds(2));
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task RejectedCapturePreservesOriginalErrorWhenMicrophoneCleanupFails()
    {
        var recorder = new Recorder("unused.wav") { StopError = new InvalidOperationException("Microphone stop failed.") };
        var model = new ColdModel();
        using var output = new LiveClipboardPaster(() => new WindowsTextTarget(new Surface()),
            (_, _) => Assert.Fail("Rejected capture must not paste."));
        var workflow = new DictationWorkflow(
            new ChunkedTranscribingDictationSessionFactory(recorder, model), output, new SessionHistory());
        try
        {
            await workflow.HandleAsync(DictationIntent.BeginHold, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));
            Assert.AreEqual(DictationWorkflowPhase.Failed, workflow.CurrentState.Phase);
            StringAssert.Contains(workflow.CurrentState.ErrorMessage!, "field");
            StringAssert.Contains(workflow.CurrentState.ErrorMessage!, "Microphone stop failed.");
            Assert.AreEqual(1, recorder.Stops);
            Assert.AreEqual(0, model.Transcriptions);
        }
        finally { model.Ready.TrySetResult(); }
    }

    [TestMethod]
    public async Task CancellationDuringRejectedCaptureCleanupDoesNotBecomeAnErrorOrStopTwice()
    {
        var path = Path.GetTempFileName();
        var recorder = new Recorder(path) { StopRelease = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var model = new ColdModel();
        using var output = new LiveClipboardPaster(() => new WindowsTextTarget(new Surface()),
            (_, _) => Assert.Fail("Rejected capture must not paste."));
        var workflow = new DictationWorkflow(
            new ChunkedTranscribingDictationSessionFactory(recorder, model), output, new SessionHistory());
        var start = workflow.HandleAsync(DictationIntent.BeginHold, CancellationToken.None);
        try
        {
            Assert.AreEqual(1, recorder.Stops);
            await workflow.HandleAsync(DictationIntent.Cancel, CancellationToken.None);
            recorder.StopRelease.TrySetResult();
            await start.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.AreEqual(DictationWorkflowPhase.Cancelled, workflow.CurrentState.Phase);
            Assert.AreEqual(1, recorder.Stops);
            Assert.AreEqual(0, model.Transcriptions);
            Assert.IsFalse(File.Exists(path));
        }
        finally
        {
            model.Ready.TrySetResult();
            recorder.StopRelease.TrySetResult();
            await start.WaitAsync(TimeSpan.FromSeconds(2));
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task RejectedInitialFieldReleasesMicrophoneWithoutWaitingForColdFinalModel()
    {
        var path = Path.GetTempFileName();
        var recorder = new Recorder(path);
        var model = new ColdModel();
        var surface = new Surface();
        using var output = new LiveClipboardPaster(() => new WindowsTextTarget(surface),
            (_, _) => Assert.Fail("A rejected target must never receive a paste."));
        var history = new SessionHistory();
        var workflow = new DictationWorkflow(
            new ChunkedTranscribingDictationSessionFactory(recorder, model, model), output, history);
        var start = workflow.HandleAsync(DictationIntent.BeginHold, CancellationToken.None);
        try
        {
            await start.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.AreEqual(DictationWorkflowPhase.Failed, workflow.CurrentState.Phase);
            StringAssert.Contains(workflow.CurrentState.ErrorMessage!, "field");
            Assert.IsFalse(recorder.Recording);
            Assert.AreEqual(1, recorder.Stops);
            Assert.AreEqual(0, model.Transcriptions,
                "A known capture rejection must not wait for final model startup.");
            Assert.IsFalse(File.Exists(path));
            Assert.AreEqual(0, history.Items.Count);

            surface.Writable = true;
            await workflow.HandleAsync(DictationIntent.BeginHold, CancellationToken.None);
            Assert.AreEqual(DictationWorkflowPhase.Recording, workflow.CurrentState.Phase);
            Assert.IsTrue(recorder.Recording);
            await workflow.HandleAsync(DictationIntent.Cancel, CancellationToken.None);
        }
        finally
        {
            model.Ready.TrySetResult();
            await start.WaitAsync(TimeSpan.FromSeconds(2));
            await workflow.HandleAsync(DictationIntent.Cancel, CancellationToken.None);
            File.Delete(path);
        }
    }

    private sealed class Recorder(string path) : IChunkedAudioRecorder
    {
        public bool Recording;
        public int Stops;
        public Exception? StopError;
        public TaskCompletionSource? StopRelease;
        public event Action<RecordedAudio>? AudioChunkReady { add { } remove { } }
        public Task StartAsync(CancellationToken cancellationToken)
        {
            Recording = true;
            return Task.CompletedTask;
        }
        public async Task<RecordedAudio> StopAsync(CancellationToken cancellationToken)
        {
            Recording = false;
            Stops++;
            if (StopRelease is not null) await StopRelease.Task;
            if (StopError is not null) throw StopError;
            return new RecordedAudio(path, TimeSpan.FromMilliseconds(57), DeleteAfterUse: true);
        }
    }

    private sealed class ColdModel : ITranscriber, IWarmableTranscriber
    {
        public readonly TaskCompletionSource Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Transcriptions;
        public Task WarmUpAsync(CancellationToken cancellationToken) => Ready.Task.WaitAsync(cancellationToken);
        public async Task<TranscriptResult> TranscribeAsync(string wavPath, CancellationToken cancellationToken)
        {
            Transcriptions++;
            await Ready.Task.WaitAsync(cancellationToken);
            return new TranscriptResult("", null, null);
        }
    }

    private sealed class Surface : IWindowsTextSurface
    {
        public bool Writable;
        public bool IsFocused => true;
        public bool SupportsReplacement => Writable;
        public bool CanPasteFallback => Writable;
        public TextTargetSnapshot Read() => new("", "", "", "");
        public bool Select(string prefix, string ownedText, string suffix) => true;
        public void RevealCaret() { }
    }
}
