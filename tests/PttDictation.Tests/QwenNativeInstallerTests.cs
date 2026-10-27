using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PttDictation.App;

namespace PttDictation.Tests;

[TestClass]
public sealed class QwenNativeInstallerTests
{
    [TestMethod]
    public async Task FreshInstallValidatesPermanentPinnedAssetsBeforeRegistration()
    {
        using var fixture = new Fixture();
        fixture.OnValidate = (_, _, config, _, _) =>
        {
            Assert.IsFalse(File.Exists(QwenInstallation.NativeManifestPath(fixture.Root)));
            using var manifest = JsonDocument.Parse(File.ReadAllText(config));
            var server = manifest.RootElement.GetProperty("serverPath").GetString()!;
            Assert.IsTrue(File.Exists(server));
            Assert.IsFalse(server.Contains(".staging-"));
            Assert.AreEqual("server bytes", File.ReadAllText(server));
            return Task.CompletedTask;
        };
        var result = await fixture.Create().InstallAsync(fixture.Python, fixture.Model, null, CancellationToken.None);
        Assert.AreEqual(QwenInstallation.NativeManifestPath(fixture.Root), result);
        Assert.AreEqual(4, fixture.Requests.Count);
        Assert.AreEqual(1, fixture.Validations);
        using var registered = JsonDocument.Parse(File.ReadAllText(result));
        Assert.AreEqual("Qwen/Qwen3-ASR-1.7B-hf", registered.RootElement.GetProperty("modelId").GetString());
        Assert.IsTrue(File.Exists(registered.RootElement.GetProperty("projectorPath").GetString()));
        Assert.IsFalse(Directory.EnumerateDirectories(fixture.Root, ".staging-*", SearchOption.AllDirectories).Any());
    }

    [TestMethod]
    public async Task FailedWarmupPreservesRegistrationAndReturnsMovedModelsToDownloadCache()
    {
        using var fixture = new Fixture();
        var manifest = QwenInstallation.NativeManifestPath(fixture.Root);
        await File.WriteAllTextAsync(manifest, "previous native registration");
        fixture.OnValidate = (_, _, _, _, _) =>
        {
            Assert.AreEqual("previous native registration", File.ReadAllText(manifest));
            Assert.AreEqual(0, Directory.GetFiles(Path.Combine(fixture.Root, "qwen", "downloads"), "*.gguf").Length,
                "Verified models must move into the permanent tree instead of consuming a second copy's disk space.");
            throw new InvalidOperationException("CUDA warmup failed");
        };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fixture.Create().InstallAsync(fixture.Python, fixture.Model, null, CancellationToken.None));
        Assert.AreEqual("previous native registration", File.ReadAllText(manifest));
        Assert.AreEqual(2, Directory.GetFiles(Path.Combine(fixture.Root, "qwen", "downloads"), "*.gguf").Length);
        Assert.AreEqual(0, Directory.GetDirectories(Path.Combine(fixture.Root, "qwen", "native")).Length);
        Assert.AreEqual(0, Directory.GetFiles(fixture.Root, "*.tmp").Length);
    }

    [TestMethod]
    [DataRow("../escape.txt", 0)]
    [DataRow("..\\escape.txt", 0)]
    [DataRow("C:/escape.txt", 0)]
    [DataRow("unlisted.dll", 0)]
    [DataRow("linked-file", -1610612736)]
    [DataRow("reparse-file", 1024)]
    [DataRow("llama-server.exe", 0)]
    public async Task UnsafeArchiveFailsWithoutChangingRegistration(string entryName, int attributes)
    {
        using var fixture = new Fixture();
        var manifest = QwenInstallation.NativeManifestPath(fixture.Root);
        File.WriteAllText(manifest, "previous native registration");
        fixture.ReplaceSource("llama.zip", Zip(("llama-server.exe", Encoding.UTF8.GetBytes("server bytes"), null),
            ("ggml.dll", Encoding.UTF8.GetBytes("ggml bytes"), null), (entryName, [1], attributes)));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Create().InstallAsync(fixture.Python, fixture.Model, null, CancellationToken.None));
        Assert.AreEqual("previous native registration", File.ReadAllText(manifest));
        Assert.AreEqual(0, fixture.Validations);
        Assert.AreEqual(0, Directory.GetDirectories(Path.Combine(fixture.Root, "qwen", "native")).Length);
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Root, "qwen", "native", "escape.txt")));
    }

    [TestMethod]
    public async Task PinnedArchiveStillRequiresEveryExtractedFileHashToMatch()
    {
        using var fixture = new Fixture();
        fixture.ReplaceSource("llama.zip", Zip(("llama-server.exe", Encoding.UTF8.GetBytes("wrong! bytes"), null),
            ("ggml.dll", Encoding.UTF8.GetBytes("ggml bytes"), null)));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Create().InstallAsync(fixture.Python, fixture.Model, null, CancellationToken.None));
        Assert.AreEqual(0, fixture.Validations);
        Assert.IsFalse(File.Exists(QwenInstallation.NativeManifestPath(fixture.Root)));
        Assert.AreEqual(0, Directory.GetDirectories(Path.Combine(fixture.Root, "qwen", "native")).Length);
    }

    [TestMethod]
    public async Task CancellationAfterWarmupDoesNotCommitOrLeaveOwnedRuntime()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var manifest = QwenInstallation.NativeManifestPath(fixture.Root);
        File.WriteAllText(manifest, "previous native registration");
        fixture.OnValidate = (_, _, _, _, _) => { cancellation.Cancel(); return Task.CompletedTask; };
        await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.Create().InstallAsync(fixture.Python, fixture.Model, null, cancellation.Token));
        Assert.AreEqual("previous native registration", File.ReadAllText(manifest));
        Assert.AreEqual(0, Directory.GetDirectories(Path.Combine(fixture.Root, "qwen", "native")).Length);
        Assert.AreEqual(0, Directory.GetFiles(fixture.Root, "*.tmp").Length);
    }

    [TestMethod]
    public async Task ExistingVerifiedNativeAssetsAreRevalidatedWithoutDownloads()
    {
        using var fixture = new Fixture();
        var installed = await fixture.Create().InstallAsync(fixture.Python, fixture.Model, null, CancellationToken.None);
        using var first = JsonDocument.Parse(File.ReadAllText(installed));
        var server = first.RootElement.GetProperty("serverPath").GetString();
        fixture.Requests.Clear();
        await fixture.Create().InstallAsync(fixture.Python, fixture.Model, null, CancellationToken.None);
        using var second = JsonDocument.Parse(File.ReadAllText(installed));
        Assert.AreEqual(server, second.RootElement.GetProperty("serverPath").GetString());
        Assert.AreEqual(0, fixture.Requests.Count);
        Assert.AreEqual(2, fixture.Validations);
    }

    [TestMethod]
    public async Task UnexpectedDllInExistingRuntimeIsNeverLoadedDuringRepair()
    {
        using var fixture = new Fixture();
        var manifest = await fixture.Create().InstallAsync(fixture.Python, fixture.Model, null, CancellationToken.None);
        using var first = JsonDocument.Parse(File.ReadAllText(manifest));
        var oldServer = first.RootElement.GetProperty("serverPath").GetString()!;
        var extraDll = Path.Combine(Path.GetDirectoryName(oldServer)!, "unexpected.dll");
        File.WriteAllText(extraDll, "not a validated library");
        fixture.OnValidate = (_, _, config, _, _) =>
        {
            using var candidate = JsonDocument.Parse(File.ReadAllText(config));
            var server = candidate.RootElement.GetProperty("serverPath").GetString()!;
            Assert.AreNotEqual(oldServer, server);
            Assert.IsFalse(File.Exists(Path.Combine(Path.GetDirectoryName(server)!, "unexpected.dll")));
            Assert.IsTrue(File.Exists(oldServer), "Repair must not mutate the prior registered runtime.");
            return Task.CompletedTask;
        };
        await fixture.Create().InstallAsync(fixture.Python, fixture.Model, null, CancellationToken.None);
        Assert.IsTrue(File.Exists(extraDll));
        Assert.AreEqual(2, fixture.Validations);
    }

    [TestMethod]
    public async Task CorruptDownloadCannotReachValidationOrReplaceRegistration()
    {
        using var fixture = new Fixture();
        var manifest = QwenInstallation.NativeManifestPath(fixture.Root);
        File.WriteAllText(manifest, "previous native registration");
        fixture.OnRequest = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2]) });
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Create().InstallAsync(fixture.Python, fixture.Model, null, CancellationToken.None));
        Assert.AreEqual("previous native registration", File.ReadAllText(manifest));
        Assert.AreEqual(0, fixture.Validations);
        Assert.AreEqual(0, Directory.GetDirectories(Path.Combine(fixture.Root, "qwen", "native")).Length);
    }

    [TestMethod]
    public async Task CancelledDownloadRemovesOnlyOwnedStaging()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var manifest = QwenInstallation.NativeManifestPath(fixture.Root);
        File.WriteAllText(manifest, "previous native registration");
        fixture.OnRequest = (_, _) =>
        {
            cancellation.Cancel();
            return Task.FromCanceled<HttpResponseMessage>(cancellation.Token);
        };
        await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.Create().InstallAsync(fixture.Python, fixture.Model, null, cancellation.Token));
        Assert.AreEqual("previous native registration", File.ReadAllText(manifest));
        Assert.AreEqual(0, fixture.Validations);
        Assert.AreEqual(0, Directory.GetDirectories(Path.Combine(fixture.Root, "qwen", "native")).Length);
        Assert.IsTrue(File.Exists(fixture.Python));
    }

    [TestMethod]
    public async Task FailedRevalidationRetainsPreviouslyRegisteredAssetsByteForByte()
    {
        using var fixture = new Fixture();
        var manifest = await fixture.Create().InstallAsync(fixture.Python, fixture.Model, null, CancellationToken.None);
        var previous = File.ReadAllBytes(manifest);
        using var old = JsonDocument.Parse(previous);
        var decoder = old.RootElement.GetProperty("modelPath").GetString()!;
        var model = File.ReadAllBytes(decoder);
        fixture.OnValidate = (_, _, _, _, _) => throw new InvalidOperationException("GPU unavailable");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fixture.Create().InstallAsync(fixture.Python, fixture.Model, null, CancellationToken.None));
        CollectionAssert.AreEqual(previous, File.ReadAllBytes(manifest));
        CollectionAssert.AreEqual(model, File.ReadAllBytes(decoder));
        Assert.AreEqual(0, Directory.GetFiles(fixture.Root, "*.tmp").Length);
    }

    [TestMethod]
    public async Task ActivationGuardContainsValidationAndRegistrationCommitButNotDownloads()
    {
        using var fixture = new Fixture();
        var guarded = false;
        var target = QwenInstallation.NativeManifestPath(fixture.Root);
        fixture.OnValidate = (_, _, _, _, _) =>
        {
            Assert.IsTrue(guarded, "Actual model validation must hold the shared worker gate.");
            Assert.IsFalse(File.Exists(target));
            return Task.CompletedTask;
        };
        fixture.OnGuard = async (activate, token) =>
        {
            Assert.AreEqual(4, fixture.Requests.Count, "Downloads must finish before reserving the shared worker.");
            guarded = true;
            await activate(token);
            Assert.IsTrue(File.Exists(target), "Queued dictation must not reload the old manifest before commit.");
            guarded = false;
        };
        await fixture.Create().InstallAsync(fixture.Python, fixture.Model, null, CancellationToken.None);
        Assert.IsFalse(guarded);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static byte[] Zip(params (string Name, byte[] Bytes, int? Attributes)[] entries)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, bytes, attributes) in entries)
            {
                var entry = archive.CreateEntry(name);
                if (attributes is { } value) entry.ExternalAttributes = value;
                using var stream = entry.Open();
                stream.Write(bytes);
            }
        return output.ToArray();
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "qwen-native-setup-" + Guid.NewGuid().ToString("N"));
        public string Python => Path.Combine(Root, "python.exe");
        public string Model => Path.Combine(Root, "hf-model");
        public Dictionary<string, byte[]> Sources { get; }
        public QwenNativeInstallerPlan Plan { get; set; }
        public List<string> Requests { get; } = [];
        public int Validations { get; private set; }
        public Func<string, string, string, IProgress<QwenSetupProgress>?, CancellationToken, Task>? OnValidate { get; set; }
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? OnRequest { get; set; }
        public Func<Func<CancellationToken, Task>, CancellationToken, Task> OnGuard { get; set; } = (action, token) => action(token);
        private readonly HttpClient _http;
        private readonly QwenInstaller _downloader;
        public Fixture()
        {
            Directory.CreateDirectory(Model);
            File.WriteAllText(Python, "placeholder");
            var files = new Dictionary<string, byte[]>
            {
                ["bin/llama-server.exe"] = Encoding.UTF8.GetBytes("server bytes"),
                ["bin/ggml.dll"] = Encoding.UTF8.GetBytes("ggml bytes"),
                ["bin/cudart.dll"] = Encoding.UTF8.GetBytes("cuda bytes"),
                ["Qwen3-ASR-1.7B-bf16.gguf"] = Encoding.UTF8.GetBytes("model bytes"),
                ["mmproj-Qwen3-ASR-1.7B-bf16.gguf"] = Encoding.UTF8.GetBytes("projector bytes")
            };
            Sources = new()
            {
                ["llama.zip"] = Zip(("llama-server.exe", files["bin/llama-server.exe"], null), ("ggml.dll", files["bin/ggml.dll"], null)),
                ["cudart.zip"] = Zip(("cudart.dll", files["bin/cudart.dll"], null)),
                ["Qwen3-ASR-1.7B-bf16.gguf"] = files["Qwen3-ASR-1.7B-bf16.gguf"],
                ["mmproj-Qwen3-ASR-1.7B-bf16.gguf"] = files["mmproj-Qwen3-ASR-1.7B-bf16.gguf"]
            };
            Plan = new(1, new string('a', 40), new string('b', 40),
                Sources.Select(x => new QwenNativeSource(x.Key, "https://github.com/test/" + x.Key, Hash(x.Value), x.Value.Length)).ToArray(),
                files.Select(x => new QwenNativeAsset(x.Key, x.Value.Length, Hash(x.Value))).ToArray());
            _http = new(new Handler(async (request, token) =>
            {
                Requests.Add(request.RequestUri!.AbsoluteUri);
                if (OnRequest is not null) return await OnRequest(request, token);
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(Sources[Path.GetFileName(request.RequestUri.AbsolutePath)]) };
            }));
            _downloader = new(Root, new("https://github.com/test/python", Hash([]), 0, "fixture", "fixture", []), _http,
                (_, _, _, _) => Task.CompletedTask);
        }
        public QwenNativeInstaller Create() => new(Root, _downloader, Plan, async (python, model, config, progress, token) =>
        {
            Validations++;
            if (OnValidate is not null) await OnValidate(python, model, config, progress, token);
        }) { ActivationGuard = OnGuard };
        public void ReplaceSource(string name, byte[] bytes)
        {
            Sources[name] = bytes;
            Plan = Plan with { Sources = Plan.Sources.Select(source => source.Name == name
                ? source with { Sha256 = Hash(bytes), Size = bytes.Length } : source).ToArray() };
        }
        public void Dispose() { _http.Dispose(); Directory.Delete(Root, recursive: true); }
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request, cancellationToken);
    }
}
