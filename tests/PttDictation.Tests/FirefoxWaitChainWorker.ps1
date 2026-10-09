#Requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$TargetsPath, [Parameter(Mandatory)][string]$OutputPath,
    [string]$DumpPath, [int]$DumpProcessId)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# This disposable worker accepts only an exact PID/start-time/name manifest.
# It does not attach a debugger, send input, query UIA, or enumerate other processes.
$targets = @(Get-Content -LiteralPath $TargetsPath -Raw | ConvertFrom-Json)
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

public static class PttWaitChainWorker
{
    public static void WriteDump(int pid, long startedUtcTicks, string processName,
        string dumpPath, Action<object> write)
    {
        if (!String.IsNullOrEmpty(dumpPath))
        {
            using (var process = Process.GetProcessById(pid))
            {
                if (process.StartTime.ToUniversalTime().Ticks != startedUtcTicks || process.ProcessName != processName)
                    throw new InvalidOperationException("Dump target identity changed.");
                write(Row("dump_started", pid, 0, "utc", DateTimeOffset.UtcNow));
                using (var file = new FileStream(dumpPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                {
                    // MiniDumpNormal | MiniDumpWithThreadInfo: stacks/modules,
                    // no full-memory or private-read/write-memory collection.
                    bool success = MiniDumpWriteDump(process.Handle, (uint)pid, file.SafeFileHandle.DangerousGetHandle(),
                        0x1000, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                    int error = success ? 0 : Marshal.GetLastWin32Error();
                    GC.KeepAlive(file);
                    var result = Row("dump_completed", pid, 0, "utc", DateTimeOffset.UtcNow);
                    result["success"] = success; result["error"] = error;
                    write(result);
                }
            }
        }
    }

    public static void Run(int pid, long startedUtcTicks, uint priorityThreadId,
        string processName, Action<object> write)
    { foreach (var row in Capture(pid, startedUtcTicks, priorityThreadId, processName)) write(row); }

    public static IEnumerable<Dictionary<string, object>> Capture(int pid, long startedUtcTicks,
        uint priorityThreadId, string processName)
    {
        using (var process = Process.GetProcessById(pid))
        {
            if (process.StartTime.ToUniversalTime().Ticks != startedUtcTicks || process.ProcessName != processName)
                throw new InvalidOperationException("Diagnostic target identity changed.");
            var initialized = CoInitializeEx(IntPtr.Zero, 0) >= 0;
            IntPtr session = IntPtr.Zero;
            try
            {
                // WCT COM callbacks and session live on this one MTA worker.
                var ole = LoadLibrary("ole32.dll");
                var call = GetProcAddress(ole, "CoGetCallState");
                var activation = GetProcAddress(ole, "CoGetActivationState");
                if (call != IntPtr.Zero && activation != IntPtr.Zero)
                    RegisterWaitChainCOMCallback(call, activation);
                yield return Row("session_opening", pid, 0, "comCallbacks", call != IntPtr.Zero && activation != IntPtr.Zero);
                session = OpenThreadWaitChainSession(0, IntPtr.Zero);
                if (session == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                var threads = process.Threads.Cast<ProcessThread>()
                    .OrderByDescending(thread => (uint)thread.Id == priorityThreadId).ToArray();
                try
                {
                    foreach (var thread in threads)
                    {
                        uint tid = (uint)thread.Id;
                        yield return Row("query_started", pid, tid, "utc", DateTimeOffset.UtcNow);
                        // SDK wct.h: two DWORD enums plus a 272-byte union.
                        // Use unmanaged storage so native layout cannot corrupt a managed array.
                        const int stride = 280, capacity = 16;
                        var buffer = Marshal.AllocHGlobal(stride * capacity);
                        try
                        {
                            uint count = capacity;
                            int cycle;
                            bool success = GetThreadWaitChain(session, UIntPtr.Zero, 7, tid, ref count, buffer, out cycle);
                            int error = success ? 0 : Marshal.GetLastWin32Error();
                            var nodes = new List<Dictionary<string, object>>();
                            if (success || error == 234 || error == 565)
                                for (int i = 0; i < Math.Min(count, capacity); i++)
                                {
                                    var node = IntPtr.Add(buffer, i * stride);
                                    int type = Marshal.ReadInt32(node, 0);
                                    var data = new Dictionary<string, object> {
                                        { "type", type }, { "status", Marshal.ReadInt32(node, 4) } };
                                    if (type == 8)
                                    {
                                        data["processId"] = (uint)Marshal.ReadInt32(node, 8);
                                        data["threadId"] = (uint)Marshal.ReadInt32(node, 12);
                                        data["waitTime"] = (uint)Marshal.ReadInt32(node, 16);
                                        data["contextSwitches"] = (uint)Marshal.ReadInt32(node, 20);
                                    }
                                    nodes.Add(data);
                                }
                            var result = Row("query_completed", pid, tid, "utc", DateTimeOffset.UtcNow);
                            result["success"] = success; result["error"] = error;
                            result["cycle"] = cycle != 0; result["nodes"] = nodes; result["nodeCount"] = count;
                            yield return result;
                        }
                        finally { Marshal.FreeHGlobal(buffer); }
                    }
                }
                finally { foreach (var thread in threads) thread.Dispose(); }
                yield return Row("session_closing", pid, 0, "utc", DateTimeOffset.UtcNow);
            }
            finally
            {
                if (session != IntPtr.Zero) CloseThreadWaitChainSession(session);
                if (initialized) CoUninitialize();
            }
            yield return Row("session_closed", pid, 0, "utc", DateTimeOffset.UtcNow);
        }
    }

    private static Dictionary<string, object> Row(string stage, int pid, uint tid, string key, object value)
    { return new Dictionary<string, object> { { "stage", stage }, { "processId", pid }, { "threadId", tid }, { key, value } }; }
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint mode);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] private static extern IntPtr LoadLibrary(string name);
    [DllImport("kernel32.dll", CharSet=CharSet.Ansi, ExactSpelling=true)] private static extern IntPtr GetProcAddress(IntPtr module, string name);
    [DllImport("advapi32.dll", SetLastError=true)] private static extern IntPtr OpenThreadWaitChainSession(uint flags, IntPtr callback);
    [DllImport("advapi32.dll")] private static extern void CloseThreadWaitChainSession(IntPtr session);
    [DllImport("advapi32.dll", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetThreadWaitChain(IntPtr session, UIntPtr context, uint flags, uint tid,
        ref uint count, IntPtr nodes, out int cycle);
    [DllImport("advapi32.dll")] private static extern void RegisterWaitChainCOMCallback(IntPtr call, IntPtr activation);
    [DllImport("dbghelp.dll", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool MiniDumpWriteDump(IntPtr process, uint pid, IntPtr file, uint type,
        IntPtr exception, IntPtr userStream, IntPtr callback);
}
'@

$writer = [IO.StreamWriter]::new($OutputPath, $false, [Text.UTF8Encoding]::new($false))
$writer.AutoFlush = $true
try {
    $writeRecord = [Action[object]]{ param($record) $writer.WriteLine(($record | ConvertTo-Json -Depth 8 -Compress)) }
    # Save both process stacks before optional WCT, whose native implementation
    # may itself hang or crash this isolated worker.
    if ($DumpPath) {
        foreach ($target in $targets) {
            $startedTicks = ([DateTime]$target.StartedUtc).ToUniversalTime().Ticks
            $targetDumpPath = if ($target.ProcessId -eq $DumpProcessId) { $DumpPath }
                else { [IO.Path]::ChangeExtension($DumpPath, '.testhost.dmp') }
            [PttWaitChainWorker]::WriteDump([int]$target.ProcessId, $startedTicks,
                [string]$target.ProcessName, $targetDumpPath, $writeRecord)
        }
    }
    foreach ($target in $targets) {
        $startedTicks = ([DateTime]$target.StartedUtc).ToUniversalTime().Ticks
        [PttWaitChainWorker]::Run([int]$target.ProcessId, $startedTicks,
            [uint32]$target.PriorityThreadId, [string]$target.ProcessName, $writeRecord)
    }
} finally { $writer.Dispose() }
