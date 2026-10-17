using PttDictation.App;
using PttDictation.Core;

namespace PttDictation.Tests;

[TestClass]
public sealed class SharedQwenTests
{
    [TestMethod]
    public async Task DesktopAndPhoneSerializeThroughOneWorkerAndQueuedCancellationDoesNotKillActiveWork()
    {
        var worker = new BlockingRecognizer();
        var creations = 0;
        var reports = 0;
        using var owner = new ConfiguredFinalTranscriber(worker,
            () => AppSettings.Default with { FinalTranscriptionEngine = FinalTranscriptionEngine.Qwen },
            () => new("python", "model", "worker"), _ => { creations++; return worker; }, _ => reports++);
        var phone = owner.TranscribeQwenFromPhoneAsync("phone", CancellationToken.None);
        await worker.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        using var cancel = new CancellationTokenSource();
        var cancelledDesktop = owner.TranscribeAsync("cancelled", cancel.Token);
        cancel.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => cancelledDesktop);
        Assert.AreEqual(1, worker.Calls);
        Assert.AreEqual(0, reports, "Phone requests must not change desktop workflow presentation.");
        var desktop = owner.TranscribeAsync("desktop", CancellationToken.None);
        Assert.AreEqual(1, worker.Calls);
        worker.Release.SetResult();
        Assert.AreEqual("phone", (await phone).Text);
        Assert.AreEqual("desktop", (await desktop).Text);
        Assert.AreEqual(1, creations);
        Assert.AreEqual(2, worker.Calls);
        Assert.AreEqual(1, reports);
    }

    [TestMethod]
    public async Task PhoneAlwaysUsesQwenEvenWhenDesktopEngineIsParakeet()
    {
        var preview = new BlockingRecognizer();
        var qwen = new BlockingRecognizer();
        qwen.Release.SetResult();
        using var owner = new ConfiguredFinalTranscriber(preview,
            () => AppSettings.Default with { FinalTranscriptionEngine = FinalTranscriptionEngine.Parakeet },
            () => new("python", "model", "worker"), _ => qwen);
        Assert.AreEqual("phone", (await owner.TranscribeQwenFromPhoneAsync("phone", CancellationToken.None)).Text);
        Assert.AreEqual(0, preview.Calls);
        Assert.AreEqual(1, qwen.Calls);
    }

    private sealed class BlockingRecognizer : ITranscriber
    {
        public int Calls;
        public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<TranscriptResult> TranscribeAsync(string path, CancellationToken token)
        {
            Calls++;
            Started.TrySetResult();
            await Release.Task.WaitAsync(token);
            return new TranscriptResult(path, TimeSpan.Zero, null);
        }
    }
}
