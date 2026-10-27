using PttDictation.App;
using PttDictation.Core;

namespace PttDictation.Tests;

[TestClass]
public sealed class SharedQwenTests
{
    [TestMethod]
    public async Task NativeValidationWaitsForActiveDictationUnloadsWorkerAndBlocksNewLoads()
    {
        var worker = new BlockingRecognizer();
        using var owner = new ConfiguredFinalTranscriber(worker,
            () => AppSettings.Default with { FinalTranscriptionEngine = FinalTranscriptionEngine.Qwen },
            () => new("python", "model", "worker"), _ => worker);
        var active = owner.TranscribeAsync("first", CancellationToken.None);
        await worker.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var validation = owner.RunWithQwenUnloadedAsync(async token =>
        {
            Assert.IsTrue(worker.Disposed);
            entered.SetResult();
            await release.Task.WaitAsync(token);
        }, CancellationToken.None);
        Assert.IsFalse(entered.Task.IsCompleted);
        worker.Release.SetResult();
        await active.WaitAsync(TimeSpan.FromSeconds(3));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var queued = owner.TranscribeQwenFromPhoneAsync("second", CancellationToken.None);
        Assert.AreEqual(1, worker.Calls);
        release.SetResult();
        await validation.WaitAsync(TimeSpan.FromSeconds(3));
        await queued.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.AreEqual(2, worker.Calls);
    }

    [TestMethod]
    public async Task QueuedDesktopAndPhoneLoadCommittedBackendAfterActivation()
    {
        var backend = "previous";
        var creations = new List<string>();
        using var owner = new ConfiguredFinalTranscriber(new ImmediateRecognizer("preview"),
            () => AppSettings.Default with { FinalTranscriptionEngine = FinalTranscriptionEngine.Qwen },
            // Registration changes at the same path: retirement, rather than path
            // inequality, must ensure that the replacement worker sees the commit.
            () => new("python", "model", "native-worker", "native-manifest"),
            _ => { creations.Add(backend); return new ImmediateRecognizer(backend); });
        await owner.WarmUpAsync(CancellationToken.None);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activation = owner.RunWithQwenUnloadedAsync(async token =>
        {
            entered.SetResult();
            await commit.Task.WaitAsync(token);
            backend = "replacement";
        }, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var desktop = owner.TranscribeAsync("desktop", CancellationToken.None);
        var phone = owner.TranscribeQwenFromPhoneAsync("phone", CancellationToken.None);
        CollectionAssert.AreEqual(new[] { "previous" }, creations);
        commit.SetResult();
        await activation.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.AreEqual("replacement", (await desktop.WaitAsync(TimeSpan.FromSeconds(3))).Text);
        Assert.AreEqual("replacement", (await phone.WaitAsync(TimeSpan.FromSeconds(3))).Text);
        CollectionAssert.AreEqual(new[] { "previous", "replacement" }, creations);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FailedOrCancelledActivationReleasesQueuedRecognition(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        using var owner = new ConfiguredFinalTranscriber(new ImmediateRecognizer("preview"),
            () => AppSettings.Default with { FinalTranscriptionEngine = FinalTranscriptionEngine.Qwen },
            () => new("python", "model", "worker"), _ => new ImmediateRecognizer("usable backend"));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fail = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activation = owner.RunWithQwenUnloadedAsync(async token =>
        {
            entered.SetResult();
            await fail.Task.WaitAsync(token);
            throw new IOException("Native validation failed.");
        }, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var queued = owner.TranscribeQwenFromPhoneAsync("phone", CancellationToken.None);
        Assert.IsFalse(queued.IsCompleted);
        if (cancel)
        {
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => activation.WaitAsync(TimeSpan.FromSeconds(3)));
        }
        else
        {
            fail.SetResult();
            await Assert.ThrowsExactlyAsync<IOException>(() => activation.WaitAsync(TimeSpan.FromSeconds(3)));
        }

        Assert.AreEqual("usable backend", (await queued.WaitAsync(TimeSpan.FromSeconds(3))).Text);
    }

    [TestMethod]
    public async Task DisposalCancelsActivationAndQueuedRecognitionWithoutStartingAnotherWorker()
    {
        var creations = 0;
        using var owner = new ConfiguredFinalTranscriber(new ImmediateRecognizer("preview"),
            () => AppSettings.Default with { FinalTranscriptionEngine = FinalTranscriptionEngine.Qwen },
            () => new("python", "model", "worker"),
            _ => { creations++; return new ImmediateRecognizer("must not run"); });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activation = owner.RunWithQwenUnloadedAsync(async token =>
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { cleanup.SetResult(); }
        }, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var queued = owner.TranscribeAsync("desktop", CancellationToken.None);
        owner.Dispose();

        await Assert.ThrowsAsync<OperationCanceledException>(() => activation.WaitAsync(TimeSpan.FromSeconds(3)));
        await Assert.ThrowsAsync<OperationCanceledException>(() => queued.WaitAsync(TimeSpan.FromSeconds(3)));
        await cleanup.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.AreEqual(0, creations);
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => owner.RunWithQwenUnloadedAsync(_ => Task.CompletedTask, CancellationToken.None));
    }

    [TestMethod]
    public async Task CancellingQueuedActivationPreservesActiveRecognition()
    {
        var worker = new BlockingRecognizer();
        using var owner = new ConfiguredFinalTranscriber(worker,
            () => AppSettings.Default with { FinalTranscriptionEngine = FinalTranscriptionEngine.Qwen },
            () => new("python", "model", "worker"), _ => worker);
        var active = owner.TranscribeAsync("active", CancellationToken.None);
        await worker.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        using var cancellation = new CancellationTokenSource();
        var entered = false;
        var activation = owner.RunWithQwenUnloadedAsync(_ =>
        {
            entered = true;
            return Task.CompletedTask;
        }, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => activation.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.IsFalse(entered);
        Assert.IsFalse(worker.Disposed);
        worker.Release.SetResult();
        Assert.AreEqual("active", (await active.WaitAsync(TimeSpan.FromSeconds(3))).Text);
    }

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

    private sealed class ImmediateRecognizer(string text) : ITranscriber
    {
        public Task<TranscriptResult> TranscribeAsync(string path, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(new TranscriptResult(text, TimeSpan.Zero, null));
        }
    }

    private sealed class BlockingRecognizer : ITranscriber, IDisposable
    {
        public bool Disposed;
        public void Dispose() => Disposed = true;
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
