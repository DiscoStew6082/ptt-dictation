using System.Runtime.ExceptionServices;
using PttDictation.App;
using PttDictation.Core;

namespace PttDictation.Tests;

[TestClass]
public sealed class QwenSettingsTests
{
    [TestMethod]
    [DataRow("PCI\\VEN_10DE&DEV_2504", "NVIDIA GeForce RTX", true)]
    [DataRow("PCI\\VEN_1002&DEV_73BF", "AMD Radeon RX", false)]
    [DataRow("PCI\\VEN_8086&DEV_9A49", "Intel Graphics", false)]
    [DataRow(null, "NVIDIA virtual adapter", true)]
    public void NvidiaDetectionUsesPciVendorIdentityOrAdapterName(string? deviceId, string description, bool expected)
    {
        Assert.AreEqual(expected, GraphicsHardware.IsNvidiaAdapter(deviceId, description));
    }

    [TestMethod]
    public void NonNvidiaRuntimeUsesCpuAndParakeetWithoutMutatingSavedSnapshot()
    {
        var saved = AppSettings.Default with
        {
            DevicePreference = DevicePreference.Cuda,
            RuntimePath = @"C:\runtime\cuda\parakeet.exe",
            FinalTranscriptionEngine = FinalTranscriptionEngine.Qwen
        };

        var effective = GraphicsHardware.UseSupportedSettings(saved, hasNvidiaGpu: false);

        Assert.AreEqual(DevicePreference.Cpu, effective.DevicePreference);
        Assert.IsNull(effective.RuntimePath);
        Assert.AreEqual(FinalTranscriptionEngine.Parakeet, effective.FinalTranscriptionEngine);
        Assert.AreEqual(DevicePreference.Cuda, saved.DevicePreference);
        Assert.AreEqual(FinalTranscriptionEngine.Qwen, saved.FinalTranscriptionEngine);
        Assert.AreSame(saved, GraphicsHardware.UseSupportedSettings(saved, hasNvidiaGpu: true));
    }

    [TestMethod]
    public void NonNvidiaMachineHidesCudaAndQwenWithoutRewritingSavedPreferences()
    {
        RunOnSta(() =>
        {
            var path = Path.Combine(Path.GetTempPath(), $"non-nvidia-settings-{Guid.NewGuid():N}.json");
            var installer = new SetupInstaller((_, _) => throw new AssertFailedException("Qwen setup must remain unreachable."));
            try
            {
                var store = new AppSettingsStore(path);
                var saved = AppSettings.Default with
                {
                    DevicePreference = DevicePreference.Cuda,
                    FinalTranscriptionEngine = FinalTranscriptionEngine.Qwen
                };
                store.SaveAsync(saved, CancellationToken.None).GetAwaiter().GetResult();
                var before = File.ReadAllBytes(path);
                using var form = new SettingsForm(store, ModelRegistry.CreateDefault(),
                    (_, _) => throw new AssertFailedException("Parakeet download not requested."),
                    _ => true, installer, hasNvidiaGpu: false);
                form.UseSettings(store.Load());

                Assert.IsFalse(form.HasNvidiaGpuForTest);
                CollectionAssert.AreEqual(new[] { DevicePreference.Cpu }, form.DeviceOptionsForTest);
                CollectionAssert.AreEqual(new[] { FinalTranscriptionEngine.Parakeet }, form.FinalEngineOptionsForTest);
                Assert.IsFalse(form.HasQwenSetupControlsForTest);
                Assert.AreEqual(DevicePreference.Cpu, form.BuildSettingsForTest().DevicePreference);
                Assert.AreEqual(FinalTranscriptionEngine.Parakeet, form.SelectedFinalEngineForTest);
                Assert.AreEqual(0, installer.GetStatusCalls);
                CollectionAssert.AreEqual(before, File.ReadAllBytes(path),
                    "Opening Settings must not migrate or rewrite the saved preference.");
            }
            finally { File.Delete(path); }
        });
    }

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
                using var form = new SettingsForm(store, ModelRegistry.CreateDefault(), hasNvidiaGpu: true);
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

    [TestMethod]
    public void QwenSetupShowsProgressAndReadyStateWithoutChangingFinalEngine()
    {
        RunOnSta(() =>
        {
            IProgress<QwenSetupProgress>? progress = null;
            var finish = new TaskCompletionSource<QwenSetupStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
            var installer = new SetupInstaller((report, _) => { progress = report; return finish.Task; });
            using var form = SetupForm(installer);
            Assert.IsTrue(form.QwenInstallButtonForTest.Enabled);
            Assert.AreEqual("Download Qwen", form.QwenInstallButtonForTest.Text);
            var operation = form.InstallQwenForTest();
            Assert.IsFalse(form.QwenInstallButtonForTest.Enabled);
            Assert.IsTrue(form.QwenCancelEnabledForTest);
            progress!.Report(new QwenSetupProgress("Downloading model", 42));
            PumpUntil(() => form.QwenSetupPercentForTest == 42);
            StringAssert.Contains(form.QwenSetupStatusForTest, "Downloading model");
            finish.SetResult(new QwenSetupStatus(true, "Qwen is ready locally."));
            PumpUntil(() => operation.IsCompleted);
            operation.GetAwaiter().GetResult();
            Assert.AreEqual("Check Qwen setup", form.QwenInstallButtonForTest.Text);
            Assert.IsTrue(form.QwenInstallButtonForTest.Enabled);
            Assert.IsFalse(form.QwenCancelEnabledForTest);
            Assert.AreEqual(FinalTranscriptionEngine.Parakeet, form.SelectedFinalEngineForTest);
        });
    }

    [TestMethod]
    public void QwenSetupCanCancelAndRetryWithoutPrompting()
    {
        RunOnSta(() =>
        {
            var calls = 0;
            var installer = new SetupInstaller(async (_, token) =>
            {
                if (++calls == 1) await Task.Delay(Timeout.Infinite, token);
                return new QwenSetupStatus(true, "Ready after retry.");
            });
            using var form = SetupForm(installer);
            var operation = form.InstallQwenForTest();
            form.CancelQwenSetupForTest();
            PumpUntil(() => operation.IsCompleted);
            operation.GetAwaiter().GetResult();
            StringAssert.Contains(form.QwenSetupStatusForTest, "cancelled");
            Assert.IsTrue(form.QwenInstallButtonForTest.Enabled);
            var retry = form.InstallQwenForTest();
            PumpUntil(() => retry.IsCompleted);
            retry.GetAwaiter().GetResult();
            Assert.AreEqual("Check Qwen setup", form.QwenInstallButtonForTest.Text);
            Assert.AreEqual(2, calls);
        });
    }

    [TestMethod]
    public void FailedQwenSetupRemainsActionableAndDisposalCancelsOwnedSetup()
    {
        RunOnSta(() =>
        {
            var calls = 0;
            CancellationToken setupToken = default;
            var installer = new SetupInstaller(async (_, token) =>
            {
                if (++calls == 1) throw new IOException("Insufficient disk space.");
                setupToken = token;
                await Task.Delay(Timeout.Infinite, token);
                return new QwenSetupStatus(true, "Ready");
            });
            var form = SetupForm(installer);
            try
            {
                var failed = form.InstallQwenForTest();
                PumpUntil(() => failed.IsCompleted);
                failed.GetAwaiter().GetResult();
                StringAssert.Contains(form.QwenSetupStatusForTest, "Insufficient disk space");
                Assert.IsTrue(form.QwenInstallButtonForTest.Enabled);
                var retry = form.InstallQwenForTest();
                form.Dispose();
                Assert.IsTrue(setupToken.IsCancellationRequested);
                PumpUntil(() => retry.IsCompleted);
                retry.GetAwaiter().GetResult();
            }
            finally { form.Dispose(); }
        });
    }

    [TestMethod]
    public void AlreadyInstalledQwenHasVisibleReadyButtonWithoutStartingDownloads()
    {
        RunOnSta(() =>
        {
            var installer = new SetupInstaller((_, _) => throw new AssertFailedException("No download should start."))
            { Status = new QwenSetupStatus(true, "Qwen is installed locally.") };
            using var form = SetupForm(installer);
            Assert.AreEqual("Check Qwen setup", form.QwenInstallButtonForTest.Text);
            Assert.IsTrue(form.QwenInstallButtonForTest.Enabled);
            StringAssert.Contains(form.QwenSetupStatusForTest, "installed locally");
        });
    }

    private static SettingsForm SetupForm(IQwenInstaller installer)
    {
        var form = new SettingsForm(new AppSettingsStore(Path.Combine(Path.GetTempPath(), $"qwen-setup-{Guid.NewGuid():N}.json")),
            ModelRegistry.CreateDefault(), (_, _) => throw new AssertFailedException("Parakeet download not requested."),
            _ => true, installer);
        form.UseSettings(AppSettings.Default);
        return form;
    }

    private sealed class SetupInstaller(Func<IProgress<QwenSetupProgress>?, CancellationToken, Task<QwenSetupStatus>> install) : IQwenInstaller
    {
        public QwenSetupStatus Status { get; set; } = new(false, "Qwen has not been installed.");
        public int GetStatusCalls { get; private set; }
        public QwenSetupStatus GetStatus()
        {
            GetStatusCalls++;
            return Status;
        }
        public async Task<QwenSetupStatus> InstallAsync(IProgress<QwenSetupProgress>? progress, CancellationToken token)
        {
            Status = await install(progress, token);
            return Status;
        }
    }

    private static void PumpUntil(Func<bool> completed)
    {
        var deadline = Environment.TickCount64 + 5000;
        while (!completed())
        {
            if (Environment.TickCount64 >= deadline) Assert.Fail("Settings operation did not finish.");
            Application.DoEvents();
            Thread.Sleep(1);
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
