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
