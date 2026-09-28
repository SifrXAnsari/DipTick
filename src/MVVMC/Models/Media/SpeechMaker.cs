using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using DipTick.Models.Chat;
using DipTick.Models.Providers;
using DipTick.Models.Tools;

namespace DipTick.Models.Media;

/// <summary>
/// Speech made text: whisper.cpp's whisper-server on this PC, as its
/// README and server.cpp give it (read 2026-09-28): <c>POST /inference</c> with the file as multipart
/// (<c>file</c>) and <c>response_format=json</c>, answering <c>{"text"}</c>, or <c>{"error"}</c> with its
/// status. It decodes with miniaudio and stb_vorbis: WAV, FLAC, MP3 and Ogg Vorbis; anything else needs
/// the server started with <c>--convert</c>, which needs ffmpeg. Which one is <c>media.speech</c>.
/// </summary>
internal static class SpeechMaker
{
    /// <summary>What <c>media.speech</c> may name, besides none.</summary>
    public static IReadOnlyList<string> Providers { get; } = ["whispercpp"];

    /// <summary>The most audio sent at once.</summary>
    public const int MostAudio = 200 * 1024 * 1024;

    /// <summary>The most text taken back: a long recording's words fit well inside it.</summary>
    private const int MostText = 8 * 1024 * 1024;

    /// <summary>What whisper-server reads itself.</summary>
    private static readonly string[] Readable = [".wav", ".flac", ".mp3", ".ogg"];

    /// <summary>Who turns speech into text now; null when <c>media.speech</c> is none.</summary>
    public static string? Provider(MasterConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.MediaSpeech == "none" ? null : config.MediaSpeech;
    }

    /// <summary>Whether speech can be made text now.</summary>
    public static bool Ready(MasterConfig config) => Problem(config) is null;

    /// <summary>What turning speech into text still needs, or null.</summary>
    public static string? Problem(MasterConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return Provider(config) is not { } provider ? "Turning speech into text is off: set media.speech to whispercpp, with whisper-server running on this PC."
            : MediaProviders.For(config, provider, config.MediaSpeechAddress) is null ? $"{ProviderCatalogue.Find(provider)?.Name ?? provider} has no address here: set media.speechAddress."
            : null;
    }

    /// <summary>Why a file will not be sent as it is, or null: a kind whisper-server reads itself.</summary>
    public static string? FileProblem(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Readable.Contains(Path.GetExtension(name).ToLowerInvariantAscii(), StringComparer.Ordinal)
            ? null
            : "whisper-server reads WAV, FLAC, MP3 and Ogg Vorbis itself; convert it to one of those first (or start the server with --convert, which needs ffmpeg)";
    }

    /// <summary>Where the audio goes, for the gate and the audit log.</summary>
    public static string Target(MasterConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return Provider(config) is { } provider && MediaProviders.For(config, provider, config.MediaSpeechAddress) is { } where
            ? $"speech: {where.Endpoint.AbsoluteUri.TrimEnd('/')}/inference"
            : "speech";
    }

    /// <summary>The words said in <paramref name="audio"/>, named <paramref name="name"/>; <paramref name="client"/> sends, DipTick's own by default.</summary>
    /// <exception cref="MediaException">They could not be had, and why.</exception>
    public static async Task<string> TranscribeAsync(MasterConfig config, string name, byte[] audio, CancellationToken cancel, HttpClient? client = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(audio);
        if (Problem(config) is { } missing)
        {
            throw new MediaException(missing);
        }

        if (audio.Length > MostAudio)
        {
            throw new MediaException("the recording is larger than 200 MiB, more than DipTick sends at once");
        }

        var provider = Provider(config)!;
        var (endpoint, _) = MediaProviders.For(config, provider, config.MediaSpeechAddress)!.Value;
        using var own = client is null ? OpenAiCompatibleEngine.NewClient() : null;
        var http = client ?? own!;

        // Each part is the form's once added (it disposes them with itself); disposing one again is harmless.
        using var form = new MultipartFormDataContent();
        using var file = new ByteArrayContent(audio);
        using var format = new StringContent("json");
        using var temperature = new StringContent("0.0");
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", Path.GetFileName(name));
        form.Add(format, "response_format");
        form.Add(temperature, "temperature");
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"{endpoint.AbsoluteUri.TrimEnd('/')}/inference")) { Content = form };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var doc = await MediaProviders.JsonAsync(http, request, MostText, cancel).ConfigureAwait(false);
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var error))
        {
            throw new MediaException($"whisper-server could not read it: {OpenAiCompatibleEngine.Clip(error.ToString())}");
        }

        return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String
            ? (text.GetString() ?? string.Empty).Trim()
            : throw new MediaException(string.Create(CultureInfo.InvariantCulture, $"whisper-server answered with no text ({root.ValueKind})"));
    }
}
