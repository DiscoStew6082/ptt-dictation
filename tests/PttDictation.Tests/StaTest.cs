using System.Runtime.ExceptionServices;

namespace PttDictation.Tests;

internal static class StaTest
{
    public static void Run(Func<Task> action) => Run(() => action().GetAwaiter().GetResult());

    public static void Run(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true, Name = "PTT test STA" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(20)), "STA test did not complete within 20 seconds.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
