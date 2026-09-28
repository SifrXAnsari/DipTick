using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DipTick.Models.Chat;
using DipTick.Models.Keys;
using DipTick.Models.Providers;
using DipTick.Models.Tools;

namespace DipTick.Models.Media;

/// <summary>
/// What a provider's own API is asked to make: an image or a video, its
/// words, the size and length asked where they were, the fields the person set for the model
/// (<c>media.imageInput</c> or <c>media.videoInput</c>), and a ComfyUI workflow's path.
/// </summary>
internal sealed record MediaAsk(string Prompt, bool Video, int? Width, int? Height, int? Seconds, JsonObject Extra, string? Workflow);

/// <summary>What a provider made: its bytes and their media type.</summary>
internal sealed record MediaMade(byte[] Bytes, string MediaType);

/// <summary>
/// Images and video through providers' own APIs, each as its own pages give it (read 2026-09-28):
/// <list type="bullet">
/// <item>Replicate: <c>POST /v1/models/{owner}/{name}/predictions</c> (or <c>/v1/predictions</c> with a
/// version), Bearer, <c>Prefer: wait=n</c> up to 60, then <c>urls.get</c> until succeeded, failed or
/// canceled; the output is an address.</item>
/// <item>fal: <c>POST https://queue.fal.run/{model}</c> with <c>Authorization: Key</c>, then its answer's
/// <c>status_url</c> until COMPLETED, then <c>response_url</c>; <c>images[].url</c> or <c>video.url</c>.</item>
/// <item>WaveSpeedAI: <c>POST https://api.wavespeed.ai/api/v3/{model}</c>, Bearer, then <c>data.urls.get</c>
/// every 2 seconds or more until completed, failed, cancelled, timeout or deleted; <c>data.outputs[]</c>.</item>
/// <item>ComfyUI: <c>POST /prompt</c> with a workflow exported in API format, <c>GET /history/{id}</c>
/// until its outputs are there, each file then from <c>GET /view</c>.</item>
/// <item>stable-diffusion.cpp's sd-server: <c>/v1/images/generations</c> in OpenAI's shape; video with
/// <c>/sdcpp/v1/vid_gen</c> then <c>/sdcpp/v1/jobs/{id}</c> (queued, generating, completed, failed,
/// cancelled), its result in base64.</item>
/// </list>
/// A key goes only to the provider's own API host, over HTTPS; what was made is fetched from its address
/// without the key, over HTTPS; a server on this PC is reached over http only there.
/// </summary>
internal static class ProviderMedia
{
    /// <summary>The providers that make images through their own API, here.</summary>
    public static IReadOnlyList<string> Own { get; } = ["replicate", "fal", "wavespeed", "comfyui", "sdcpp"];

    /// <summary>The most an image may be.</summary>
    public const int MostImage = 64 * 1024 * 1024;

    /// <summary>The most a JSON answer about a job may be.</summary>
    private const int MostStatus = 4 * 1024 * 1024;

    /// <summary>The most a ComfyUI workflow may be.</summary>
    private const int MostWorkflow = 2 * 1024 * 1024;

    /// <summary>How long Replicate may hold the first answer open while it works (its page: 1 to 60 seconds).</summary>
    private const int ReplicateWait = 60;

    /// <summary>How often a job is asked about: WaveSpeedAI asks for "at least 2 seconds between requests"; the others take the same.</summary>
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(2);

    /// <summary>Makes one image or video with <paramref name="provider"/>'s own API; <paramref name="client"/> sends, DipTick's own by default.</summary>
    /// <exception cref="MediaException">It could not be made, and why.</exception>
    public static async Task<MediaMade> MakeAsync(string provider, Uri endpoint, KeyFinder keys, string model, MediaAsk ask, Action<TimeSpan>? waited, CancellationToken cancel, HttpClient? client = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(ask);
        using var own = client is null ? OpenAiCompatibleEngine.NewClient() : null;
        var http = client ?? own!;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        void Waited() => waited?.Invoke(clock.Elapsed);
        return provider switch
        {
            "replicate" => await ReplicateAsync(http, endpoint, await MediaProviders.KeyAsync(keys, cancel).ConfigureAwait(false), model, ask, Waited, cancel).ConfigureAwait(false),
            "fal" => await FalAsync(http, keys.Scheme, await MediaProviders.KeyAsync(keys, cancel).ConfigureAwait(false), model, ask, Waited, cancel).ConfigureAwait(false),
            "wavespeed" => await WaveSpeedAsync(http, endpoint, await MediaProviders.KeyAsync(keys, cancel).ConfigureAwait(false), model, ask, Waited, cancel).ConfigureAwait(false),
            "comfyui" => await ComfyAsync(http, endpoint, ask, Waited, cancel).ConfigureAwait(false),
            "sdcpp" => ask.Video
                ? await SdVideoAsync(http, endpoint, ask, Waited, cancel).ConfigureAwait(false)
                : await SdImageAsync(http, endpoint, ask, cancel).ConfigureAwait(false),
            _ => throw new MediaException($"{provider} makes nothing through its own API here"),
        };
    }

    /// <summary>A model's name as Replicate takes it: owner/name, or owner/name:version.</summary>
    internal static bool ReplicateModel(string model) =>
        model.Split(':') is var parts && parts.Length <= 2 && parts[0].Split('/') is { Length: 2 } names
        && names.All(n => n.Length > 0 && n.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
        && (parts.Length == 1 || parts[1].All(char.IsAsciiLetterOrDigit));

    /// <summary>A model's id as fal and WaveSpeedAI take it: parts of letters, digits, - _ . joined by /.</summary>
    internal static bool PathModel(string model) =>
        model.Length is > 0 and <= 200 && model.Split('/').All(p => p.Length > 0 && p != ".." && p.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'));

    /// <summary>A ComfyUI workflow with its marks filled: <c>{{prompt}}</c> inside any string, and a string that is exactly <c>{{width}}</c>, <c>{{height}}</c>, <c>{{seed}}</c> or <c>{{seconds}}</c> made that number.</summary>
    /// <exception cref="MediaException">It is not a workflow in API format, or names no <c>{{prompt}}</c>.</exception>
    internal static JsonObject Filled(string workflow, MediaAsk ask, long seed)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(ask);
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(workflow);
        }
        catch (JsonException ex)
        {
            throw new MediaException("the ComfyUI workflow is not JSON: export it with File > Export (API)", ex);
        }

        if (parsed is not JsonObject nodes || nodes.Count == 0 || nodes.Any(n => n.Value is not JsonObject node || !node.ContainsKey("class_type")))
        {
            throw new MediaException("the ComfyUI workflow is not in API format (each node an object with class_type): export it with File > Export (API)");
        }

        var prompted = false;
        void Fill(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject obj:
                    foreach (var key in obj.Select(p => p.Key).ToList())
                    {
                        if (Replace(obj[key]) is { } replaced)
                        {
                            obj[key] = replaced;
                        }
                        else
                        {
                            Fill(obj[key]);
                        }
                    }

                    break;
                case JsonArray array:
                    for (var i = 0; i < array.Count; i++)
                    {
                        if (Replace(array[i]) is { } replaced)
                        {
                            array[i] = replaced;
                        }
                        else
                        {
                            Fill(array[i]);
                        }
                    }

                    break;
            }
        }

        JsonNode? Replace(JsonNode? node)
        {
            if (node is not JsonValue value || !value.TryGetValue<string>(out var text))
            {
                return null;
            }

            switch (text)
            {
                case "{{width}}" when ask.Width is { } width:
                    return JsonValue.Create(width);
                case "{{height}}" when ask.Height is { } height:
                    return JsonValue.Create(height);
                case "{{seed}}":
                    return JsonValue.Create(seed);
                case "{{seconds}}" when ask.Seconds is { } seconds:
                    return JsonValue.Create(seconds);
            }

            if (text.Contains("{{prompt}}", StringComparison.Ordinal))
            {
                prompted = true;
                return JsonValue.Create(text.Replace("{{prompt}}", ask.Prompt, StringComparison.Ordinal));
            }

            return null;
        }

        Fill(nodes);
        return prompted ? nodes : throw new MediaException("the ComfyUI workflow names no {{prompt}}: put {{prompt}} in the text of its prompt node");
    }

    private static async Task<MediaMade> ReplicateAsync(HttpClient http, Uri endpoint, string? key, string model, MediaAsk ask, Action waited, CancellationToken cancel)
    {
        if (!ReplicateModel(model))
        {
            throw new MediaException($"Replicate takes a model as owner/name, or owner/name:version, not {SafeText.Line(model)}");
        }

        var input = new JsonObject { ["prompt"] = ask.Prompt };
        Merge(input, ask.Extra);
        var root = endpoint.AbsoluteUri.TrimEnd('/');
        var versioned = model.Contains(':', StringComparison.Ordinal);
        var body = versioned ? new JsonObject { ["version"] = model, ["input"] = input } : new JsonObject { ["input"] = input };
        using var start = new HttpRequestMessage(HttpMethod.Post, new Uri(versioned ? $"{root}/predictions" : $"{root}/models/{model}/predictions"))
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        start.Headers.Add("Prefer", string.Create(CultureInfo.InvariantCulture, $"wait={ReplicateWait}"));
        Bearer(start, key);
        using var first = await MediaProviders.JsonAsync(http, start, MostStatus, cancel).ConfigureAwait(false);
        var state = first.RootElement.Clone();
        while (true)
        {
            var status = Text(state, "status");
            if (status == "succeeded")
            {
                var output = Has(state, "output", out var o) ? First(o) : null;
                return output is null
                    ? throw new MediaException("Replicate finished with no output")
                    : await OutputAsync(http, output, ask.Video, cancel).ConfigureAwait(false);
            }

            if (status is "failed" or "canceled")
            {
                throw new MediaException($"Replicate could not make it ({status}): {OpenAiCompatibleEngine.Clip(Text(state, "error") ?? "no reason given")}");
            }

            // Asked again where its own answer says, and only at Replicate's own API host: the key goes nowhere else.
            if (!Has(state, "urls", out var urls) || Text(urls, "get") is not { } get || !Uri.TryCreate(get, UriKind.Absolute, out var poll) || !SameHost(poll, endpoint))
            {
                throw new MediaException("Replicate started no prediction DipTick can follow");
            }

            await Task.Delay(Poll, cancel).ConfigureAwait(false);
            waited();
            using var next = await GetJsonAsync(http, poll, r => Bearer(r, key), MostStatus, cancel).ConfigureAwait(false);
            state = next.RootElement.Clone();
        }
    }

    private static async Task<MediaMade> FalAsync(HttpClient http, KeyScheme scheme, string? key, string model, MediaAsk ask, Action waited, CancellationToken cancel)
    {
        if (!PathModel(model))
        {
            throw new MediaException($"fal takes a model's id, like fal-ai/flux/schnell, not {SafeText.Line(model)}");
        }

        var input = new JsonObject { ["prompt"] = ask.Prompt };
        if (!ask.Video && ask.Width is { } width && ask.Height is { } height)
        {
            input["image_size"] = new JsonObject { ["width"] = width, ["height"] = height };
        }

        Merge(input, ask.Extra);
        var queue = new Uri("https://queue.fal.run/");
        using var submit = new HttpRequestMessage(HttpMethod.Post, new Uri(queue, model))
        {
            Content = new StringContent(input.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        OpenAiCompatibleEngine.Sign(submit, scheme, key);
        Uri statusUrl;
        Uri responseUrl;
        using (var submitted = await MediaProviders.JsonAsync(http, submit, MostStatus, cancel).ConfigureAwait(false))
        {
            // fal's own queue host only: the key goes nowhere else.
            if (Text(submitted.RootElement, "status_url") is not { } s || !Uri.TryCreate(s, UriKind.Absolute, out var foundStatus) || !SameHost(foundStatus, queue)
                || Text(submitted.RootElement, "response_url") is not { } r || !Uri.TryCreate(r, UriKind.Absolute, out var foundResponse) || !SameHost(foundResponse, queue))
            {
                throw new MediaException("fal queued nothing DipTick can follow");
            }

            statusUrl = foundStatus;
            responseUrl = foundResponse;
        }

        while (true)
        {
            await Task.Delay(Poll, cancel).ConfigureAwait(false);
            waited();
            using var status = await GetJsonAsync(http, statusUrl, r => OpenAiCompatibleEngine.Sign(r, scheme, key), MostStatus, cancel).ConfigureAwait(false);
            if (Text(status.RootElement, "status") == "COMPLETED")
            {
                if (Text(status.RootElement, "error") is { Length: > 0 } error)
                {
                    throw new MediaException($"fal could not make it: {OpenAiCompatibleEngine.Clip(error)}");
                }

                break;
            }
        }

        using var result = await GetJsonAsync(http, responseUrl, r => OpenAiCompatibleEngine.Sign(r, scheme, key), MostStatus, cancel).ConfigureAwait(false);
        var root = result.RootElement;
        var address = Has(root, "images", out var images) && images.ValueKind == JsonValueKind.Array && images.GetArrayLength() > 0 ? Text(images[0], "url")
            : Has(root, "video", out var video) ? Text(video, "url")
            : Has(root, "image", out var image) ? Text(image, "url")
            : null;
        return address is null
            ? throw new MediaException($"fal answered with no file{(Has(root, "detail", out var detail) ? $": {OpenAiCompatibleEngine.Clip(detail.ToString())}" : string.Empty)}")
            : await OutputAsync(http, address, ask.Video, cancel).ConfigureAwait(false);
    }

    private static async Task<MediaMade> WaveSpeedAsync(HttpClient http, Uri endpoint, string? key, string model, MediaAsk ask, Action waited, CancellationToken cancel)
    {
        if (!PathModel(model))
        {
            throw new MediaException($"WaveSpeedAI takes a model's id, like wavespeed-ai/flux-dev, not {SafeText.Line(model)}");
        }

        var input = new JsonObject { ["prompt"] = ask.Prompt };
        if (!ask.Video && ask.Width is { } width && ask.Height is { } height)
        {
            input["size"] = string.Create(CultureInfo.InvariantCulture, $"{width}*{height}");
        }

        Merge(input, ask.Extra);
        using var submit = new HttpRequestMessage(HttpMethod.Post, new Uri($"{endpoint.AbsoluteUri.TrimEnd('/')}/{model}"))
        {
            Content = new StringContent(input.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        Bearer(submit, key);
        Uri poll;
        using (var submitted = await MediaProviders.JsonAsync(http, submit, MostStatus, cancel).ConfigureAwait(false))
        {
            var data = Data(submitted.RootElement, "WaveSpeedAI");
            if (!Has(data, "urls", out var urls) || Text(urls, "get") is not { } get || !Uri.TryCreate(get, UriKind.Absolute, out var found) || !SameHost(found, endpoint))
            {
                throw new MediaException("WaveSpeedAI started no task DipTick can follow");
            }

            poll = found;
        }

        while (true)
        {
            await Task.Delay(Poll, cancel).ConfigureAwait(false);
            waited();
            using var answer = await GetJsonAsync(http, poll, r => Bearer(r, key), MostStatus, cancel).ConfigureAwait(false);
            var data = Data(answer.RootElement, "WaveSpeedAI");
            switch (Text(data, "status"))
            {
                case "completed":
                    var output = Has(data, "outputs", out var outputs) ? First(outputs) : null;
                    return output is null
                        ? throw new MediaException("WaveSpeedAI finished with no output")
                        : await OutputAsync(http, output, ask.Video, cancel).ConfigureAwait(false);
                case "failed" or "cancelled" or "timeout" or "deleted":
                    throw new MediaException($"WaveSpeedAI could not make it ({Text(data, "status")}): {OpenAiCompatibleEngine.Clip(Text(data, "error") ?? "no reason given")}");
            }
        }
    }

    private static async Task<MediaMade> ComfyAsync(HttpClient http, Uri endpoint, MediaAsk ask, Action waited, CancellationToken cancel)
    {
        if (ask.Workflow is not { } path)
        {
            throw new MediaException($"ComfyUI runs a workflow: set media.{(ask.Video ? "comfyVideoWorkflow" : "comfyImageWorkflow")} to one exported with File > Export (API)");
        }

        string text;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MostWorkflow)
            {
                throw new MediaException(info.Exists ? "the ComfyUI workflow is larger than 2 MiB" : $"there is no ComfyUI workflow at {path}");
            }

            text = await File.ReadAllTextAsync(path, Encoding.UTF8, cancel).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new MediaException($"the ComfyUI workflow could not be read: {SafeText.Line(ex.Message)}", ex);
        }

        var workflow = Filled(text, ask, RandomNumberGenerator.GetInt32(int.MaxValue));
        var root = endpoint.AbsoluteUri.TrimEnd('/');
        using var queue = new HttpRequestMessage(HttpMethod.Post, new Uri($"{root}/prompt"))
        {
            Content = new StringContent(new JsonObject { ["prompt"] = workflow, ["client_id"] = Guid.NewGuid().ToString("N") }.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        string id;
        using (var queued = await MediaProviders.JsonAsync(http, queue, MostStatus, cancel).ConfigureAwait(false))
        {
            if (Has(queued.RootElement, "node_errors", out var errors) && errors.ValueKind == JsonValueKind.Object && errors.EnumerateObject().Any())
            {
                throw new MediaException($"ComfyUI refused the workflow: {OpenAiCompatibleEngine.Clip(errors.ToString())}");
            }

            id = Text(queued.RootElement, "prompt_id") is { } promptId && promptId.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')
                ? promptId
                : throw new MediaException("ComfyUI queued nothing DipTick can follow");
        }

        JsonElement outputs;
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancel).ConfigureAwait(false);
            waited();
            using var history = await GetJsonAsync(http, new Uri($"{root}/history/{id}"), _ => { }, MostStatus, cancel).ConfigureAwait(false);
            if (!Has(history.RootElement, id, out var run))
            {
                continue;
            }

            if (Has(run, "status", out var status) && Text(status, "status_str") == "error")
            {
                throw new MediaException($"ComfyUI stopped with an error: {OpenAiCompatibleEngine.Clip(Has(status, "messages", out var messages) ? messages.ToString() : "no reason given")}");
            }

            if (Has(run, "outputs", out var found) && found.ValueKind == JsonValueKind.Object && found.EnumerateObject().Any())
            {
                outputs = found.Clone();
                break;
            }
        }

        // A file each node saved, as ComfyUI's own nodes report them: "images", with "animated" for video.
        string[] wanted = ask.Video ? [".mp4", ".webm", ".mkv"] : [".png", ".jpg", ".jpeg", ".webp"];
        foreach (var node in outputs.EnumerateObject())
        {
            if (!Has(node.Value, "images", out var files) || files.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var file in files.EnumerateArray())
            {
                if (Text(file, "filename") is { } name && wanted.Contains(Path.GetExtension(name).ToLowerInvariantAscii()))
                {
                    var query = $"filename={Uri.EscapeDataString(name)}&subfolder={Uri.EscapeDataString(Text(file, "subfolder") ?? string.Empty)}&type={Uri.EscapeDataString(Text(file, "type") ?? "output")}";
                    var bytes = await BytesAsync(http, new Uri($"{root}/view?{query}"), ask.Video ? VideoMaker.MostVideo : MostImage, cancel).ConfigureAwait(false);
                    return new MediaMade(bytes.Bytes, TypeOf(name, bytes.Type, ask.Video));
                }
            }
        }

        throw new MediaException($"ComfyUI finished, and its workflow saved no {(ask.Video ? "video (.mp4, .webm)" : "image")} in the outputs its own nodes report");
    }

    private static async Task<MediaMade> SdImageAsync(HttpClient http, Uri endpoint, MediaAsk ask, CancellationToken cancel)
    {
        var body = new JsonObject { ["prompt"] = ask.Prompt, ["n"] = 1, ["output_format"] = "png" };
        if (ask.Width is { } width && ask.Height is { } height)
        {
            body["size"] = string.Create(CultureInfo.InvariantCulture, $"{width}x{height}");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"{endpoint.AbsoluteUri.TrimEnd('/')}/v1/images/generations"))
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        using var doc = await MediaProviders.JsonAsync(http, request, MostImage, cancel).ConfigureAwait(false);
        if (Has(doc.RootElement, "data", out var data) && data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0 && Text(data[0], "b64_json") is { } b64)
        {
            return new MediaMade(Decode(b64), "image/png");
        }

        throw new MediaException("sd-server answered with no image");
    }

    private static async Task<MediaMade> SdVideoAsync(HttpClient http, Uri endpoint, MediaAsk ask, Action waited, CancellationToken cancel)
    {
        const int Fps = 16;
        var root = endpoint.AbsoluteUri.TrimEnd('/');
        var body = new JsonObject
        {
            ["prompt"] = ask.Prompt,
            ["width"] = ask.Width ?? 832,
            ["height"] = ask.Height ?? 480,
            ["video_frames"] = ((ask.Seconds ?? 5) * Fps) + 1,
            ["fps"] = Fps,
            ["output_format"] = "webm",
        };
        Merge(body, ask.Extra);
        using var start = new HttpRequestMessage(HttpMethod.Post, new Uri($"{root}/sdcpp/v1/vid_gen"))
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        string id;
        using (var started = await MediaProviders.JsonAsync(http, start, MostStatus, cancel).ConfigureAwait(false))
        {
            id = Text(started.RootElement, "id") is { } job && job.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
                ? job
                : throw new MediaException("sd-server started no job DipTick can follow");
        }

        while (true)
        {
            await Task.Delay(Poll, cancel).ConfigureAwait(false);
            waited();
            using var state = await GetJsonAsync(http, new Uri($"{root}/sdcpp/v1/jobs/{id}"), _ => { }, VideoMaker.MostVideo * 2, cancel).ConfigureAwait(false);
            switch (Text(state.RootElement, "status"))
            {
                case "completed":
                    if (Has(state.RootElement, "result", out var result) && Text(result, "b64_json") is { } b64)
                    {
                        return new MediaMade(Decode(b64), Text(result, "mime_type") is { Length: > 0 } type ? type : "video/webm");
                    }

                    throw new MediaException("sd-server finished with no video");
                case "failed" or "cancelled":
                    throw new MediaException($"sd-server could not make it ({Text(state.RootElement, "status")}): {OpenAiCompatibleEngine.Clip(Has(state.RootElement, "error", out var error) ? error.ToString() : "no reason given")}");
            }
        }
    }

    /// <summary>A job's state, asked for at <paramref name="address"/>, signed by <paramref name="sign"/> (nothing for a server on this PC).</summary>
    private static async Task<JsonDocument> GetJsonAsync(HttpClient http, Uri address, Action<HttpRequestMessage> sign, int most, CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        sign(request);
        return await MediaProviders.JsonAsync(http, request, most, cancel).ConfigureAwait(false);
    }

    /// <summary>What a provider made, from the address it gave: HTTPS only, never with the key, redirects followed to HTTPS at most three times.</summary>
    private static async Task<MediaMade> OutputAsync(HttpClient http, string address, bool video, CancellationToken cancel)
    {
        if (address.StartsWith("data:", StringComparison.Ordinal))
        {
            var comma = address.IndexOf(',', StringComparison.Ordinal);
            var head = comma > 5 ? address[5..comma] : string.Empty;
            if (comma < 0 || !head.EndsWith(";base64", StringComparison.Ordinal))
            {
                throw new MediaException("the provider gave a data address DipTick does not read");
            }

            return new MediaMade(Decode(address[(comma + 1)..]), head[..^";base64".Length]);
        }

        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length > 0)
        {
            throw new MediaException("the provider gave an address that is not https, which DipTick does not fetch");
        }

        var (bytes, type) = await BytesAsync(http, uri, video ? VideoMaker.MostVideo : MostImage, cancel).ConfigureAwait(false);
        return new MediaMade(bytes, TypeOf(uri.AbsolutePath, type, video));
    }

    private static async Task<(byte[] Bytes, string? Type)> BytesAsync(HttpClient http, Uri address, int most, CancellationToken cancel)
    {
        var at = address;
        for (var hop = 0; hop < 4; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, at);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                if (response.Headers.Location is not { } next || !Uri.TryCreate(at, next.OriginalString, out var target) || target.Scheme != Uri.UriSchemeHttps)
                {
                    throw new MediaException("the file's address redirected somewhere DipTick does not follow");
                }

                at = target;
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new MediaException(await OpenAiCompatibleEngine.Refusal(at.Host, response, cancel).ConfigureAwait(false));
            }

            if (response.Content.Headers.ContentLength > most)
            {
                throw new MediaException("the file is larger than DipTick keeps");
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
                    throw new MediaException("the file is larger than DipTick keeps");
                }

                await kept.WriteAsync(buffer.AsMemory(0, read), cancel).ConfigureAwait(false);
            }

            return (kept.ToArray(), response.Content.Headers.ContentType?.MediaType);
        }

        throw new MediaException("the file's address redirected too many times");
    }

    /// <summary>A file's media type: what its server said when it is an image's or a video's, else what its name says.</summary>
    private static string TypeOf(string name, string? said, bool video)
    {
        if (said is not null && (said.StartsWith("image/", StringComparison.Ordinal) || said.StartsWith("video/", StringComparison.Ordinal)))
        {
            return said;
        }

        return Path.GetExtension(name).ToLowerInvariantAscii() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".png" => "image/png",
            ".webm" => "video/webm",
            ".mkv" => "video/x-matroska",
            ".mp4" => "video/mp4",
            _ => video ? "video/mp4" : "image/png",
        };
    }

    /// <summary>The first address in an output: the output itself, the first of a list, or the first address among an object's values.</summary>
    private static string? First(JsonElement output) => output.ValueKind switch
    {
        JsonValueKind.String => output.GetString(),
        JsonValueKind.Array => output.EnumerateArray().Select(First).FirstOrDefault(a => a is not null),
        JsonValueKind.Object => output.EnumerateObject().Select(p => First(p.Value)).FirstOrDefault(a => a is not null),
        _ => null,
    };

    /// <summary>WaveSpeedAI's <c>data</c>, once its <c>code</c> says 200; its message otherwise.</summary>
    private static JsonElement Data(JsonElement root, string who)
    {
        if (Has(root, "code", out var code) && code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out var c) && c != 200)
        {
            throw new MediaException($"{who} refused it ({c.ToString(CultureInfo.InvariantCulture)}): {OpenAiCompatibleEngine.Clip(Text(root, "message") ?? "no reason given")}");
        }

        return Has(root, "data", out var data) && data.ValueKind == JsonValueKind.Object ? data : throw new MediaException($"{who} answered with no data");
    }

    /// <summary>The person's own fields for the model, over what DipTick put: theirs win.</summary>
    private static void Merge(JsonObject into, JsonObject extra)
    {
        foreach (var (name, value) in extra)
        {
            into[name] = value?.DeepClone();
        }
    }

    /// <summary>A member of an object; false for an answer that is not an object where one was expected, as a provider's odd answer may be.</summary>
    internal static bool Has(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value))
        {
            return true;
        }

        value = default;
        return false;
    }

    private static bool SameHost(Uri address, Uri own) =>
        address.Scheme == Uri.UriSchemeHttps && address.UserInfo.Length == 0 && string.Equals(address.Host, own.Host, StringComparison.OrdinalIgnoreCase) && address.Port == own.Port;

    private static void Bearer(HttpRequestMessage request, string? key)
    {
        if (key is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static byte[] Decode(string base64)
    {
        try
        {
            return Convert.FromBase64String(base64);
        }
        catch (FormatException ex)
        {
            throw new MediaException("what came back was not base64", ex);
        }
    }
}
