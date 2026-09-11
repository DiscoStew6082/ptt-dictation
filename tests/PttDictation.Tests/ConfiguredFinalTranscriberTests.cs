using System.Text.Json;
using PttDictation.App;
using PttDictation.Core;

namespace PttDictation.Tests;

[TestClass]
public sealed class ConfiguredFinalTranscriberTests
{
    private static readonly QwenTranscriberOptions Options = new("python", "model", "worker");

    [TestMethod]
    public async Task WarmResidentQwenOwnsOnlyFinalRecognitionAndParakeetRemainsSelectable()
    {
        var settings = AppSettings.Default with { FinalTranscriptionEngine = FinalTranscriptionEngine.Qwen };
        var preview = new Recognizer("preview words");
        var qwen = new Recognizer("Qwen final words");
        var creations = 0;
        using var final = new ConfiguredFinalTranscriber(preview, () => settings, () => Options,
            _ => { creations++; return qwen; });
        await final.WarmUpAsync(CancellationToken.None);
        Assert.AreEqual("Qwen final words", (await final.TranscribeAsync("audio", CancellationToken.None)).Text);
        await final.TranscribeAsync("audio", CancellationToken.None);
        Assert.AreEqual(1, creations);
        Assert.AreEqual(1, qwen.Warmups);
        Assert.AreEqual(2, qwen.Calls);
        Assert.AreEqual(0, preview.Calls);
        settings = settings with { FinalTranscriptionEngine = FinalTranscriptionEngine.Parakeet };
        await final.WarmUpAsync(CancellationToken.None);
        Assert.AreEqual(0, preview.Warmups, "The session already warms its preview recognizer.");
        Assert.AreEqual("preview words", (await final.TranscribeAsync("audio", CancellationToken.None)).Text);
        Assert.IsTrue(qwen.Disposed);
        Assert.IsFalse(preview.Disposed, "The final selector does not own the preview recognizer.");
    }

    [TestMethod]
    public async Task SettingsChangesApplyToNextDictationWithoutChangingTheCurrentFinalEngine()
    {
        var settings = AppSettings.Default with { FinalTranscriptionEngine = FinalTranscriptionEngine.Qwen };
        var preview = new Recognizer("Parakeet final");
        var qwen = new Recognizer("Qwen final");
        using var owner = new ConfiguredFinalTranscriber(preview, () => settings, () => Options, _ => qwen);
        var current = owner.CreateSessionTranscriber();
        settings = settings with { FinalTranscriptionEngine = FinalTranscriptionEngine.Parakeet };
        await ((IWarmableTranscriber)current).WarmUpAsync(CancellationToken.None);
        Assert.AreEqual("Qwen final", (await current.TranscribeAsync("audio", CancellationToken.None)).Text);
        var next = owner.CreateSessionTranscriber();
        Assert.AreEqual("Parakeet final", (await next.TranscribeAsync("audio", CancellationToken.None)).Text);
        Assert.AreEqual(1, qwen.Calls);
        Assert.AreEqual(1, preview.Calls);
    }

    [TestMethod]
    public async Task MissingQwenAndFailedQwenNeverSilentlyReturnParakeetText()
    {
        var preview = new Recognizer("wrong fallback");
        var settings = AppSettings.Default with { FinalTranscriptionEngine = FinalTranscriptionEngine.Qwen };
        using var missing = new ConfiguredFinalTranscriber(preview, () => settings,
            () => throw new InvalidOperationException("installation missing"), _ => preview);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => missing.TranscribeAsync("audio", CancellationToken.None));
        StringAssert.Contains(error.Message, "installation missing");
        var broken = new Recognizer("") { Failure = new IOException("worker exited") };
        using var failed = new ConfiguredFinalTranscriber(preview, () => settings, () => Options, _ => broken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => failed.TranscribeAsync("audio", CancellationToken.None));
        Assert.AreEqual(0, preview.Calls);
    }

    [TestMethod]
    public async Task CancellationAndDisposalCancelFinalWorkWithoutReturningText()
    {
        var qwen = new Recognizer("must not return") { WaitForCancellation = true };
        using var final = new ConfiguredFinalTranscriber(new Recognizer("preview"),
            () => AppSettings.Default with { FinalTranscriptionEngine = FinalTranscriptionEngine.Qwen },
            () => Options, _ => qwen);
        using var cancellation = new CancellationTokenSource();
        var pending = final.TranscribeAsync("audio", cancellation.Token);
        await qwen.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
        var pendingAgain = final.TranscribeAsync("audio", CancellationToken.None);
        final.Dispose();
        await Assert.ThrowsAsync<OperationCanceledException>(() => pendingAgain);
        Assert.IsTrue(qwen.Disposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => final.TranscribeAsync("audio", CancellationToken.None));
    }

    [TestMethod]
    public void InstallationRejectsUnsupportedModelAndMissingRuntime()
    {
        var root = Path.Combine(Path.GetTempPath(), "ptt-qwen-installation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.IsFalse(QwenInstallation.IsAvailable(root));
            StringAssert.Contains(QwenInstallation.Describe(root), "not ready");
            var python = Path.Combine(root, "python.exe");
            File.WriteAllText(python, "fixture");
            File.WriteAllText(Path.Combine(root, "model.safetensors"), "fixture");
            File.WriteAllText(Path.Combine(root, "config.json"), """{"model_type":"qwen3_tts","text_config":{"hidden_size":2048}}""");
            File.WriteAllText(QwenInstallation.ManifestPath(root), JsonSerializer.Serialize(new
            { schema = 1, pythonPath = python, modelPath = root }));
            Assert.Throws<InvalidOperationException>(() => QwenInstallation.Load(root));
            File.WriteAllText(Path.Combine(root, "config.json"), """{"model_type":"qwen3_asr","text_config":{"hidden_size":2048}}""");
            var configured = QwenInstallation.Load(root);
            Assert.AreEqual(python, configured.PythonPath);
            File.Delete(python);
            Assert.IsFalse(QwenInstallation.IsAvailable(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class Recognizer(string text) : ITranscriber, IWarmableTranscriber, IDisposable
    {
        public int Calls, Warmups;
        public bool Disposed, WaitForCancellation;
        public Exception? Failure;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task WarmUpAsync(CancellationToken token) { Warmups++; return Task.CompletedTask; }
        public async Task<TranscriptResult> TranscribeAsync(string path, CancellationToken token)
        {
            Calls++;
            Started.TrySetResult();
            if (Failure is not null) throw Failure;
            if (WaitForCancellation) await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new(text, null, null);
        }
        public void Dispose() => Disposed = true;
    }
}
