using PttDictation.Core;

namespace PttDictation.App;

// Dedicated final recognizer. The preview transcriber remains owned by the app.
internal sealed class ConfiguredFinalTranscriber : ITranscriber, IWarmableTranscriber, IDisposable
{
    private readonly ITranscriber _preview;
    private readonly Func<AppSettings> _settings;
    private readonly Func<QwenTranscriberOptions> _loadOptions;
    private readonly Func<QwenTranscriberOptions, ITranscriber> _create;
    private readonly Action<string> _report;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private ITranscriber? _qwen;
    private QwenTranscriberOptions? _options;
    private int _disposed;
    private readonly object _lifecycle = new();

    public ConfiguredFinalTranscriber(ITranscriber preview, Func<AppSettings> settings, string appData,
        Action<string> report)
        : this(preview, settings, () => QwenInstallation.Load(appData),
            options => new PersistentQwenTranscriber(options), report) { }

    internal ConfiguredFinalTranscriber(ITranscriber preview, Func<AppSettings> settings,
        Func<QwenTranscriberOptions> loadOptions, Func<QwenTranscriberOptions, ITranscriber> create,
        Action<string>? report = null)
    {
        _preview = preview; _settings = settings; _loadOptions = loadOptions; _create = create;
        _report = report ?? (_ => { });
    }

    public ITranscriber CreateSessionTranscriber() => new SessionBinding(this, _settings().FinalTranscriptionEngine);

    public Task WarmUpAsync(CancellationToken cancellationToken) => WarmUpAsync(_settings().FinalTranscriptionEngine, cancellationToken);

    private async Task WarmUpAsync(FinalTranscriptionEngine engine, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        // The session already warms the Parakeet preview. Avoid warming it twice.
        if (engine != FinalTranscriptionEngine.Qwen) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _gate.WaitAsync(linked.Token);
        try
        {
            var inner = EnsureQwen();
            if (inner is IWarmableTranscriber warmable) await warmable.WarmUpAsync(linked.Token);
        }
        finally { _gate.Release(); }
    }

    public Task<TranscriptResult> TranscribeAsync(string wavPath, CancellationToken cancellationToken) =>
        TranscribeAsync(_settings().FinalTranscriptionEngine, wavPath, cancellationToken);

    private async Task<TranscriptResult> TranscribeAsync(FinalTranscriptionEngine engine, string wavPath, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _gate.WaitAsync(linked.Token);
        try
        {
            if (engine != FinalTranscriptionEngine.Qwen)
            {
                RetireQwen();
                return await _preview.TranscribeAsync(wavPath, linked.Token);
            }
            _report("Finishing transcription with Qwen.");
            var inner = EnsureQwen();
            DiagnosticTrace.Write("qwen.final_requested");
            var result = await inner.TranscribeAsync(wavPath, linked.Token);
            DiagnosticTrace.Write("qwen.final_completed", new { characters = result.Text.Length });
            return result;
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { throw; }
        catch (Exception error) when (engine == FinalTranscriptionEngine.Qwen)
        {
            DiagnosticTrace.Write("qwen.final_failed", error: error);
            throw new InvalidOperationException("Qwen final transcription failed. " + error.Message, error);
        }
        finally { _gate.Release(); }
    }

    private sealed class SessionBinding(ConfiguredFinalTranscriber owner, FinalTranscriptionEngine engine)
        : ITranscriber, IWarmableTranscriber
    {
        public Task WarmUpAsync(CancellationToken token) => owner.WarmUpAsync(engine, token);
        public Task<TranscriptResult> TranscribeAsync(string path, CancellationToken token) => owner.TranscribeAsync(engine, path, token);
    }

    private ITranscriber EnsureQwen()
    {
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            var options = _loadOptions();
            if (_qwen is not null && options == _options) return _qwen;
            RetireQwen();
            _qwen = _create(options);
            _options = options;
            return _qwen;
        }
    }

    private void RetireQwen()
    {
        lock (_lifecycle)
        {
            if (_qwen is IDisposable disposable) disposable.Dispose();
            _qwen = null; _options = null;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        RetireQwen();
    }
}
