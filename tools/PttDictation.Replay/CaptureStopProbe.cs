using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using NAudio.Wave;
using PttDictation.App;

internal static class CaptureStopProbe
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static async Task<int> RunAsync(string outputDirectory)
    {
        var output = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(output);
        var scratch = Path.Combine(output, "temporary-capture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        var workerReport = Path.Combine(output, "capture-stop-worker.json");
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--capture-stop-probe-worker");
        start.ArgumentList.Add(workerReport);
        start.ArgumentList.Add(scratch);
        var timedOut = false;
        int? exitCode = null;
        var cleanupErrors = new List<string>();
        try
        {
            using var worker = Process.Start(start) ?? throw new InvalidOperationException("Capture probe did not start.");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try
            {
                await worker.WaitForExitAsync(deadline.Token);
            }
            catch (OperationCanceledException)
            {
                // NAudio Dispose joins its capture thread without a deadline. Isolate all
                // real capture work so a hung driver cannot hang this probe or the live app.
                timedOut = true;
                worker.Kill(entireProcessTree: true);
                await worker.WaitForExitAsync();
            }
            exitCode = worker.ExitCode;
        }
        finally
        {
            // Only this probe's unique directory and its own audio files are removed.
            foreach (var file in Directory.EnumerateFiles(scratch, "*.wav"))
            {
                try { File.Delete(file); }
                catch (Exception error) { cleanupErrors.Add(error.Message); }
            }
            try { Directory.Delete(scratch, recursive: false); }
            catch (Exception error) { cleanupErrors.Add(error.Message); }
            await File.WriteAllTextAsync(Path.Combine(output, "capture-stop-summary.json"), JsonSerializer.Serialize(new
            {
                timedOut, exitCode, cleanupErrors, workerReport,
                scope = "Real microphone lifecycle only. No ASR, settings, clipboard, textbox, hotkey, or live-app manipulation.",
                baseline = "One observation of the old single-stop behavior. Passing does not disprove the deterministic startup race."
            }, JsonOptions));
        }
        Console.WriteLine(JsonSerializer.Serialize(new { output, timedOut, exitCode, cleanupErrors }));
        return !timedOut && exitCode == 0 && cleanupErrors.Count == 0 ? 0 : 1;
    }

    public static async Task<int> RunWorkerAsync(string reportPath, string scratch)
    {
        var results = new List<ProbeResult>();
        void Save() => File.WriteAllText(reportPath, JsonSerializer.Serialize(new
        {
            naudioVersion = typeof(WasapiRecorder).Assembly.GetName().Version?.ToString(),
            productionAssembly = typeof(WasapiAudioRecorder).Assembly.ManifestModule.ModuleVersionId,
            results
        }, JsonOptions));

        async Task Run(string name, Func<ProbeResult, Task> action)
        {
            var result = new ProbeResult { Name = name, Status = "running" };
            results.Add(result);
            Save();
            var timer = Stopwatch.StartNew();
            try { await action(result); result.Status = "passed"; }
            catch (Exception error) { result.Status = "failed"; result.Error = error.ToString(); }
            result.TotalMilliseconds = timer.Elapsed.TotalMilliseconds;
            Save();
        }

        await Run("baseline-single-stop", result =>
        {
            using var recorder = new WasapiRecorderBuilder().WithSharedMode().WithEventSync()
                .WithBufferLength(100).WithFormat(WasapiAudioRecorder.CreateCaptureFormat()).Build();
            result.Device = recorder.DeviceFriendlyName;
            var stopped = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
            recorder.RecordingStopped += (_, e) => stopped.TrySetResult(e.Exception);
            recorder.StartRecording();
            result.StateAfterStart = recorder.CaptureState.ToString();
            var stopTimer = Stopwatch.StartNew();
            recorder.StopRecording();
            result.BaselineTimedOut = !stopped.Task.Wait(TimeSpan.FromSeconds(5));
            result.StopMilliseconds = stopTimer.Elapsed.TotalMilliseconds;
            result.StateAfterSingleStop = recorder.CaptureState.ToString();
            Save();
            // Rescue the old behavior using the production handshake before disposing.
            var cleanup = WasapiAudioRecorder.StopCapture(() => recorder.CaptureState,
                recorder.StopRecording, stopped.Task, TimeSpan.FromSeconds(5));
            if (cleanup is not null) throw cleanup;
            return Task.CompletedTask;
        });

        for (var i = 1; i <= 10; i++)
            await Run("production-immediate-stop-" + i, result => CaptureAsync(scratch, result, 0));
        for (var i = 1; i <= 3; i++)
            await Run("production-immediate-dispose-" + i, async result =>
            {
                var recorder = new WasapiAudioRecorder(scratch);
                try
                {
                    await recorder.StartAsync(CancellationToken.None);
                    var stopTimer = Stopwatch.StartNew();
                    recorder.Dispose();
                    result.StopMilliseconds = stopTimer.Elapsed.TotalMilliseconds;
                }
                finally { recorder.Dispose(); }
            });
        await Run("production-normal-250ms", result => CaptureAsync(scratch, result, 250));
        return results.All(result => result.Status == "passed") ? 0 : 1;
    }

    private static async Task CaptureAsync(string scratch, ProbeResult result, int captureMilliseconds)
    {
        using var recorder = new WasapiAudioRecorder(scratch);
        await recorder.StartAsync(CancellationToken.None);
        if (captureMilliseconds > 0) await Task.Delay(captureMilliseconds);
        var stopTimer = Stopwatch.StartNew();
        var audio = await recorder.StopAsync(CancellationToken.None);
        result.StopMilliseconds = stopTimer.Elapsed.TotalMilliseconds;
        try
        {
            result.WavBytes = new FileInfo(audio.Path).Length;
            if (captureMilliseconds > 0 && result.WavBytes <= 44)
                throw new InvalidOperationException("Normal capture produced no PCM samples.");
        }
        finally { File.Delete(audio.Path); }
    }

    private sealed class ProbeResult
    {
        public required string Name { get; init; }
        public required string Status { get; set; }
        public double TotalMilliseconds { get; set; }
        public double StopMilliseconds { get; set; }
        public string? Device { get; set; }
        public string? StateAfterStart { get; set; }
        public string? StateAfterSingleStop { get; set; }
        public bool? BaselineTimedOut { get; set; }
        public long? WavBytes { get; set; }
        public string? Error { get; set; }
    }
}
