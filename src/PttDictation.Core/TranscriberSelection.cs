namespace PttDictation.Core;

public enum TranscriberKind
{
    Batch,
    Streaming
}

public static class TranscriberSelection
{
    public static TranscriberKind Resolve(AppSettings settings, ModelInfo model)
    {
        return settings.TranscriptionMode switch
        {
            TranscriptionMode.Batch => model.SupportsBatch
                ? TranscriberKind.Batch
                : throw new InvalidOperationException($"{model.DisplayName} does not support batch transcription."),
            TranscriptionMode.Streaming => model.SupportsStreaming
                ? TranscriberKind.Streaming
                : throw new InvalidOperationException($"{model.DisplayName} does not support native streaming."),
            _ => model.SupportsStreaming ? TranscriberKind.Streaming : TranscriberKind.Batch
        };
    }
}
