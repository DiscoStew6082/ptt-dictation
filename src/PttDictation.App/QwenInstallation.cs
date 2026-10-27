using System.Text.Json;

namespace PttDictation.App;

internal static class QwenInstallation
{
    public static string ManifestPath(string appData) => Path.Combine(appData, "qwen-installation.json");
    internal static string NativeManifestPath(string appData) => Path.Combine(appData, "qwen-native-installation.json");

    internal static (string Python, string Model) ReadRegisteredPaths(string appData)
    {
        var path = ManifestPath(appData);
        if (!File.Exists(path)) throw new InvalidOperationException("Qwen is not installed. Open Settings and choose Download Qwen.");
        using var manifest = JsonDocument.Parse(File.ReadAllText(path));
        if (manifest.RootElement.GetProperty("schema").GetInt32() != 1)
            throw new InvalidOperationException("This Qwen installation manifest version is not supported.");
        return (LocalPath(manifest.RootElement.GetProperty("pythonPath").GetString()),
            LocalPath(manifest.RootElement.GetProperty("modelPath").GetString()));
    }

    public static QwenTranscriberOptions Load(string appData)
    {
        var path = ManifestPath(appData);
        if (!File.Exists(path))
            throw new InvalidOperationException("Qwen is not installed. Open Settings and choose Download Qwen, or select Parakeet.");
        using var manifest = JsonDocument.Parse(File.ReadAllText(path));
        var entry = manifest.RootElement;
        if (entry.GetProperty("schema").GetInt32() != 1)
            throw new InvalidOperationException("This Qwen installation manifest version is not supported.");
        var python = LocalPath(entry.GetProperty("pythonPath").GetString());
        var model = LocalPath(entry.GetProperty("modelPath").GetString());
        if (!File.Exists(python) || !Directory.Exists(model))
            throw new InvalidOperationException("The configured Qwen runtime or model is missing. Open Settings and choose Download Qwen to repair it.");
        using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(model, "config.json")));
        if (config.RootElement.GetProperty("model_type").GetString() != "qwen3_asr"
            || config.RootElement.GetProperty("text_config").GetProperty("hidden_size").GetInt32() != 2048)
            throw new InvalidOperationException("Select the Qwen3-ASR 1.7B Hugging Face model for final transcription.");
        if (!File.Exists(Path.Combine(model, "model.safetensors"))
            && !File.Exists(Path.Combine(model, "model.safetensors.index.json")))
            throw new InvalidOperationException("The local Qwen model weights are missing.");
        string? native = NativeManifestPath(appData);
        if (File.Exists(native))
        {
            using var nativeManifest = JsonDocument.Parse(File.ReadAllText(native));
            var nativeEntry = nativeManifest.RootElement;
            if (nativeEntry.GetProperty("schema").GetInt32() != 1
                || nativeEntry.GetProperty("modelId").GetString() != "Qwen/Qwen3-ASR-1.7B-hf"
                || nativeEntry.GetProperty("llamaCommit").GetString() != "620d2225cc436ab0b001e3200e590e3b286ef9ab"
                || nativeEntry.GetProperty("modelRevision").GetString() != "36a678687ba7d07a74ca70ccb0e36902e005fb80")
                throw new InvalidOperationException("The optional native Qwen installation is not supported. Re-register it or disable it to use the existing runtime.");
            foreach (var property in new[] { "serverPath", "modelPath", "projectorPath" })
                if (!File.Exists(LocalPath(nativeEntry.GetProperty(property).GetString())))
                    throw new InvalidOperationException("An optional native Qwen asset is missing. Re-register it or disable it to use the existing runtime.");
        }
        else native = null;
        var worker = Path.Combine(AppContext.BaseDirectory, "qwen-worker", native is null ? "qwen_worker.py" : "native_qwen.py");
        if (!File.Exists(worker)) throw new InvalidOperationException("The Qwen worker is missing from this app package.");
        return new(python, model, worker, native);
    }

    private static string LocalPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)
            || path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
            throw new InvalidOperationException("Qwen runtime and model paths must be absolute local paths.");
        var full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\", StringComparison.Ordinal))
            throw new InvalidOperationException("Qwen runtime and model paths must be absolute local paths.");
        return full;
    }

    public static bool IsAvailable(string appData)
    {
        try { _ = Load(appData); return true; }
        catch { return false; }
    }

    public static string Describe(string appData)
    {
        try
        {
            var options = Load(appData);
            return options.NativeConfigPath is not null
                ? "Qwen is ready with the faster native CUDA engine. When selected, loads in the background and stays ready for later dictations."
                : "Qwen is ready using the standard engine. Choose Check Qwen setup to enable native acceleration on supported NVIDIA hardware.";
        }
        catch (Exception error)
        {
            return "Qwen is not ready: " + error.Message;
        }
    }
}
