using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using PttDictation.App;
using PttDictation.Core;

namespace PttDictation.Tests;

[TestClass]
public sealed class PersistentParakeetSecurityTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [TestMethod]
    public async Task ProcessBoundConnectorSendsPayloadOnlyToTheExpectedProcess()
    {
        var testDirectory = CreateTestDirectory();
        Process? acceptedProcess = null;
        Process? rejectedProcess = null;
        try
        {
            var accepted = StartPayloadProbe(testDirectory, ReserveAvailablePort(), "accepted");
            acceptedProcess = accepted.Process;
            await WaitForFileAsync(accepted.ReadyPath);
            await using (var stream = await ProcessBoundLoopbackConnector.ConnectAsync(
                new DnsEndPoint(IPAddress.Loopback.ToString(), accepted.Port),
                accepted.Process.Id,
                CancellationToken.None))
            {
                await stream.WriteAsync(new byte[] { 42 });
            }

            Assert.AreEqual("1", await WaitForFileTextAsync(accepted.ResultPath));
            await WaitForExitAsync(accepted.Process);

            var rejected = StartPayloadProbe(testDirectory, ReserveAvailablePort(), "rejected");
            rejectedProcess = rejected.Process;
            await WaitForFileAsync(rejected.ReadyPath);
            var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            {
                await using var _ = await ProcessBoundLoopbackConnector.ConnectAsync(
                    new DnsEndPoint(IPAddress.Loopback.ToString(), rejected.Port),
                    Environment.ProcessId,
                    CancellationToken.None);
            });

            StringAssert.Contains(exception.Message, "different local process");
            Assert.AreEqual("0", await WaitForFileTextAsync(rejected.ResultPath));
            await WaitForExitAsync(rejected.Process);
        }
        finally
        {
            StopProcess(rejectedProcess);
            StopProcess(acceptedProcess);
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    [TestMethod]
    public async Task TranscriberDoesNotUploadAudioAfterItsServerPortIsTakenOver()
    {
        var testDirectory = CreateTestDirectory();
        var port = ReserveAvailablePort();
        var releasePath = Path.Combine(testDirectory, "release");
        var releasedPath = Path.Combine(testDirectory, "released");
        var exitPath = Path.Combine(testDirectory, "exit");
        Process? expectedServer = null;
        Process? attacker = null;
        try
        {
            using var transcriber = new PersistentParakeetServerTranscriber(
                new CliTranscriberOptions("unused-cli.exe", "unused-model.gguf"),
                "unused-server.exe",
                processPort => expectedServer = CreateListeningProcess(
                    KeeperScript,
                    processPort,
                    new Dictionary<string, string>
                    {
                        ["PTT_TEST_RELEASE"] = releasePath,
                        ["PTT_TEST_RELEASED"] = releasedPath,
                        ["PTT_TEST_EXIT"] = exitPath
                    }),
                () => port);

            await transcriber.WarmUpAsync(CancellationToken.None).WaitAsync(TestTimeout);
            await File.WriteAllTextAsync(releasePath, "release");
            await WaitForFileAsync(releasedPath);

            var probe = StartPayloadProbe(testDirectory, port, "attacker");
            attacker = probe.Process;
            await WaitForFileAsync(probe.ReadyPath);

            var wavPath = Path.Combine(testDirectory, "private-dictation.wav");
            await File.WriteAllBytesAsync(wavPath, CreatePcmWave(TimeSpan.FromMilliseconds(100)));
            TranscriptResult? result = null;
            Exception? failure = null;
            try
            {
                result = await transcriber.TranscribeAsync(wavPath, CancellationToken.None).WaitAsync(TestTimeout);
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            var receivedBytes = int.Parse(
                await WaitForFileTextAsync(probe.ResultPath),
                System.Globalization.CultureInfo.InvariantCulture);
            Assert.AreEqual(0, receivedBytes, "The process that took over the port received dictated audio bytes.");
            Assert.IsNull(result);
            Assert.IsInstanceOfType<HttpRequestException>(failure);
            StringAssert.Contains(failure.ToString(), "different local process");
        }
        finally
        {
            await File.WriteAllTextAsync(exitPath, "exit");
            StopProcess(attacker);
            StopProcess(expectedServer);
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    private static readonly string PayloadProbeScript = """
        $listener = [System.Net.Sockets.TcpListener]::new(
            [System.Net.IPAddress]::Loopback,
            [int]$env:PTT_TEST_PORT)
        $listener.Start()
        [System.IO.File]::WriteAllText($env:PTT_TEST_READY, 'ready')
        try {
            $client = $listener.AcceptTcpClient()
            try {
                $stream = $client.GetStream()
                $buffer = [byte[]]::new(4096)
                $received = [System.IO.MemoryStream]::new()
                $expectedLength = -1
                while (($read = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                    $received.Write($buffer, 0, $read)
                    if ($expectedLength -lt 0) {
                        $text = [System.Text.Encoding]::ASCII.GetString($received.ToArray())
                        $headerEnd = $text.IndexOf("`r`n`r`n", [System.StringComparison]::Ordinal)
                        if ($headerEnd -ge 0) {
                            $match = [regex]::Match($text.Substring(0, $headerEnd), '(?im)^Content-Length:\s*(\d+)\s*$')
                            if ($match.Success) {
                                $expectedLength = $headerEnd + 4 + [int]$match.Groups[1].Value
                            }
                        }
                    }
                    if ($expectedLength -ge 0 -and $received.Length -ge $expectedLength) {
                        break
                    }
                }
                [System.IO.File]::WriteAllText($env:PTT_TEST_RESULT, [string]$received.Length)
                if ($received.Length -gt 0) {
                    try {
                        $body = [System.Text.Encoding]::UTF8.GetBytes('{"text":"attacker received audio","words":[]}')
                        $headers = [System.Text.Encoding]::ASCII.GetBytes(
                            "HTTP/1.1 200 OK`r`nContent-Type: application/json`r`nContent-Length: $($body.Length)`r`nConnection: close`r`n`r`n")
                        $stream.Write($headers, 0, $headers.Length)
                        $stream.Write($body, 0, $body.Length)
                        $stream.Flush()
                    }
                    catch {
                    }
                }
            }
            finally {
                $client.Dispose()
            }
        }
        finally {
            $listener.Stop()
        }
        """;

    private static readonly string KeeperScript = """
        $listener = [System.Net.Sockets.TcpListener]::new(
            [System.Net.IPAddress]::Loopback,
            [int]$env:PTT_TEST_PORT)
        $listener.Start()
        try {
            while (-not [System.IO.File]::Exists($env:PTT_TEST_RELEASE)) {
                if ($listener.Pending()) {
                    $client = $listener.AcceptTcpClient()
                    $client.Dispose()
                }
                Start-Sleep -Milliseconds 10
            }
            $listener.Stop()
            [System.IO.File]::WriteAllText($env:PTT_TEST_RELEASED, 'released')
            while (-not [System.IO.File]::Exists($env:PTT_TEST_EXIT)) {
                Start-Sleep -Milliseconds 10
            }
        }
        finally {
            $listener.Stop()
        }
        """;

    private static PayloadProbe StartPayloadProbe(string testDirectory, int port, string name)
    {
        var readyPath = Path.Combine(testDirectory, $"{name}-ready");
        var resultPath = Path.Combine(testDirectory, $"{name}-result");
        var process = CreateListeningProcess(
            PayloadProbeScript,
            port,
            new Dictionary<string, string>
            {
                ["PTT_TEST_READY"] = readyPath,
                ["PTT_TEST_RESULT"] = resultPath
            });
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("Could not start the TCP payload probe.");
        }

        return new PayloadProbe(process, port, readyPath, resultPath);
    }

    private static Process CreateListeningProcess(
        string script,
        int port,
        IReadOnlyDictionary<string, string> environment)
    {
        var encodedScript = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            ErrorDialog = false
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-EncodedCommand");
        startInfo.ArgumentList.Add(encodedScript);
        startInfo.Environment["PTT_TEST_PORT"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        foreach (var (name, value) in environment)
        {
            startInfo.Environment[name] = value;
        }

        return new Process { StartInfo = startInfo };
    }

    private static int ReserveAvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task WaitForFileAsync(string path) =>
        _ = await WaitForFileTextAsync(path);

    private static async Task<string> WaitForFileTextAsync(string path)
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        while (!File.Exists(path))
        {
            await Task.Delay(20, timeout.Token);
        }

        return await File.ReadAllTextAsync(path, timeout.Token);
    }

    private static async Task WaitForExitAsync(Process process)
    {
        await process.WaitForExitAsync().WaitAsync(TestTimeout);
        Assert.AreEqual(0, process.ExitCode, await process.StandardError.ReadToEndAsync());
    }

    private static void StopProcess(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            process.Dispose();
        }
    }

    private static string CreateTestDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ptt-security-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static byte[] CreatePcmWave(TimeSpan duration)
    {
        var pcm = new byte[checked((int)(duration.TotalSeconds * 32000))];
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + pcm.Length);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(16000);
        writer.Write(32000);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(pcm.Length);
        writer.Write(pcm);
        writer.Flush();
        return stream.ToArray();
    }

    private sealed record PayloadProbe(Process Process, int Port, string ReadyPath, string ResultPath);
}
