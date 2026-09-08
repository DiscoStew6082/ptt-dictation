using System.Collections.Concurrent;
using System.Threading.Channels;
using PttDictation.App;
using PttDictation.Core;

namespace PttDictation.Tests;

[TestClass]
public sealed class CumulativePreviewTests
{
    [TestMethod]
    public async Task LatestSnapshotRevisesEarlierWordsAndSupersedesQueuedAudio()
    {
        var paths = Enumerable.Range(0, 5).Select(_ => Path.GetTempFileName()).ToArray();
        try
        {
            var recorder = new SnapshotRecorder();
            var recognizer = new ControlledRecognizer(paths[0]);
            var session = new ChunkedTranscribingDictationSession(recorder, recognizer, new FinalRecognizer());
            var previews = Channel.CreateUnbounded<string>();
            session.TranscriptUpdated += update => previews.Writer.TryWrite(update.StableText);
            await session.StartAsync(CancellationToken.None);
            recorder.Publish(paths[0], 2);
            await recognizer.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            recorder.Publish(paths[1], 2.8);
            recorder.Publish(paths[2], 3.6);
            recorder.Publish(paths[3], 4.4);
            recorder.Publish(paths[4], 2.8);
            Assert.IsFalse(File.Exists(paths[1]), "Superseded snapshots must release their audio immediately.");
            Assert.IsFalse(File.Exists(paths[2]));
            Assert.IsFalse(File.Exists(paths[4]), "Late older audio must not supersede the newest pending snapshot.");
            Assert.IsTrue(File.Exists(paths[0]), "Active inference must keep its audio.");
            Assert.IsTrue(File.Exists(paths[3]));
            recognizer.ReleaseFirst.TrySetResult();
            Assert.AreEqual("mon bur", await previews.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.AreEqual("monkeys burger at all", await previews.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
            await session.StopAsync(CancellationToken.None);
            CollectionAssert.AreEqual(new[] { paths[0], paths[3] }, recognizer.Calls.ToArray());
            Assert.IsTrue(paths.All(path => !File.Exists(path)));
        }
        finally { foreach (var path in paths) File.Delete(path); }
    }

    [TestMethod]
    public async Task OlderSnapshotArrivingAfterNewerInferenceStartsIsReleased()
    {
        var paths = Enumerable.Range(0, 3).Select(_ => Path.GetTempFileName()).ToArray();
        try
        {
            var recorder = new SnapshotRecorder();
            var recognizer = new ControlledRecognizer(paths[0], delayLater: true);
            var session = new ChunkedTranscribingDictationSession(recorder, recognizer, new FinalRecognizer());
            await session.StartAsync(CancellationToken.None);
            recorder.Publish(paths[0], 2);
            await recognizer.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            recorder.Publish(paths[1], 4.4);
            recognizer.ReleaseFirst.TrySetResult();
            await recognizer.LaterStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            recorder.Publish(paths[2], 2.8);
            Assert.IsFalse(File.Exists(paths[2]));
            recognizer.ReleaseLater.TrySetResult();
            await session.StopAsync(CancellationToken.None);
            CollectionAssert.AreEqual(new[] { paths[0], paths[1] }, recognizer.Calls.ToArray());
            Assert.IsTrue(paths.All(path => !File.Exists(path)));
        }
        finally { foreach (var path in paths) File.Delete(path); }
    }

    [TestMethod]
    public async Task CancelReleasesPendingSnapshotsAndPublishesNoCancelledResult()
    {
        var paths = Enumerable.Range(0, 2).Select(_ => Path.GetTempFileName()).ToArray();
        try
        {
            var recorder = new SnapshotRecorder();
            var recognizer = new ControlledRecognizer(paths[0]);
            var session = new ChunkedTranscribingDictationSession(recorder, recognizer, new FinalRecognizer());
            var previews = new ConcurrentQueue<string>();
            session.TranscriptUpdated += update => previews.Enqueue(update.StableText);
            await session.StartAsync(CancellationToken.None);
            recorder.Publish(paths[0], 2);
            await recognizer.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            recorder.Publish(paths[1], 2.8);
            await session.CancelAsync(CancellationToken.None);
            Assert.AreEqual(0, previews.Count);
            Assert.AreEqual(1, recognizer.Calls.Count);
            Assert.IsTrue(paths.All(path => !File.Exists(path)));
        }
        finally { foreach (var path in paths) File.Delete(path); }
    }

    private sealed class SnapshotRecorder : IChunkedAudioRecorder
    {
        public event Action<RecordedAudio>? AudioChunkReady;
        public void Publish(string path, double seconds) => AudioChunkPublisher.Publish(new PendingAudioChunk(path,
            [0, 0], TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(Math.Max(0, seconds - .8)), IsCumulative: true),
            AudioChunkReady, File.WriteAllBytes, File.Delete);
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<RecordedAudio> StopAsync(CancellationToken cancellationToken) => Task.FromResult(new RecordedAudio("unused-final", TimeSpan.FromSeconds(5)));
        public Task CancelAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class ControlledRecognizer(string firstPath, bool delayLater = false) : ITranscriber
    {
        public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirst { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource LaterStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseLater { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<string> Calls { get; } = new();
        public async Task<TranscriptResult> TranscribeAsync(string path, CancellationToken cancellationToken)
        {
            Calls.Enqueue(path);
            if (path == firstPath)
            {
                FirstStarted.TrySetResult();
                await ReleaseFirst.Task.WaitAsync(cancellationToken);
                return new TranscriptResult("mon bur", null, null);
            }
            LaterStarted.TrySetResult();
            if (delayLater) await ReleaseLater.Task.WaitAsync(cancellationToken);
            return new TranscriptResult("monkeys burger at all", null, null);
        }
    }

    private sealed class FinalRecognizer : ITranscriber
    {
        public Task<TranscriptResult> TranscribeAsync(string path, CancellationToken cancellationToken)
            => Task.FromResult(new TranscriptResult("monkeys burger at all", null, null));
    }
}
