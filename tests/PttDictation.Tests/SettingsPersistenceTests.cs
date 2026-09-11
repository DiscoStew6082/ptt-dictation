using System.Runtime.ExceptionServices;
using PttDictation.App;
using PttDictation.Core;

namespace PttDictation.Tests;

[TestClass]
public sealed class SettingsPersistenceTests
{
    [TestMethod]
    public void ExplicitDeviceSaveSurvivesFreshSettingsFormAndStore()
    {
        RunOnSta(() =>
        {
            using var fixture = new Fixture();
            fixture.Settings = fixture.Settings with { DevicePreference = DevicePreference.Cpu };
            fixture.Store.SaveAsync(fixture.Settings, CancellationToken.None).GetAwaiter().GetResult();
            using (var first = new SettingsForm(fixture.Store, ModelRegistry.CreateDefault()))
            {
                first.UseSettings(fixture.Store.Load());
                var selector = Descendants(first).OfType<ComboBox>()
                    .Single(control => control.Items.Contains(DevicePreference.Cuda));
                selector.SelectedItem = DevicePreference.Cuda;
                first.SaveForTest();
            }
            using var reopened = new SettingsForm(new AppSettingsStore(fixture.SettingsPath), ModelRegistry.CreateDefault());
            reopened.UseSettings(new AppSettingsStore(fixture.SettingsPath).Load());
            Assert.AreEqual(DevicePreference.Cuda, reopened.BuildSettingsForTest().DevicePreference);
        });
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task CancelledPreviewNeverChangesOrSavesCpuPreference(bool tokenCancelled)
    {
        using var fixture = new Fixture();
        using var operation = new CancellationTokenSource();
        var creations = 0;
        var messages = new List<string>();
        using var transcriber = fixture.Create((_, _) =>
        {
            creations++;
            return new DelegateTranscriber((_, token) =>
            {
                if (tokenCancelled) operation.Cancel();
                throw new OperationCanceledException("Preview cancelled when stopping dictation.", token);
            });
        }, messages.Add);
        Exception? failure = null;
        try { await transcriber.TranscribeAsync("preview.wav", operation.Token); }
        catch (Exception error) { failure = error; }
        Assert.IsInstanceOfType<OperationCanceledException>(failure);
        Assert.AreEqual(DevicePreference.Cuda, fixture.Settings.DevicePreference, "Cancelling preview is not a CUDA fault.");
        Assert.AreEqual(DevicePreference.Cuda, fixture.Store.Load().DevicePreference);
        Assert.AreEqual(1, creations, "Cancellation must not start a CPU retry.");
        Assert.IsFalse(messages.Any(message => message.Contains("retrying", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task GenuineCudaFaultRetriesCpuWithoutOverwritingSavedCudaChoice()
    {
        using var fixture = new Fixture();
        var paths = new List<string>();
        var messages = new List<string>();
        using var transcriber = fixture.Create((options, _) =>
        {
            paths.Add(options.CliPath);
            return new DelegateTranscriber((_, _) => options.CliPath == fixture.CudaPath
                ? Task.FromException<TranscriptResult>(new InvalidOperationException("CUDA runtime failed."))
                : Task.FromResult(new TranscriptResult("CPU recovered transcript", TimeSpan.Zero, null)));
        }, messages.Add);
        var result = await transcriber.TranscribeAsync("recording.wav", CancellationToken.None);
        Assert.AreEqual("CPU recovered transcript", result.Text);
        CollectionAssert.AreEqual(new[] { fixture.CudaPath, fixture.CpuPath }, paths);
        Assert.AreEqual(DevicePreference.Cuda, fixture.Settings.DevicePreference);
        Assert.AreEqual(DevicePreference.Cuda, fixture.Store.Load().DevicePreference);
        Assert.AreEqual(fixture.CudaPath, fixture.Store.Load().RuntimePath, "CPU fallback must not poison the preferred GPU runtime path.");
        Assert.IsTrue(messages.Any(message => message.Contains("CPU", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void ChangingDeviceDiscardsOldRuntimePathButKeepsModel()
    {
        RunOnSta(() =>
        {
            using var fixture = new Fixture();
            using var form = new SettingsForm(fixture.Store, ModelRegistry.CreateDefault());
            form.UseSettings(fixture.Settings with { DevicePreference = DevicePreference.Cpu, RuntimePath = fixture.CpuPath });
            Descendants(form).OfType<ComboBox>().Single(control => control.Items.Contains(DevicePreference.Cuda))
                .SelectedItem = DevicePreference.Cuda;
            var saved = form.BuildSettingsForTest();
            Assert.IsNull(saved.RuntimePath, "Switching device must resolve that device's runtime, not reuse the old CPU executable.");
            Assert.AreEqual(fixture.ModelPath, saved.ModelPath);
            form.SaveForTest();
            form.SaveForTest();
            Assert.IsNull(fixture.Store.Load().RuntimePath, "A second Save must not restore the old CPU runtime path.");
        });
    }

    [TestMethod]
    public async Task CancelledSettingsSavePreservesPreviousFile()
    {
        using var fixture = new Fixture();
        var before = await File.ReadAllTextAsync(fixture.SettingsPath);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try { await fixture.Store.SaveAsync(fixture.Settings with { DevicePreference = DevicePreference.Cpu }, cancellation.Token); }
        catch (OperationCanceledException) { }
        Assert.AreEqual(before, await File.ReadAllTextAsync(fixture.SettingsPath), "Cancellation must not truncate the saved settings.");
    }

    [TestMethod]
    public void FailedSettingsSaveKeepsDeviceDraftAndReportsFailureForRetry()
    {
        RunOnSta(() =>
        {
            var directory = Path.Combine(Path.GetTempPath(), $"ptt-save-failure-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            try
            {
                var store = new AppSettingsStore(directory);
                using var form = new SettingsForm(store, ModelRegistry.CreateDefault());
                form.UseSettings(AppSettings.Default with { DevicePreference = DevicePreference.Cpu });
                Descendants(form).OfType<ComboBox>().Single(control => control.Items.Contains(DevicePreference.Cuda))
                    .SelectedItem = DevicePreference.Cuda;
                form.SaveForTest();
                StringAssert.Contains(form.SaveStatusTextForTest, "not saved");
                Assert.AreEqual(DevicePreference.Cuda, form.BuildSettingsForTest().DevicePreference);
                Directory.Delete(directory);
                form.SaveForTest();
                Assert.AreEqual(DevicePreference.Cuda, store.Load().DevicePreference);
                StringAssert.Contains(form.SaveStatusTextForTest, "Saved");
            }
            finally
            {
                if (Directory.Exists(directory)) Directory.Delete(directory);
                else File.Delete(directory);
            }
        });
    }

    [TestMethod]
    [DataRow("device")]
    [DataRow("model")]
    [DataRow("preferences")]
    public async Task RuntimeProvisioningCannotOverwriteSettingsSavedWhileItWasWaiting(string change)
    {
        using var fixture = new Fixture();
        fixture.Settings = fixture.Settings with { RuntimePath = null };
        await fixture.Store.SaveAsync(fixture.Settings, CancellationToken.None);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var transcriber = new LazyAssetTranscriber(Path.GetDirectoryName(fixture.SettingsPath)!, fixture.Store,
            () => fixture.Settings, value => fixture.Settings = value, _ => { },
            resolveRuntime: (_, token) => { started.TrySetResult(); return release.Task.WaitAsync(token); },
            createTranscriber: (_, _) => new DelegateTranscriber((_, _) => Task.FromResult(new TranscriptResult("text", null, null))));
        var pending = transcriber.TranscribeAsync("recording.wav", CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var chosen = change switch
        {
            "model" => fixture.Settings with { SelectedModelId = "realtime-eou-120m-v1-f16" },
            "preferences" => fixture.Settings with { FinalTranscriptionEngine = FinalTranscriptionEngine.Qwen, ToggleHotkey = DictationHotkey.F8, NotificationsEnabled = false },
            _ => fixture.Settings with { DevicePreference = DevicePreference.Cpu }
        };
        fixture.Settings = chosen;
        await fixture.Store.SaveAsync(chosen, CancellationToken.None);
        release.SetResult(fixture.CudaPath);
        await pending.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.AreEqual(chosen.DevicePreference, fixture.Settings.DevicePreference);
        Assert.AreEqual(chosen.SelectedModelId, fixture.Settings.SelectedModelId);
        var expectedRuntime = change == "preferences" ? fixture.CudaPath : chosen.RuntimePath;
        Assert.AreEqual(expectedRuntime, fixture.Settings.RuntimePath);
        Assert.AreEqual(chosen.FinalTranscriptionEngine, fixture.Settings.FinalTranscriptionEngine);
        Assert.AreEqual(chosen.ToggleHotkey, fixture.Settings.ToggleHotkey);
        Assert.AreEqual(chosen.NotificationsEnabled, fixture.Settings.NotificationsEnabled);
        Assert.AreEqual(chosen.DevicePreference, fixture.Store.Load().DevicePreference);
        Assert.AreEqual(chosen.SelectedModelId, fixture.Store.Load().SelectedModelId);
        Assert.AreEqual(expectedRuntime, fixture.Store.Load().RuntimePath);
        Assert.AreEqual(chosen.FinalTranscriptionEngine, fixture.Store.Load().FinalTranscriptionEngine);
        Assert.AreEqual(chosen.ToggleHotkey, fixture.Store.Load().ToggleHotkey);
        Assert.AreEqual(chosen.NotificationsEnabled, fixture.Store.Load().NotificationsEnabled);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SettingsPublicationCompletesBeforeDerivedPathMerge(bool modelDownload)
    {
        RunOnSta(() =>
        {
            using var fixture = new Fixture();
            fixture.Settings = fixture.Settings with { RuntimePath = null };
            fixture.Store.SaveAsync(fixture.Settings, CancellationToken.None).GetAwaiter().GetResult();
            using var form = new SettingsForm(fixture.Store, ModelRegistry.CreateDefault(),
                (_, _) => Task.FromResult(fixture.ModelPath), _ => false);
            form.UseSettings(fixture.Settings);
            var uiThread = Environment.CurrentManagedThreadId;
            var publicationCompleted = false;
            Task? derivedMerge = null;
            var latestPublished = fixture.Settings;
            form.SettingsSaved += (_, committed) =>
            {
                Assert.AreEqual(uiThread, Environment.CurrentManagedThreadId, "Settings publication must run on the UI caller context.");
                // Begin another settings update before this UI callback has published its choice.
                // It must wait for the entire callback, not just the preceding disk write.
                derivedMerge = fixture.Store.TryUpdateAsync(current =>
                {
                    Assert.IsTrue(publicationCompleted, "A derived update entered before the UI finished publishing its older snapshot.");
                    return current with { RuntimePath = fixture.CudaPath };
                }, updated => latestPublished = updated, CancellationToken.None);
                latestPublished = committed;
                publicationCompleted = true;
            };
            if (modelDownload) form.DownloadSelectedModelForTest();
            else form.SaveForTest();
            Assert.IsNotNull(derivedMerge);
            var deadline = Environment.TickCount64 + 5000;
            while (!derivedMerge.IsCompleted)
            {
                if (Environment.TickCount64 >= deadline) Assert.Fail("Serialized settings merge did not complete.");
                Application.DoEvents();
                Thread.Sleep(1);
            }
            derivedMerge.GetAwaiter().GetResult();
            Assert.AreEqual(fixture.CudaPath, latestPublished.RuntimePath);
            Assert.AreEqual(fixture.CudaPath, fixture.Store.Load().RuntimePath);
        });
    }
    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"ptt-device-{Guid.NewGuid():N}");
        public string SettingsPath => Path.Combine(_directory, "settings.json");
        public string CudaPath => Path.Combine(_directory, "cuda.exe");
        public string CpuPath => Path.Combine(_directory, "cpu.exe");
        public string ModelPath => Path.Combine(_directory, "model.gguf");
        public AppSettings Settings { get; set; }
        public AppSettingsStore Store { get; }
        public Fixture()
        {
            Directory.CreateDirectory(_directory);
            File.WriteAllText(CudaPath, "isolated fixture");
            File.WriteAllText(CpuPath, "isolated fixture");
            File.WriteAllText(ModelPath, "isolated fixture");
            Settings = AppSettings.Default with { RuntimePath = CudaPath, ModelPath = ModelPath, DevicePreference = DevicePreference.Cuda };
            Store = new AppSettingsStore(SettingsPath);
            Store.SaveAsync(Settings, CancellationToken.None).GetAwaiter().GetResult();
        }
        public LazyAssetTranscriber Create(Func<CliTranscriberOptions, TranscriberKind, ITranscriber> factory, Action<string> report) =>
            new(_directory, Store, () => Settings, settings => Settings = settings, report,
                resolveRuntime: (runtime, _) => Task.FromResult(runtime.DevicePreference == DevicePreference.Cuda ? CudaPath : CpuPath),
                createTranscriber: factory);
        public void Dispose()
        {
            File.Delete(SettingsPath); File.Delete(CudaPath); File.Delete(CpuPath); File.Delete(ModelPath);
            Directory.Delete(_directory);
        }
    }
    private sealed class DelegateTranscriber(Func<string, CancellationToken, Task<TranscriptResult>> action) : ITranscriber
    {
        public Task<TranscriptResult> TranscribeAsync(string path, CancellationToken token) => action(path, token);
    }
    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception error) { failure = error; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(15)));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
