using System.Text.Json;
using PttDictation.App;

namespace PttDictation.Tests;

[TestClass]
public sealed class QwenNativeInstallationTests
{
    [TestMethod]
    public void OptionalNativeRegistrationPreservesExistingModelAndPythonPaths()
    {
        using var fixture = new Fixture();
        fixture.RegisterNative();
        var options = QwenInstallation.Load(fixture.Root);
        Assert.AreEqual(fixture.Python, options.PythonPath);
        Assert.AreEqual(fixture.Model, options.ModelPath);
        Assert.AreEqual(Path.Combine(fixture.Root, "qwen-native-installation.json"), options.NativeConfigPath);
        Assert.AreEqual("native_qwen.py", Path.GetFileName(options.WorkerPath));
        using var transcriber = new PersistentQwenTranscriber(options);
        CollectionAssert.AreEqual(new[] { "-u", options.WorkerPath, "--model", fixture.Model,
            "--native-config", options.NativeConfigPath }, transcriber.CreateStartInfo().ArgumentList.ToArray());
        Assert.AreEqual((fixture.Python, fixture.Model), QwenInstallation.ReadRegisteredPaths(fixture.Root));
    }

    [TestMethod]
    public void UnsupportedNativeRevisionFailsBeforeWorkerLaunch()
    {
        using var fixture = new Fixture();
        fixture.RegisterNative();
        var path = QwenInstallation.NativeManifestPath(fixture.Root);
        File.WriteAllText(path, File.ReadAllText(path).Replace("620d2225cc436ab0b001e3200e590e3b286ef9ab", "unvalidated-build"));
        Assert.ThrowsExactly<InvalidOperationException>(() => QwenInstallation.Load(fixture.Root));
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "ptt-native-test-" + Guid.NewGuid().ToString("N"));
        public string Python => Path.Combine(Root, "python.exe");
        public string Model => Path.Combine(Root, "model");
        public Fixture()
        {
            Directory.CreateDirectory(Model);
            File.WriteAllText(Python, "placeholder");
            File.WriteAllText(Path.Combine(Model, "model.safetensors"), "placeholder");
            File.WriteAllText(Path.Combine(Model, "config.json"), "{\"model_type\":\"qwen3_asr\",\"text_config\":{\"hidden_size\":2048}}");
            File.WriteAllText(QwenInstallation.ManifestPath(Root), JsonSerializer.Serialize(new { schema = 1, pythonPath = Python, modelPath = Model }));
        }
        public void RegisterNative()
        {
            var server = Path.Combine(Root, "llama-server.exe");
            var decoder = Path.Combine(Root, "model.gguf");
            var projector = Path.Combine(Root, "mmproj.gguf");
            foreach (var path in new[] { server, decoder, projector }) File.WriteAllText(path, "placeholder");
            File.WriteAllText(Path.Combine(Root, "qwen-native-installation.json"), JsonSerializer.Serialize(new
            {
                schema = 1, serverPath = server, modelPath = decoder, projectorPath = projector,
                modelId = "Qwen/Qwen3-ASR-1.7B-hf", llamaCommit = "620d2225cc436ab0b001e3200e590e3b286ef9ab",
                modelRevision = "36a678687ba7d07a74ca70ccb0e36902e005fb80"
            }));
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
