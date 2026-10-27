using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using PttDictation.App;
using PttDictation.Core;

// Explicit real-download probe. It never reads a registration from the daily app,
// injects installer dependencies, starts the desktop app, or changes its settings.
internal static class QwenSetupProbe
{
    public static async Task<int> RunAsync(string appDataDirectory, string reportDirectory, bool requireEmpty = true)
    {
        string root, output;
        try
        {
            (root, output) = ValidatePaths(appDataDirectory, reportDirectory, requireEmpty);
        }
        catch (Exception error) when (error is ArgumentException or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("Fresh setup preflight rejected the paths: " + error.GetType().Name);
            return 2;
        }

        Directory.CreateDirectory(root);
        Directory.CreateDirectory(output);
        var clock = Stopwatch.StartNew();
        var assembly = typeof(QwenInstaller).Assembly;
        var report = new Dictionary<string, object?>
        {
            ["startedUtc"] = DateTimeOffset.UtcNow,
            ["appData"] = root,
            ["freshRootVerifiedEmpty"] = requireEmpty,
            ["productionInstallerWithoutOverrides"] = true,
            ["assemblyVersion"] = assembly.GetName().Version?.ToString(),
            ["assemblyInformationalVersion"] = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            ["assemblyModuleVersionId"] = assembly.ManifestModule.ModuleVersionId,
            ["completed"] = false
        };
        using var progress = new BoundedProgress(Path.Combine(output, "progress.jsonl"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromHours(4) + TimeSpan.FromMinutes(10));
        ConsoleCancelEventHandler cancel = (_, args) => { args.Cancel = true; deadline.Cancel(); };
        Console.CancelKeyPress += cancel;
        var stage = "hardware";
        try
        {
            // Record the real eligibility independently of the installer's own probe.
            var hardware = await Task.Run(QwenNativeHardware.Probe, deadline.Token);
            report["hardware"] = hardware;
            Save(output, report);
            var installer = new QwenInstaller(root);
            progress.Pass = 1;
            stage = "first_install";
            var firstClock = Stopwatch.StartNew();
            var firstStatus = await installer.InstallAsync(progress, deadline.Token);
            report["firstSetupSeconds"] = firstClock.Elapsed.TotalSeconds;
            report["firstSetupReady"] = firstStatus.IsReady;
            if (!firstStatus.IsReady) throw new InvalidOperationException("First setup did not become ready.");
            stage = "first_registration_validation";
            var first = ValidateInstallation(root, hardware.IsSupported);
            var runtimeDirectories = RuntimeDirectories(root);
            var nativeAssetPaths = NativeAssetPaths(first);
            if (runtimeDirectories.Length == 0)
                throw new InvalidDataException("Fresh setup did not create its own managed runtime.");
            report["firstInstallation"] = first;
            WriteBenchmarkCommand(output, first);
            report["runtimeDirectoriesAfterFirst"] = runtimeDirectories;
            report["nativeAssetPathsAfterFirst"] = nativeAssetPaths;
            report["runtimeBackend"] = first.NativeConfigPath is null ? "standard_hugging_face" : "native_cuda";
            report["fallbackRuntimeProvisioning"] = "fresh_managed_cpu_pytorch";
            Save(output, report);

            // Exercise the same public Check Qwen setup operation a second time.
            progress.Pass = 2;
            stage = "second_install_reuse";
            var secondClock = Stopwatch.StartNew();
            var secondStatus = await installer.InstallAsync(progress, deadline.Token);
            report["secondSetupSeconds"] = secondClock.Elapsed.TotalSeconds;
            report["secondSetupReady"] = secondStatus.IsReady;
            if (!secondStatus.IsReady || !installer.GetStatus().IsReady)
                throw new InvalidOperationException("Repeated setup did not remain ready.");
            stage = "reuse_validation";
            var second = ValidateInstallation(root, hardware.IsSupported);
            var repeatedDirectories = RuntimeDirectories(root);
            if (!runtimeDirectories.SequenceEqual(repeatedDirectories, StringComparer.OrdinalIgnoreCase)
                || !nativeAssetPaths.SequenceEqual(NativeAssetPaths(second), StringComparer.OrdinalIgnoreCase)
                || !SamePath(first.PythonPath, second.PythonPath) || !SamePath(first.ModelPath, second.ModelPath)
                || !SamePath(first.NativeConfigPath, second.NativeConfigPath))
                throw new InvalidDataException("Repeated setup replaced or added a runtime or changed the registered asset paths.");
            report["runtimeDirectoriesAfterSecond"] = repeatedDirectories;
            report["secondInstallation"] = second;
            report["reusePassed"] = true;
            report["completed"] = true;
            stage = "complete";
            Console.WriteLine("Production setup and repeated-setup reuse passed. See setup-report.json.");
            return 0;
        }
        catch (Exception error)
        {
            // Installer exceptions can include subprocess output or signed URLs.
            // Keep those out of this shareable, bounded diagnostic report.
            report["failureType"] = error.GetType().FullName;
            var safeMessage = System.Text.RegularExpressions.Regex.Replace(error.Message, @"https?://\S+", "<download URL>");
            report["failureMessage"] = safeMessage.Length > 4096 ? safeMessage[^4096..] : safeMessage;
            Console.Error.WriteLine($"Fresh setup probe failed at {stage} ({error.GetType().Name}); see setup-report.json.");
            return error is OperationCanceledException ? 130 : 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
            report["lastStage"] = stage;
            report["totalSeconds"] = clock.Elapsed.TotalSeconds;
            report["finishedUtc"] = DateTimeOffset.UtcNow;
            report["progressRowsWritten"] = progress.Written;
            report["progressRowsDropped"] = progress.Dropped;
            Save(output, report);
        }
    }

    public static async Task<int> RunUiAsync(string appDataDirectory, string reportDirectory)
    {
        string root, output;
        try { (root, output) = ValidatePaths(appDataDirectory, reportDirectory); }
        catch (Exception error) when (error is ArgumentException or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("Fresh setup UI preflight rejected the paths: " + error.GetType().Name);
            return 2;
        }
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(output);
        var finished = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using var capture = new BoundedProgress(Path.Combine(output, "progress.jsonl"));
                var installer = new ObservedInstaller(root, output, capture);
                using (var form = new SettingsForm(new AppSettingsStore(Path.Combine(root, "settings.json")),
                    ModelRegistry.CreateDefault(),
                    (_, _) => Task.FromException<string>(new InvalidOperationException("This isolated host supports only the Qwen setup buttons.")),
                    _ => false, installer, GraphicsHardware.HasNvidiaAdapter()))
                {
                    form.Text = "Qwen fresh setup verification — isolated Settings";
                    // The actual Qwen controls remain unchanged. Disable the unrelated
                    // Parakeet download button so this host cannot touch daily assets.
                    DisableParakeetDownload(form);
                    var shown = false;
                    form.VisibleChanged += (_, _) =>
                    {
                        if (form.Visible) shown = true;
                        else if (shown) Application.ExitThread();
                    };
                    form.QuitRequested += (_, _) => Application.ExitThread();
                    Application.Run(form); // A human/CUA must click Download Qwen.
                }
                // Disposing Settings cancels its owned setup token. The wrapper never
                // requires the UI context to finish logging or installer cleanup.
                try { installer.Current?.GetAwaiter().GetResult(); }
                catch { /* Failure is recorded, never converted into a passing result. */ }
                finished.TrySetResult(installer.ReusePassed ? 0 : 1);
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("Fresh setup UI host failed: " + error.GetType().Name);
                finished.TrySetResult(1);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return await finished.Task.ConfigureAwait(false);
    }

    private static void DisableParakeetDownload(Control parent)
    {
        foreach (Control control in parent.Controls)
        {
            if (control is Button && control.Text == "Download") control.Enabled = false;
            DisableParakeetDownload(control);
        }
    }

    private sealed class ObservedInstaller(string root, string output, BoundedProgress capture) : IQwenInstaller
    {
        private readonly QwenInstaller production = new(root);
        private QwenTranscriberOptions? first;
        private string[]? firstDirectories;
        private string[]? firstNativeAssets;
        private readonly List<object> attempts = [];
        public Task<QwenSetupStatus>? Current { get; private set; }
        public bool ReusePassed { get; private set; }
        public QwenSetupStatus GetStatus() => production.GetStatus();
        public Task<QwenSetupStatus> InstallAsync(IProgress<QwenSetupProgress>? progress, CancellationToken cancellationToken)
            => Current = ObserveAsync(progress, cancellationToken);

        private async Task<QwenSetupStatus> ObserveAsync(IProgress<QwenSetupProgress>? progress, CancellationToken token)
        {
            var clock = Stopwatch.StartNew();
            var number = attempts.Count + 1;
            capture.Pass = number;
            var attempt = new Dictionary<string, object?> { ["number"] = number, ["startedUtc"] = DateTimeOffset.UtcNow, ["completed"] = false };
            attempts.Add(attempt);
            var assembly = typeof(QwenInstaller).Assembly;
            void SaveReport() => Save(output, new
            {
                mode = "real_settings_ui", appData = root, freshRootVerifiedEmpty = true,
                productionInstallerWithoutOverrides = true, assemblyVersion = assembly.GetName().Version?.ToString(),
                assemblyInformationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                assemblyModuleVersionId = assembly.ManifestModule.ModuleVersionId,
                attempts, firstSetupCompleted = first is not null, reusePassed = ReusePassed,
                progressRowsWritten = capture.Written, progressRowsDropped = capture.Dropped
            });
            SaveReport();
            try
            {
                var hardware = await Task.Run(QwenNativeHardware.Probe, token).ConfigureAwait(false);
                attempt["hardware"] = hardware;
                SaveReport();
                var status = await production.InstallAsync(new ForwardProgress(value =>
                {
                    capture.Report(value);
                    progress?.Report(value);
                }), token).ConfigureAwait(false);
                attempt["isReady"] = status.IsReady;
                if (!status.IsReady) throw new InvalidOperationException("Setup did not become ready.");
                var options = ValidateInstallation(root, hardware.IsSupported);
                var directories = RuntimeDirectories(root);
                if (directories.Length == 0) throw new InvalidDataException("Fresh setup did not create its own managed runtime.");
                if (first is null)
                {
                    first = options;
                    firstDirectories = directories;
                    firstNativeAssets = NativeAssetPaths(options);
                }
                else
                {
                    if (!firstDirectories!.SequenceEqual(directories, StringComparer.OrdinalIgnoreCase)
                        || !firstNativeAssets!.SequenceEqual(NativeAssetPaths(options), StringComparer.OrdinalIgnoreCase)
                        || !SamePath(first.PythonPath, options.PythonPath) || !SamePath(first.ModelPath, options.ModelPath)
                        || !SamePath(first.NativeConfigPath, options.NativeConfigPath))
                        throw new InvalidDataException("Repeated setup did not preserve the installed runtime paths.");
                    ReusePassed = true;
                }
                attempt["installation"] = options;
                attempt["runtimeDirectories"] = directories;
                attempt["nativeAssetPaths"] = NativeAssetPaths(options);
                attempt["runtimeBackend"] = options.NativeConfigPath is null ? "standard_hugging_face" : "native_cuda";
                attempt["fallbackRuntimeProvisioning"] = "fresh_managed_cpu_pytorch";
                attempt["completed"] = true;
                WriteBenchmarkCommand(output, options);
                return status;
            }
            catch (Exception error)
            {
                attempt["failureType"] = error.GetType().FullName;
                throw;
            }
            finally
            {
                attempt["setupSeconds"] = clock.Elapsed.TotalSeconds;
                attempt["finishedUtc"] = DateTimeOffset.UtcNow;
                SaveReport();
            }
        }
    }

    private sealed class ForwardProgress(Action<QwenSetupProgress> report) : IProgress<QwenSetupProgress>
    {
        public void Report(QwenSetupProgress value) => report(value);
    }

    private static (string Root, string Output) ValidatePaths(string appDataDirectory, string reportDirectory, bool requireEmpty = true)
    {
        var root = QwenInstaller.RequireLocalPath(appDataDirectory);
        var output = QwenInstaller.RequireLocalPath(reportDirectory);
        if (Inside(root, AppPaths.RootDirectory) || Inside(output, AppPaths.RootDirectory))
            throw new InvalidOperationException("The probe must be isolated from the daily app data.");
        if (Inside(root, output) || Inside(output, root))
            throw new InvalidOperationException("Use separate sibling setup and report directories.");
        if (File.Exists(root) || (requireEmpty && Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any()))
            throw new InvalidOperationException("The first-run app-data directory must be empty.");
        if (File.Exists(output) || (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any()))
            throw new InvalidOperationException("The report directory must be empty.");
        return (root, output);
    }

    private static void WriteBenchmarkCommand(string output, QwenTranscriberOptions options)
    {
        var command = new List<string> { options.PythonPath, "-u", "-B", options.WorkerPath, "--model", options.ModelPath };
        if (options.NativeConfigPath is { } native) command.AddRange(["--native-config", native]);
        File.WriteAllText(Path.Combine(output, "benchmark-command.json"), JsonSerializer.Serialize(command, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static QwenTranscriberOptions ValidateInstallation(string root, bool nativeExpected)
    {
        var options = QwenInstallation.Load(root);
        RequireInside(options.PythonPath, root);
        RequireInside(options.ModelPath, root);
        RequireInside(QwenInstallation.ManifestPath(root), root);
        // Worker code is the packaged product, not a downloaded runtime asset.
        RequireInside(options.WorkerPath, Path.Combine(AppContext.BaseDirectory, "qwen-worker"));
        if ((options.NativeConfigPath is not null) != nativeExpected)
            throw new InvalidDataException("The selected backend does not match the real hardware eligibility.");
        if (options.NativeConfigPath is { } native)
        {
            RequireInside(native, root);
            using var manifest = JsonDocument.Parse(File.ReadAllText(native));
            foreach (var property in new[] { "serverPath", "modelPath", "projectorPath" })
                RequireInside(manifest.RootElement.GetProperty(property).GetString()!, root);
        }
        return options;
    }

    private static string[] RuntimeDirectories(string root)
    {
        var setup = Path.Combine(root, "qwen");
        var directories = Directory.GetDirectories(setup, "runtime-*", SearchOption.TopDirectoryOnly);
        foreach (var directory in directories) RequireInside(directory, root);
        return directories.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string[] NativeAssetPaths(QwenTranscriberOptions options)
    {
        if (options.NativeConfigPath is null) return [];
        using var manifest = JsonDocument.Parse(File.ReadAllText(options.NativeConfigPath));
        return new[] { "serverPath", "modelPath", "projectorPath" }
            .Select(property => Path.GetFullPath(manifest.RootElement.GetProperty(property).GetString()!)).ToArray();
    }

    private static void RequireInside(string path, string root)
    {
        var verified = QwenInstaller.RequireLocalPath(path);
        if (!Inside(verified, root)) throw new InvalidDataException("A setup asset escaped the isolated directory.");
    }

    private static bool Inside(string path, string root)
    {
        var parent = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var child = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        return child.Equals(parent, StringComparison.OrdinalIgnoreCase)
            || child.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool SamePath(string? first, string? second) => first is null || second is null
        ? first == second : Path.GetFullPath(first).Equals(Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);

    private static void Save(string output, object report) => File.WriteAllText(Path.Combine(output, "setup-report.json"),
        JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));

    private sealed class BoundedProgress(string path) : IProgress<QwenSetupProgress>, IDisposable
    {
        private readonly object gate = new();
        private readonly StreamWriter writer = new(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
        public int Pass { get; set; }
        public int Written { get; private set; }
        public int Dropped { get; private set; }

        public void Report(QwenSetupProgress value)
        {
            lock (gate)
            {
                if (Written >= 4096) { Dropped++; return; }
                // Emit a finite vocabulary instead of package output, paths, URLs,
                // error bodies, or credentials that may occur in progress text.
                var text = value.Message.ToLowerInvariant();
                var phase = text.Contains("fallback") ? "fallback"
                    : text.Contains("download") ? "download"
                    : text.Contains("warm") ? "warmup"
                    : text.Contains("reus") ? "reuse"
                    : text.Contains("unpack") || text.Contains("extract") ? "extract"
                    : text.Contains("checking") || text.Contains("validat") || text.Contains("verif") ? "validation"
                    : text.Contains("packages") ? "packages" : "setup";
                writer.WriteLine(JsonSerializer.Serialize(new { utc = DateTimeOffset.UtcNow, pass = Pass,
                    phase, percent = value.Percent is { } percent ? Math.Clamp(percent, 0, 100) : (int?)null }));
                Written++;
            }
        }

        public void Dispose() { lock (gate) writer.Dispose(); }
    }
}
