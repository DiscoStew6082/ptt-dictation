#Requires -Version 7.0
[CmdletBinding()]
param([string]$TargetsPath, [Parameter(Mandatory)][string]$OutputPath,
    [string]$DumpPath, [int]$DumpProcessId, [string]$RequestPath, [string]$ReadyPath)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Flush bootstrap stages before loading the C# helper. A cold PowerShell or
# Add-Type startup can consume the bounded worker lifetime before native capture
# starts; its absence of dumps must not look like a hung MiniDumpWriteDump call.
$writer = [IO.StreamWriter]::new($OutputPath, $false, [Text.UTF8Encoding]::new($false))
$writer.AutoFlush = $true
try {
    $writeRecord = [Action[object]]{ param($record)
        $record['timestamp'] = [Diagnostics.Stopwatch]::GetTimestamp()
        $record['timestampFrequency'] = [Diagnostics.Stopwatch]::Frequency
        $writer.WriteLine(($record | ConvertTo-Json -Depth 8 -Compress))
    }
    $writeRecord.Invoke(@{ stage = 'worker_script_started'; utc = [DateTimeOffset]::UtcNow; processId = $PID })

# This disposable worker accepts only an exact PID/start-time/name manifest.
# It does not attach a debugger, send input, query UIA, or enumerate other processes.
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

public static class PttWaitChainWorker
{
    public static void Prepare()
    {
        System.Runtime.CompilerServices.RuntimeHelpers.PrepareMethod(
            typeof(PttWaitChainWorker).GetMethod("WriteDump").MethodHandle);
        LoadLibrary("dbghelp.dll");
    }

    public static void WriteDump(int pid, long startedUtcTicks, string processName,
        string dumpPath, Action<object> write)
    {
        if (!String.IsNullOrEmpty(dumpPath))
        {
            using (var process = Process.GetProcessById(pid))
            {
                if (process.StartTime.ToUniversalTime().Ticks != startedUtcTicks || process.ProcessName != processName)
                    throw new InvalidOperationException("Dump target identity changed.");
                var began = Row("dump_started", pid, 0, "utc", DateTimeOffset.UtcNow);
                began["dumpPath"] = dumpPath; began["processName"] = processName;
                began["startedUtcTicks"] = startedUtcTicks;
                write(began);
                using (var file = new FileStream(dumpPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                {
                    // MiniDumpNormal | MiniDumpWithThreadInfo: stacks/modules,
                    // no full-memory or private-read/write-memory collection.
                    bool success = MiniDumpWriteDump(process.Handle, (uint)pid, file.SafeFileHandle.DangerousGetHandle(),
                        0x1000, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                    int error = success ? 0 : Marshal.GetLastWin32Error();
                    GC.KeepAlive(file);
                    var result = Row("dump_completed", pid, 0, "utc", DateTimeOffset.UtcNow);
                    result["dumpPath"] = dumpPath; result["processName"] = processName;
                    result["startedUtcTicks"] = startedUtcTicks;
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
                                        { "index", i }, { "type", type }, { "status", Marshal.ReadInt32(node, 4) } };
                                    if (type == 8)
                                    {
                                        data["processId"] = (uint)Marshal.ReadInt32(node, 8);
                                        data["threadId"] = (uint)Marshal.ReadInt32(node, 12);
                                        data["waitTime"] = (uint)Marshal.ReadInt32(node, 16);
                                        data["contextSwitches"] = (uint)Marshal.ReadInt32(node, 20);
                                    }
                                    else
                                    {
                                        // wct.h: ObjectName is WCHAR[128] at
                                        // union offset8. Names exist only for
                                        // some objects; empty is not proof of
                                        // an unnamed critical section's owner.
                                        var name = Marshal.PtrToStringUni(IntPtr.Add(node, 8), 128);
                                        data["objectName"] = (name ?? "").Split('\0')[0];
                                    }
                                    nodes.Add(data);
                                }
                            // WCT alternates waiting threads, synchronization
                            // objects and their owning thread. Preserve owner
                            // IDs only when the following native node provides
                            // them; do not enumerate or dump that other process.
                            for (int i = 0; i + 1 < nodes.Count; i++)
                                if ((int)nodes[i]["type"] != 8 && (int)nodes[i + 1]["type"] == 8)
                                {
                                    nodes[i]["ownerProcessId"] = nodes[i + 1]["processId"];
                                    nodes[i]["ownerThreadId"] = nodes[i + 1]["threadId"];
                                    nodes[i]["ownerSource"] = "following_thread_node";
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

$writeRecord.Invoke(@{ stage = 'worker_helper_loaded'; utc = [DateTimeOffset]::UtcNow; processId = $PID })
if ($RequestPath) {
    if (-not $ReadyPath) { throw 'A prepared worker requires a readiness path.' }
    [PttWaitChainWorker]::Prepare()
    $writeRecord.Invoke(@{ stage = 'worker_prepared'; utc = [DateTimeOffset]::UtcNow; processId = $PID })
    [IO.File]::WriteAllText($ReadyPath, [DateTimeOffset]::UtcNow.ToString('O'))
    while (-not [IO.File]::Exists($RequestPath)) { [Threading.Thread]::Sleep(5) }
    $request = Get-Content -LiteralPath $RequestPath -Raw | ConvertFrom-Json
    $writeRecord.Invoke(@{ stage = 'worker_request_received'; utc = [DateTimeOffset]::UtcNow; processId = $PID })
    $targets = @($request.Targets)
    $DumpPath = [string]$request.DumpPath
    $DumpProcessId = [int]$request.DumpProcessId
    $dumpPairs = if ($request.PSObject.Properties['DumpPairs']) { [int]$request.DumpPairs } else { 1 }
    $dumpInterval = if ($request.PSObject.Properties['DumpIntervalMilliseconds']) { [int]$request.DumpIntervalMilliseconds } else { 350 }
    if ($dumpPairs -notin @(1, 2) -or $dumpInterval -lt 250 -or $dumpInterval -gt 500) {
        throw 'Prepared diagnostic requests allow only one or two dump pairs with a bounded 250-500ms interval.'
    }
} else {
    if (-not $TargetsPath) { throw 'A one-shot worker requires a target manifest.' }
    $targets = @(Get-Content -LiteralPath $TargetsPath -Raw | ConvertFrom-Json)
    $dumpPairs = 1
    $dumpInterval = 350
}

    # Save both process stacks before optional WCT, whose native implementation
    # may itself hang or crash this isolated worker.
    if ($DumpPath) {
        for ($sample = 1; $sample -le $dumpPairs; $sample++) {
            $writeRecord.Invoke(@{ stage = 'dump_pair_started'; sample = $sample; utc = [DateTimeOffset]::UtcNow })
            # Caller first: a stuck browser dump must not erase evidence of the
            # PTT automation MTA awaiting its specific UIA/RPC call.
            foreach ($target in @($targets | Sort-Object { $_.ProcessId -eq $DumpProcessId })) {
                $startedTicks = ([DateTime]$target.StartedUtc).ToUniversalTime().Ticks
                $role = if ($target.ProcessId -eq $DumpProcessId) { 'firefox' } else { 'testhost' }
                $targetDumpPath = if ($dumpPairs -gt 1) {
                    [IO.Path]::ChangeExtension($DumpPath, ".sample-$sample.$role.dmp")
                } elseif ($target.ProcessId -eq $DumpProcessId) { $DumpPath }
                  else { [IO.Path]::ChangeExtension($DumpPath, '.testhost.dmp') }
                [PttWaitChainWorker]::WriteDump([int]$target.ProcessId, $startedTicks,
                    [string]$target.ProcessName, $targetDumpPath, $writeRecord)
            }
            $writeRecord.Invoke(@{ stage = 'dump_pair_completed'; sample = $sample; utc = [DateTimeOffset]::UtcNow })
            if ($sample -lt $dumpPairs) { [Threading.Thread]::Sleep($dumpInterval) }
        }
    }
    foreach ($target in $targets) {
        $startedTicks = ([DateTime]$target.StartedUtc).ToUniversalTime().Ticks
        [PttWaitChainWorker]::Run([int]$target.ProcessId, $startedTicks,
            [uint32]$target.PriorityThreadId, [string]$target.ProcessName, $writeRecord)
    }
} finally { $writer.Dispose() }
