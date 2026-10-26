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

    [TestMethod]
    public async Task DescriptorsCoalesceBeforeBothMaterializationAndRecognition()
    {
        using var recorder = new DescriptorRecorder();
        var recognizer = new ControlledRecognizer(recorder.PathFor(32000));
        var session = new ChunkedTranscribingDictationSession(recorder, recognizer, new FinalRecognizer());
        var previews = Channel.CreateUnbounded<string>();
        session.TranscriptUpdated += update => previews.Writer.TryWrite(update.StableText);
        await session.StartAsync(CancellationToken.None);
        try
        {
            recorder.Publish(32000);
            await recorder.MaterializationStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            for (var sample = 44800; sample <= 960000; sample += 12800) recorder.Publish(sample);
            CollectionAssert.AreEqual(new long[] { 32000 }, recorder.Materialized.ToArray(),
                "Pending descriptors must not copy PCM or write WAVs while materialization is busy.");
            recorder.ReleaseMaterialization.Set();
            await recognizer.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            recorder.Publish(972800);
            recorder.Publish(44800); // Out-of-order callback from this recording.
            recorder.Publish(1000000, Guid.NewGuid()); // Stale callback from another recording.
            CollectionAssert.AreEqual(new long[] { 32000 }, recorder.Materialized.ToArray(),
                "Recognition and publication share one scheduler.");
            recognizer.ReleaseFirst.TrySetResult();
            await previews.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            await previews.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            await session.StopAsync(CancellationToken.None);
            CollectionAssert.AreEqual(new long[] { 32000, 972800 }, recorder.Materialized.ToArray());
            Assert.IsEmpty(Directory.GetFiles(recorder.DirectoryPath), "All selected WAVs must be released.");
        }
        finally
        {
            recorder.ReleaseMaterialization.Set();
            recognizer.ReleaseFirst.TrySetResult();
            await session.CancelAsync(CancellationToken.None);
        }
    }

    [TestMethod]
    public async Task CancelDuringMaterializationDropsPendingDescriptorsAndReleasesSelectedWav()
    {
        using var recorder = new DescriptorRecorder();
        var recognizer = new ControlledRecognizer(recorder.PathFor(32000));
        var session = new ChunkedTranscribingDictationSession(recorder, recognizer, new FinalRecognizer());
        await session.StartAsync(CancellationToken.None);
        recorder.Publish(32000);
        await recorder.MaterializationStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        recorder.Publish(44800);
        // This materializer deliberately returns its owned file after cancellation.
        // The session must release it without calling recognition or publishing.
        var cancel = session.CancelAsync(CancellationToken.None);
        recorder.ReleaseMaterialization.Set();
        await cancel;
        CollectionAssert.AreEqual(new long[] { 32000 }, recorder.Materialized.ToArray());
        Assert.IsEmpty(recognizer.Calls);
        Assert.IsEmpty(Directory.GetFiles(recorder.DirectoryPath));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FactorySessionsShareRecorderWithoutSharingLatePreviewOrWavOwnership(bool finishNewSessionBeforeOldRecognizer)
    {
        using var recorder = new DescriptorRecorder(bindNames: true);
        recorder.ReleaseMaterialization.Set();
        var recognizer = new LateRecognizer();
        var factory = new ChunkedTranscribingDictationSessionFactory(recorder, recognizer, new FinalRecognizer());
        var first = factory.CreateSession();
        var second = factory.CreateSession();
        var oldUpdates = 0;
        first.TranscriptUpdated += _ => oldUpdates++;
        var newUpdate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        second.TranscriptUpdated += _ => newUpdate.TrySetResult();
        try
        {
            await first.StartAsync(CancellationToken.None);
            var oldId = recorder.RecordingId;
            recorder.Publish(32000);
            var oldPath = await recognizer.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await first.CancelAsync(CancellationToken.None);
            Assert.IsTrue(File.Exists(oldPath));
            Assert.AreEqual(oldPath, first.CleanupWarningPath);

            await second.StartAsync(CancellationToken.None);
            Assert.AreNotEqual(oldId, recorder.RecordingId);
            recorder.Publish(32000);
            var newPath = await recognizer.SecondStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.AreNotEqual(oldPath, newPath);
            recorder.Publish(1000000, oldId); // Late callback routed to the current subscriber.
            if (finishNewSessionBeforeOldRecognizer)
            {
                recognizer.ReleaseSecond.TrySetResult();
                await newUpdate.Task.WaitAsync(TimeSpan.FromSeconds(3));
                var result = await second.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
                Assert.AreEqual("monkeys burger at all", result.Transcript.Text);
                Assert.IsNull(second.CleanupWarningPath);
                Assert.IsTrue(File.Exists(oldPath), "The next recording must finish without deleting the old recognizer's audio.");
                Assert.IsFalse(File.Exists(newPath));
                Assert.IsFalse(recognizer.ReleaseFirst.Task.IsCompleted);
                Assert.AreEqual(0, oldUpdates);
            }
            recognizer.ReleaseFirst.TrySetResult();
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (File.Exists(oldPath) && DateTime.UtcNow < deadline) await Task.Delay(10);
            Assert.IsFalse(File.Exists(oldPath));
            if (!finishNewSessionBeforeOldRecognizer)
                Assert.IsTrue(File.Exists(newPath), "Old cleanup cannot delete the next recording's active WAV.");
            Assert.AreEqual(0, oldUpdates);
            if (!finishNewSessionBeforeOldRecognizer)
            {
                recognizer.ReleaseSecond.TrySetResult();
                await newUpdate.Task.WaitAsync(TimeSpan.FromSeconds(3));
                await second.StopAsync(CancellationToken.None);
            }
            CollectionAssert.AreEqual(new long[] { 32000, 32000 }, recorder.Materialized.ToArray());
            Assert.IsEmpty(Directory.GetFiles(recorder.DirectoryPath));
        }
        finally
        {
            recognizer.ReleaseFirst.TrySetResult();
            recognizer.ReleaseSecond.TrySetResult();
            await first.CancelAsync(CancellationToken.None);
            await second.CancelAsync(CancellationToken.None);
        }
    }

    private sealed class LateRecognizer : ITranscriber
    {
        private int _calls;
        public TaskCompletionSource<string> FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<string> SecondStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirst { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSecond { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<TranscriptResult> TranscribeAsync(string path, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                FirstStarted.TrySetResult(path);
                await ReleaseFirst.Task; // Model a provider that ignores cancellation.
            }
            else
            {
                SecondStarted.TrySetResult(path);
                await ReleaseSecond.Task;
            }
            Assert.IsTrue(File.Exists(path));
            return new("owned preview", null, null);
        }
    }

    private sealed class DescriptorRecorder(bool bindNames = false) : ICumulativeAudioRecorder, IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "ptt-descriptors-" + Guid.NewGuid().ToString("N"));
        public Guid RecordingId { get; private set; }
        public ConcurrentQueue<long> Materialized { get; } = new();
        public TaskCompletionSource MaterializationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim ReleaseMaterialization { get; } = new(false);
        public event Action<CumulativeAudioSnapshot>? SnapshotReady;
        public string PathFor(long sample, Guid? recording = null) => Path.Combine(DirectoryPath,
            (bindNames ? (recording ?? RecordingId).ToString("N") + "-" : "") + sample + ".wav");
        public Task StartAsync(CancellationToken cancellationToken) => throw new AssertFailedException("Session must bind recording identity.");
        public Task StartAsync(Guid recordingId, CancellationToken cancellationToken)
        {
            RecordingId = recordingId;
            Directory.CreateDirectory(DirectoryPath);
            return Task.CompletedTask;
        }
        public void Publish(long sample, Guid? recording = null) => SnapshotReady?.Invoke(new(recording ?? RecordingId, sample));
        public RecordedAudio MaterializeSnapshot(CumulativeAudioSnapshot snapshot, CancellationToken cancellationToken)
        {
            Materialized.Enqueue(snapshot.EndSample);
            Assert.AreEqual(RecordingId, snapshot.RecordingId);
            var path = PathFor(snapshot.EndSample, snapshot.RecordingId);
            MaterializationStarted.TrySetResult();
            if (!ReleaseMaterialization.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Test materialization was not released.");
            File.WriteAllBytes(path, [0, 0]);
            return new(path, TimeSpan.FromSeconds(snapshot.EndSample / 16000d), DeleteAfterUse: true, IsCumulative: true);
        }
        public Task<RecordedAudio> StopAsync(CancellationToken cancellationToken)
        {
            var path = Path.Combine(DirectoryPath, "final.wav");
            File.WriteAllBytes(path, [0, 0]);
            return Task.FromResult(new RecordedAudio(path, TimeSpan.FromSeconds(61), DeleteAfterUse: true));
        }
        public void Dispose()
        {
            ReleaseMaterialization.Dispose();
            if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, true);
        }
    }

    private sealed class SnapshotRecorder : IChunkedAudioRecorder
    {
        public event Action<RecordedAudio>? AudioChunkReady;
        public void Publish(string path, double seconds)
        {
            File.WriteAllBytes(path, [0, 0]);
            AudioChunkReady?.Invoke(new RecordedAudio(path, TimeSpan.FromSeconds(seconds), DeleteAfterUse: true,
                OverlapDuration: TimeSpan.FromSeconds(Math.Max(0, seconds - .8)), IsCumulative: true));
        }
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
