namespace PttDictation.App;

internal static class TrayIconFactory
{
    public static Icon Create(bool active = false)
    {
        var resourceName = active ? "PttDictation.TrayActive.ico" : "PttDictation.TrayIdle.ico";
        using var stream = typeof(TrayIconFactory).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded tray icon '{resourceName}' was not found.");
        using var icon = new Icon(stream, 16, 16);
        return (Icon)icon.Clone();
    }
}
