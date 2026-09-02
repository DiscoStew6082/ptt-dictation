using PttDictation.Core;

namespace PttDictation.Tests;

[TestClass]
public sealed class DictationSessionOwnershipTests
{
    [TestMethod]
    public async Task BatchSessionDeletesTemporaryAudioBeforePasteBegins()
    {
        var path = Path.GetTempFileName();
        try
        {
            var paster = new FileObservingClipboardPaster(path);
            var workflow = new DictationWorkflow(
                new SingleAudioRecorder(path),
                new ConstantTranscriber("privacy first"),
                paster,
                new SessionHistory());

            await workflow.HandleAsync(DictationIntent.BeginHold, CancellationToken.None);
            await workflow.HandleAsync(DictationIntent.EndHold, CancellationToken.None);

            Assert.IsFalse(paster.FileExistedWhenPasteBegan);
            Assert.IsFalse(File.Exists(path));
            Assert.AreEqual(DictationWorkflowPhase.Pasted, workflow.CurrentState.Phase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task WorkflowCarriesSessionCleanupWarningIntoTerminalState()
    {
        var workflow = new DictationWorkflow(
            new SingleSessionFactory(new WarningSession("cleanup-warning.wav")),
            new FileObservingClipboardPaster("unused.wav"),
            new SessionHistory());

        await workflow.HandleAsync(DictationIntent.BeginHold, CancellationToken.None);
        await workflow.HandleAsync(DictationIntent.EndHold, CancellationToken.None);

        Assert.AreEqual(DictationWorkflowPhase.Pasted, workflow.CurrentState.Phase);
        Assert.AreEqual("cleanup-warning.wav", workflow.CurrentState.CleanupWarningPath);
    }

    [TestMethod]
    public async Task ChunkedSessionDeletesFinalAudioBeforeReturning()
    {
        var path = Path.GetTempFileName();
        try
        {
            var recorder = new ChunkRecorder(path);
            var session = new ChunkedTranscribingDictationSession(
                recorder,
                new ConstantTranscriber("preview"),
                new ConstantTranscriber("final"));

            await session.StartAsync(CancellationToken.None);
            var result = await session.StopAsync(CancellationToken.None);

            Assert.AreEqual("final", result.Transcript.Text);
            Assert.IsNull(result.CleanupWarningPath);
            Assert.IsFalse(File.Exists(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task FailedTranscriptionPreservesTemporaryAudioCleanupWarning()
    {
        var path = Path.GetTempFileName();
        try
        {
            using var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var workflow = new DictationWorkflow(
                new SingleAudioRecorder(path),
                new ThrowingTranscriber(),
                new FileObservingClipboardPaster(path),
                new SessionHistory());

            await workflow.HandleAsync(DictationIntent.BeginHold, CancellationToken.None);
            await workflow.HandleAsync(DictationIntent.EndHold, CancellationToken.None);

            Assert.AreEqual(DictationWorkflowPhase.Failed, workflow.CurrentState.Phase);
            Assert.AreEqual(path, workflow.CurrentState.CleanupWarningPath);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task CancelledRecordingPreservesTemporaryAudioCleanupWarning()
    {
        var path = Path.GetTempFileName();
        try
        {
            using var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var workflow = new DictationWorkflow(
                new SingleAudioRecorder(path),
                new ConstantTranscriber("unused"),
                new FileObservingClipboardPaster(path),
                new SessionHistory());

            await workflow.HandleAsync(DictationIntent.BeginHold, CancellationToken.None);
            await workflow.HandleAsync(DictationIntent.Cancel, CancellationToken.None);

            Assert.AreEqual(DictationWorkflowPhase.Cancelled, workflow.CurrentState.Phase);
            Assert.AreEqual(path, workflow.CurrentState.CleanupWarningPath);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task ChunkedSessionPreservesPreviewChunkCleanupWarning()
    {
        var chunkPath = Path.GetTempFileName();
        var finalPath = Path.GetTempFileName();
        try
        {
            using var locked = new FileStream(chunkPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var recorder = new ChunkRecorder(finalPath);
            var session = new ChunkedTranscribingDictationSession(
                recorder,
                new ConstantTranscriber("preview"),
                new ConstantTranscriber("final"));

            await session.StartAsync(CancellationToken.None);
            recorder.PublishChunk(new RecordedAudio(
                chunkPath,
                TimeSpan.FromSeconds(1),
                DeleteAfterUse: true));
            var result = await session.StopAsync(CancellationToken.None);

            Assert.AreEqual(chunkPath, result.CleanupWarningPath);
        }
        finally
        {
            File.Delete(chunkPath);
            File.Delete(finalPath);
        }
    }

    [TestMethod]
    public async Task ChunkedSessionReleasesPreviewChunkBeforeReturningWhenPreviewIgnoresCancellation()
    {
        var chunkPath = Path.GetTempFileName();
        var finalPath = Path.GetTempFileName();
        try
        {
            using var locked = new FileStream(chunkPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var previewTranscriber = new DelayedTranscriber(
                "preview",
                TimeSpan.FromMilliseconds(2500));
            var recorder = new ChunkRecorder(finalPath);
            var session = new ChunkedTranscribingDictationSession(
                recorder,
                previewTranscriber,
                new ConstantTranscriber("final"));

            await session.StartAsync(CancellationToken.None);
            recorder.PublishChunk(new RecordedAudio(
                chunkPath,
                TimeSpan.FromSeconds(1),
                DeleteAfterUse: true));
            await previewTranscriber.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

            var result = await session.StopAsync(CancellationToken.None);

            Assert.AreEqual(chunkPath, result.CleanupWarningPath);
        }
        finally
        {
            File.Delete(chunkPath);
            File.Delete(finalPath);
        }
    }

    [TestMethod]
    public async Task ProcessingCancellationPreservesLateSessionCleanupWarning()
    {
        var session = new CleanupOnCancelledStopSession("processing-leftover.wav");
        var workflow = new DictationWorkflow(
            new SingleSessionFactory(session),
            new FileObservingClipboardPaster("unused.wav"),
            new SessionHistory());

        await workflow.HandleAsync(DictationIntent.BeginHold, CancellationToken.None);
        var finish = workflow.HandleAsync(DictationIntent.EndHold, CancellationToken.None);
        await session.StopStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await workflow.HandleAsync(DictationIntent.Cancel, CancellationToken.None);
        await finish;

        Assert.AreEqual(DictationWorkflowPhase.Cancelled, workflow.CurrentState.Phase);
        Assert.AreEqual("processing-leftover.wav", workflow.CurrentState.CleanupWarningPath);
    }

    [TestMethod]
    public async Task ProcessingCancellationPreservesCleanupWarningReturnedBySessionResult()
    {
        var session = new ResultWarningAfterCancellationSession("result-only-leftover.wav");
        var workflow = new DictationWorkflow(
            new SingleSessionFactory(session),
            new FileObservingClipboardPaster("unused.wav"),
            new SessionHistory());

        await workflow.HandleAsync(DictationIntent.BeginHold, CancellationToken.None);
        var finish = workflow.HandleAsync(DictationIntent.EndHold, CancellationToken.None);
        await session.StopStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await workflow.HandleAsync(DictationIntent.Cancel, CancellationToken.None);
        await finish;

        Assert.AreEqual(DictationWorkflowPhase.Cancelled, workflow.CurrentState.Phase);
        Assert.AreEqual("result-only-leftover.wav", workflow.CurrentState.CleanupWarningPath);
    }

    private sealed class SingleAudioRecorder(string path) : IAudioRecorder
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<RecordedAudio> StopAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(new RecordedAudio(path, TimeSpan.FromSeconds(1), DeleteAfterUse: true));
        }
    }

    private sealed class ChunkRecorder(string path) : IChunkedAudioRecorder
    {
        public event Action<RecordedAudio>? AudioChunkReady;

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<RecordedAudio> StopAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(new RecordedAudio(path, TimeSpan.FromSeconds(1), DeleteAfterUse: true));
        }

        public void PublishChunk(RecordedAudio audio) => AudioChunkReady?.Invoke(audio);
    }

    private sealed class ConstantTranscriber(string text) : ITranscriber
    {
        public Task<TranscriptResult> TranscribeAsync(string wavPath, CancellationToken cancellationToken)
        {
            return Task.FromResult(new TranscriptResult(text, null, null));
        }
    }

    private sealed class DelayedTranscriber(string text, TimeSpan delay) : ITranscriber
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<TranscriptResult> TranscribeAsync(string wavPath, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(delay, CancellationToken.None);
            return new TranscriptResult(text, null, null);
        }
    }

    private sealed class ThrowingTranscriber : ITranscriber
    {
        public Task<TranscriptResult> TranscribeAsync(string wavPath, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("transcription failed");
        }
    }

    private sealed class FileObservingClipboardPaster(string path) : IClipboardPaster
    {
        public bool FileExistedWhenPasteBegan { get; private set; }

        public void CaptureTarget()
        {
        }

        public Task PasteAsync(string text, CancellationToken cancellationToken)
        {
            FileExistedWhenPasteBegan = File.Exists(path);
            return Task.CompletedTask;
        }
    }

    private sealed class SingleSessionFactory(IDictationSession session) : IDictationSessionFactory
    {
        public IDictationSession CreateSession() => session;
    }

    private sealed class WarningSession(string cleanupWarningPath) : IDictationSession
    {
        public event Action<TranscriptUpdate>? TranscriptUpdated
        {
            add { }
            remove { }
        }

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<DictationSessionResult> StopAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(new DictationSessionResult(
                new TranscriptResult("warning transcript", null, null),
                CleanupWarningPath: cleanupWarningPath));
        }

        public Task CancelAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class CleanupOnCancelledStopSession(string cleanupWarningPath) : IDictationSession
    {
        public event Action<TranscriptUpdate>? TranscriptUpdated
        {
            add { }
            remove { }
        }

        public string? CleanupWarningPath { get; private set; }

        public TaskCompletionSource StopStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public async Task<DictationSessionResult> StopAsync(CancellationToken cancellationToken)
        {
            StopStarted.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("Unreachable");
            }
            catch (OperationCanceledException)
            {
                CleanupWarningPath = cleanupWarningPath;
                throw;
            }
        }

        public Task CancelAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class ResultWarningAfterCancellationSession(string cleanupWarningPath) : IDictationSession
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
            var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = cancellationToken.Register(cancellationObserved.SetResult);
            await cancellationObserved.Task;
            return new DictationSessionResult(
                new TranscriptResult("cancelled transcript", null, null),
                CleanupWarningPath: cleanupWarningPath);
        }

        public Task CancelAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
