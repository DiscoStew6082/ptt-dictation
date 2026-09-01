using System.Text.Json;
using System.Text.Json.Serialization;

namespace PttDictation.Core;

public sealed record AppSettings
{
    public DictationHotkey HoldHotkey { get; init; } = DictationHotkey.RightControl;
    public DictationHotkey ToggleHotkey { get; init; } = DictationHotkey.RightShift;
    public string SelectedModelId { get; init; } = ModelRegistry.DefaultModelId;
    public TranscriptionMode TranscriptionMode { get; init; } = TranscriptionMode.Auto;
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

[JsonConverter(typeof(JsonStringEnumConverter<DevicePreference>))]
public enum DevicePreference
{
    Cuda,
    Cpu
}

public sealed class AppSettingsStore(string path)
{
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
        return await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions, cancellationToken)
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
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken);
    }
}
