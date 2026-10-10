using System.Diagnostics;

namespace PttDictation.Tests;

internal static class ColdStartProcessGate
{
    public sealed record Observation(int? ProcessId, bool? HasExited, string? Error);

    public static void AssertNoneRunning(IEnumerable<Process> processes, string browserName,
        Action<Observation[]> report)
    {
        var observations = processes.Select(Observe).ToArray();
        report(observations);
        // Enumeration can retain an exited process record. Only a positively
        // observed exit is safe to ignore; inaccessible/unknown stays blocking.
        Assert.AreEqual(0, observations.Count(value => value.HasExited != true),
            $"Cold test requires no running {browserName} processes on its dedicated runner. Unrelated processes will not be terminated.");
    }

    private static Observation Observe(Process process)
    {
        int? id = null;
        try
        {
            id = process.Id;
            return new(id, process.HasExited, null);
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        { return new(id, null, error.ToString()); }
    }
}
