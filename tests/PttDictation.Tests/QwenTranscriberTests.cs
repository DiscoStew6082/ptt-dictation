using System.Diagnostics;
using System.Text;
using PttDictation.App;

namespace PttDictation.Tests;

[TestClass]
public sealed class QwenTranscriberTests
{
    [TestMethod]
    public void LaunchUsesLocalArgumentsOfflineEnvironmentAndNoShell()
    {
        using var fixture = new WorkerFixture("normal");
        using var adapter = fixture.CreateAdapter();
        var launch = adapter.CreateStartInfo();
        Assert.AreEqual(fixture.Options.PythonPath, launch.FileName);
        CollectionAssert.AreEqual(new[] { "-u", fixture.Options.WorkerPath, "--model", fixture.Options.ModelPath },
            launch.ArgumentList.ToArray());
        Assert.IsFalse(launch.UseShellExecute);
        Assert.IsTrue(launch.CreateNoWindow);
        Assert.IsTrue(launch.RedirectStandardInput && launch.RedirectStandardOutput && launch.RedirectStandardError);
        Assert.AreEqual("1", launch.Environment["HF_HUB_OFFLINE"]);
        Assert.AreEqual("1", launch.Environment["TRANSFORMERS_OFFLINE"]);
        Assert.ThrowsExactly<ArgumentException>(() => new PersistentQwenTranscriber(fixture.Options with { PythonPath = "python.exe" }));
        Assert.ThrowsExactly<ArgumentException>(() => new PersistentQwenTranscriber(fixture.Options with { ModelPath = @"\\server\share\model" }));
        Assert.ThrowsExactly<FileNotFoundException>(() => new PersistentQwenTranscriber(fixture.Options with { WorkerPath = Path.Combine(fixture.Root, "missing.py") }));
    }

    [TestMethod]
    public async Task WarmupAndSerializedRequestsReuseOneActualWorkerAndPreserveUnicodePaths()
    {
        using var fixture = new WorkerFixture("normal");
        using var adapter = fixture.CreateAdapter();
        await adapter.WarmUpAsync(CancellationToken.None);
        await adapter.WarmUpAsync(CancellationToken.None);
        var first = fixture.Audio("first recording's café.wav");
        var second = fixture.Audio("second.wav");
        var results = await Task.WhenAll(adapter.TranscribeAsync(first, CancellationToken.None),
            adapter.TranscribeAsync(second, CancellationToken.None));
        Assert.AreEqual("heard:" + first, results[0].Text);
        Assert.AreEqual("heard:" + second, results[1].Text);
        Assert.IsTrue(results.All(result => result.InferenceTime is { } elapsed && elapsed >= TimeSpan.Zero));
        Assert.AreEqual(1, fixture.FactoryCalls);
        Assert.AreEqual(2, fixture.RequestIds.Length);
        Assert.AreEqual(2, fixture.RequestIds.Distinct().Count());
        adapter.Dispose();
        await fixture.AssertOwnedProcessesExitedAsync();
    }

    [TestMethod]
    public async Task EmptySpeechResultIsValid()
    {
        using var fixture = new WorkerFixture("empty");
        using var adapter = fixture.CreateAdapter();
        var result = await adapter.TranscribeAsync(fixture.Audio(), CancellationToken.None);
        Assert.AreEqual("", result.Text);
    }

    [TestMethod]
    public async Task CancellingActiveRequestKillsWorkerAndNextRequestStartsCleanly()
    {
        using var fixture = new WorkerFixture("hang", "normal");
        using var adapter = fixture.CreateAdapter();
        await adapter.WarmUpAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var request = adapter.TranscribeAsync(fixture.Audio(), cancellation.Token);
        await fixture.WaitForRequestsAsync(1);
        var stopwatch = Stopwatch.StartNew();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await request.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(5));
        await fixture.AssertOwnedProcessesExitedAsync();
        var next = fixture.Audio("next.wav");
        Assert.AreEqual("heard:" + next, (await adapter.TranscribeAsync(next, CancellationToken.None)).Text);
        Assert.AreEqual(2, fixture.FactoryCalls);
    }

    [TestMethod]
    public async Task CancellingQueuedRequestDoesNotKillAnotherRequestsWorker()
    {
        using var fixture = new WorkerFixture("hang");
        using var adapter = fixture.CreateAdapter();
        await adapter.WarmUpAsync(CancellationToken.None);
        using var activeCancellation = new CancellationTokenSource();
        var active = adapter.TranscribeAsync(fixture.Audio(), activeCancellation.Token);
        await fixture.WaitForRequestsAsync(1);
        using var queuedCancellation = new CancellationTokenSource();
        var queued = adapter.TranscribeAsync(fixture.Audio("queued.wav"), queuedCancellation.Token);
        queuedCancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await queued);
        Assert.AreEqual(1, fixture.RequestIds.Length);
        Assert.IsTrue(WorkerFixture.IsRunning(fixture.ProcessIds.Single()));
        activeCancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await active);
    }

    [TestMethod]
    [DataRow("malformed", typeof(InvalidDataException))]
    [DataRow("wrong-id", typeof(InvalidDataException))]
    [DataRow("error", typeof(InvalidOperationException))]
    [DataRow("exit", typeof(EndOfStreamException))]
    [DataRow("bad-ready", typeof(InvalidDataException))]
    public async Task FaultedWorkerIsNotReusedOrSilentlyReplayed(string mode, Type errorType)
    {
        using var fixture = new WorkerFixture(mode, "normal");
        using var adapter = fixture.CreateAdapter();
        var error = await CaptureFailureAsync(adapter.TranscribeAsync(fixture.Audio(), CancellationToken.None));
        Assert.AreEqual(errorType, error.GetType());
        Assert.AreEqual(1, fixture.FactoryCalls, "The failed request must not silently replay.");
        await fixture.AssertOwnedProcessesExitedAsync();
        var next = fixture.Audio("retry.wav");
        Assert.AreEqual("heard:" + next, (await adapter.TranscribeAsync(next, CancellationToken.None)).Text);
        Assert.AreEqual(2, fixture.FactoryCalls);
    }

    [TestMethod]
    public async Task RequestTimeoutIsExplicitAndAllowsANewWorker()
    {
        using var fixture = new WorkerFixture("hang", "normal");
        using var adapter = fixture.CreateAdapter(requestTimeout: TimeSpan.FromMilliseconds(250));
        await adapter.WarmUpAsync(CancellationToken.None);
        await Assert.ThrowsExactlyAsync<TimeoutException>(() => adapter.TranscribeAsync(fixture.Audio(), CancellationToken.None));
        await fixture.AssertOwnedProcessesExitedAsync();
        Assert.IsTrue((await adapter.TranscribeAsync(fixture.Audio("retry.wav"), CancellationToken.None)).Text.StartsWith("heard:"));
    }

    [TestMethod]
    public async Task StartupTimeoutKillsWorkerAndCanBeRetried()
    {
        using var fixture = new WorkerFixture("hang-start", "normal");
        using var adapter = fixture.CreateAdapter(startupTimeout: TimeSpan.FromSeconds(3));
        await Assert.ThrowsExactlyAsync<TimeoutException>(() => adapter.WarmUpAsync(CancellationToken.None));
        await fixture.AssertOwnedProcessesExitedAsync();
        await adapter.WarmUpAsync(CancellationToken.None);
        Assert.AreEqual(2, fixture.FactoryCalls);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CancellingOrDisposingStartupKillsItsWorker(bool dispose)
    {
        using var fixture = new WorkerFixture("hang-start", "normal");
        using var adapter = fixture.CreateAdapter();
        using var cancellation = new CancellationTokenSource();
        var startup = adapter.WarmUpAsync(cancellation.Token);
        await fixture.WaitForProcessesAsync(1);
        if (dispose) adapter.Dispose();
        else cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await startup.WaitAsync(TimeSpan.FromSeconds(5)));
        await fixture.AssertOwnedProcessesExitedAsync();
        if (dispose)
            await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => adapter.WarmUpAsync(CancellationToken.None));
        else
        {
            await adapter.WarmUpAsync(CancellationToken.None);
            Assert.AreEqual(2, fixture.FactoryCalls);
        }
    }

    [TestMethod]
    public async Task LargeStandardErrorOutputDoesNotBlockTheProtocol()
    {
        using var fixture = new WorkerFixture("stderr");
        using var adapter = fixture.CreateAdapter();
        var audio = fixture.Audio();
        Assert.AreEqual("heard:" + audio, (await adapter.TranscribeAsync(audio, CancellationToken.None)).Text);
    }

    [TestMethod]
    public async Task DisposalStopsActiveWorkAndRejectsFurtherRequests()
    {
        using var fixture = new WorkerFixture("hang");
        var adapter = fixture.CreateAdapter();
        try
        {
            await adapter.WarmUpAsync(CancellationToken.None);
            var active = adapter.TranscribeAsync(fixture.Audio(), CancellationToken.None);
            await fixture.WaitForRequestsAsync(1);
            adapter.Dispose();
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await active.WaitAsync(TimeSpan.FromSeconds(5)));
            await fixture.AssertOwnedProcessesExitedAsync();
            await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => adapter.WarmUpAsync(CancellationToken.None));
        }
        finally { adapter.Dispose(); }
    }

    [TestMethod]
    public async Task ClosingOwnedJobKillsItsWorkerTreeAndLeavesUnrelatedProcessRunning()
    {
        using var fixture = new WorkerFixture("child");
        using var unrelated = new Process { StartInfo = WorkerFixture.SleepingProcess() };
        Assert.IsTrue(unrelated.Start());
        try
        {
            using var adapter = fixture.CreateAdapter();
            await adapter.TranscribeAsync(fixture.Audio(), CancellationToken.None);
            Assert.AreEqual(2, fixture.ProcessIds.Length, "Fixture must have created an actual descendant.");
            Assert.IsTrue(fixture.ProcessIds.All(WorkerFixture.IsRunning));
            adapter.Dispose();
            await fixture.AssertOwnedProcessesExitedAsync();
            Assert.IsFalse(unrelated.HasExited, "Closing the owned job must not stop unrelated processes.");
        }
        finally
        {
            if (!unrelated.HasExited) unrelated.Kill(entireProcessTree: true);
            unrelated.WaitForExit(5000);
        }
    }

    private static async Task<Exception> CaptureFailureAsync(Task task)
    {
        try { await task; }
        catch (Exception error) { return error; }
        Assert.Fail("Expected the worker operation to fail.");
        throw new InvalidOperationException();
    }

    private sealed class WorkerFixture : IDisposable
    {
        private readonly Queue<string> _modes;
        private readonly List<Process> _created = [];
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "ptt-qwen-protocol-" + Guid.NewGuid().ToString("N"));
        public QwenTranscriberOptions Options { get; }
        public int FactoryCalls { get; private set; }
        private string RequestLog => Path.Combine(Root, "requests.txt");
        private string ProcessLog => Path.Combine(Root, "processes.txt");
        public string[] RequestIds => ReadCompleteLines(RequestLog);
        public int[] ProcessIds => ReadCompleteLines(ProcessLog).Select(int.Parse).ToArray();

        public WorkerFixture(params string[] modes)
        {
            _modes = new Queue<string>(modes);
            Directory.CreateDirectory(Root);
            var model = Path.Combine(Root, "model folder");
            Directory.CreateDirectory(model);
            var script = Path.Combine(Root, "protocol fixture.ps1");
            File.WriteAllText(script, """
                param([string]$Mode, [string]$RequestLog, [string]$ProcessLog)
                $ErrorActionPreference = 'Stop'
                [Console]::InputEncoding = [System.Text.UTF8Encoding]::new($false)
                [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
                [System.IO.File]::AppendAllText($ProcessLog, $PID.ToString() + [Environment]::NewLine)
                if ($Mode -eq 'hang-start') { while ($true) { Start-Sleep -Seconds 1 } }
                if ($Mode -eq 'bad-ready') { [Console]::WriteLine('{"type":"ready","protocol":2}') }
                else { [Console]::WriteLine('{"type":"ready","protocol":1}') }
                while ($null -ne ($line = [Console]::ReadLine())) {
                    $request = $line | ConvertFrom-Json
                    [System.IO.File]::AppendAllText($RequestLog, $request.id + [Environment]::NewLine)
                    if ($Mode -eq 'hang') { while ($true) { Start-Sleep -Seconds 1 } }
                    if ($Mode -eq 'exit') { exit 17 }
                    if ($Mode -eq 'malformed') { [Console]::WriteLine('not JSON'); continue }
                    if ($Mode -eq 'stderr') { [Console]::Error.Write(('x' * 100000)); [Console]::Error.Flush() }
                    if ($Mode -eq 'child') {
                        $child = Start-Process -FilePath (Join-Path $PSHOME 'powershell.exe') -ArgumentList '-NoLogo -NoProfile -NonInteractive -Command "Start-Sleep -Seconds 60"' -PassThru -WindowStyle Hidden
                        [System.IO.File]::AppendAllText($ProcessLog, $child.Id.ToString() + [Environment]::NewLine)
                    }
                    $id = $request.id
                    if ($Mode -eq 'wrong-id') { $id = 'not-this-request' }
                    if ($Mode -eq 'error') { $response = @{id=$id; type='error'; message='fixture recognition failed'} }
                    else {
                        $text = 'heard:' + $request.audioPath
                        if ($Mode -eq 'empty') { $text = '' }
                        $response = @{id=$id; type='result'; text=$text}
                    }
                    [Console]::WriteLine(($response | ConvertTo-Json -Compress))
                }
                """, new UTF8Encoding(true));
            Options = new(PowerShellPath, model, script);
        }

        public PersistentQwenTranscriber CreateAdapter(TimeSpan? startupTimeout = null, TimeSpan? requestTimeout = null)
            => new(Options, CreateProcess, startupTimeout ?? TimeSpan.FromSeconds(15), requestTimeout ?? TimeSpan.FromSeconds(10));

        private Process CreateProcess(ProcessStartInfo requested)
        {
            FactoryCalls++;
            var start = new ProcessStartInfo
            {
                FileName = PowerShellPath,
                UseShellExecute = requested.UseShellExecute,
                CreateNoWindow = requested.CreateNoWindow,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = requested.StandardInputEncoding,
                StandardOutputEncoding = requested.StandardOutputEncoding,
                StandardErrorEncoding = requested.StandardErrorEncoding
            };
            foreach (var value in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                "-File", Options.WorkerPath, "-Mode", _modes.Count > 0 ? _modes.Dequeue() : "normal",
                "-RequestLog", RequestLog, "-ProcessLog", ProcessLog })
                start.ArgumentList.Add(value);
            var process = new Process { StartInfo = start };
            _created.Add(process);
            return process;
        }

        public string Audio(string name = "audio.wav")
        {
            var path = Path.Combine(Root, name);
            File.WriteAllBytes(path, new byte[44]);
            return path;
        }

        private static string[] ReadCompleteLines(string path)
        {
            if (!File.Exists(path)) return [];
            using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            var text = reader.ReadToEnd();
            var end = text.LastIndexOf('\n');
            return end < 0 ? [] : text[..end].Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r')).ToArray();
        }

        public async Task WaitForProcessesAsync(int count)
        {
            var timeout = Stopwatch.StartNew();
            while (ProcessIds.Length < count && timeout.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(10);
            Assert.AreEqual(count, ProcessIds.Length, "Fixture process did not start.");
        }

        public async Task WaitForRequestsAsync(int count)
        {
            var timeout = Stopwatch.StartNew();
            while (RequestIds.Length < count && timeout.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(10);
            Assert.AreEqual(count, RequestIds.Length, "Fixture did not receive the expected actual stdin requests.");
        }

        public async Task AssertOwnedProcessesExitedAsync()
        {
            var timeout = Stopwatch.StartNew();
            while (ProcessIds.Any(IsRunning) && timeout.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(10);
            Assert.IsFalse(ProcessIds.Any(IsRunning), "An owned Qwen fixture process remained alive.");
        }

        public static bool IsRunning(int pid)
        {
            try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
            catch (ArgumentException) { return false; }
        }

        private static string PowerShellPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");

        public static ProcessStartInfo SleepingProcess()
        {
            var start = new ProcessStartInfo { FileName = PowerShellPath, UseShellExecute = false, CreateNoWindow = true };
            foreach (var value in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 60" })
                start.ArgumentList.Add(value);
            return start;
        }

        public void Dispose()
        {
            foreach (var process in _created)
            {
                try
                {
                    if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(5000); }
                }
                catch (InvalidOperationException) { }
                finally { process.Dispose(); }
            }
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}