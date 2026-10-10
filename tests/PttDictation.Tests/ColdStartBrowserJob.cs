using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PttDictation.Tests;

// CI ownership boundary, not browser automation. Suspending before job assignment
// ensures every browser descendant belongs to this trial before it can execute.
internal sealed class ColdStartBrowserJob : IDisposable
{
    private readonly SafeFileHandle _job;
    private readonly Action<CleanupObservation>? _report;
    public int LauncherId { get; }

    internal sealed record MemberObservation(int ProcessId, DateTime? StartedUtc, string? Executable,
        int? ParentProcessIdAtCreation, bool? IsJobMember, bool? ExitSignaled, string? Error);
    internal sealed record CleanupObservation(DateTime ObservedUtc, long ElapsedMilliseconds, string Stage,
        uint? ActiveProcesses, MemberObservation[] Members, string? Error);
    private sealed record RetainedMember(SafeProcessHandle Handle, MemberObservation Identity);

    public ColdStartBrowserJob(string executable, IReadOnlyList<string> arguments,
        Action<CleanupObservation>? report = null)
    {
        _report = report;
        _job = CreateJobObject(IntPtr.Zero, null);
        if (_job.IsInvalid) throw new Win32Exception();
        var limits = new ExtendedLimits { Basic = new BasicLimits { LimitFlags = 0x2000 } };
        ProcessInformation process = default;
        try
        {
            if (!SetInformationJobObject(_job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>())) throw new Win32Exception();
            var startup = new StartupInfo { Size = (uint)Marshal.SizeOf<StartupInfo>() };
            var command = new StringBuilder(string.Join(" ", new[] { executable }.Concat(arguments).Select(QuoteArgument)));
            if (!CreateProcess(executable, command, IntPtr.Zero, IntPtr.Zero, false, 4,
                IntPtr.Zero, null, ref startup, out process)) throw new Win32Exception();
            if (!AssignProcessToJobObject(_job, process.Process)) throw new Win32Exception();
            LauncherId = (int)process.ProcessId;
            if (ResumeThread(process.Thread) == uint.MaxValue) throw new Win32Exception();
        }
        catch
        {
            if (process.Process != IntPtr.Zero) TerminateProcess(process.Process, 1);
            _job.Dispose();
            throw;
        }
        finally
        {
            if (process.Thread != IntPtr.Zero) CloseHandle(process.Thread);
            if (process.Process != IntPtr.Zero) CloseHandle(process.Process);
        }
    }

    public bool Contains(Process process) => IsProcessInJob(process.SafeHandle, _job, out var member) && member;

    // Query this job only. PID values are observations until a retained process
    // handle proves membership; never act on browser-name enumeration here.
    internal int[] GetOwnedProcessIds()
    {
        var capacity = 32;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var bytes = checked(8 + capacity * IntPtr.Size);
            var buffer = Marshal.AllocHGlobal(bytes);
            try
            {
                if (QueryInformationJobObject(_job, 3, buffer, (uint)bytes, IntPtr.Zero))
                {
                    var count = Marshal.ReadInt32(buffer, 4);
                    if (count < 0 || count > capacity) throw new InvalidOperationException("Invalid job member count.");
                    return Enumerable.Range(0, count)
                        .Select(index => checked((int)Marshal.ReadIntPtr(buffer, 8 + index * IntPtr.Size).ToInt64()))
                        .ToArray();
                }
                var error = Marshal.GetLastWin32Error();
                if (error != 234) throw new Win32Exception(error); // ERROR_MORE_DATA: children raced the query.
                capacity = checked(capacity * 2);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        throw new InvalidOperationException("Owned job membership did not stabilize within the bounded query.");
    }

    public void Dispose()
    {
        if (_job.IsClosed) return;
        var deadline = Stopwatch.StartNew();
        var retained = new List<RetainedMember>();
        var unavailable = new List<MemberObservation>();
        uint? active = null;
        MemberObservation[]? exitProof = null;
        MemberObservation[] ObserveMembers() => exitProof ?? unavailable.Concat(retained.Select(member =>
        {
            try { return member.Identity with { ExitSignaled = IsExitSignaled(member.Handle) }; }
            catch (Win32Exception error)
            { return member.Identity with { ExitSignaled = null, Error = member.Identity.Error + Environment.NewLine + error }; }
        })).ToArray();
        void Report(string stage, string? error = null) => _report?.Invoke(new(DateTime.UtcNow,
            deadline.ElapsedMilliseconds, stage, active, ObserveMembers(), error));
        try
        {
            foreach (var pid in GetOwnedProcessIds())
            {
                SafeProcessHandle? handle = null;
                try
                {
                    // Minimum query/synchronization rights avoid demanding full
                    // access to browser sandbox children. This pins identity.
                    handle = OpenProcess(0x101000, false, (uint)pid);
                    if (handle.IsInvalid) throw new Win32Exception();
                    if (!IsProcessInJob(handle, _job, out var member)) throw new Win32Exception();
                    var identity = ColdStartProcessGate.ReadIdentity(handle);
                    var observation = new MemberObservation(pid, identity.StartedUtc, identity.Executable,
                        identity.ParentProcessIdAtCreation, member, null, identity.Error);
                    if (!member)
                    {
                        unavailable.Add(observation with { Error = "Listed PID no longer belongs to this job; no termination by PID performed." });
                        continue;
                    }
                    retained.Add(new(handle, observation));
                    handle = null; // Keep the verified handle until exit proof is complete.
                }
                catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception)
                {
                    // A listed member may have exited before its handle could be
                    // opened. Preserve that race; final accounting must still be zero.
                    unavailable.Add(new(pid, null, null, null, null, null, error.ToString()));
                }
                finally { handle?.Dispose(); }
            }
            Report("before_termination");
            if (!TerminateJobObject(_job, 0)) throw new Win32Exception();
            Report("termination_requested");
            while (true)
            {
                if (!QueryInformationJobObject(_job, 1, out var accounting,
                    (uint)Marshal.SizeOf<BasicAccounting>(), IntPtr.Zero)) throw new Win32Exception();
                active = accounting.ActiveProcesses;
                var exited = retained.All(member => IsExitSignaled(member.Handle));
                // One shared ceiling includes capture, termination, exit signals,
                // handle release and final accounting. No phase renews it.
                if (deadline.ElapsedMilliseconds >= 5000)
                    throw new TimeoutException("Owned browser job did not prove all retained process exits within five seconds.");
                if (exited) break;
                Thread.Sleep(25);
            }
            exitProof = ObserveMembers();
            Report("exit_signals_proven");
            // Windows accounting can retain a terminated process until all
            // references close. Preserve the signal proof before releasing ours.
            foreach (var member in retained) member.Handle.Dispose();
            Report("retained_handles_released");
            while (true)
            {
                if (!QueryInformationJobObject(_job, 1, out var accounting,
                    (uint)Marshal.SizeOf<BasicAccounting>(), IntPtr.Zero)) throw new Win32Exception();
                active = accounting.ActiveProcesses;
                if (deadline.ElapsedMilliseconds >= 5000)
                    throw new TimeoutException("Owned browser job accounting did not reach zero within the shared five-second cleanup ceiling.");
                if (active == 0) break;
                Thread.Sleep(25);
            }
            Report("cleanup_completed");
        }
        catch (Exception error)
        {
            Report("cleanup_failed", error.ToString());
            throw;
        }
        finally
        {
            // KILL_ON_JOB_CLOSE also applies if evidence collection failed.
            _job.Dispose();
            foreach (var member in retained) member.Handle.Dispose();
        }
    }

    private static bool IsExitSignaled(SafeProcessHandle process)
    {
        var result = WaitForSingleObject(process, 0);
        return result switch
        {
            0 => true,
            258 => false, // WAIT_TIMEOUT
            _ => throw new Win32Exception()
        };
    }

    internal static string QuoteArgument(string value)
    {
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { slashes++; continue; }
            result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
            result.Append(character);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public uint Size;
        public IntPtr Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XChars, YChars, FillAttribute, Flags;
        public ushort ShowWindow, ReservedLength;
        public IntPtr ReservedBytes, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicAccounting
    {
        public long UserTime, KernelTime, PeriodUserTime, PeriodKernelTime;
        public uint PageFaults, TotalProcesses, ActiveProcesses, TerminatedProcesses;
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int type, ref ExtendedLimits limits, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsProcessInJob(SafeProcessHandle process, SafeFileHandle job, out bool member);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryInformationJobObject(SafeFileHandle job, int type, out BasicAccounting accounting, uint size, IntPtr returnedSize);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryInformationJobObject(SafeFileHandle job, int type, IntPtr buffer, uint size, IntPtr returnedSize);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inheritHandle, uint processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcess(string application, StringBuilder commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string? directory,
        ref StartupInfo startup, out ProcessInformation process);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll")]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
