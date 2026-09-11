using System.Text.Json;
using System.Text.Json.Serialization;

namespace PttDictation.Core;

public sealed record AppSettings
{
    public DictationHotkey HoldHotkey { get; init; } = DictationHotkey.RightControl;
    public DictationHotkey ToggleHotkey { get; init; } = DictationHotkey.RightShift;
    public string SelectedModelId { get; init; } = ModelRegistry.DefaultModelId;
    public TranscriptionMode TranscriptionMode { get; init; } = TranscriptionMode.Auto;
    public FinalTranscriptionEngine FinalTranscriptionEngine { get; init; } = FinalTranscriptionEngine.Parakeet;
    public string? RuntimePath { get; init; }
    public string? ModelPath { get; init; }
    public DevicePreference DevicePreference { get; init; } = DevicePreference.Cuda;
    public bool NotificationsEnabled { get; init; } = true;
    public bool AudibleStatusEnabled { get; init; } = true;
    public List<TranscriptCorrection> TranscriptCorrections { get; init; } = [];

    public static AppSettings Default { get; } = new();
}

[JsonConverter(typeof(JsonStringEnumConverter<DictationHotkey>))]
public enum DictationHotkey
{
    RightControl,
    LeftControl,
    RightShift,
    LeftShift,
    RightAlt,
    LeftAlt,
    F1,
    F2,
    F3,
    F4,
    F5,
    F6,
    F7,
    F8,
    F9,
    F10,
    F11,
    F12,
    F13,
    F14,
    F15,
    F16,
    F17,
    F18,
    F19,
    F20,
    F21,
    F22,
    F23,
    F24
}

[JsonConverter(typeof(JsonStringEnumConverter<TranscriptionMode>))]
public enum TranscriptionMode
{
    Auto,
    Batch,
    Streaming
}

[JsonConverter(typeof(JsonStringEnumConverter<FinalTranscriptionEngine>))]
public enum FinalTranscriptionEngine
{
    Parakeet,
    Qwen
}

[JsonConverter(typeof(JsonStringEnumConverter<DevicePreference>))]
public enum DevicePreference
{
    Cuda,
    Cpu
}

public sealed class AppSettingsStore(string path)
{
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter<DevicePreference>() }
    };

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return AppSettings.Default;
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? AppSettings.Default;
    }

    public AppSettings Load()
    {
        if (!File.Exists(path))
        {
            return AppSettings.Default;
        }

        return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions)
            ?? AppSettings.Default;
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await WriteAsync(settings, cancellationToken).ConfigureAwait(false); }
        finally { _writeGate.Release(); }
    }

    // UI callers publish their committed choice on their own context before a derived
    // settings update can acquire the gate. SaveAsync remains context-independent.
    public async Task SaveAndPublishAsync(
        AppSettings settings,
        Action<AppSettings> onCommitted,
        CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await WriteAsync(settings, cancellationToken);
            onCommitted(settings);
        }
        finally { _writeGate.Release(); }
    }
    // Derived values share the explicit-save gate and merge with the latest saved choices.
    public async Task TryUpdateAsync(
        Func<AppSettings, AppSettings?> update,
        Action<AppSettings> onCommitted,
        CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var updated = update(Load());
            if (updated is null) return;
            await WriteAsync(updated, cancellationToken).ConfigureAwait(false);
            onCommitted(updated);
        }
        finally { _writeGate.Release(); }
    }

    private async Task WriteAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var destination = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);
        var temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 4096, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}