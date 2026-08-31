using PttDictation.Core;

namespace PttDictation.App;

internal sealed record DictationHotkeyOption(DictationHotkey Value, string DisplayName)
{
    public override string ToString() => DisplayName;
}

internal static class DictationHotkeyCatalog
{
    private const int VkF1 = 0x70;

    public static IReadOnlyList<DictationHotkeyOption> Options { get; } =
        Enum.GetValues<DictationHotkey>()
            .Select(value => new DictationHotkeyOption(value, DisplayName(value)))
            .ToArray();

    public static string DisplayName(DictationHotkey hotkey)
    {
        return hotkey switch
        {
            DictationHotkey.RightControl => "Right Ctrl",
            DictationHotkey.LeftControl => "Left Ctrl",
            DictationHotkey.RightShift => "Right Shift",
            DictationHotkey.LeftShift => "Left Shift",
            DictationHotkey.RightAlt => "Right Alt",
            DictationHotkey.LeftAlt => "Left Alt",
            >= DictationHotkey.F1 and <= DictationHotkey.F24 => hotkey.ToString(),
            _ => throw new ArgumentOutOfRangeException(nameof(hotkey), hotkey, "Unsupported dictation hotkey.")
        };
    }

    public static int VirtualKey(DictationHotkey hotkey)
    {
        return hotkey switch
        {
            DictationHotkey.LeftShift => 0xA0,
            DictationHotkey.RightShift => 0xA1,
            DictationHotkey.LeftControl => 0xA2,
            DictationHotkey.RightControl => 0xA3,
            DictationHotkey.LeftAlt => 0xA4,
            DictationHotkey.RightAlt => 0xA5,
            >= DictationHotkey.F1 and <= DictationHotkey.F24 =>
                VkF1 + ((int)hotkey - (int)DictationHotkey.F1),
            _ => throw new ArgumentOutOfRangeException(nameof(hotkey), hotkey, "Unsupported dictation hotkey.")
        };
    }

    public static DictationHotkeyOption Option(DictationHotkey hotkey)
    {
        return Options.First(option => option.Value == hotkey);
    }
}
