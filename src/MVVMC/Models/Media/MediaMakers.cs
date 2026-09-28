using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DipTick.Models.Chat;
using DipTick.Models.Keys;
using DipTick.Models.Providers;
using DipTick.Models.Tools;

namespace DipTick.Models.Media;

/// <summary>Why an image or a video could not be made, in words for the person and the model.</summary>
internal sealed class MediaException : Exception
{
    public MediaException()
    {
    }

    public MediaException(string message)
        : base(message)
    {
    }

    public MediaException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>An image made: its bytes, its type, who made it with which model, and its size as asked.</summary>
internal sealed record MadeImage(byte[] Bytes, string MediaType, string Provider, string Model, string Size);

/// <summary>A video made: its bytes, the model, its length and resolution, whether it stands upright, and its media type (Veo's an MP4; others as they come).</summary>
internal sealed record MadeVideo(byte[] Bytes, string Model, int Seconds, string Resolution, bool Portrait, string MediaType = "video/mp4")
{
    /// <summary>The extension its file takes, from its media type.</summary>
    public string Extension => MediaType switch
    {
        "video/webm" => ".webm",
        "video/x-matroska" => ".mkv",
        "video/x-msvideo" or "video/avi" => ".avi",
        "image/webp" => ".webp",
        _ => ".mp4",
    };
}

/// <summary>
/// Where a provider that makes images or video is reached, and with which key:
/// the section that names it (main or a seat), with its own address and key; else the provider's own
/// address and the key saved for its host. DipTick has no key of its own.
/// </summary>
internal static class MediaProviders
{
    /// <summary>
    /// The provider's address and key finder, or null when DipTick knows no address for it. A provider that
    /// makes but does not chat is reached at <paramref name="setAddress"/> when the person set one
    /// (<c>media.imageAddress</c> and the like), else at its catalogue address; its key is the one saved for
    /// that host.
    /// </summary>
    public static (Uri Endpoint, KeyFinder Keys)? For(MasterConfig config, string provider, string setAddress = "")
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(setAddress);
        if (ProviderCatalogue.Find(provider) is { WorksNow: false } entry)
        {
            var at = setAddress.Length > 0 && Uri.TryCreate(setAddress, UriKind.Absolute, out var given) ? given : entry.Address;
            return at is null ? null : (at, Engines.KeysFor("media", provider, at, string.Empty, Keys.WindowsKeyStore.Instance, Environment.GetEnvironmentVariable));
        }

        if (string.Equals(config.MainProvider, provider, StringComparison.OrdinalIgnoreCase)
            && Engines.Endpoint(provider, config.MainEndpoint) is { } main && Engines.KeysFor(config) is { } mainKeys)
        {
            return (main, mainKeys);
        }

        foreach (var seat in config.Seats.Where(s => string.Equals(s.Provider, provider, StringComparison.OrdinalIgnoreCase)))
        {
            if (Engines.Endpoint(seat.Provider, seat.Endpoint) is { } address && Engines.KeysFor(seat) is { } keys)
            {
                return (address, keys);
            }
        }

        return Engines.Endpoint(provider, string.Empty) is { } own && Engines.KeysFor("media", provider, string.Empty, string.Empty) is { } found ? (own, found) : null;
    }

    /// <summary>Whether DipTick has a key it may send to this provider.</summary>
    public static bool HasKey(MasterConfig config, string provider) => For(config, provider) is { } where && where.Keys.Describe().Usable;

    /// <summary>The key, taken now; the provider's refusal words when there is none.</summary>
    /// <exception cref="MediaException">No key.</exception>
    public static async Task<string?> KeyAsync(KeyFinder keys, CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(keys);
        try
        {
            return await keys.TakeAsync(cancel).ConfigureAwait(false);
        }
        catch (ChatException ex)
        {
            throw new MediaException(ex.Message, ex);
        }
    }

    /// <summary>A JSON answer, capped; a refusal made words.</summary>
    /// <exception cref="MediaException">Refused, unreachable, or larger than <paramref name="most"/>.</exception>
    public static async Task<JsonDocument> JsonAsync(HttpClient http, HttpRequestMessage request, int most, CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(request);
        var host = request.RequestUri?.Host ?? "the provider";
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var refusal = await OpenAiCompatibleEngine.Refusal(host, response, cancel).ConfigureAwait(false);
                throw new MediaException(refusal.Contains("moderation_blocked", StringComparison.Ordinal) ? $"{host}'s filter refused it (moderation_blocked). Say it another way." : refusal);
            }

            var body = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
            await using var bodyScope = body.ConfigureAwait(false);
            using var kept = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = await body.ReadAsync(buffer, cancel).ConfigureAwait(false)) > 0)
            {
                if (kept.Length + read > most)
                {
                    throw new MediaException($"{host}'s answer was larger than DipTick reads");
                }

                await kept.WriteAsync(buffer.AsMemory(0, read), cancel).ConfigureAwait(false);
            }

            return JsonDocument.Parse(kept.ToArray());
        }
        catch (HttpRequestException ex)
        {
            throw new MediaException($"could not reach {host}: {SafeText.Line(ex.Message)}", ex);
        }
        catch (JsonException ex)
        {
            throw new MediaException($"{host}'s answer was not JSON", ex);
        }
    }
}

/// <summary>
/// Images from models: OpenAI's Images API (<c>POST {base}/images/generations</c>,
/// base64 back; sizes as its image guide gives them) or the Gemini API's image models through
/// <c>generateContent</c>, whose image comes back as an <c>inlineData</c> part; and Nscale
/// in OpenAI's shape, as its own docs give it, and Replicate, fal, WaveSpeedAI, ComfyUI and
/// stable-diffusion.cpp through their own APIs (<see cref="ProviderMedia"/>). Who makes them is
/// <c>media.images</c>; the model <c>media.imageModel</c>.
/// </summary>
internal static class ImageMaker
{
    public const string OpenAiModel = "gpt-image-2.5-flare";
    public const string GoogleModel = "gemini-3.1-flash-image";

    /// <summary>Every provider <c>media.images</c> may name, besides auto and none.</summary>
    public static IReadOnlyList<string> Providers { get; } = ["openai", "google", "nscale", .. ProviderMedia.Own];

    /// <summary>Those whose model the person must name: the API has no default DipTick could choose.</summary>
    private static readonly string[] Named = ["nscale", "replicate", "fal", "wavespeed"];

    /// <summary>The most an answer with an image may be: a 4K PNG in base64 fits well inside it.</summary>
    private const int MostAnswer = 64 * 1024 * 1024;

    /// <summary>What Google's image models are asked to answer with: words and an image.</summary>
    private static readonly string[] TextAndImage = ["TEXT", "IMAGE"];

    /// <summary>Gemini's image aspect ratios (its API reference, ImageConfig).</summary>
    private static readonly string[] AspectRatios = ["1:1", "1:4", "4:1", "1:8", "8:1", "2:3", "3:2", "3:4", "4:3", "4:5", "5:4", "9:16", "16:9", "21:9"];

    /// <summary>Who makes images now: the one <c>media.images</c> names, or on auto OpenAI where DipTick has its key, else Google; null for none.</summary>
    public static string? Provider(MasterConfig config, string? asked = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        var named = asked ?? config.MediaImages;
        return named switch
        {
            "none" => null,
            _ when Providers.Contains(named, StringComparer.Ordinal) => named,
            _ => MediaProviders.HasKey(config, "openai") ? "openai" : MediaProviders.HasKey(config, "google") ? "google" : null,
        };
    }

    /// <summary>Whether images can be made now.</summary>
    public static bool Ready(MasterConfig config) => Problem(config) is null;

    /// <summary>What making images still needs, or null: a provider, its key where it takes one, its model where it has no default, a ComfyUI workflow.</summary>
    public static string? Problem(MasterConfig config, string? asked = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        if ((asked ?? config.MediaImages) == "none")
        {
            return "Making images is off (media.images is none).";
        }

        if (Provider(config, asked) is not { } provider)
        {
            return "No provider that makes images has a key here: OpenAI's or Google's (diptick key set openai, or google), or name another in media.images.";
        }

        var name = ProviderCatalogue.Find(provider)?.Name ?? provider;
        return MediaProviders.For(config, provider, config.MediaImageAddress) is not { } where ? $"{name} has no address here: set media.imageAddress."
            : ProviderCatalogue.Find(provider) is { NeedsKey: true } && !where.Keys.Describe().Usable ? $"{name} needs its key: diptick key set {provider}."
            : Named.Contains(provider, StringComparer.Ordinal) && config.MediaImageModel.Length == 0 ? $"{name} makes images with the model you name: set media.imageModel to one of its image models."
            : provider == "comfyui" && config.MediaComfyImageWorkflow.Length == 0 ? "ComfyUI runs your workflow: set media.comfyImageWorkflow to one exported with File > Export (API)."
            : null;
    }

    /// <summary>The model: <c>media.imageModel</c>, or the provider's own default; for ComfyUI and sd-server, what they run themselves.</summary>
    public static string Model(MasterConfig config, string provider)
    {
        ArgumentNullException.ThrowIfNull(config);
        return provider switch
        {
            "comfyui" => $"your workflow {Path.GetFileName(config.MediaComfyImageWorkflow)}",
            "sdcpp" => "the model sd-server loaded",
            _ when config.MediaImageModel.Length > 0 => config.MediaImageModel,
            "google" => GoogleModel,
            _ => OpenAiModel,
        };
    }

    /// <summary>
    /// Why a size will not do for OpenAI, or null (its image guide): WxH, each a multiple of 16, the
    /// aspect between 1:3 and 3:1, no edge over 3840, between 655,360 and 8,294,400 pixels.
    /// </summary>
    public static string? SizeProblem(string size)
    {
        ArgumentNullException.ThrowIfNull(size);
        if (Parse(size) is not var (w, h))
        {
            return "a size is WIDTHxHEIGHT, like 1024x1024";
        }

        var pixels = (long)w * h;
        return w % 16 != 0 || h % 16 != 0 ? "both sides must be multiples of 16"
            : w > 3840 || h > 3840 ? "no side may be over 3840"
            : w * 3L < h || h * 3L < w ? "the shape must be between 1:3 and 3:1"
            : pixels is < 655_360 or > 8_294_400 ? "it must be between 655,360 and 8,294,400 pixels (1024x640 to 3840x2160)"
            : null;
    }

    /// <summary>Makes one image from the words, at the size asked (1024x1024 when none is).</summary>
    /// <exception cref="MediaException">It could not be made, and why.</exception>
    public static async Task<MadeImage> MakeAsync(MasterConfig config, string prompt, string? size, string? asked, CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(prompt);
        if (Problem(config, asked) is { } missing)
        {
            throw new MediaException(missing);
        }

        var provider = Provider(config, asked)!;
        var (endpoint, keys) = MediaProviders.For(config, provider, config.MediaImageAddress)!.Value;
        var model = Model(config, provider);
        if (ProviderMedia.Own.Contains(provider, StringComparer.Ordinal))
        {
            // The provider's own API: the size only where one was asked, its model's own fields from media.imageInput.
            int? width = null;
            int? height = null;
            if (size is not null)
            {
                if (Parse(size) is not { } dims)
                {
                    throw new MediaException($"{size}: a size is WIDTHxHEIGHT, like 1024x1024");
                }

                (width, height) = (dims.Width, dims.Height);
            }

            var made = await ProviderMedia.MakeAsync(provider, endpoint, keys, config.MediaImageModel, new MediaAsk(prompt, Video: false, width, height, null, Extra(config.MediaImageInput), config.MediaComfyImageWorkflow.Length > 0 ? config.MediaComfyImageWorkflow : null), null, cancel).ConfigureAwait(false);
            if (!made.MediaType.StartsWith("image/", StringComparison.Ordinal))
            {
                throw new MediaException($"{ProviderCatalogue.Find(provider)?.Name ?? provider} answered with {made.MediaType}, not an image");
            }

            return new MadeImage(made.Bytes, made.MediaType, provider, model, size ?? "its default");
        }

        var wanted = size ?? "1024x1024";
        if (provider == "openai" && SizeProblem(wanted) is { } bad)
        {
            throw new MediaException($"{wanted}: {bad}");
        }

        var key = await MediaProviders.KeyAsync(keys, cancel).ConfigureAwait(false);
        using var http = OpenAiCompatibleEngine.NewClient();
        return provider == "google"
            ? await GoogleAsync(http, endpoint, key, model, prompt, wanted, cancel).ConfigureAwait(false)
            : await OpenAiAsync(http, provider, endpoint, keys.Scheme, key, model, prompt, wanted, cancel).ConfigureAwait(false);
    }

    /// <summary>A model's own fields, as the person set them (<c>media.imageInput</c>, <c>media.videoInput</c>): a JSON object; none when not set.</summary>
    internal static System.Text.Json.Nodes.JsonObject Extra(string text) =>
        text.Length > 0 && System.Text.Json.Nodes.JsonNode.Parse(text) is System.Text.Json.Nodes.JsonObject fields ? fields : new System.Text.Json.Nodes.JsonObject();

    /// <summary>OpenAI's Images API, as OpenAI serves it and as Nscale's own docs give it (<c>/v1/images/generations</c>, <c>data[].b64_json</c>).</summary>
    private static async Task<MadeImage> OpenAiAsync(HttpClient http, string provider, Uri endpoint, KeyScheme scheme, string? key, string model, string prompt, string size, CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint.AbsoluteUri.TrimEnd('/') + "/images/generations"))
        {
            Content = new StringContent(JsonSerializer.Serialize(new Dictionary<string, object> { ["model"] = model, ["prompt"] = prompt, ["size"] = size, ["n"] = 1 }), Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        OpenAiCompatibleEngine.Sign(request, scheme, key);
        using var doc = await MediaProviders.JsonAsync(http, request, MostAnswer, cancel).ConfigureAwait(false);
        if (ProviderMedia.Has(doc.RootElement, "data", out var data) && data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0
            && ProviderMedia.Has(data[0], "b64_json", out var b64) && b64.ValueKind == JsonValueKind.String)
        {
            var format = ProviderMedia.Has(doc.RootElement, "output_format", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() ?? "png" : "png";
            return new MadeImage(Decode(b64.GetString()!), $"image/{format}", provider, model, size);
        }

        throw new MediaException($"{ProviderCatalogue.Find(provider)?.Name ?? provider} answered with no image");
    }

    /// <summary>
    /// Google's image models through <c>generateContent</c>: text and image asked for, the size as the
    /// nearest of Gemini's aspect ratios and 1K, 2K or 4K; the first <c>inlineData</c> part is the image.
    /// </summary>
    private static async Task<MadeImage> GoogleAsync(HttpClient http, Uri endpoint, string? key, string model, string prompt, string size, CancellationToken cancel)
    {
        var (w, h) = Parse(size) ?? (1024, 1024);
        var ratio = AspectRatios.OrderBy(r => Math.Abs(Math.Log(Ratio(r) / ((double)w / h)))).First();
        var longest = Math.Max(w, h);
        var imageSize = longest > 2048 ? "4K" : longest > 1024 ? "2K" : "1K";
        var body = JsonSerializer.Serialize(new
        {
            contents = new[] { new { role = "user", parts = new[] { new { text = prompt } } } },
            generationConfig = new { responseModalities = TextAndImage, imageConfig = new { aspectRatio = ratio, imageSize } },
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"{endpoint.AbsoluteUri.TrimEnd('/')}/models/{Uri.EscapeDataString(GeminiEngine.Bare(model))}:generateContent"))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (key is not null)
        {
            request.Headers.Add("x-goog-api-key", key);
        }

        using var doc = await MediaProviders.JsonAsync(http, request, MostAnswer, cancel).ConfigureAwait(false);
        var words = string.Empty;
        if (ProviderMedia.Has(doc.RootElement, "candidates", out var candidates) && candidates.ValueKind == JsonValueKind.Array && candidates.GetArrayLength() > 0)
        {
            var candidate = candidates[0];
            if (ProviderMedia.Has(candidate, "content", out var content) && ProviderMedia.Has(content, "parts", out var parts) && parts.ValueKind == JsonValueKind.Array)
            {
                foreach (var part in parts.EnumerateArray())
                {
                    if (ProviderMedia.Has(part, "inlineData", out var inline) && ProviderMedia.Has(inline, "data", out var data) && data.ValueKind == JsonValueKind.String)
                    {
                        var type = ProviderMedia.Has(inline, "mimeType", out var mime) && mime.ValueKind == JsonValueKind.String ? mime.GetString() ?? "image/png" : "image/png";
                        return new MadeImage(Decode(data.GetString()!), type, "google", model, $"{ratio} {imageSize}");
                    }

                    if (ProviderMedia.Has(part, "text", out var text) && text.ValueKind == JsonValueKind.String)
                    {
                        words += text.GetString();
                    }
                }
            }

            if (ProviderMedia.Has(candidate, "finishReason", out var finish) && finish.ValueKind == JsonValueKind.String && finish.GetString() is { } reason && reason != "STOP")
            {
                throw new MediaException($"Google stopped without an image ({reason})");
            }
        }

        throw new MediaException(words.Length > 0 ? $"Google answered with words and no image: {OpenAiCompatibleEngine.Clip(words)}" : "Google answered with no image");
    }

    private static double Ratio(string ratio)
    {
        var parts = ratio.Split(':');
        return double.Parse(parts[0], CultureInfo.InvariantCulture) / double.Parse(parts[1], CultureInfo.InvariantCulture);
    }

    /// <summary>WxH as two whole numbers, or null.</summary>
    internal static (int Width, int Height)? Parse(string size)
    {
        var parts = size.Trim().ToLowerInvariantAscii().Split('x');
        return parts.Length == 2
            && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var w) && w > 0
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var h) && h > 0
            ? (w, h)
            : null;
    }

    private static byte[] Decode(string base64)
    {
        try
        {
            return Convert.FromBase64String(base64);
        }
        catch (FormatException ex)
        {
            throw new MediaException("the image did not come back as base64", ex);
        }
    }
}

/// <summary>
/// Video from Google's Veo, through the Gemini API (Google's Veo page):
/// <c>predictLongRunning</c> starts a job; its operation is asked again every 10 seconds until done;
/// the video's address takes the key to Google's own host only, and a redirect from there is followed
/// without it. 4, 6 or 8 seconds; 720p, 1080p or 4k, the last two at 8 seconds only; Lite has no 4k.
/// </summary>
internal static class VideoMaker
{
    public const string DefaultModel = "veo-3.1-generate-preview";

    /// <summary>The most a video may be: an 8-second 4k MP4 is well inside it.</summary>
    public const int MostVideo = 256 * 1024 * 1024;

    /// <summary>How often a job is asked about (Google's example waits 10 seconds).</summary>
    public static readonly TimeSpan Poll = TimeSpan.FromSeconds(10);

    /// <summary>Every provider <c>media.video</c> may name, besides auto and none (the providers' own APIs beside Veo).</summary>
    public static IReadOnlyList<string> Providers { get; } = ["google", .. ProviderMedia.Own];

    /// <summary>Those whose model the person must name.</summary>
    private static readonly string[] Named = ["replicate", "fal", "wavespeed"];

    /// <summary>Who makes video now: the one <c>media.video</c> names, or on auto Google's Veo where DipTick has its key; null for none.</summary>
    public static string? Provider(MasterConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.MediaVideo switch
        {
            "none" => null,
            var named when Providers.Contains(named, StringComparer.Ordinal) => named,
            _ => MediaProviders.HasKey(config, "google") ? "google" : null,
        };
    }

    /// <summary>Whether video can be made now: <c>media.video</c> not none, and its provider ready.</summary>
    public static bool Ready(MasterConfig config) => Problem(config) is null;

    /// <summary>What making video still needs, or null.</summary>
    public static string? Problem(MasterConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.MediaVideo == "none")
        {
            return "Making video is off (media.video is none).";
        }

        if (Provider(config) is not { } provider)
        {
            return "Veo needs Google's Gemini API key: diptick key set google; or name another provider in media.video.";
        }

        if (provider == "google")
        {
            return MediaProviders.HasKey(config, "google") ? null : "Veo needs Google's Gemini API key: diptick key set google.";
        }

        var name = ProviderCatalogue.Find(provider)?.Name ?? provider;
        return MediaProviders.For(config, provider, config.MediaVideoAddress) is not { } where ? $"{name} has no address here: set media.videoAddress."
            : ProviderCatalogue.Find(provider) is { NeedsKey: true } && !where.Keys.Describe().Usable ? $"{name} needs its key: diptick key set {provider}."
            : Named.Contains(provider, StringComparer.Ordinal) && config.MediaVideoModel.Length == 0 ? $"{name} makes video with the model you name: set media.videoModel to one of its video models."
            : provider == "comfyui" && config.MediaComfyVideoWorkflow.Length == 0 ? "ComfyUI runs your workflow: set media.comfyVideoWorkflow to one exported with File > Export (API)."
            : null;
    }

    /// <summary>The model: <c>media.videoModel</c>, or Veo 3.1; for ComfyUI and sd-server, what they run themselves.</summary>
    public static string Model(MasterConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return Provider(config) switch
        {
            "comfyui" => $"your workflow {Path.GetFileName(config.MediaComfyVideoWorkflow)}",
            "sdcpp" => "the model sd-server loaded",
            _ when config.MediaVideoModel.Length > 0 => config.MediaVideoModel,
            _ => DefaultModel,
        };
    }

    /// <summary>A resolution's size, standing up for portrait: 720p, 1080p or 4k; null for anything else.</summary>
    internal static (int Width, int Height)? Size(string? resolution, bool portrait) => resolution switch
    {
        "720p" => portrait ? (720, 1280) : (1280, 720),
        "1080p" => portrait ? (1080, 1920) : (1920, 1080),
        "4k" => portrait ? (2160, 3840) : (3840, 2160),
        _ => null,
    };

    /// <summary>Why a length and resolution will not do for this model, or null (Google's Veo page).</summary>
    public static string? Problem(int seconds, string resolution, string model)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        ArgumentNullException.ThrowIfNull(model);
        return seconds is not (4 or 6 or 8) ? "a video is 4, 6 or 8 seconds"
            : resolution is not ("720p" or "1080p" or "4k") ? "the resolution is 720p, 1080p or 4k"
            : resolution != "720p" && seconds != 8 ? $"{resolution} is made at 8 seconds only"
            : resolution == "4k" && model.Contains("lite", StringComparison.OrdinalIgnoreCase) ? "Veo 3.1 Lite makes no 4k"
            : null;
    }

    /// <summary>
    /// Makes one video; <paramref name="waited"/> hears how long it has taken, at each look. Veo takes the
    /// length, resolution and shape asked (720p when none is); another provider's own API takes the words and
    /// the person's <c>media.videoInput</c>, with the size and length only where a server on this PC takes
    /// them (sd-server's frames, a ComfyUI workflow's marks).
    /// </summary>
    /// <exception cref="MediaException">It could not be made, and why.</exception>
    public static async Task<MadeVideo> MakeAsync(MasterConfig config, string prompt, int seconds, string? asked, bool portrait, Action<TimeSpan>? waited, CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(prompt);
        if (Problem(config) is { } missing)
        {
            throw new MediaException(missing);
        }

        var model = Model(config);
        var provider = Provider(config)!;
        if (provider != "google")
        {
            var size = Size(asked, portrait);
            var (address, providerKeys) = MediaProviders.For(config, provider, config.MediaVideoAddress)!.Value;
            var ask = new MediaAsk(prompt, Video: true, size?.Width, size?.Height, seconds, ImageMaker.Extra(config.MediaVideoInput), config.MediaComfyVideoWorkflow.Length > 0 ? config.MediaComfyVideoWorkflow : null);
            var made = await ProviderMedia.MakeAsync(provider, address, providerKeys, config.MediaVideoModel, ask, waited, cancel).ConfigureAwait(false);
            return made.MediaType.StartsWith("video/", StringComparison.Ordinal) || made.MediaType == "image/webp"
                ? new MadeVideo(made.Bytes, model, seconds, asked ?? "its default", portrait, made.MediaType)
                : throw new MediaException($"{ProviderCatalogue.Find(provider)?.Name ?? provider} answered with {made.MediaType}, not a video");
        }

        var resolution = asked ?? "720p";
        if (Problem(seconds, resolution, model) is { } bad)
        {
            throw new MediaException(bad);
        }

        var (endpoint, keys) = MediaProviders.For(config, "google")!.Value;
        var key = await MediaProviders.KeyAsync(keys, cancel).ConfigureAwait(false);
        var root = endpoint.AbsoluteUri.TrimEnd('/');
        using var http = OpenAiCompatibleEngine.NewClient();
        var body = JsonSerializer.Serialize(new
        {
            instances = new[] { new { prompt } },
            parameters = new { aspectRatio = portrait ? "9:16" : "16:9", durationSeconds = seconds, resolution },
        });
        using var start = new HttpRequestMessage(HttpMethod.Post, new Uri($"{root}/models/{Uri.EscapeDataString(GeminiEngine.Bare(model))}:predictLongRunning"))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        Sign(start, key);
        string operation;
        using (var started = await MediaProviders.JsonAsync(http, start, 1024 * 1024, cancel).ConfigureAwait(false))
        {
            operation = ProviderMedia.Has(started.RootElement, "name", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() ?? string.Empty : string.Empty;
        }

        // The operation's name goes into Google's own address, so nothing it holds can move where the key goes.
        if (operation.Length == 0 || operation.Contains("..", StringComparison.Ordinal) || operation.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '/' or '-' or '_' or '.')))
        {
            throw new MediaException("Google started no job DipTick can follow");
        }

        var clock = System.Diagnostics.Stopwatch.StartNew();
        Uri? video = null;
        while (video is null)
        {
            await Task.Delay(Poll, cancel).ConfigureAwait(false);
            waited?.Invoke(clock.Elapsed);
            using var ask = new HttpRequestMessage(HttpMethod.Get, new Uri($"{root}/{operation}"));
            Sign(ask, key);
            using var status = await MediaProviders.JsonAsync(http, ask, 1024 * 1024, cancel).ConfigureAwait(false);
            var state = status.RootElement;
            if (!ProviderMedia.Has(state, "done", out var done) || done.ValueKind != JsonValueKind.True)
            {
                continue;
            }

            if (ProviderMedia.Has(state, "error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                throw new MediaException($"Google could not make it: {OpenAiCompatibleEngine.Clip(ProviderMedia.Has(error, "message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() ?? string.Empty : error.ToString())}");
            }

            video = Address(state) ?? throw new MediaException(Filtered(state) ?? "Google finished with no video");
        }

        var bytes = await DownloadAsync(http, video, key, cancel).ConfigureAwait(false);
        return new MadeVideo(bytes, model, seconds, resolution, portrait);
    }

    /// <summary>The video's address in a finished operation: <c>response.generateVideoResponse.generatedSamples[0].video.uri</c>.</summary>
    internal static Uri? Address(JsonElement state)
    {
        if (ProviderMedia.Has(state, "response", out var response) && ProviderMedia.Has(response, "generateVideoResponse", out var generated)
            && ProviderMedia.Has(generated, "generatedSamples", out var samples) && samples.ValueKind == JsonValueKind.Array && samples.GetArrayLength() > 0
            && ProviderMedia.Has(samples[0], "video", out var video) && ProviderMedia.Has(video, "uri", out var uri) && uri.ValueKind == JsonValueKind.String
            && Uri.TryCreate(uri.GetString(), UriKind.Absolute, out var address) && address.Scheme == Uri.UriSchemeHttps)
        {
            return address;
        }

        return null;
    }

    /// <summary>Google's words when its filters kept the video back, where it says so; null otherwise.</summary>
    private static string? Filtered(JsonElement state) =>
        ProviderMedia.Has(state, "response", out var response) && ProviderMedia.Has(response, "generateVideoResponse", out var generated)
        && ProviderMedia.Has(generated, "raiMediaFilteredReasons", out var reasons) && reasons.ValueKind == JsonValueKind.Array && reasons.GetArrayLength() > 0
            ? $"Google's filters kept the video back: {OpenAiCompatibleEngine.Clip(string.Join("; ", reasons.EnumerateArray().Select(r => r.ToString())))}"
            : null;

    /// <summary>
    /// The MP4: asked for with the key only at Google's own host; a redirect (Google's example follows
    /// one) is followed without the key, to an https address, at most three times.
    /// </summary>
    private static async Task<byte[]> DownloadAsync(HttpClient http, Uri video, string? key, CancellationToken cancel)
    {
        if (!GoogleCloud.IsGoogleHost(video))
        {
            throw new MediaException($"the video's address is not Google's own ({video.Host}), so the key is not sent there");
        }

        var at = video;
        var signed = true;
        for (var hop = 0; hop < 4; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, at);
            if (signed)
            {
                Sign(request, key);
            }

            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                if (response.Headers.Location is not { } next || !Uri.TryCreate(at, next.OriginalString, out var target) || target.Scheme != Uri.UriSchemeHttps)
                {
                    throw new MediaException("the video's address redirected somewhere DipTick does not follow");
                }

                at = target;
                signed = false;
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new MediaException(await OpenAiCompatibleEngine.Refusal(at.Host, response, cancel).ConfigureAwait(false));
            }

            if (response.Content.Headers.ContentLength is > MostVideo)
            {
                throw new MediaException("the video is larger than DipTick keeps");
            }

            var body = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
            await using var bodyScope = body.ConfigureAwait(false);
            using var kept = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = await body.ReadAsync(buffer, cancel).ConfigureAwait(false)) > 0)
            {
                if (kept.Length + read > MostVideo)
                {
                    throw new MediaException("the video is larger than DipTick keeps");
                }

                await kept.WriteAsync(buffer.AsMemory(0, read), cancel).ConfigureAwait(false);
            }

            return kept.ToArray();
        }

        throw new MediaException("the video's address redirected too many times");
    }

    private static void Sign(HttpRequestMessage request, string? key)
    {
        if (key is not null)
        {
            request.Headers.Add("x-goog-api-key", key);
        }
    }
}
