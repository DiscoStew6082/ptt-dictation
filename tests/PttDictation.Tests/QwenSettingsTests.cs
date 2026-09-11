using System.Runtime.ExceptionServices;
using PttDictation.App;
using PttDictation.Core;

namespace PttDictation.Tests;

[TestClass]
public sealed class QwenSettingsTests
{
    [TestMethod]
    public async Task ExistingSettingsRemainParakeetAndQwenRoundTripsAsAString()
    {
        var path = Path.Combine(Path.GetTempPath(), $"qwen-settings-{Guid.NewGuid():N}.json");
        try
        {
            var store = new AppSettingsStore(path);
            Assert.AreEqual(FinalTranscriptionEngine.Parakeet, store.Load().FinalTranscriptionEngine);
            await File.WriteAllTextAsync(path, "{\"selectedModelId\":\"realtime-eou-120m-v1-f16\",\"devicePreference\":\"Cpu\"}");
            var old = await store.LoadAsync(CancellationToken.None);
            Assert.AreEqual(FinalTranscriptionEngine.Parakeet, old.FinalTranscriptionEngine);
            await store.SaveAsync(old with { FinalTranscriptionEngine = FinalTranscriptionEngine.Qwen }, CancellationToken.None);
            StringAssert.Contains(await File.ReadAllTextAsync(path), "\"finalTranscriptionEngine\": \"Qwen\"");
            Assert.AreEqual(FinalTranscriptionEngine.Qwen, store.Load().FinalTranscriptionEngine);
            Assert.AreEqual(DevicePreference.Cpu, store.Load().DevicePreference);
            Assert.AreEqual(old.SelectedModelId, store.Load().SelectedModelId);
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public void SettingsSelectionSavesQwenAndCanReturnToParakeetWithoutChangingPreviewModel()
    {
        RunOnSta(() =>
        {
            var path = Path.Combine(Path.GetTempPath(), $"qwen-form-{Guid.NewGuid():N}.json");
            try
            {
                var store = new AppSettingsStore(path);
                using var form = new SettingsForm(store, ModelRegistry.CreateDefault());
                var original = AppSettings.Default with
                {
                    SelectedModelId = "realtime-eou-120m-v1-f16",
                    DevicePreference = DevicePreference.Cpu,
                    TranscriptionMode = TranscriptionMode.Streaming
                };
                form.UseSettings(original);
                form.SelectedFinalEngineForTest = FinalTranscriptionEngine.Qwen;
                Assert.IsTrue(form.SelectorsUseDarkFlatStyleForTest);
                StringAssert.Contains(form.FinalEngineStatusForTest, "after you stop");
                StringAssert.Contains(form.FinalEngineStatusForTest, "live Parakeet");
                form.SaveForTest();
                var saved = store.Load();
                Assert.AreEqual(FinalTranscriptionEngine.Qwen, saved.FinalTranscriptionEngine);
                Assert.AreEqual(original.SelectedModelId, saved.SelectedModelId);
                Assert.AreEqual(original.DevicePreference, saved.DevicePreference);
                Assert.AreEqual(original.TranscriptionMode, saved.TranscriptionMode);
                form.UseSettings(saved);
                Assert.AreEqual(FinalTranscriptionEngine.Qwen, form.SelectedFinalEngineForTest);
                form.SelectedFinalEngineForTest = FinalTranscriptionEngine.Parakeet;
                form.SaveForTest();
                Assert.AreEqual(FinalTranscriptionEngine.Parakeet, store.Load().FinalTranscriptionEngine);
            }
            finally { File.Delete(path); }
        });
    }

    [TestMethod]
    public async Task DistinctFinalWarmsDuringRecordingAndStopPreservesItsWarmupToken()
    {
        using var operation = new CancellationTokenSource();
        var recorder = new Recorder();
        var preview = new WarmRecognizer("Parakeet live words");
        var final = new WarmRecognizer("Qwen corrected words");
        var session = new ChunkedTranscribingDictationSession(recorder, preview, final);
        var update = new TaskCompletionSource<TranscriptUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.TranscriptUpdated += value => update.TrySetResult(value);
        try
        {
            await session.StartAsync(operation.Token).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.IsTrue(recorder.Started);
            await Task.WhenAll(preview.Started.Task, final.Started.Task).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.IsFalse(final.Finished.Task.IsCompleted, "Warmup must not block microphone startup.");
            Assert.AreEqual(operation.Token, final.Token);
            recorder.Publish();
            Assert.AreEqual("Parakeet live words", (await update.Task.WaitAsync(TimeSpan.FromSeconds(3))).StableText);
            var result = await session.StopAsync(operation.Token);
            Assert.AreEqual("Qwen corrected words", result.Transcript.Text);
            Assert.AreEqual("final.wav", final.LastAudio);
            Assert.IsFalse(final.Token.IsCancellationRequested, "Stopping preview work must not cancel final warmup.");
            Assert.AreEqual(1, preview.WarmupCount);
            Assert.AreEqual(1, final.WarmupCount);
        }
        finally
        {
            preview.Release.TrySetResult();
            final.Release.TrySetResult();
        }
    }

    [TestMethod]
    public async Task SameRecognizerWarmsOnlyOnce()
    {
        var recognizer = new WarmRecognizer("text");
        var session = new ChunkedTranscribingDictationSession(new Recorder(), recognizer, recognizer);
        try
        {
            await session.StartAsync(CancellationToken.None);
            await recognizer.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await session.StopAsync(CancellationToken.None);
            Assert.AreEqual(1, recognizer.WarmupCount);
        }
        finally { recognizer.Release.TrySetResult(); }
    }

    [TestMethod]
    public async Task OperationCancellationReachesBothWarmups()
    {
        using var operation = new CancellationTokenSource();
        var preview = new WarmRecognizer("preview");
        var final = new WarmRecognizer("final");
        var session = new ChunkedTranscribingDictationSession(new Recorder(), preview, final);
        await session.StartAsync(operation.Token);
        await Task.WhenAll(preview.Started.Task, final.Started.Task).WaitAsync(TimeSpan.FromSeconds(3));
        await operation.CancelAsync();
        await Task.WhenAll(preview.Finished.Task, final.Finished.Task).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.IsTrue(preview.Token.IsCancellationRequested);
        Assert.IsTrue(final.Token.IsCancellationRequested);
        await session.CancelAsync(CancellationToken.None);
    }

    [TestMethod]
    public async Task FinalWarmupFailureDoesNotFailRecordingOrSkipFinalRecognition()
    {
        var preview = new WarmRecognizer("preview");
        var final = new WarmRecognizer("recovered final", failWarmup: true);
        var recorder = new Recorder();
        var session = new ChunkedTranscribingDictationSession(recorder, preview, final);
        try
        {
            await session.StartAsync(CancellationToken.None);
            await final.Finished.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.IsTrue(recorder.Started);
            var result = await session.StopAsync(CancellationToken.None);
            Assert.AreEqual("recovered final", result.Transcript.Text);
            Assert.AreEqual("final.wav", final.LastAudio);
        }
        finally { preview.Release.TrySetResult(); }
    }

    [TestMethod]
    public async Task SessionFactoryBindsFinalRecognizerAtCreationOnceForEachSession()
    {
        var preview = new WarmRecognizer("preview");
        var qwen = new WarmRecognizer("Qwen final");
        var parakeet = new WarmRecognizer("Parakeet final");
        ITranscriber selected = qwen;
        var resolutions = 0;
        var factory = new ChunkedTranscribingDictationSessionFactory(new Recorder(), preview,
            () => { resolutions++; return selected; });
        try
        {
            Assert.AreEqual(0, resolutions);
            var first = factory.CreateSession();
            Assert.AreEqual(1, resolutions);
            selected = parakeet;
            await first.StartAsync(CancellationToken.None);
            Assert.AreEqual("Qwen final", (await first.StopAsync(CancellationToken.None)).Transcript.Text);
            Assert.AreEqual(1, resolutions);
            var second = factory.CreateSession();
            Assert.AreEqual(2, resolutions);
            selected = qwen;
            await second.StartAsync(CancellationToken.None);
            Assert.AreEqual("Parakeet final", (await second.StopAsync(CancellationToken.None)).Transcript.Text);
            Assert.AreEqual(2, resolutions);
        }
        finally
        {
            preview.Release.TrySetResult();
            qwen.Release.TrySetResult();
            parakeet.Release.TrySetResult();
        }
    }

    private sealed class Recorder : IChunkedAudioRecorder
    {
        public event Action<RecordedAudio>? AudioChunkReady;
        public bool Started { get; private set; }
        public Task StartAsync(CancellationToken token) { Started = true; return Task.CompletedTask; }
        public Task<RecordedAudio> StopAsync(CancellationToken token) =>
            Task.FromResult(new RecordedAudio("final.wav", TimeSpan.FromSeconds(2)));
        public void Publish() => AudioChunkReady?.Invoke(new RecordedAudio("preview.wav", TimeSpan.FromSeconds(1)));
    }

    private sealed class WarmRecognizer(string text, bool failWarmup = false) : ITranscriber, IWarmableTranscriber
    {
        private int _warmupCount;
        public int WarmupCount => Volatile.Read(ref _warmupCount);
        public CancellationToken Token { get; private set; }
        public string? LastAudio { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task WarmUpAsync(CancellationToken token)
        {
            Interlocked.Increment(ref _warmupCount);
            Token = token;
            Started.TrySetResult();
            try
            {
                if (failWarmup) throw new InvalidOperationException("Warmup unavailable; final call may retry.");
                await Release.Task.WaitAsync(token);
            }
            finally { Finished.TrySetResult(); }
        }
        public Task<TranscriptResult> TranscribeAsync(string path, CancellationToken token)
        {
            LastAudio = path;
            return Task.FromResult(new TranscriptResult(text, TimeSpan.Zero, null));
        }
    }

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception error) { failure = error; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(15)), "Settings test did not complete.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
