using System.IO.Compression;

namespace PttDictation.Core;

public sealed record RuntimeArchiveInfo(Uri DownloadUrl, string Sha256, string FileName);

public sealed record RuntimeAssetInfo(
    string Id,
    Uri DownloadUrl,
    string Sha256,
    string FileName,
    DevicePreference DevicePreference)
{
    public IReadOnlyList<RuntimeArchiveInfo> AdditionalArchives { get; init; } = [];
}

public sealed class RuntimeAssetRegistry(RuntimeAssetInfo cuda, RuntimeAssetInfo cpu, string releaseTag)
{
    public RuntimeAssetInfo Cuda { get; } = cuda;
    public RuntimeAssetInfo Cpu { get; } = cpu;
    public string ReleaseTag { get; } = releaseTag;

    public RuntimeAssetInfo For(DevicePreference preference)
    {
        return preference == DevicePreference.Cpu ? Cpu : Cuda;
    }

    public static RuntimeAssetRegistry CreateDefault()
    {
        const string releaseTag = "v0.4.0";
        return new RuntimeAssetRegistry(
            new RuntimeAssetInfo(
                "win-cuda-x64",
                new Uri("https://github.com/mudler/parakeet.cpp/releases/download/v0.4.0/parakeet-v0.4.0-bin-win-cuda-x64.zip"),
                "2a377eeb7f92e0d0cd28df768750a8132296de0c07454ad908d5eaceeb9ad5e4",
                "parakeet-v0.4.0-bin-win-cuda-x64.zip",
                DevicePreference.Cuda)
            {
                AdditionalArchives =
                [
                    new RuntimeArchiveInfo(
                        new Uri("https://github.com/mudler/parakeet.cpp/releases/download/v0.4.0/cudart-parakeet-bin-win-cuda-x64.zip"),
                        "cc2b5fb99951720130e4a701e0978419d0a878e25c88bebc1416152616bd1d94",
                        "cudart-parakeet-bin-win-cuda-x64.zip")
                ]
            },
            new RuntimeAssetInfo(
                "win-cpu-x64",
                new Uri("https://github.com/mudler/parakeet.cpp/releases/download/v0.4.0/parakeet-v0.4.0-bin-win-cpu-x64.zip"),
                "2880150a1bad2944baed46f2e6bb9f1bc55263a9f2bb85573785a7ec4fa35f27",
                "parakeet-v0.4.0-bin-win-cpu-x64.zip",
                DevicePreference.Cpu),
            releaseTag);
    }
}

public interface IFileDownloader
{
    Task DownloadAsync(Uri source, string destinationPath, CancellationToken cancellationToken);
}

public sealed record FileDownloadProgress(Uri Source, long BytesReceived, long? TotalBytes)
{
    public int? Percent => TotalBytes is > 0
        ? (int)Math.Clamp(BytesReceived * 100d / TotalBytes.Value, 0d, 100d)
        : null;
}

public sealed class HttpFileDownloader(Action<FileDownloadProgress>? progressReady = null) : IFileDownloader
{
    private readonly HttpClient _httpClient = new();

    public async Task DownloadAsync(Uri source, string destinationPath, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, source);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength;
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = new FileStream(
            destinationPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var buffer = new byte[128 * 1024];
        long bytesReceived = 0;
        long lastReportedBytes = -1;
        int? lastReportedPercent = null;
        ReportProgress(source, bytesReceived, totalBytes);

        while (true)
        {
            var bytesRead = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (bytesRead == 0)
            {
                break;
            }

            await output.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
            bytesReceived += bytesRead;

            var progress = new FileDownloadProgress(source, bytesReceived, totalBytes);
            var shouldReport = progress.Percent is { } percent
                ? percent != lastReportedPercent
                : bytesReceived - lastReportedBytes >= 1024 * 1024;
            if (shouldReport)
            {
                ReportProgress(source, bytesReceived, totalBytes);
                lastReportedBytes = bytesReceived;
                lastReportedPercent = progress.Percent;
            }
        }

        if (bytesReceived != lastReportedBytes)
        {
            ReportProgress(source, bytesReceived, totalBytes);
        }
    }

    private void ReportProgress(Uri source, long bytesReceived, long? totalBytes)
    {
        try
        {
            progressReady?.Invoke(new FileDownloadProgress(source, bytesReceived, totalBytes));
        }
        catch (Exception)
        {
        }
    }
}

public sealed class AssetManager(string rootDirectory, IFileDownloader downloader)
{
    public async Task<string> EnsureRuntimeAsync(RuntimeAssetInfo asset, CancellationToken cancellationToken)
    {
        var runtimeDirectory = Path.Combine(rootDirectory, "runtimes", asset.Id);
        Directory.CreateDirectory(runtimeDirectory);

        await DownloadVerifyExtractAsync(
            new RuntimeArchiveInfo(asset.DownloadUrl, asset.Sha256, asset.FileName),
            runtimeDirectory,
            cancellationToken);

        foreach (var archive in asset.AdditionalArchives)
        {
            await DownloadVerifyExtractAsync(archive, runtimeDirectory, cancellationToken);
        }

        return FindCli(runtimeDirectory)
            ?? throw new FileNotFoundException("Runtime archive did not contain parakeet-cli.exe.", runtimeDirectory);
    }

    public async Task<string> EnsureModelAsync(ModelInfo model, CancellationToken cancellationToken)
    {
        var modelsDirectory = Path.Combine(rootDirectory, "models");
        Directory.CreateDirectory(modelsDirectory);
        var path = Path.Combine(modelsDirectory, Path.GetFileName(model.DownloadUrl.LocalPath));

        if (await IsUsableModelAsync(path, model, cancellationToken))
        {
            return path;
        }

        await DownloadAtomicallyAsync(model.DownloadUrl, path, cancellationToken);
        if (new FileInfo(path).Length < model.MinimumBytes)
        {
            File.Delete(path);
            throw new InvalidOperationException($"Downloaded model is smaller than expected: {model.DisplayName}");
        }

        if (model.Sha256 is not null && !await ChecksumVerifier.VerifySha256Async(path, model.Sha256, cancellationToken))
        {
            File.Delete(path);
            throw new InvalidOperationException($"Downloaded model failed SHA-256 verification: {model.DisplayName}");
        }

        return path;
    }

    private static string? FindCli(string runtimeDirectory)
    {
        if (!Directory.Exists(runtimeDirectory))
        {
            return null;
        }

        return Directory.EnumerateFiles(runtimeDirectory, "parakeet-cli.exe", SearchOption.AllDirectories)
            .FirstOrDefault();
    }

    private async Task DownloadVerifyExtractAsync(RuntimeArchiveInfo archive, string runtimeDirectory, CancellationToken cancellationToken)
    {
        var markerPath = Path.Combine(runtimeDirectory, $".{archive.FileName}.sha256");
        var manifestPath = Path.Combine(runtimeDirectory, $".{archive.FileName}.manifest");
        if (File.Exists(markerPath)
            && string.Equals(await File.ReadAllTextAsync(markerPath, cancellationToken), archive.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            if (await ManifestMatchesAsync(manifestPath, runtimeDirectory, cancellationToken))
            {
                return;
            }
        }

        var downloadsDirectory = Path.Combine(rootDirectory, "downloads");
        Directory.CreateDirectory(downloadsDirectory);
        var zipPath = Path.Combine(downloadsDirectory, archive.FileName);

        if (!File.Exists(zipPath) || !await ChecksumVerifier.VerifySha256Async(zipPath, archive.Sha256, cancellationToken))
        {
            await DownloadAtomicallyAsync(archive.DownloadUrl, zipPath, cancellationToken);
        }

        if (!await ChecksumVerifier.VerifySha256Async(zipPath, archive.Sha256, cancellationToken))
        {
            throw new InvalidOperationException($"Downloaded runtime asset failed SHA-256 verification: {archive.FileName}");
        }

        ValidateArchiveEntries(zipPath, runtimeDirectory);
        ZipFile.ExtractToDirectory(zipPath, runtimeDirectory, overwriteFiles: true);
        await File.WriteAllTextAsync(markerPath, archive.Sha256, cancellationToken);
        await WriteManifestAsync(zipPath, manifestPath, runtimeDirectory, cancellationToken);
    }

    private async Task DownloadAtomicallyAsync(Uri source, string destinationPath, CancellationToken cancellationToken)
    {
        var tempPath = destinationPath + ".download";
        if (File.Exists(tempPath))
        {
            File.Delete(tempPath);
        }

        try
        {
            await downloader.DownloadAsync(source, tempPath, cancellationToken);
            if (File.Exists(destinationPath))
            {
                File.Delete(destinationPath);
            }

            File.Move(tempPath, destinationPath);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static async Task<bool> IsUsableModelAsync(string path, ModelInfo model, CancellationToken cancellationToken)
    {
        if (!File.Exists(path) || new FileInfo(path).Length < model.MinimumBytes)
        {
            return false;
        }

        return model.Sha256 is null || await ChecksumVerifier.VerifySha256Async(path, model.Sha256, cancellationToken);
    }

    private static async Task<bool> ManifestMatchesAsync(string manifestPath, string runtimeDirectory, CancellationToken cancellationToken)
    {
        if (!File.Exists(manifestPath))
        {
            return false;
        }

        foreach (var line in await File.ReadAllLinesAsync(manifestPath, cancellationToken))
        {
            var separator = line.IndexOf(' ');
            if (separator != 64)
            {
                return false;
            }

            var expectedSha256 = line[..separator];
            var relativePath = line[(separator + 1)..];
            var targetPath = GetSafeTargetPath(runtimeDirectory, relativePath);
            if (!File.Exists(targetPath)
                || !await ChecksumVerifier.VerifySha256Async(targetPath, expectedSha256, cancellationToken))
            {
                return false;
            }
        }

        return true;
    }

    private static async Task WriteManifestAsync(
        string zipPath,
        string manifestPath,
        string runtimeDirectory,
        CancellationToken cancellationToken)
    {
        var lines = new List<string>();
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var entry in zip.Entries.Where(entry => !IsDirectoryEntry(entry)))
        {
            var targetPath = GetSafeTargetPath(runtimeDirectory, entry.FullName);
            var sha256 = await ChecksumVerifier.ComputeSha256Async(targetPath, cancellationToken);
            lines.Add($"{sha256} {entry.FullName.Replace('\\', '/')}");
        }

        await File.WriteAllLinesAsync(manifestPath, lines, cancellationToken);
    }

    private static void ValidateArchiveEntries(string zipPath, string runtimeDirectory)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var entry in zip.Entries.Where(entry => !IsDirectoryEntry(entry)))
        {
            GetSafeTargetPath(runtimeDirectory, entry.FullName);
        }
    }

    private static string GetSafeTargetPath(string runtimeDirectory, string relativePath)
    {
        var targetPath = Path.GetFullPath(Path.Combine(runtimeDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var relativeToRuntime = Path.GetRelativePath(runtimeDirectory, targetPath);
        if (Path.IsPathRooted(relativeToRuntime)
            || string.Equals(relativeToRuntime, "..", StringComparison.Ordinal)
            || relativeToRuntime.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Runtime archive entry escapes the runtime directory: {relativePath}");
        }

        return targetPath;
    }

    private static bool IsDirectoryEntry(ZipArchiveEntry entry)
    {
        return entry.FullName.EndsWith("/", StringComparison.Ordinal);
    }
}
