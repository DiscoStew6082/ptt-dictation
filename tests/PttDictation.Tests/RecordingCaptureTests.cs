using NAudio.Wave;
using PttDictation.Core;

namespace PttDictation.Tests;

[TestClass]
public sealed class RecordingCaptureTests
{
    [TestMethod]
    public async Task FinalizedRecordingRemainsReadableAfterAppDeletesOriginal()
    {
        var root = Path.Combine(Path.GetTempPath(), "ptt-recording-capture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var ready = new TaskCompletionSource<Stream>(TaskCreationOptions.RunContinuationsAsynchronously);
        var failures = new List<Exception>();
        var capture = new RecordingCapture(root, (_, stream) => ready.TrySetResult(stream), (_, error) => { lock (failures) failures.Add(error); });
        var path = Path.Combine(root, "utterance-test.wav");
        var pcm = Enumerable.Range(0, 32000).Select(i => (byte)(i % 251)).ToArray();
        try
        {
            using (var writer = new WaveFileWriter(path, new WaveFormat(16000, 16, 1)))
            {
                writer.Write(pcm, 0, pcm.Length);
                // Keep the real writer active. A partial/unfinalized header must not be retained.
                await Task.Delay(150);
                Assert.IsFalse(ready.Task.IsCompleted, "Capture delivered an unfinished WAV.");
            }
            using var retained = await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            File.Delete(path);
            Assert.IsFalse(File.Exists(path), "Capture interfered with the app's temporary-file cleanup.");
            using var wav = new WaveFileReader(retained);
            var actual = new byte[pcm.Length];
            Assert.AreEqual(pcm.Length, wav.Read(actual, 0, actual.Length));
            CollectionAssert.AreEqual(pcm, actual);
            lock (failures) Assert.IsEmpty(failures);
        }
        finally
        {
            await capture.DisposeAsync();
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public async Task ChunkFilesAreIgnoredAndChangedEventsDoNotDuplicateFullRecordings()
    {
        var root = Path.Combine(Path.GetTempPath(), "ptt-recording-capture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var names = new List<string>();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var stale = new WaveFileWriter(Path.Combine(root, "utterance-stale.wav"), new WaveFormat(16000, 16, 1)))
            stale.Write(new byte[3200], 0, 3200);
        var capture = new RecordingCapture(root, (path, audio) =>
        {
            audio.Dispose();
            lock (names) names.Add(Path.GetFileName(path));
            ready.TrySetResult();
        });
        try
        {
            foreach (var name in new[] { "chunk-test.wav", "utterance-test.wav" })
            {
                using var writer = new WaveFileWriter(Path.Combine(root, name), new WaveFormat(16000, 16, 1));
                writer.Write(new byte[3200], 0, 3200);
            }
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            File.SetLastWriteTimeUtc(Path.Combine(root, "utterance-test.wav"), DateTime.UtcNow.AddSeconds(1));
            await Task.Delay(150);
            lock (names) CollectionAssert.AreEqual(new[] { "utterance-test.wav" }, names);
        }
        finally
        {
            await capture.DisposeAsync();
            Directory.Delete(root, true);
        }
    }
}
