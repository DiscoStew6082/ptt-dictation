using NAudio.Wave;
using PttDictation.App;

namespace PttDictation.Tests;

[TestClass]
public sealed class WasapiStopHandshakeTests
{
    [TestMethod]
    public async Task StopDuringStartupStopsCaptureEvenWhenStartupOverwritesStopping()
    {
        var state = (int)CaptureState.Starting;
        var stopCalls = 0;
        var stopped = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var firstStop = new ManualResetEventSlim();
        var captureThread = Task.Run(() =>
        {
            Assert.IsTrue(firstStop.Wait(TimeSpan.FromSeconds(2)));
            // NAudio 3.1.0 CaptureThread overwrites an early Stopping state.
            Volatile.Write(ref state, (int)CaptureState.Capturing);
        });

        var error = WasapiAudioRecorder.StopCapture(
            () => (CaptureState)Volatile.Read(ref state),
            () =>
            {
                Interlocked.Increment(ref stopCalls);
                var previous = (CaptureState)Interlocked.Exchange(ref state, (int)CaptureState.Stopping);
                if (previous == CaptureState.Starting)
                {
                    firstStop.Set();
                }
                else if (previous == CaptureState.Capturing)
                {
                    Volatile.Write(ref state, (int)CaptureState.Stopped);
                    stopped.TrySetResult(null);
                }
            },
            stopped.Task,
            TimeSpan.FromSeconds(1));

        await captureThread;
        Assert.IsNull(error, "An immediate stop must stop the capture thread after startup overwrites Stopping.");
        Assert.AreEqual(CaptureState.Stopped, (CaptureState)Volatile.Read(ref state));
        Assert.AreEqual(2, stopCalls);
    }

    [TestMethod]
    public void StopWhileCapturingStopsOnceAndWaitsForCompletionEvent()
    {
        var state = CaptureState.Capturing;
        var stopCalls = 0;
        var stopped = new TaskCompletionSource<Exception?>();
        var failure = new IOException("Capture shutdown failed.");
        var result = WasapiAudioRecorder.StopCapture(
            () =>
            {
                // Stopping is only a request; completion carries the device result.
                Assert.AreEqual(CaptureState.Stopping, state);
                state = CaptureState.Stopped;
                stopped.SetResult(failure);
                return state;
            },
            () =>
            {
                stopCalls++;
                state = CaptureState.Stopping;
            },
            stopped.Task,
            TimeSpan.FromSeconds(1));

        Assert.AreSame(failure, result);
        Assert.AreEqual(1, stopCalls);
    }

    [TestMethod]
    public void CompletedCaptureReturnsOriginalDeviceFailureWithoutAnotherStop()
    {
        var failure = new IOException("Device disconnected.");
        var result = WasapiAudioRecorder.StopCapture(
            () => CaptureState.Stopped,
            () => Assert.Fail("Already stopped capture must not be restarted or stopped again."),
            Task.FromResult<Exception?>(failure),
            TimeSpan.FromSeconds(1));

        Assert.AreSame(failure, result);
    }

    [TestMethod]
    public void StopThatNeverCompletesStillReportsTimeout()
    {
        var stopped = new TaskCompletionSource<Exception?>();
        var result = WasapiAudioRecorder.StopCapture(
            () => CaptureState.Stopping,
            () => { },
            stopped.Task,
            TimeSpan.Zero);

        Assert.IsInstanceOfType<TimeoutException>(result);
    }
}
