using PttDictation.Core;

namespace PttDictation.App;

internal sealed class LazyAssetTranscriber(
    string appData,
    AppSettingsStore settingsStore,
    Func<AppSettings> getSettings,
    Action<AppSettings> updateSettings,
    Action<string> reportStatus,
    TranscriptionMode? modeOverride = null) : ITranscriber, IWarmableTranscriber, IDisposable
{
    private readonly SemaphoreSlim _setupLock = new(1, 1);
    private ITranscriber? _inner;
    private TranscriberCacheKey? _cacheKey;

    public async Task WarmUpAsync(CancellationToken cancellationToken)
    {
        var inner = await EnsureInnerAsync(cancellationToken);
        if (inner is IWarmableTranscriber warmable)
        {
            await warmable.WarmUpAsync(cancellationToken);
        }
    }

    public async Task<TranscriptResult> TranscribeAsync(string wavPath, CancellationToken cancellationToken)
    {
        var inner = await EnsureInnerAsync(cancellationToken);
        try
        {
            return await inner.TranscribeAsync(wavPath, cancellationToken);
        }
        catch when (getSettings().DevicePreference == DevicePreference.Cuda)
        {
            reportStatus("CUDA transcription failed; retrying with CPU runtime.");
            var settings = getSettings() with
            {
                DevicePreference = DevicePreference.Cpu,
                RuntimePath = null
            };
            updateSettings(settings);
            await settingsStore.SaveAsync(settings, cancellationToken);
            ClearInner();
            inner = await EnsureInnerAsync(cancellationToken);
            return await inner.TranscribeAsync(wavPath, cancellationToken);
        }
    }

    private async Task<ITranscriber> EnsureInnerAsync(CancellationToken cancellationToken)
    {
        var startingSettings = EffectiveSettings(getSettings());
        if (_inner is not null && _cacheKey?.Matches(startingSettings) == true)
        {
            return _inner;
        }

        await _setupLock.WaitAsync(cancellationToken);
        try
        {
            if (_inner is not null && _cacheKey?.Matches(EffectiveSettings(getSettings())) == true)
            {
                return _inner;
            }

            Directory.CreateDirectory(appData);
            var settings = getSettings();
            var manager = new AssetManager(
                appData,
                new HttpFileDownloader(progress => reportStatus(FormatDownloadProgress(progress))));
            var runtimePath = settings.RuntimePath;
            if (string.IsNullOrWhiteSpace(runtimePath) || !File.Exists(runtimePath))
            {
                var runtime = RuntimeAssetRegistry.CreateDefault().For(settings.DevicePreference);
                reportStatus($"Downloading/verifying {runtime.Id} runtime.");
                runtimePath = await manager.EnsureRuntimeAsync(runtime, cancellationToken);
            }

            var registry = ModelRegistry.CreateDefault();
            var model = registry.Find(settings.SelectedModelId) ?? registry.DefaultModel;
            var modelPath = settings.ModelPath;
            if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
            {
                reportStatus($"Downloading/verifying {model.DisplayName}.");
                modelPath = await manager.EnsureModelAsync(model, cancellationToken);
            }

            settings = settings with { RuntimePath = runtimePath, ModelPath = modelPath };
            updateSettings(settings);
            await settingsStore.SaveAsync(settings, cancellationToken);

            var effectiveSettings = EffectiveSettings(settings);
            var options = new CliTranscriberOptions(runtimePath, modelPath, TimeSpan.FromMinutes(5));
            var runtimeDirectory = Path.GetDirectoryName(runtimePath);
            var serverPath = runtimeDirectory is null
                ? null
                : Path.Combine(runtimeDirectory, "parakeet-server.exe");
            ClearInner();
            if (serverPath is not null && File.Exists(serverPath))
            {
                _inner = new PersistentParakeetServerTranscriber(options, serverPath);
            }
            else
            {
                var kind = TranscriberSelection.Resolve(effectiveSettings, model);
                _inner = kind == TranscriberKind.Streaming
                    ? new ParakeetStreamingCliTranscriber(options, new SystemProcessRunner())
                    : new ParakeetCliTranscriber(options, new SystemProcessRunner());
            }
            _cacheKey = new TranscriberCacheKey(
                effectiveSettings.SelectedModelId,
                effectiveSettings.TranscriptionMode,
                effectiveSettings.DevicePreference,
                runtimePath,
                modelPath);
            return _inner;
        }
        finally
        {
            _setupLock.Release();
        }
    }

    public void Dispose()
    {
        ClearInner();
    }

    private void ClearInner()
    {
        if (_inner is IDisposable disposable)
        {
            disposable.Dispose();
        }

        _inner = null;
        _cacheKey = null;
    }

    private AppSettings EffectiveSettings(AppSettings settings)
    {
        return modeOverride is { } mode
            ? settings with { TranscriptionMode = mode }
            : settings;
    }

    private static string FormatDownloadProgress(FileDownloadProgress progress)
    {
        var fileName = Path.GetFileName(progress.Source.LocalPath);
        var receivedMegabytes = progress.BytesReceived / (1024d * 1024d);
        if (progress.TotalBytes is { } totalBytes && totalBytes > 0 && progress.Percent is { } percent)
        {
            var totalMegabytes = totalBytes / (1024d * 1024d);
            return $"Downloading {fileName}: {percent}% ({receivedMegabytes:F0} of {totalMegabytes:F0} MB).";
        }

        return $"Downloading {fileName}: {receivedMegabytes:F0} MB received.";
    }

    private sealed record TranscriberCacheKey(
        string SelectedModelId,
        TranscriptionMode TranscriptionMode,
        DevicePreference DevicePreference,
        string RuntimePath,
        string ModelPath)
    {
        public bool Matches(AppSettings settings)
        {
            return string.Equals(SelectedModelId, settings.SelectedModelId, StringComparison.OrdinalIgnoreCase)
                && TranscriptionMode == settings.TranscriptionMode
                && DevicePreference == settings.DevicePreference
                && string.Equals(RuntimePath, settings.RuntimePath, StringComparison.OrdinalIgnoreCase)
                && string.Equals(ModelPath, settings.ModelPath, StringComparison.OrdinalIgnoreCase);
        }
    }
}
