using System.Text.Json;

namespace PttDictation.App;

internal static class QwenInstallation
{
    public static string ManifestPath(string appData) => Path.Combine(appData, "qwen-installation.json");

    public static QwenTranscriberOptions Load(string appData)
    {
        var path = ManifestPath(appData);
        if (!File.Exists(path))
            throw new InvalidOperationException("Qwen is not set up. Run scripts/Register-QwenInstallation.ps1 with your local Qwen installation, or select Parakeet in Settings.");
        using var manifest = JsonDocument.Parse(File.ReadAllText(path));
        var entry = manifest.RootElement;
        if (entry.GetProperty("schema").GetInt32() != 1)
            throw new InvalidOperationException("This Qwen installation manifest version is not supported.");
        var python = LocalPath(entry.GetProperty("pythonPath").GetString());
        var model = LocalPath(entry.GetProperty("modelPath").GetString());
        if (!File.Exists(python) || !Directory.Exists(model))
            throw new InvalidOperationException("The configured Qwen runtime or model is missing. Register the local installation again.");
        using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(model, "config.json")));
        if (config.RootElement.GetProperty("model_type").GetString() != "qwen3_asr"
            || config.RootElement.GetProperty("text_config").GetProperty("hidden_size").GetInt32() != 2048)
            throw new InvalidOperationException("Select the Qwen3-ASR 1.7B Hugging Face model for final transcription.");
        if (!File.Exists(Path.Combine(model, "model.safetensors"))
            && !File.Exists(Path.Combine(model, "model.safetensors.index.json")))
            throw new InvalidOperationException("The local Qwen model weights are missing.");
        var worker = Path.Combine(AppContext.BaseDirectory, "qwen-worker", "qwen_worker.py");
        if (!File.Exists(worker)) throw new InvalidOperationException("The Qwen worker is missing from this app package.");
        return new(python, model, worker);
    }

    private static string LocalPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)
            || path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
            throw new InvalidOperationException("Qwen runtime and model paths must be absolute local paths.");
        return Path.GetFullPath(path);
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
            _ = Load(appData);
            return "Configured locally. Loads on first use and stays ready for later dictations.";
        }
        catch (Exception error)
        {
            return "Qwen is not ready: " + error.Message;
        }
    }
}
