using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PttDictation.App;

internal sealed record QwenSetupStatus(bool IsReady, string Message);
internal sealed record QwenSetupProgress(string Message, int? Percent = null);
internal interface IQwenInstaller
{
    QwenSetupStatus GetStatus();
    Task<QwenSetupStatus> InstallAsync(IProgress<QwenSetupProgress>? progress, CancellationToken cancellationToken);
}

internal sealed record QwenModelFile(string Name, string Sha256, long Size);
internal sealed record QwenInstallerPlan(string RuntimeUrl, string RuntimeSha256, long RuntimeSize,
    string ModelId, string Revision, QwenModelFile[] ModelFiles)
{
    public string Requirements { get; init; } = "";
    public static QwenInstallerPlan Load()
    {
        string Read(string name)
        {
            using var source = typeof(QwenInstaller).Assembly.GetManifestResourceStream("PttDictation.QwenSetup." + name)
                ?? throw new InvalidOperationException("The Qwen setup resources are missing from this app package.");
            using var reader = new StreamReader(source);
            return reader.ReadToEnd();
        }
        return (JsonSerializer.Deserialize<QwenInstallerPlan>(Read("assets.json"),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException("Missing Qwen setup plan."))
            with { Requirements = Read("requirements-win-cu128.txt") };
    }
}

/// <summary>Provisions only a dedicated user-local environment; registration is the final commit.</summary>
internal sealed class QwenInstaller : IQwenInstaller
{
    private static readonly HttpClient Downloads = new(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = Timeout.InfiniteTimeSpan };
    private readonly string _appData;
    private readonly QwenInstallerPlan _plan;
    private readonly HttpClient _http;
    internal TimeSpan DownloadIdleTimeout { get; init; } = TimeSpan.FromSeconds(60);
    private readonly Func<string, IReadOnlyList<string>, IProgress<QwenSetupProgress>?, CancellationToken, Task> _run;

    public QwenInstaller(string appData)
        : this(appData, QwenInstallerPlan.Load(), Downloads, QwenSetupProcess.RunAsync) { }

    internal QwenInstaller(string appData, QwenInstallerPlan plan, HttpClient http,
        Func<string, IReadOnlyList<string>, IProgress<QwenSetupProgress>?, CancellationToken, Task> run)
    {
        _appData = RequireLocalPath(appData);
        _plan = plan;
        _http = http;
        _run = run;
    }

    public QwenSetupStatus GetStatus() => new(QwenInstallation.IsAvailable(_appData), QwenInstallation.Describe(_appData));

    public Task<QwenSetupStatus> InstallAsync(IProgress<QwenSetupProgress>? progress, CancellationToken cancellationToken) =>
        Task.Run(() => InstallCoreAsync(progress, cancellationToken), cancellationToken);

    private async Task<QwenSetupStatus> InstallCoreAsync(IProgress<QwenSetupProgress>? progress, CancellationToken token)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromHours(2));
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        try { return await InstallWithinDeadlineAsync(progress, operation.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && deadline.IsCancellationRequested)
        { throw new TimeoutException("Qwen setup exceeded its two-hour deadline. Completed downloads are retained; try setup again."); }
    }

    private async Task<QwenSetupStatus> InstallWithinDeadlineAsync(IProgress<QwenSetupProgress>? progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        RequireLocalPath(_appData);
        Directory.CreateDirectory(_appData);
        var setup = RequireLocalPath(Path.Combine(_appData, "qwen"));
        Directory.CreateDirectory(setup);
        using var setupLock = AcquireLock(Path.Combine(setup, "setup.lock"));
        var cache = RequireLocalPath(Path.Combine(setup, "downloads"));
        Directory.CreateDirectory(cache);
        string? registeredPython = null, registeredModel = null;
        try
        {
            (registeredPython, registeredModel) = QwenInstallation.ReadRegisteredPaths(_appData);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            progress?.Report(new("Preparing Qwen setup. Existing registration is unavailable."));
        }

        var model = registeredModel;
        if (model is null || !await ModelMatchesAsync(model, progress, token).ConfigureAwait(false))
        {
            model = RequireLocalPath(Path.Combine(setup, "models", _plan.Revision));
            Directory.CreateDirectory(model);
            foreach (var file in _plan.ModelFiles)
            {
                ValidateAssetName(file.Name);
                var url = new Uri($"https://huggingface.co/{_plan.ModelId}/resolve/{_plan.Revision}/{Uri.EscapeDataString(file.Name)}");
                await DownloadAsync(url, Path.Combine(model, file.Name), file.Sha256, file.Size,
                    "Qwen " + file.Name, progress, token).ConfigureAwait(false);
            }
        }
        else progress?.Report(new("Reusing the verified local Qwen model. No model download is needed."));

        if (registeredPython is not null && File.Exists(registeredPython))
        {
            try
            {
                progress?.Report(new("Checking the existing Qwen runtime and NVIDIA GPU…"));
                await ValidateRuntimeAsync(registeredPython, model, progress, token).ConfigureAwait(false);
                return await RegisterAsync(registeredPython, model, progress, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error)
            {
                if (error.Message.Contains("No NVIDIA CUDA device", StringComparison.Ordinal)
                    || error.Message.Contains("does not support Qwen", StringComparison.Ordinal))
                    throw; // Downloading the same packages cannot repair missing/unsupported hardware.
                progress?.Report(new("Existing runtime needs repair. Preparing an isolated Qwen runtime. " + error.Message));
            }
        }

        // Never pip-install into a registered/user-owned runtime. An interrupted
        // setup leaves its download cache reusable and the registration untouched.
        var runtime = RequireLocalPath(Path.Combine(setup, "runtime-" + Guid.NewGuid().ToString("N")));
        var registered = false;
        try
        {
            var archive = Path.Combine(cache, "python-3.12.14-windows-x64.tar.gz");
            await DownloadAsync(new Uri(_plan.RuntimeUrl), archive, _plan.RuntimeSha256, _plan.RuntimeSize,
                "Python runtime", progress, token).ConfigureAwait(false);
            progress?.Report(new("Unpacking the private Python runtime…"));
            await ExtractRuntimeAsync(archive, runtime, token).ConfigureAwait(false);
            var python = RequireLocalPath(Path.Combine(runtime, "python", "python.exe"));
            if (!File.Exists(python)) throw new InvalidDataException("The downloaded Python runtime is incomplete.");
            var requirements = Path.Combine(runtime, "requirements-win-cu128.txt");
            await File.WriteAllTextAsync(requirements, _plan.Requirements, new UTF8Encoding(false), token).ConfigureAwait(false);
            progress?.Report(new("Installing the verified Qwen packages. The first download can take several minutes."));
            await _run(python, new[] { "-I", "-B", "-m", "pip", "--isolated", "--disable-pip-version-check",
                "install", "--no-input", "--no-index", "--no-deps", "--only-binary=:all:", "--require-hashes",
                "--cache-dir", Path.Combine(setup, "wheel-cache"), "-r", requirements }, progress, token).ConfigureAwait(false);
            await _run(python, new[] { "-I", "-B", "-m", "pip", "--isolated", "check" }, progress, token).ConfigureAwait(false);
            progress?.Report(new("Checking Qwen packages, model processor, and NVIDIA GPU…"));
            await ValidateRuntimeAsync(python, model, progress, token).ConfigureAwait(false);
            var result = await RegisterAsync(python, model, progress, token).ConfigureAwait(false);
            registered = true;
            return result;
        }
        finally
        {
            if (!registered && Directory.Exists(runtime))
            {
                // This unique directory belongs only to this setup operation.
                // Child cleanup is awaited by _run before this deletion.
                try { RequireLocalPath(runtime); Directory.Delete(runtime, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static FileStream AcquireLock(string path)
    {
        RequireLocalPath(path);
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException error) { throw new InvalidOperationException("Another Qwen setup is already running. Wait for it to finish or cancel it.", error); }
    }

    private async Task<bool> ModelMatchesAsync(string model, IProgress<QwenSetupProgress>? progress, CancellationToken token)
    {
        RequireLocalPath(model);
        foreach (var file in _plan.ModelFiles)
        {
            ValidateAssetName(file.Name);
            progress?.Report(new("Checking the local Qwen " + file.Name + "…"));
            if (!await MatchesAsync(Path.Combine(model, file.Name), file.Sha256, file.Size, token).ConfigureAwait(false))
                return false;
        }
        return true;
    }

    private Task ValidateRuntimeAsync(string python, string model, IProgress<QwenSetupProgress>? progress, CancellationToken token) =>
        _run(RequireLocalPath(python), new[] { "-I", "-B", "-c", RuntimeValidation, RequireLocalPath(model) }, progress, token);

    private async Task<QwenSetupStatus> RegisterAsync(string python, string model,
        IProgress<QwenSetupProgress>? progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        RequireLocalPath(python);
        RequireLocalPath(model);
        var target = RequireLocalPath(QwenInstallation.ManifestPath(_appData));
        var pending = Path.Combine(_appData, "qwen-installation-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var json = JsonSerializer.Serialize(new { schema = 1, pythonPath = python, modelPath = model,
                modelId = _plan.ModelId, revision = _plan.Revision, registeredAtUtc = DateTime.UtcNow.ToString("O") });
            await File.WriteAllTextAsync(pending, json, new UTF8Encoding(false), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            File.Move(pending, target, overwrite: true);
            // Registration is committed. A cancellation arriving after this point
            // cannot turn a completed setup into a reported cancelled setup.
            try { progress?.Report(new("Qwen is ready. Choose Qwen for final recognition and save Settings.", 100)); }
            catch { /* A presentation callback cannot undo committed registration. */ }
            return new(true, "Qwen is ready locally. Live preview continues using Parakeet.");
        }
        finally { if (File.Exists(pending)) File.Delete(pending); }
    }

    internal async Task DownloadAsync(Uri uri, string destination, string sha256, long expectedSize,
        string label, IProgress<QwenSetupProgress>? progress, CancellationToken token)
    {
        RequireLocalPath(destination);
        if (await MatchesAsync(destination, sha256, expectedSize, token).ConfigureAwait(false))
        {
            progress?.Report(new("Reusing " + label + ".", 100));
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var pending = RequireLocalPath(destination + ".partial");
        var existing = File.Exists(pending) ? new FileInfo(pending).Length : 0;
        if (existing >= expectedSize) { File.Delete(pending); existing = 0; }
        using var response = await SendDownloadAsync(uri, existing, token).ConfigureAwait(false);
        var resume = existing > 0 && response.StatusCode == HttpStatusCode.PartialContent
            && response.Content.Headers.ContentRange?.From == existing;
        if (response.StatusCode == HttpStatusCode.PartialContent && !resume)
            throw new InvalidDataException("The download server returned an unexpected partial range for " + label + ".");
        response.EnsureSuccessStatusCode();
        await using (var output = new FileStream(pending, resume ? FileMode.Append : FileMode.Create,
            FileAccess.Write, FileShare.None, 131072, useAsync: true))
        await using (var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
        {
            var total = resume ? existing : 0;
            var buffer = new byte[131072];
            var previousPercent = -1;
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(token);
            while (true)
            {
                idle.CancelAfter(DownloadIdleTimeout);
                int read;
                try { read = await input.ReadAsync(buffer, idle.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                { throw new TimeoutException("The Qwen download stopped responding. Try setup again to resume it."); }
                if (read == 0) break;
                total += read;
                if (total > expectedSize) throw new InvalidDataException("Downloaded " + label + " exceeds its verified size.");
                await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                var percent = (int)(total * 100 / expectedSize);
                if (percent != previousPercent)
                {
                    previousPercent = percent;
                    progress?.Report(new($"Downloading {label}: {total / 1048576d:F1} / {expectedSize / 1048576d:F1} MB", percent));
                }
            }
        }
        progress?.Report(new("Verifying " + label + "…"));
        if (!await MatchesAsync(pending, sha256, expectedSize, token).ConfigureAwait(false))
        {
            File.Delete(pending);
            throw new InvalidDataException("The downloaded " + label + " failed its integrity check. Try setup again.");
        }
        token.ThrowIfCancellationRequested();
        File.Move(pending, destination, overwrite: true);
    }

    private async Task<HttpResponseMessage> SendDownloadAsync(Uri uri, long offset, CancellationToken token)
    {
        for (var redirects = 0; redirects < 8; redirects++)
        {
            if (!TrustedDownload(uri)) throw new InvalidDataException("Qwen setup refused a download from an untrusted origin.");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("PttDictation-QwenSetup/1");
            if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
            using var headersTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            headersTimeout.CancelAfter(DownloadIdleTimeout);
            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headersTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { throw new TimeoutException("The Qwen download server did not respond before the deadline. Try setup again."); }
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location is null) throw new InvalidDataException("A Qwen download redirect has no destination.");
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                continue;
            }
            return response;
        }
        throw new InvalidDataException("A Qwen download exceeded its redirect limit.");
    }

    internal static bool TrustedDownload(Uri uri) => uri.Scheme == Uri.UriSchemeHttps
        && string.IsNullOrEmpty(uri.UserInfo) && uri.IsDefaultPort
        && (uri.Host is "github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com"
            or "huggingface.co" || uri.Host.EndsWith(".hf.co", StringComparison.OrdinalIgnoreCase));

    internal static async Task<bool> MatchesAsync(string path, string sha256, long length, CancellationToken token)
    {
        RequireLocalPath(path);
        if (!File.Exists(path) || new FileInfo(path).Length != length) return false;
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, useAsync: true);
        var hash = await SHA256.HashDataAsync(file, token).ConfigureAwait(false);
        return Convert.ToHexString(hash).Equals(sha256, StringComparison.OrdinalIgnoreCase);
    }

    internal static async Task ExtractRuntimeAsync(string archive, string destination, CancellationToken token)
    {
        var root = RequireLocalPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(root);
        await using var file = File.OpenRead(RequireLocalPath(archive));
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);
        while (await reader.GetNextEntryAsync(copyData: false, cancellationToken: token).ConfigureAwait(false) is { } entry)
        {
            token.ThrowIfCancellationRequested();
            var relative = entry.Name.Replace('/', Path.DirectorySeparatorChar);
            if (Path.IsPathRooted(relative) || relative.Contains(':'))
                throw new InvalidDataException("The Python archive contains an unsafe path.");
            var target = RequireLocalPath(Path.GetFullPath(Path.Combine(root, relative)));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The Python archive attempts to escape its directory.");
            if (entry.EntryType == TarEntryType.Directory) Directory.CreateDirectory(target);
            else if (entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true);
                if (entry.DataStream is not null) await entry.DataStream.CopyToAsync(output, token).ConfigureAwait(false);
            }
            else throw new InvalidDataException("The Python archive contains an unsupported link or entry.");
        }
    }

    internal static string RequireLocalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)
            || path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
            throw new InvalidOperationException("Qwen setup requires absolute local paths.");
        var full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\", StringComparison.Ordinal)
            || new DriveInfo(Path.GetPathRoot(full)!).DriveType == DriveType.Network)
            throw new InvalidOperationException("Qwen setup cannot use a network path.");
        for (var entry = full; !string.IsNullOrEmpty(entry); entry = Path.GetDirectoryName(entry))
            if ((File.Exists(entry) || Directory.Exists(entry)) && (File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Qwen setup cannot use linked runtime or model paths.");
        return full;
    }

    private static void ValidateAssetName(string name)
    {
        if (string.IsNullOrEmpty(name) || name != Path.GetFileName(name) || name.Contains(':') || name is "." or "..")
            throw new InvalidDataException("Invalid Qwen model asset name.");
    }

    private const string RuntimeValidation = """
        import os, sys
        os.environ["HF_HUB_OFFLINE"] = "1"
        os.environ["TRANSFORMERS_OFFLINE"] = "1"
        os.environ["HF_HUB_DISABLE_TELEMETRY"] = "1"
        import torch, transformers
        from transformers import AutoModelForMultimodalLM, AutoProcessor
        assert sys.version_info[:2] == (3, 12), "Qwen setup requires Python 3.12."
        assert torch.__version__ == "2.11.0+cu128", "Qwen setup requires PyTorch 2.11.0 with CUDA 12.8."
        assert transformers.__version__ == "5.13.0", "Qwen setup requires Transformers 5.13.0."
        assert torch.cuda.is_available(), "No NVIDIA CUDA device is available. Update the NVIDIA driver or use Parakeet."
        assert torch.cuda.is_bf16_supported(), "This NVIDIA GPU does not support Qwen's BF16 configuration. Use Parakeet."
        AutoProcessor.from_pretrained(sys.argv[1], local_files_only=True, trust_remote_code=False)
        print("Validated Qwen runtime on " + torch.cuda.get_device_name(0), flush=True)
        """;
}