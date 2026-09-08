using PttDictation.Core;

namespace PttDictation.App;

static class Program
{
    [STAThread]
    static void Main()
    {
        using var singleInstanceGuard = SingleInstanceGuard.TryAcquire();
        if (singleInstanceGuard is null)
        {
            SingleInstanceActivation.TryNotify();
            return;
        }

        using var diagnostics = DiagnosticTrace.Configure(
            Path.Combine(AppPaths.RootDirectory, "diagnostics", "experimental"),
            "experimental-live-insertion-" + typeof(Program).Assembly.ManifestModule.ModuleVersionId);
        DiagnosticTrace.Write("app.started", new { executable = Environment.ProcessPath, processId = Environment.ProcessId });
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            DiagnosticTrace.Write("app.unhandled_exception", new { args.IsTerminating }, args.ExceptionObject as Exception);
            if (args.IsTerminating)
                try { DiagnosticTrace.FlushAsync().Wait(TimeSpan.FromSeconds(1)); } catch { }
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
            DiagnosticTrace.Write("app.unobserved_task_exception", error: args.Exception);

        ApplicationConfiguration.Initialize();
        var context = new TrayApplicationContext();
        using var activation = SingleInstanceActivation.Listen();
        using var activationTimer = new System.Windows.Forms.Timer
        {
            Interval = 100
        };
        activationTimer.Tick += (_, _) =>
        {
            if (activation.ConsumePending())
            {
                context.OpenSettings();
            }
        };
        activationTimer.Start();

        Application.Run(context);
    }
}
