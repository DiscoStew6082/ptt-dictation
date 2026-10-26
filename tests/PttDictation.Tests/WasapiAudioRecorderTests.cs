using NAudio.Wave;
using PttDictation.App;
using PttDictation.Core;
using System.Threading.Channels;

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
    public async Task LongRecordingSnapshotsRemainExactWhileCaptureAppends()
    {
        // Five minutes of 16-kHz PCM, delivered in 100-ms capture blocks.
        const int blockBytes = 3200;
        var expected = new byte[32000 * 60 * 5];
        for (var index = 0; index < expected.Length; index++)
            expected[index] = (byte)(index * 31 + index / 251);
        var buffer = new CumulativePcmBuffer(Guid.NewGuid(), firstSamples: 32000, intervalSamples: 12800);
        var selected = Channel.CreateBounded<CumulativeAudioSnapshot>(1);
        using var stop = new CancellationTokenSource();
        CumulativeAudioSnapshot? first = null;
        var capture = Task.Run(async () =>
        {
            try
            {
                for (var offset = 0; offset < expected.Length; offset += blockBytes)
                {
                    buffer.Append(expected[offset..(offset + blockBytes)]);
                    var snapshot = buffer.TryCreateSnapshot();
                    if (snapshot is null) continue;
                    first ??= snapshot;
                    // A slow preview worker selects occasional prefixes while capture continues.
                    if (snapshot == first || (offset + blockBytes) % (32000 * 30) == 0)
                        await selected.Writer.WriteAsync(snapshot, stop.Token);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            finally { selected.Writer.TryComplete(); }
        });
        var copied = 0;
        try
        {
            await foreach (var snapshot in selected.Reader.ReadAllAsync())
            {
                var prefix = buffer.CopyPrefix(snapshot, CancellationToken.None);
                Assert.AreEqual(snapshot.EndSample * 2, (long)prefix.Length);
                Assert.IsTrue(expected.AsSpan(0, prefix.Length).SequenceEqual(prefix),
                    "Concurrent appends must not change any sample of a selected prefix.");
                copied++;
            }
        }
        finally
        {
            stop.Cancel();
            await capture;
        }
        Assert.IsTrue(copied > 1);
        Assert.IsNotNull(first);
        Assert.IsTrue(expected.AsSpan(0, checked((int)first.EndSample * 2))
            .SequenceEqual(buffer.CopyPrefix(first, CancellationToken.None)),
            "The first preview must remain readable after five minutes of newer audio.");
        Assert.IsTrue(expected.AsSpan().SequenceEqual(buffer.ToArray()), "Final audio must contain every sample.");
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
