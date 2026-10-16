using NAudio.Wave;
using PttDictation.App;
using PttDictation.Core;

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
        var recording = Guid.NewGuid();
        var buffer = new CumulativePcmBuffer(recording, firstSamples: 10, intervalSamples: 4);
        var pcm = Enumerable.Range(0, 100).Select(value => (byte)value).ToArray();
        buffer.Append(pcm[..18]);
        Assert.IsNull(buffer.TryCreateSnapshot());
        buffer.Append(pcm[18..20]);
        var first = buffer.TryCreateSnapshot()!;
        Assert.AreEqual(10L, first.EndSample);
        Assert.AreEqual(recording, first.RecordingId);
        for (var end = 28; end <= 100; end += 8)
        {
            buffer.Append(pcm[(end - 8)..end]);
            var snapshot = buffer.TryCreateSnapshot()!;
            Assert.AreEqual((long)end / 2, snapshot.EndSample);
            CollectionAssert.AreEqual(pcm[..end], buffer.CopyPrefix(snapshot, CancellationToken.None));
            Assert.IsNull(buffer.TryCreateSnapshot());
        }
        CollectionAssert.AreEqual(pcm[..20], buffer.CopyPrefix(first, CancellationToken.None),
            "A selected prefix remains immutable while capture appends newer blocks.");
        CollectionAssert.AreEqual(pcm, buffer.ToArray(), "Final audio retains every captured sample.");
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
    public void LargeCallbackSelectsLatestBoundaryAndRejectsForeignOrFutureSnapshots()
    {
        var recording = Guid.NewGuid();
        var buffer = new CumulativePcmBuffer(recording, firstSamples: 10, intervalSamples: 4);
        buffer.Append(new byte[100]);
        Assert.AreEqual(50L, buffer.TryCreateSnapshot()!.EndSample);
        Assert.IsNull(buffer.TryCreateSnapshot());
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            buffer.CopyPrefix(new(Guid.NewGuid(), 10), CancellationToken.None));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            buffer.CopyPrefix(new(recording, 51), CancellationToken.None));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() =>
            buffer.CopyPrefix(new(recording, 10), cancelled.Token));
    }
}
