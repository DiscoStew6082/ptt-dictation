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
