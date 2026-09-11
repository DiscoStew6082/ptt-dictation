using System.Diagnostics;
using System.Text;

namespace PttDictation.App;

/// <summary>Bounded hidden setup child, with a Windows job tied to the app's lifetime.</summary>
internal static class QwenSetupProcess
{
    internal static async Task RunAsync(string executable, IReadOnlyList<string> arguments,
        IProgress<QwenSetupProgress>? progress, CancellationToken cancellationToken)
    {
        var install = arguments.Contains("install");
        using var timeout = new CancellationTokenSource(install ? TimeSpan.FromMinutes(45) : TimeSpan.FromSeconds(90));
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        operation.Token.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(QwenInstaller.RequireLocalPath(executable))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["PYTHONNOUSERSITE"] = "1";
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        start.Environment["HF_HUB_DISABLE_TELEMETRY"] = "1";
        start.Environment["HF_HUB_OFFLINE"] = "1";
        start.Environment["TRANSFORMERS_OFFLINE"] = "1";
        start.Environment["NO_COLOR"] = "1";
        using var process = new Process { StartInfo = start };
        QwenOwnedProcessJob? job = null;
        var tail = new StringBuilder();
        var tailLock = new object();
        Task output = Task.CompletedTask, error = Task.CompletedTask;
        try
        {
            if (!process.Start()) throw new InvalidOperationException("Windows could not start Qwen setup.");
            job = QwenOwnedProcessJob.Assign(process);
            process.StandardInput.Close();
            async Task DrainAsync(StreamReader reader)
            {
                var buffer = new char[512];
                while (true)
                {
                    var count = await reader.ReadAsync(buffer).ConfigureAwait(false);
                    if (count == 0) return;
                    var chunk = new string(buffer, 0, count);
                    lock (tailLock)
                    {
                        tail.Append(chunk);
                        if (tail.Length > 4096) tail.Remove(0, tail.Length - 4096);
                    }
                    if (install && !string.IsNullOrWhiteSpace(chunk))
                        progress?.Report(new("Installing Qwen packages: " + chunk.Trim()));
                }
            }
            output = DrainAsync(process.StandardOutput);
            error = DrainAsync(process.StandardError);
            await process.WaitForExitAsync(operation.Token).ConfigureAwait(false);
            // Close descendants too, before draining inherited pipe handles.
            job.Dispose();
            job = null;
            await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(5), operation.Token).ConfigureAwait(false);
            operation.Token.ThrowIfCancellationRequested();
            if (process.ExitCode != 0)
            {
                string diagnostic;
                lock (tailLock) diagnostic = tail.ToString().Trim();
                throw new InvalidOperationException($"Qwen setup failed (exit {process.ExitCode}). {diagnostic}");
            }
        }
        catch (Exception failure) when (operation.IsCancellationRequested)
        {
            if (cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException("Qwen setup was cancelled.", failure, cancellationToken);
            throw new TimeoutException(install
                ? "Qwen package installation exceeded 45 minutes. Try setup again; downloaded wheels are cached."
                : "Qwen runtime validation exceeded 90 seconds. Check the NVIDIA driver or use Parakeet.", failure);
        }
        finally
        {
            job?.Dispose();
            try
            {
                if (process.Id > 0 && !process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                }
            }
            catch (InvalidOperationException) { }
            finally
            {
                // All owned descendants have exited or cleanup reports a failure.
                await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
        }
    }
}