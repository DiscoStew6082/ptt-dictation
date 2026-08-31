namespace PttDictation.Core;

public sealed record ModelInfo(
    string Id,
    string DisplayName,
    string LanguageNotes,
    string DecoderDefault,
    string Quantization,
    Uri DownloadUrl,
    string? Sha256,
    long MinimumBytes,
    bool SupportsBatch = true,
    bool SupportsStreaming = false);

public sealed class ModelRegistry(IReadOnlyList<ModelInfo> models, string defaultModelId)
{
    public const string DefaultModelId = "tdt_ctc-110m-f16";

    public IReadOnlyList<ModelInfo> Models { get; } = models;

    public ModelInfo DefaultModel => Find(defaultModelId)
        ?? throw new InvalidOperationException($"Default model '{defaultModelId}' is not registered.");

    public ModelInfo? Find(string id)
    {
        return Models.FirstOrDefault(model => string.Equals(model.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    public static ModelRegistry CreateDefault()
    {
        return new ModelRegistry(
            [
                new ModelInfo(
                    DefaultModelId,
                    "Parakeet TDT+CTC 110M F16",
                    "Fast English dictation default",
                    "tdt_ctc",
                    "F16",
                    new Uri("https://huggingface.co/mudler/parakeet-cpp-gguf/resolve/main/tdt_ctc-110m-f16.gguf"),
                    "7f9a6376edde6a74592ace48b2ebdc27a1ac972d0be9dfcc29e668d99381faf1",
                    267_452_544),
                new ModelInfo(
                    "tdt-0.6b-v3-f16",
                    "Parakeet TDT 0.6B v3 F16",
                    "Larger multilingual model",
                    "tdt",
                    "F16",
                    new Uri("https://huggingface.co/mudler/parakeet-cpp-gguf/resolve/main/tdt-0.6b-v3-f16.gguf"),
                    "8ba47343e1e919895aca90e099150a01ed203ee0942d8ed31e27295efc5abb22",
                    1_441_046_400),
                new ModelInfo(
                    "realtime-eou-120m-v1-q8_0",
                    "Parakeet Realtime EOU 120M Q8_0",
                    "Experimental English native streaming model",
                    "tdt",
                    "q8_0",
                    new Uri("https://huggingface.co/mudler/parakeet-cpp-gguf/resolve/main/realtime_eou_120m-v1-q8_0.gguf"),
                    "62616b914d6f5a683a5dea672df055b57de5c49dddf871b8b44b9c814dc3d896",
                    176_001_472,
                    SupportsStreaming: true),
                new ModelInfo(
                    "realtime-eou-120m-v1-f16",
                    "Parakeet Realtime EOU 120M F16",
                    "Experimental English native streaming model",
                    "tdt",
                    "F16",
                    new Uri("https://huggingface.co/mudler/parakeet-cpp-gguf/resolve/main/realtime_eou_120m-v1-f16.gguf"),
                    "d1a2b12f12b8a096a57499c9111ed13b442a2b786e17a292c168be45088f0edc",
                    266_517_952,
                    SupportsStreaming: true)
            ],
            DefaultModelId);
    }
}
