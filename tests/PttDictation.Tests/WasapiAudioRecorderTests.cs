using NAudio.Wave;
using PttDictation.App;

namespace PttDictation.Tests;

[TestClass]
public sealed class WasapiAudioRecorderTests
{
    [TestMethod]
    public void CaptureFormatIsPcm16KhzMono()
    {
        var format = WasapiAudioRecorder.CreateCaptureFormat();

        Assert.AreEqual(WaveFormatEncoding.Pcm, format.Encoding);
        Assert.AreEqual(16000, format.SampleRate);
        Assert.AreEqual(16, format.BitsPerSample);
        Assert.AreEqual(1, format.Channels);
        Assert.AreEqual(32000, format.AverageBytesPerSecond);
    }

    [TestMethod]
    public void LivePreviewKeepsPublicationCadence()
    {
        Assert.AreEqual(TimeSpan.FromSeconds(2), WasapiAudioRecorder.ChunkDurationForTest);
        Assert.AreEqual(TimeSpan.FromMilliseconds(1200), WasapiAudioRecorder.ChunkOverlapForTest);
    }

    [TestMethod]
    public void CumulativeSnapshotsKeepEarlyAudioAndPublicationCadence()
    {
        using var buffer = new PcmChunkBuffer(10, 20, 12, cumulative: true);
        var pcm = Enumerable.Range(0, 100).Select(value => (byte)value).ToArray();
        buffer.Append(pcm.AsSpan(0, 19));
        Assert.IsNull(buffer.TryCreateChunk("early"));
        buffer.Append(pcm.AsSpan(19, 1));
        var first = buffer.TryCreateChunk("first")!;
        Assert.AreEqual(TimeSpan.FromSeconds(2), first.Duration);
        Assert.IsTrue(first.IsCumulative);
        for (var end = 28; end <= 100; end += 8)
        {
            buffer.Append(pcm.AsSpan(end - 8, 8));
            var chunk = buffer.TryCreateChunk("next")!;
            CollectionAssert.AreEqual(pcm[..end], chunk.Pcm);
            Assert.IsTrue(chunk.IsCumulative);
            Assert.AreEqual(TimeSpan.FromSeconds(end / 10d), chunk.Duration);
            Assert.IsNull(buffer.TryCreateChunk("too-soon"));
        }
    }

    [TestMethod]
    public void RecognitionContextGrowsWithoutDelayingFirstOrSubsequentChunks()
    {
        using var buffer = new PcmChunkBuffer(10, 20, 12, contextBytes: 60);
        var pcm = Enumerable.Range(0, 100).Select(value => (byte)value).ToArray();
        buffer.Append(pcm.AsSpan(0, 19));
        Assert.IsNull(buffer.TryCreateChunk("early"));
        buffer.Append(pcm.AsSpan(19, 1));
        Assert.AreEqual(TimeSpan.FromSeconds(2), buffer.TryCreateChunk("first")!.Duration);
        for (var end = 28; end <= 100; end += 8)
        {
            buffer.Append(pcm.AsSpan(end - 8, 8));
            var chunk = buffer.TryCreateChunk("next");
            Assert.IsNotNull(chunk);
            var start = Math.Max(0, end - 60);
            CollectionAssert.AreEqual(pcm[start..end], chunk.Pcm);
            Assert.AreEqual(TimeSpan.FromSeconds((end - start) / 10d), chunk.Duration);
            Assert.AreEqual(chunk.Duration - TimeSpan.FromMilliseconds(800), chunk.OverlapDuration);
            Assert.IsNull(buffer.TryCreateChunk("too-soon"));
        }
        CollectionAssert.AreEqual(pcm, buffer.ToArray(), "Full recording must remain intact.");
    }

    [TestMethod]
    public void CaptureFailureNamesTheAttemptedEndpoint()
    {
        var message = WasapiAudioRecorder.DescribeCaptureFailure("Microphone (Galaxy S25 Hands-Free HF Audio)");

        StringAssert.Contains(message, "Galaxy S25 Hands-Free HF Audio");
        StringAssert.Contains(message, "Settings > System > Sound > Input");
    }

    [TestMethod]
    public async Task ChunkPublicationQueueDrainsAcceptedPublisherBeforeClosing()
    {
        var queue = new AudioChunkPublicationQueue();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Open();

        Assert.IsTrue(queue.TryQueue(() =>
        {
            started.SetResult();
            release.Task.GetAwaiter().GetResult();
        }));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        queue.StopAccepting();
        var drain = Task.Run(queue.Drain);
        await Task.Delay(100);

        Assert.IsFalse(drain.IsCompleted);
        Assert.IsFalse(queue.TryQueue(() => throw new InvalidOperationException("must not run")));

        release.SetResult();
        await drain.WaitAsync(TimeSpan.FromSeconds(2));
    }
}
