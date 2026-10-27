using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PttDictation.App;

internal sealed record QwenNativeSource(string Name, string Url, string Sha256, long Size);
internal sealed record QwenNativeAsset(string Path, long Size, string Sha256);
internal sealed record QwenNativeInstallerPlan(int Schema, string LlamaCommit, string ModelRevision,
    QwenNativeSource[] Sources, QwenNativeAsset[] Files)
{
    public static QwenNativeInstallerPlan Load()
    {
        using var stream = typeof(QwenNativeInstaller).Assembly.GetManifestResourceStream("PttDictation.QwenSetup.native-assets.json")
            ?? throw new InvalidOperationException("The native Qwen setup resources are missing from this app package.");
        return JsonSerializer.Deserialize<QwenNativeInstallerPlan>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("The native Qwen setup plan is missing.");
    }
}

/// <summary>Stages pinned native assets and commits registration only after actual worker validation.</summary>
internal sealed class QwenNativeInstaller
{
    private const string Decoder = "Qwen3-ASR-1.7B-bf16.gguf";
    private const string Projector = "mmproj-Qwen3-ASR-1.7B-bf16.gguf";
    private readonly string _appData;
    private readonly QwenInstaller _downloader;
    private readonly QwenNativeInstallerPlan _plan;
    private readonly Func<string, string, string, IProgress<QwenSetupProgress>?, CancellationToken, Task> _validate;
    internal Func<Func<CancellationToken, Task>, CancellationToken, Task>? ActivationGuard { get; init; }

    public QwenNativeInstaller(string appData, QwenInstaller downloader,
        Func<string, string, string, IProgress<QwenSetupProgress>?, CancellationToken, Task> validate)
        : this(appData, downloader, QwenNativeInstallerPlan.Load(), validate) { }

    internal QwenNativeInstaller(string appData, QwenInstaller downloader, QwenNativeInstallerPlan plan,
        Func<string, string, string, IProgress<QwenSetupProgress>?, CancellationToken, Task> validate)
    {
        _appData = QwenInstaller.RequireLocalPath(appData);
        _downloader = downloader;
        _plan = plan;
        _validate = validate;
        ValidatePlan();
    }

    // Caller holds the shared Qwen setup lock. Validation must await process cleanup
    // before it returns or throws; this installer never owns arbitrary app processes.
    public async Task<string> InstallAsync(string python, string model, IProgress<QwenSetupProgress>? progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        python = QwenInstaller.RequireLocalPath(python);
        model = QwenInstaller.RequireLocalPath(model);
        if (!File.Exists(python) || !Directory.Exists(model)) throw new InvalidOperationException("The existing Qwen fallback runtime or model is missing.");
        var root = QwenInstaller.RequireLocalPath(Path.Combine(_appData, "qwen", "native"));
        Directory.CreateDirectory(root);
        var target = QwenInstaller.RequireLocalPath(QwenInstallation.NativeManifestPath(_appData));
        var pending = QwenInstaller.RequireLocalPath(Path.Combine(_appData, "qwen-native-" + Guid.NewGuid().ToString("N") + ".tmp"));
        var staging = QwenInstaller.RequireLocalPath(Path.Combine(root, ".staging-" + Guid.NewGuid().ToString("N")));
        string? ownedInstallation = null;
        var movedModels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var committed = false;
        try
        {
            var installed = await FindRegisteredAssetsAsync(target, token).ConfigureAwait(false);
            if (installed is null)
            {
                var canonical = QwenInstaller.RequireLocalPath(Path.Combine(root, _plan.LlamaCommit[..12] + "-" + _plan.ModelRevision[..12]));
                if (await TreeMatchesAsync(canonical, token).ConfigureAwait(false)) installed = canonical;
                else
                {
                    var cache = QwenInstaller.RequireLocalPath(Path.Combine(_appData, "qwen", "downloads"));
                    Directory.CreateDirectory(cache);
                    Directory.CreateDirectory(Path.Combine(staging, "bin"));
                    var extracted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var source in _plan.Sources)
                    {
                        var download = QwenInstaller.RequireLocalPath(Path.Combine(cache, "native-" + source.Sha256[..16] + "-" + source.Name));
                        await _downloader.DownloadAsync(new Uri(source.Url), download, source.Sha256, source.Size,
                            "native Qwen " + source.Name, progress, token).ConfigureAwait(false);
                        if (source.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                            await ExtractAsync(download, staging, extracted, token).ConfigureAwait(false);
                        else
                        {
                            token.ThrowIfCancellationRequested();
                            // Cache and staging share the dedicated app-data volume.
                            // Move multi-GB models instead of requiring a second copy.
                            File.Move(download, QwenInstaller.RequireLocalPath(Path.Combine(staging, source.Name)));
                            movedModels.Add(source.Name, download);
                        }
                    }
                    progress?.Report(new("Checking all native Qwen runtime and model files…"));
                    if (!await TreeMatchesAsync(staging, token).ConfigureAwait(false)) throw new InvalidDataException("The extracted native Qwen assets failed verification.");
                    token.ThrowIfCancellationRequested();
                    installed = Directory.Exists(canonical) ? canonical + "-" + Guid.NewGuid().ToString("N") : canonical;
                    QwenInstaller.RequireLocalPath(installed);
                    Directory.Move(staging, installed);
                    ownedInstallation = installed;
                }
            }
            var registration = JsonSerializer.Serialize(new
            {
                schema = 1, serverPath = Path.Combine(installed, "bin", "llama-server.exe"),
                modelPath = Path.Combine(installed, Decoder), projectorPath = Path.Combine(installed, Projector),
                modelId = "Qwen/Qwen3-ASR-1.7B-hf", llamaCommit = _plan.LlamaCommit, modelRevision = _plan.ModelRevision,
                registeredAtUtc = DateTime.UtcNow.ToString("O")
            });
            await File.WriteAllTextAsync(pending, registration, new UTF8Encoding(false), token).ConfigureAwait(false);
            progress?.Report(new("Loading native Qwen to verify this computer's graphics runtime…"));
            var guard = ActivationGuard ?? ((action, cancellation) => action(cancellation));
            await guard(async guardedToken =>
            {
                using var activation = CancellationTokenSource.CreateLinkedTokenSource(token, guardedToken);
                await _validate(python, model, pending, progress, activation.Token).ConfigureAwait(false);
                activation.Token.ThrowIfCancellationRequested();
                File.Move(pending, target, overwrite: true);
                committed = true;
            }, token).ConfigureAwait(false);
            return target;
        }
        finally
        {
            if (File.Exists(pending)) File.Delete(QwenInstaller.RequireLocalPath(pending));
            if (!committed)
                foreach (var (name, cache) in movedModels)
                {
                    var source = QwenInstaller.RequireLocalPath(Path.Combine(ownedInstallation ?? staging, name));
                    if (File.Exists(source) && !File.Exists(cache)) File.Move(source, QwenInstaller.RequireLocalPath(cache));
                }
            DeleteOwnedTree(staging, root);
            if (!committed && ownedInstallation is not null) DeleteOwnedTree(ownedInstallation, root);
        }
    }

    private async Task<string?> FindRegisteredAssetsAsync(string manifest, CancellationToken token)
    {
        if (!File.Exists(manifest)) return null;
        string? installation;
        try
        {
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(manifest, token).ConfigureAwait(false));
            var entry = document.RootElement;
            if (entry.GetProperty("schema").GetInt32() != 1 || entry.GetProperty("llamaCommit").GetString() != _plan.LlamaCommit
                || entry.GetProperty("modelRevision").GetString() != _plan.ModelRevision) return null;
            var server = QwenInstaller.RequireLocalPath(entry.GetProperty("serverPath").GetString()!);
            installation = Path.GetDirectoryName(Path.GetDirectoryName(server));
            if (installation is null || !server.Equals(Path.Combine(installation, "bin", "llama-server.exe"), StringComparison.OrdinalIgnoreCase)
                || !string.Equals(entry.GetProperty("modelPath").GetString(), Path.Combine(installation, Decoder), StringComparison.OrdinalIgnoreCase)
                || !string.Equals(entry.GetProperty("projectorPath").GetString(), Path.Combine(installation, Projector), StringComparison.OrdinalIgnoreCase)) return null;
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException) { return null; }
        return await TreeMatchesAsync(installation, token).ConfigureAwait(false) ? installation : null;
    }

    private async Task<bool> TreeMatchesAsync(string root, CancellationToken token)
    {
        QwenInstaller.RequireLocalPath(root);
        if (!Directory.Exists(root) || !Directory.Exists(Path.Combine(root, "bin"))) return false;
        QwenInstaller.RequireLocalPath(Path.Combine(root, "bin"));
        if (Directory.EnumerateDirectories(root).Any(path => !Path.GetFileName(path).Equals("bin", StringComparison.OrdinalIgnoreCase))
            || Directory.EnumerateDirectories(Path.Combine(root, "bin")).Any()) return false;
        var expected = _plan.Files.Select(file => file.Path.Replace('/', Path.DirectorySeparatorChar)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(root).Concat(Directory.EnumerateFiles(Path.Combine(root, "bin"))))
            if (!expected.Contains(Path.GetRelativePath(root, QwenInstaller.RequireLocalPath(file)))) return false;
        foreach (var file in _plan.Files)
            if (!await QwenInstaller.MatchesAsync(Path.Combine(root, file.Path), file.Sha256, file.Size, token).ConfigureAwait(false)) return false;
        return true;
    }

    private async Task ExtractAsync(string archivePath, string destination, HashSet<string> extracted, CancellationToken token)
    {
        using var archive = ZipFile.OpenRead(QwenInstaller.RequireLocalPath(archivePath));
        var assets = _plan.Files.Where(file => file.Path.StartsWith("bin/", StringComparison.Ordinal)).ToDictionary(file => file.Path[4..], StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            var name = entry.FullName.Replace('\\', '/');
            if (name.StartsWith('/') || name.Contains(':') || name.Split('/').Any(part => part is "." or ".."))
                throw new InvalidDataException("The native Qwen archive contains an unsafe path.");
            var unixType = (entry.ExternalAttributes >> 16) & 0xf000;
            if ((entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0 || unixType is not (0 or 0x8000 or 0x4000))
                throw new InvalidDataException("The native Qwen archive contains an unsupported link or entry.");
            if (name.EndsWith('/')) continue;
            if (!assets.TryGetValue(name, out var asset))
            {
                if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The native Qwen archive contains an unpinned DLL.");
                continue;
            }
            if (!extracted.Add(asset.Path) || entry.Length != asset.Size) throw new InvalidDataException("The native Qwen archive contains duplicate or incorrectly sized assets.");
            var target = QwenInstaller.RequireLocalPath(Path.Combine(destination, asset.Path));
            await using var input = entry.Open();
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true);
            var buffer = new byte[131072];
            long written = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
            {
                written += read;
                if (written > asset.Size) throw new InvalidDataException("The native Qwen archive exceeded a pinned asset size.");
                await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            }
        }
    }

    private void ValidatePlan()
    {
        static bool Hash(string value, int length) => value is not null && value.Length == length && value.All(Uri.IsHexDigit);
        if (_plan.Schema != 1 || !Hash(_plan.LlamaCommit, 40) || !Hash(_plan.ModelRevision, 40)
            || _plan.Sources is null || _plan.Files is null || _plan.Sources.Length != 4
            || _plan.Sources.Count(source => source.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) != 2)
            throw new InvalidDataException("Unsupported native Qwen asset plan.");
        var sourceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in _plan.Sources)
            if (!Regex.IsMatch(source.Name, "^[A-Za-z0-9_.-]+$") || source.Name is "." or ".." || !sourceNames.Add(source.Name)
                || !Hash(source.Sha256, 64) || source.Size <= 0 || !Uri.TryCreate(source.Url, UriKind.Absolute, out _))
                throw new InvalidDataException("Invalid native Qwen download asset.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in _plan.Files)
            if (!Regex.IsMatch(file.Path, "^(bin/)?[A-Za-z0-9_.-]+$") || file.Path.Split('/').Any(part => part is "." or "..")
                || !paths.Add(file.Path) || !Hash(file.Sha256, 64) || file.Size <= 0)
                throw new InvalidDataException("Invalid native Qwen installed asset.");
        if (!sourceNames.Contains(Decoder) || !sourceNames.Contains(Projector) || !paths.Contains("bin/llama-server.exe")
            || !paths.Contains(Decoder) || !paths.Contains(Projector)) throw new InvalidDataException("The native Qwen asset plan is incomplete.");
    }

    private static void DeleteOwnedTree(string path, string parent)
    {
        var full = QwenInstaller.RequireLocalPath(path);
        var root = QwenInstaller.RequireLocalPath(parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe native Qwen cleanup path.");
        if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
    }
}
