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
    public void LivePreviewPublishesShortLowOverlapChunks()
    {
        Assert.AreEqual(TimeSpan.FromSeconds(2), WasapiAudioRecorder.ChunkDurationForTest);
        Assert.AreEqual(TimeSpan.FromMilliseconds(1200), WasapiAudioRecorder.ChunkOverlapForTest);
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
