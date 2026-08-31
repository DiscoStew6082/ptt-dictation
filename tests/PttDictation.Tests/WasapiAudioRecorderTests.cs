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
    public void CaptureFailureNamesTheAttemptedEndpoint()
    {
        var message = WasapiAudioRecorder.DescribeCaptureFailure("Microphone (Galaxy S25 Hands-Free HF Audio)");

        StringAssert.Contains(message, "Galaxy S25 Hands-Free HF Audio");
        StringAssert.Contains(message, "Settings > System > Sound > Input");
    }
}
