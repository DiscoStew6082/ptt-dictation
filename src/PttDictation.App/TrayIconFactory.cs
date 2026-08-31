namespace PttDictation.App;

internal static class TrayIconFactory
{
    private const string IconResourceName = "PttDictation.AppIcon.ico";

    public static Icon Create()
    {
        using var stream = typeof(TrayIconFactory).Assembly.GetManifestResourceStream(IconResourceName)
            ?? throw new InvalidOperationException($"Embedded app icon '{IconResourceName}' was not found.");
        using var icon = new Icon(stream, 16, 16);
        return (Icon)icon.Clone();
    }
}
