using System.Collections.Concurrent;
using PttDictation.App;
using PttDictation.Core;

namespace PttDictation.Tests;

[TestClass]
public sealed class SelectedQwenPreloaderTests
{
    [TestMethod]
    public async Task ParakeetSelectionDoesNotInspectOrWarmAnyAssets()
    {
        var calls = 0;
        var preloader = new SelectedQwenPreloader(_ =>
        {
            calls++;
            throw new AssertFailedException("Parakeet startup must not resolve or download assets.");
        }, CancellationToken.None);

        await preloader.ApplySelection(FinalTranscriptionEngine.Parakeet);
        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    public async Task StartupReturnsBeforeSynchronousWarmupAndSharesOneBackgroundOperation()
    {
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        SynchronizationContext? workerContext = new();
        var preloader = new SelectedQwenPreloader(token =>
        {
            Interlocked.Increment(ref calls);
            workerContext = SynchronizationContext.Current;
            started.TrySetResult();
            release.Wait(token);
            return Task.CompletedTask;
        }, CancellationToken.None);
        var previous = SynchronizationContext.Current;
        Task pending;
        try
        {
            SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
            pending = preloader.ApplySelection(FinalTranscriptionEngine.Qwen);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }

        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.IsFalse(pending.IsCompleted, "Startup must return while model loading is still blocked.");
            Assert.AreSame(pending, preloader.ApplySelection(FinalTranscriptionEngine.Qwen));
            Assert.IsNull(workerContext, "Background warmup must not capture the UI synchronization context.");
            Assert.AreEqual(1, calls);
        }
        finally { release.Set(); }
        await pending.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [TestMethod]
    public async Task CompletedWarmupIsReusedUntilSelectionChanges()
    {
        var calls = 0;
        var preloader = new SelectedQwenPreloader(_ =>
        {
            Interlocked.Increment(ref calls);
            return Task.CompletedTask;
        }, CancellationToken.None);

        await preloader.ApplySelection(FinalTranscriptionEngine.Qwen);
        await preloader.ApplySelection(FinalTranscriptionEngine.Qwen);
        Assert.AreEqual(1, calls);
        await preloader.ApplySelection(FinalTranscriptionEngine.Parakeet);
        await preloader.ApplySelection(FinalTranscriptionEngine.Qwen);
        Assert.AreEqual(2, calls);
    }

    [TestMethod]
    public async Task AppExitCancelsPreloadAndPreventsFurtherStartup()
    {
        using var lifetime = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stages = new ConcurrentQueue<string>();
        var calls = 0;
        var preloader = new SelectedQwenPreloader(async token =>
        {
            Interlocked.Increment(ref calls);
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }, lifetime.Token, (stage, _, _) => stages.Enqueue(stage));

        var pending = preloader.ApplySelection(FinalTranscriptionEngine.Qwen);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        lifetime.Cancel();
        await pending.WaitAsync(TimeSpan.FromSeconds(2));
        await preloader.ApplySelection(FinalTranscriptionEngine.Qwen);
        Assert.AreEqual(1, calls);
        CollectionAssert.AreEqual(new[] { "started", "cancelled" }, stages.ToArray());
    }

    [TestMethod]
    public async Task SwitchingAwayCancelsAndSwitchingBackWaitsForRetirement()
    {
        using var lifetime = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retire = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var active = 0;
        var peakActive = 0;
        var preloader = new SelectedQwenPreloader(async token =>
        {
            peakActive = Math.Max(peakActive, Interlocked.Increment(ref active));
            var call = Interlocked.Increment(ref calls);
            try
            {
                if (call != 1) return;
                started.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                catch (OperationCanceledException)
                {
                    cancelled.TrySetResult();
                    await retire.Task;
                    throw;
                }
            }
            finally { Interlocked.Decrement(ref active); }
        }, lifetime.Token);

        var pending = preloader.ApplySelection(FinalTranscriptionEngine.Qwen);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            _ = preloader.ApplySelection(FinalTranscriptionEngine.Parakeet);
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.AreSame(pending, preloader.ApplySelection(FinalTranscriptionEngine.Qwen));
            Assert.AreEqual(1, calls, "Do not start another worker until the cancelled warmup retires.");
        }
        finally { retire.TrySetResult(); }
        await pending.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(2, calls);
        Assert.AreEqual(1, peakActive);
    }

    [TestMethod]
    public async Task FailedPreloadIsObservedAndLaterSettingsCanRetry()
    {
        var failure = new IOException("Local model is missing.");
        var events = new ConcurrentQueue<(string Stage, double Milliseconds, Exception? Error)>();
        var calls = 0;
        var preloader = new SelectedQwenPreloader(_ =>
            Interlocked.Increment(ref calls) == 1 ? Task.FromException(failure) : Task.CompletedTask,
            CancellationToken.None, (stage, duration, error) => events.Enqueue((stage, duration, error)));

        await preloader.ApplySelection(FinalTranscriptionEngine.Qwen);
        await preloader.ApplySelection(FinalTranscriptionEngine.Qwen);

        CollectionAssert.AreEqual(new[] { "started", "failed", "started", "completed" },
            events.Select(e => e.Stage).ToArray());
        Assert.AreSame(failure, events.Single(e => e.Stage == "failed").Error);
        Assert.IsTrue(events.All(e => e.Milliseconds >= 0));
    }

    [TestMethod]
    public async Task FirstDictationReusesPreloadedConfiguredWorkerWithoutWarmingPreview()
    {
        var settings = AppSettings.Default with { FinalTranscriptionEngine = FinalTranscriptionEngine.Qwen };
        var preview = new Recognizer();
        var qwen = new Recognizer();
        var creations = 0;
        using var final = new ConfiguredFinalTranscriber(preview, () => settings,
            () => new QwenTranscriberOptions("python", "model", "worker"),
            _ => { Interlocked.Increment(ref creations); return qwen; });
        var preloader = new SelectedQwenPreloader(final.WarmUpAsync, CancellationToken.None);

        await preloader.ApplySelection(settings.FinalTranscriptionEngine);
        Assert.AreEqual(1, qwen.Warmups, "The model must be ready before first dictation starts.");
        var session = final.CreateSessionTranscriber();
        Assert.AreEqual("recognized", (await session.TranscribeAsync("audio", CancellationToken.None)).Text);
        Assert.AreEqual(1, creations);
        Assert.AreEqual(0, preview.Warmups);
        Assert.AreEqual(0, preview.Calls);
        Assert.AreEqual(1, qwen.Calls);
    }

    [TestMethod]
    public async Task CancellingQueuedPreloadCannotCancelAnActiveFinalTranscription()
    {
        var settings = AppSettings.Default with { FinalTranscriptionEngine = FinalTranscriptionEngine.Qwen };
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var preloadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var qwen = new Recognizer { TranscriptionStarted = started, WaitBeforeReturn = release.Task };
        using var final = new ConfiguredFinalTranscriber(new Recognizer(), () => settings,
            () => new QwenTranscriberOptions("python", "model", "worker"), _ => qwen);
        var preloader = new SelectedQwenPreloader(final.WarmUpAsync, CancellationToken.None,
            (stage, _, _) => { if (stage == "started") preloadStarted.TrySetResult(); });

        var transcription = final.TranscribeAsync("audio", CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            var preload = preloader.ApplySelection(FinalTranscriptionEngine.Qwen);
            await preloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            _ = preloader.ApplySelection(FinalTranscriptionEngine.Parakeet);
            await preload.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.IsFalse(transcription.IsCompleted, "The real request must remain in progress.");
            Assert.AreEqual(0, qwen.Warmups, "The cancelled preload must not enter the occupied final gate.");
        }
        finally { release.TrySetResult(); }
        Assert.AreEqual("recognized", (await transcription.WaitAsync(TimeSpan.FromSeconds(2))).Text);
    }

    [TestMethod]
    public async Task EarlierParakeetSessionCannotRetireNewlySelectedPreloadedQwen()
    {
        var settings = AppSettings.Default with { FinalTranscriptionEngine = FinalTranscriptionEngine.Parakeet };
        var preview = new Recognizer();
        var workers = new List<Recognizer>();
        using var final = new ConfiguredFinalTranscriber(preview, () => settings,
            () => new QwenTranscriberOptions("python", "model", "worker"),
            _ => { var worker = new Recognizer(); workers.Add(worker); return worker; });
        var earlierSession = final.CreateSessionTranscriber();
        var preloader = new SelectedQwenPreloader(final.WarmUpAsync, CancellationToken.None);

        settings = settings with { FinalTranscriptionEngine = FinalTranscriptionEngine.Qwen };
        await preloader.ApplySelection(settings.FinalTranscriptionEngine);
        Assert.HasCount(1, workers);
        Assert.AreEqual(1, workers[0].Warmups);

        await earlierSession.TranscribeAsync("earlier-audio", CancellationToken.None);
        Assert.AreEqual(1, preview.Calls, "The existing session must keep its captured Parakeet engine.");
        Assert.IsFalse(workers[0].Disposed, "An old Parakeet session must preserve newly selected warm Qwen.");
        await preloader.ApplySelection(settings.FinalTranscriptionEngine);
        await final.CreateSessionTranscriber().TranscribeAsync("next-audio", CancellationToken.None);

        Assert.HasCount(1, workers, "The next Qwen dictation must reuse the preloaded worker.");
        Assert.AreEqual(1, workers[0].Warmups);
        Assert.AreEqual(1, workers[0].Calls);
    }

    private sealed class Recognizer : ITranscriber, IWarmableTranscriber, IDisposable
    {
        public int Warmups, Calls;
        public bool Disposed;
        public TaskCompletionSource? TranscriptionStarted;
        public Task? WaitBeforeReturn;
        public Task WarmUpAsync(CancellationToken token) { Warmups++; return Task.CompletedTask; }
        public async Task<TranscriptResult> TranscribeAsync(string path, CancellationToken token)
        {
            Calls++;
            TranscriptionStarted?.TrySetResult();
            if (WaitBeforeReturn is not null) await WaitBeforeReturn.WaitAsync(token);
            return new TranscriptResult("recognized", null, null);
        }
        public void Dispose() => Disposed = true;
    }
}
