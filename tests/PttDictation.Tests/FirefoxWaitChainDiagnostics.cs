using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace PttDictation.Tests;

// Failure evidence only. Never called before, or used to accept, a dictation.
internal static class FirefoxWaitChainDiagnostics
{
    private static readonly Lazy<string> ComRegistration = new(RegisterComCallbacks);
    internal sealed record ProcessTarget(int ProcessId, DateTime StartedUtc, uint PriorityThreadId);

    public static async Task CaptureAsync(ProcessTarget firefox, uint testUiThreadId, string path)
    {
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true"
            || Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") != "github-hosted"
            || Environment.GetEnvironmentVariable("PTT_RUN_FIREFOX_COLD_START_TESTS") != "1")
            throw new InvalidOperationException("Wait-chain diagnostics are restricted to the isolated Firefox CI test.");

        var startedUtc = DateTimeOffset.UtcNow;
        var records = new ConcurrentQueue<object>();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var token = stop.Token;
        // Synchronous WCT cannot be cancelled. A background worker owns its
        // native buffers/session until return; caller timeout never frees them.
        using var host = Process.GetCurrentProcess();
        ProcessTarget[] targets = [firefox, new(Environment.ProcessId, host.StartTime.ToUniversalTime(), testUiThreadId)];
        var worker = Task.WhenAll(targets.Select(target => Task.Factory.StartNew(
            () => Capture(target, records, token), CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default)));
        var timedOut = false;
        string? error = null;
        try { await worker.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (TimeoutException)
        {
            timedOut = true;
            _ = worker.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }
        catch (Exception failure) { error = failure.ToString(); }
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            startedUtc, completedUtc = DateTimeOffset.UtcNow, timedOut, error,
            limitations = "A single-node chain can mean an unsupported wait; it does not prove responsiveness.",
            records = records.ToArray()
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void Capture(ProcessTarget target,
        ConcurrentQueue<object> records, CancellationToken token)
    {
        records.Enqueue(new { target, comCallbacks = ComRegistration.Value });
        var session = OpenThreadWaitChainSession(0, IntPtr.Zero);
        if (session == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            // Enumerate only this testhost and the bound Firefox window owner.
            // WCT may report their causal dependencies in other processes.
            var processId = target.ProcessId;
            if (token.IsCancellationRequested || processId <= 0) return;
            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.StartTime.ToUniversalTime() != target.StartedUtc
                    || processId != Environment.ProcessId && process.ProcessName != "firefox")
                {
                    records.Enqueue(new { processId, error = "Owned process identity no longer matches." });
                    return;
                }
                // Each process has its own WCT session/worker so one blocked
                // query cannot prevent observing the other process's UI thread.
                var threads = process.Threads.Cast<ProcessThread>()
                    .OrderByDescending(thread => (uint)thread.Id == target.PriorityThreadId).ToArray();
                try
                {
                    foreach (var thread in threads)
                    {
                        if (token.IsCancellationRequested) return;
                        CaptureThread(session, processId, (uint)thread.Id, records);
                    }
                }
                finally { foreach (var thread in threads) thread.Dispose(); }
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException
                or System.ComponentModel.Win32Exception)
            {
                records.Enqueue(new { processId, error = error.ToString() });
            }
        }
        finally { CloseThreadWaitChainSession(session); }
    }

    private static void CaptureThread(IntPtr session, int processId, uint threadId,
        ConcurrentQueue<object> records)
    {
        var nodes = new NativeNode[16];
        uint count = (uint)nodes.Length;
        var startedUtc = DateTimeOffset.UtcNow;
        records.Enqueue(new { processId, threadId, startedUtc, stage = "query_started" });
        var success = GetThreadWaitChain(session, UIntPtr.Zero, 7, threadId, ref count, nodes, out var cycle);
        var error = success ? 0 : Marshal.GetLastWin32Error();
        // ERROR_MORE_DATA / ERROR_TOO_MANY_THREADS still return a valid prefix.
        var available = success || error is 234 or 565;
        records.Enqueue(new
        {
            processId, threadId, startedUtc, completedUtc = DateTimeOffset.UtcNow,
            stage = "query_completed", success, error, cycle, nodeCount = count,
            nodes = available ? nodes.Take((int)Math.Min(count, 16)).Select(node => new
            {
                type = node.Type.ToString(), status = node.Status.ToString(),
                processId = node.Type == ObjectType.Thread ? (uint?)node.ProcessId : null,
                threadId = node.Type == ObjectType.Thread ? (uint?)node.ThreadId : null,
                waitTime = node.Type == ObjectType.Thread ? (uint?)node.WaitTime : null,
                contextSwitches = node.Type == ObjectType.Thread ? (uint?)node.ContextSwitches : null
            }).ToArray() : []
        });
    }

    private static string RegisterComCallbacks()
    {
        // WCT stores these native callbacks process-wide. Retain the module for
        // the testhost lifetime rather than leaving pointers into an unloaded DLL.
        try
        {
            var module = NativeLibrary.Load("ole32.dll");
            RegisterWaitChainCOMCallback(NativeLibrary.GetExport(module, "CoGetCallState"),
                NativeLibrary.GetExport(module, "CoGetActivationState"));
            return "registered";
        }
        catch (Exception error) { return error.ToString(); }
    }

    // Microsoft SDK wct.h: 8 bytes of enums, followed by a 272-byte union.
    // LockObject holds WCHAR[128], LARGE_INTEGER, BOOL, then alignment padding.
    // https://learn.microsoft.com/windows/win32/api/wct/ns-wct-waitchain_node_info
    [StructLayout(LayoutKind.Explicit, Size = 280)]
    internal struct NativeNode
    {
        [FieldOffset(0)] public ObjectType Type;
        [FieldOffset(4)] public ObjectStatus Status;
        [FieldOffset(8)] public uint ProcessId;
        [FieldOffset(12)] public uint ThreadId;
        [FieldOffset(16)] public uint WaitTime;
        [FieldOffset(20)] public uint ContextSwitches;
    }

    internal enum ObjectType : uint
    {
        CriticalSection = 1, SendMessage, Mutex, Alpc, Com, ThreadWait, ProcessWait,
        Thread, ComActivation, Unknown, SocketIo, SmbIo, Max
    }

    internal enum ObjectStatus : uint
    {
        NoAccess = 1, Running, Blocked, PidOnly, PidOnlyRpcss, Owned, NotOwned,
        Abandoned, Unknown, Error, Max
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern IntPtr OpenThreadWaitChainSession(uint flags, IntPtr callback);
    [DllImport("advapi32.dll")]
    private static extern void CloseThreadWaitChainSession(IntPtr session);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetThreadWaitChain(IntPtr session, UIntPtr context, uint flags,
        uint threadId, ref uint count, [In, Out] NativeNode[] nodes,
        [MarshalAs(UnmanagedType.Bool)] out bool cycle);
    [DllImport("advapi32.dll")]
    private static extern void RegisterWaitChainCOMCallback(IntPtr getCallState, IntPtr getActivationState);
}
