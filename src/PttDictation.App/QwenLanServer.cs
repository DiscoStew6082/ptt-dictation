using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NAudio.Wave;
using PttDictation.Core;

namespace PttDictation.App;

internal sealed record QwenLanOptions(string Address, int Port, string ApiKey, string FfmpegPath)
{
    public const string FileName = "qwen-lan.json";

    public IPAddress Validate()
    {
        if (!IPAddress.TryParse(Address, out var address) || address.AddressFamily != AddressFamily.InterNetwork)
            throw new InvalidDataException("Qwen LAN requires an explicit private IPv4 address.");
        var bytes = address.GetAddressBytes();
        if (!(IPAddress.IsLoopback(address) || bytes[0] == 10 ||
              (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) || (bytes[0] == 192 && bytes[1] == 168)))
            throw new InvalidDataException("Qwen LAN must bind to a private IPv4 address, not all interfaces.");
        if (Port is < 1024 or > 65535 || string.IsNullOrWhiteSpace(ApiKey) || ApiKey.Length < 32 || ApiKey.Length > 128 ||
            ApiKey.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_'))
            throw new InvalidDataException("Qwen LAN requires a port above 1023 and a strong URL-safe access key.");
        if (!Path.IsPathFullyQualified(FfmpegPath) || FfmpegPath.StartsWith(@"\\") || !File.Exists(FfmpegPath))
            throw new InvalidDataException("Qwen LAN requires an installed local FFmpeg executable.");
        return address;
    }
}

/// <summary>Opt-in LAN adapter. Shares the desktop recognizer; never captures or pastes on the PC.</summary>
internal sealed class QwenLanServer : IAsyncDisposable
{
    public const string ModelId = "qwen3-asr-1.7b";
    internal const long MaxUploadBytes = 20 * 1024 * 1024;
    private readonly QwenLanOptions _options;
    private readonly Func<string, CancellationToken, Task<TranscriptResult>> _transcribe;
    private readonly Func<IFormFile, string, CancellationToken, Task> _decode;
    private readonly string _temporaryRoot;
    private readonly SemaphoreSlim _phoneSlot = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private WebApplication? _app;

    public QwenLanServer(QwenLanOptions options, string temporaryRoot,
        Func<string, CancellationToken, Task<TranscriptResult>> transcribe,
        Func<IFormFile, string, CancellationToken, Task>? decode = null)
    {
        options.Validate();
        _options = options;
        _temporaryRoot = temporaryRoot;
        _transcribe = transcribe;
        _decode = decode ?? DecodeAsync;
    }

    public async Task StartAsync(CancellationToken token = default)
    {
        // Slim builder avoids unrelated configuration, launch settings, and desktop console lifetime.
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders(); // Never log authorization headers, audio, or transcripts.
        builder.WebHost.ConfigureKestrel(server =>
        {
            server.Listen(_options.Validate(), _options.Port);
            server.Limits.MaxRequestBodySize = MaxUploadBytes;
            server.Limits.MaxConcurrentConnections = 8;
            server.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
            server.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(30);
        });
        builder.Services.Configure<FormOptions>(form =>
        {
            form.MultipartBodyLengthLimit = MaxUploadBytes;
            form.MemoryBufferThreshold = (int)MaxUploadBytes;
            form.ValueLengthLimit = 4096;
            form.ValueCountLimit = 16;
            form.MultipartHeadersCountLimit = 16;
        });
        var app = builder.Build();
        _app = app;
        app.Run(HandleAsync);
        await app.StartAsync(token).ConfigureAwait(false);
    }

    private bool Authorized(HttpContext context)
    {
        var supplied = Encoding.UTF8.GetBytes(context.Request.Headers.Authorization.ToString());
        var expected = Encoding.UTF8.GetBytes("Bearer " + _options.ApiKey);
        return CryptographicOperations.FixedTimeEquals(supplied, expected);
    }

    private async Task HandleAsync(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!Authorized(context)) { await Error(context, 401, "An access key is required."); return; }
        if (context.Request.Method == "GET" && context.Request.Path is var path &&
            (path == "/v1/models" || path == "/v1/audio/models"))
        {
            await context.Response.WriteAsJsonAsync(new { @object = "list", data = new[] {
                new { id = ModelId, @object = "model", owned_by = "local", created = 0 } } });
            return;
        }
        if (context.Request.Method != "POST" || context.Request.Path != "/v1/audio/transcriptions")
        { await Error(context, 404, "Unknown endpoint."); return; }
        if (!await _phoneSlot.WaitAsync(0, context.RequestAborted))
        { await Error(context, 429, "Another phone recording is being processed. Please retry."); return; }
        string? directory = null;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, _shutdown.Token);
        operation.CancelAfter(TimeSpan.FromMinutes(12));
        try
        {
            if (!context.Request.HasFormContentType)
            { await Error(context, 400, "Send a multipart audio file and model."); return; }
            var form = await context.Request.ReadFormAsync(operation.Token);
            if (form["model"] != ModelId)
            { await Error(context, 400, "Select model " + ModelId + "."); return; }
            var format = form["response_format"].ToString();
            if (format != "" && format != "json" && format != "text")
            { await Error(context, 400, "Supported response formats: json, text."); return; }
            var language = form["language"].ToString();
            if (language != "" && !language.Equals("en", StringComparison.OrdinalIgnoreCase) &&
                !language.Equals("English", StringComparison.OrdinalIgnoreCase))
            { await Error(context, 400, "This Qwen worker is configured for English."); return; }
            if (form["stream"].ToString().Equals("true", StringComparison.OrdinalIgnoreCase))
            { await Error(context, 400, "Record first, then transcribe; streaming is not supported."); return; }
            var file = form.Files.GetFile("file");
            if (file is null || file.Length == 0 || file.Length > MaxUploadBytes || form.Files.Count != 1)
            { await Error(context, 400, "Send exactly one nonempty audio file, at most 20 MiB."); return; }
            directory = Path.Combine(_temporaryRoot, "request-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var wav = Path.Combine(directory, "audio.wav");
            await _decode(file, wav, operation.Token).ConfigureAwait(false);
            var result = await _transcribe(wav, operation.Token).ConfigureAwait(false);
            operation.Token.ThrowIfCancellationRequested();
            if (format == "text")
            {
                context.Response.ContentType = "text/plain; charset=utf-8";
                await context.Response.WriteAsync(result.Text, operation.Token);
            }
            else await context.Response.WriteAsJsonAsync(new { text = result.Text }, operation.Token);
        }
        catch (BadHttpRequestException error) { await Error(context, error.StatusCode, "Audio upload was rejected."); }
        catch (InvalidDataException) { await Error(context, 400, "Invalid audio or recording longer than five minutes."); }
        catch (OperationCanceledException)
        {
            if (!context.RequestAborted.IsCancellationRequested) await Error(context, 504, "Transcription cancelled or timed out.");
        }
        catch (Exception error)
        {
            DiagnosticTrace.Write("qwen.lan_request_failed", new { errorType = error.GetType().Name });
            await Error(context, 503, "Qwen transcription failed. Check the PC and retry.");
        }
        finally
        {
            if (directory is not null)
            {
                try { Directory.Delete(directory, recursive: true); }
                catch (IOException) { DiagnosticTrace.Write("qwen.lan_audio_cleanup_failed"); }
                catch (UnauthorizedAccessException) { DiagnosticTrace.Write("qwen.lan_audio_cleanup_failed"); }
            }
            _phoneSlot.Release();
        }
    }

    private async Task DecodeAsync(IFormFile file, string destination, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        // Android M4A may put its index after the audio; FFmpeg needs a seekable input.
        var upload = Path.Combine(Path.GetDirectoryName(destination)!, "upload.audio");
        await using (var stream = File.Create(upload)) await file.CopyToAsync(stream, timeout.Token);
        var start = new ProcessStartInfo(_options.FfmpegPath)
        { UseShellExecute = false, CreateNoWindow = true,
          RedirectStandardOutput = true, RedirectStandardError = true };
        // No network protocols or playlist demuxers. MOV external data references remain disabled (default).
        foreach (var argument in new[] { "-hide_banner", "-loglevel", "error", "-nostdin",
            "-protocol_whitelist", "file", "-format_whitelist", "wav,ogg,mov,mp3,flac,matroska,webm,aac",
            "-i", upload, "-map", "0:a:0", "-vn", "-t", "301", "-ac", "1", "-ar", "16000",
            "-c:a", "pcm_s16le", "-y", destination }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Cannot start audio decoder.");
        using var kill = timeout.Token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        });
        var errors = process.StandardError.ReadToEndAsync(timeout.Token);
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            await errors;
            await output;
            if (process.ExitCode != 0) throw new InvalidDataException("Unsupported or malformed audio.");
            using var wav = new WaveFileReader(destination);
            if (wav.TotalTime > TimeSpan.FromMinutes(5) || wav.Length == 0)
                throw new InvalidDataException("Recording is empty or exceeds five minutes.");
        }
        catch (IOException) when (!timeout.IsCancellationRequested)
        { throw new InvalidDataException("Unsupported or malformed audio."); }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); }
            try { await Task.WhenAll(errors, output); } catch (OperationCanceledException) { }
        }
    }

    private static async Task Error(HttpContext context, int status, string message)
    {
        if (context.Response.HasStarted || context.RequestAborted.IsCancellationRequested) return;
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new { error = new { message, type = "invalid_request_error" } });
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        if (_app is not null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await _app.StopAsync(timeout.Token).ConfigureAwait(false); }
            finally { await _app.DisposeAsync().ConfigureAwait(false); }
        }
    }

    internal static void DeleteAbandonedAudio(string root)
    {
        if (!Directory.Exists(root) || File.GetAttributes(root).HasFlag(FileAttributes.ReparsePoint)) return;
        foreach (var directory in Directory.GetDirectories(root, "request-*"))
        {
            if (!Guid.TryParseExact(Path.GetFileName(directory)[8..], "N", out _) ||
                File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint)) continue;
            try
            {
                File.Delete(Path.Combine(directory, "audio.wav"));
                File.Delete(Path.Combine(directory, "upload.audio"));
                Directory.Delete(directory); // Never recursively remove unexpected content.
            }
            catch (IOException) { DiagnosticTrace.Write("qwen.lan_audio_cleanup_failed"); }
            catch (UnauthorizedAccessException) { DiagnosticTrace.Write("qwen.lan_audio_cleanup_failed"); }
        }
    }

    public static async Task RunConfiguredAsync(string appData,
        Func<string, CancellationToken, Task<TranscriptResult>> transcribe, CancellationToken token)
    {
        var configuration = Path.Combine(appData, QwenLanOptions.FileName);
        if (!File.Exists(configuration)) return; // Off unless explicitly configured locally.
        try
        {
            var options = JsonSerializer.Deserialize<QwenLanOptions>(await File.ReadAllTextAsync(configuration, token),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidDataException("Missing Qwen LAN configuration.");
            var audioRoot = Path.Combine(appData, "lan-audio");
            DeleteAbandonedAudio(audioRoot);
            await using var server = new QwenLanServer(options, audioRoot, transcribe);
            await server.StartAsync(token).ConfigureAwait(false);
            DiagnosticTrace.Write("qwen.lan_started", new { options.Address, options.Port });
            await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { DiagnosticTrace.Write("qwen.lan_start_failed", new { errorType = error.GetType().Name }); }
    }
}
