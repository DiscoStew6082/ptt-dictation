using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Text;

namespace PttDictation.Tests;

internal static class ColdStartProcessGate
{
    public sealed record Observation(int? ProcessId, bool? HasExited, string? Error,
        DateTime? StartedUtc = null, string? Executable = null, int? ParentProcessIdAtCreation = null,
        string? IdentityError = null);
    internal sealed record Identity(DateTime? StartedUtc, string? Executable, int? ParentProcessIdAtCreation, string? Error);

    // Read metadata through this process's retained handle. Parent PID is the
    // recorded creator PID, not proof that a currently reused PID is its parent.
    internal static Identity ReadIdentity(Process process)
    {
        try { return ReadIdentity(process.SafeHandle); }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception)
        { return new(null, null, null, error.ToString()); }
    }

    internal static Identity ReadIdentity(SafeProcessHandle handle)
    {
        DateTime? started = null;
        string? executable = null;
        int? parent = null;
        var errors = new List<string>();
        void Read(Action action)
        {
            try { action(); }
            catch (Exception error) when (error is InvalidOperationException or Win32Exception or ArgumentException)
            { errors.Add(error.ToString()); }
        }
        Read(() =>
        {
            if (!GetProcessTimes(handle, out var creation, out _, out _, out _)) throw new Win32Exception();
            started = DateTime.FromFileTimeUtc(creation);
        });
        Read(() =>
        {
            var path = new StringBuilder(32768);
            var length = path.Capacity;
            if (!QueryFullProcessImageName(handle, 0, path, ref length)) throw new Win32Exception();
            executable = path.ToString();
        });
        Read(() =>
        {
            var status = NtQueryInformationProcess(handle, 0, out var information,
                Marshal.SizeOf<ProcessBasicInformation>(), out _);
            if (status != 0) throw new InvalidOperationException($"Process parent query failed: NTSTATUS 0x{status:x8}.");
            parent = checked((int)information.InheritedFromProcessId.ToInt64());
        });
        return new(started, executable, parent, errors.Count == 0 ? null : string.Join(Environment.NewLine, errors));
    }

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

    public static async Task WaitUntilNoneRunningAsync(Func<Process[]> enumerate, string browserName,
        Action<Observation[]> report, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(timeout, TimeSpan.Zero);
        var started = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var processes = enumerate();
            Observation[] observations;
            try { observations = processes.Select(Observe).ToArray(); }
            finally { foreach (var process in processes) process.Dispose(); }
            report(observations);
            cancellationToken.ThrowIfCancellationRequested();
            if (observations.All(value => value.HasExited == true))
            {
                // Zero requests one immediate observation; a positive budget
                // must still be valid when the observation is ready to use.
                Assert.IsTrue(timeout == TimeSpan.Zero || started.Elapsed < timeout,
                    $"The empty {browserName} observation arrived after the cold-start deadline.");
                return;
            }

            // A busy runner may become cold naturally. Never reset this budget
            // for a new PID, terminate unowned work, or treat unknown as exited.
            var remaining = timeout - started.Elapsed;
            Assert.IsTrue(remaining > TimeSpan.Zero,
                $"Cold test requires no running {browserName} processes after waiting {timeout.TotalSeconds:0.###} seconds. Unrelated processes will not be terminated.");
            await Task.Delay(remaining < TimeSpan.FromMilliseconds(100) ? remaining : TimeSpan.FromMilliseconds(100),
                cancellationToken);
        }
    }

    private static Observation Observe(Process process)
    {
        int? id = null;
        try
        {
            id = process.Id;
            // Pin the handle before correlating metadata with exit state.
            // Metadata failures stay separate from the unchanged strict gate.
            var identity = ReadIdentity(process);
            var exited = process.HasExited;
            return new(id, exited, null, identity.StartedUtc, identity.Executable,
                identity.ParentProcessIdAtCreation, identity.Error);
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        { return new(id, null, error.ToString()); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr ExitStatus, PebAddress, AffinityMask, BasePriority, ProcessId, InheritedFromProcessId;
    }
    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags,
        StringBuilder path, ref int length);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation,
        out long exit, out long kernel, out long user);
    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(SafeProcessHandle process, int informationClass,
        out ProcessBasicInformation information, int length, out int returnedLength);
}
