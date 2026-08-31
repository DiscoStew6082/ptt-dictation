namespace PttDictation.App;

internal static class AppPaths
{
    public static string SettingsPath
    {
        get
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(root, "PttDictation", "settings.json");
        }
    }

    public static string RootDirectory => Path.GetDirectoryName(SettingsPath)
        ?? Path.Combine(Path.GetTempPath(), "PttDictation");
}
