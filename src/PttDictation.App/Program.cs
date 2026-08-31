namespace PttDictation.App;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        using var singleInstanceGuard = SingleInstanceGuard.TryAcquire();
        if (singleInstanceGuard is null)
        {
            return;
        }

        ApplicationConfiguration.Initialize();
        var openSettings = args.Any(argument =>
            string.Equals(argument, "--settings", StringComparison.OrdinalIgnoreCase));
        Application.Run(new TrayApplicationContext(openSettings));
    }
}
