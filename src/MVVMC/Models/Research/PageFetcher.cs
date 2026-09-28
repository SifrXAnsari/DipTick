using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using DipTick.Models.Tools;

namespace DipTick.Models.Research;

/// <summary>What one fetch brought back: the page it ended on, how many redirects led there, and what it reads as.</summary>
/// <param name="Pdf">The page's bytes when it is a PDF, for a model that reads one; null otherwise.</param>
internal sealed record FetchedPage(
    Uri Address,
    int Hops,
    string MediaType,
    string Title,
    string Text,
    bool Article,
    byte[]? Pdf,
    string Sha256,
    long Bytes,
    IReadOnlyList<string> Links,
    DateTimeOffset Fetched);

/// <summary>Why a fetch did not bring a page back, in words for the model and the chat.</summary>
internal sealed class FetchException : Exception
{
    public FetchException()
    {
    }

    public FetchException(string message)
        : base(message)
    {
    }

    public FetchException(string message, Exception inner)
        : base(message, inner)
    {
    }

    /// <summary>The gate refused this hop; the model is told only that.</summary>
    public bool Refused { get; init; }
}

/// <summary>
/// Fetches one page for an agent or for the person: HTTPS and GET only,
/// no credentials in the address; the gate asked for the address and again for every redirect, each
/// hop a fetch of its own, at most <see cref="MostHops"/>; at most <see cref="MostBytes"/> and
/// <see cref="Limit"/>; HTML and XHTML read as text (<see cref="Readable"/>), plain text, Markdown, CSV,
/// JSON and XML as they are, a PDF kept as bytes; any other type refused. The connection is
/// <see cref="SafeConnect"/>'s unless another client is given.
/// </summary>
internal sealed class PageFetcher : IDisposable
{
    /// <summary>The most redirects one fetch follows.</summary>
    public const int MostHops = 5;

    /// <summary>The most a page may be: 5 MiB, well past any article and short of a download.</summary>
    public const int MostBytes = 5 * 1024 * 1024;

    /// <summary>The longest one fetch may take, inside a tool call's own 60 seconds.</summary>
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    private static readonly string[] TextTypes = ["text/plain", "text/markdown", "text/x-markdown", "text/csv", "application/json", "text/xml", "application/xml"];
    private static readonly string[] HtmlTypes = ["text/html", "application/xhtml+xml"];

    private readonly HttpClient _http;
    private readonly bool _owned;

    /// <summary>A fetcher that connects only to public addresses.</summary>
    public PageFetcher()
        : this(SafeConnect.NewClient(), owned: true)
    {
    }

    /// <summary>A fetcher over a given client.</summary>
    internal PageFetcher(HttpClient http, bool owned = false)
    {
        ArgumentNullException.ThrowIfNull(http);
        _http = http;
        _owned = owned;
    }

    public void Dispose()
    {
        if (_owned)
        {
            _http.Dispose();
        }
    }

    /// <summary>
    /// Why an address is not one DipTick fetches, or null: https only, a host, no user name or
    /// password in it (a model must not carry a credential out in an address).
    /// </summary>
    public static string? Problem(Uri address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (!address.IsAbsoluteUri || address.Scheme != Uri.UriSchemeHttps)
        {
            return "only https addresses are fetched";
        }

        if (address.UserInfo.Length > 0)
        {
            return "an address carrying a user name or password is refused";
        }

        return address.Host.Length == 0 ? "the address has no host" : null;
    }

    /// <summary>
    /// Fetches a page. <paramref name="mayGo"/> is asked for the address (hop 0) and for every redirect
    /// (hop 1 on), with the address it would go to; a no ends the fetch as refused.
    /// </summary>
    /// <exception cref="FetchException">The page could not be had, and why.</exception>
    public async Task<FetchedPage> FetchAsync(Uri address, Func<Uri, int, bool> mayGo, CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(mayGo);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        limit.CancelAfter(Limit);
        var at = new UriBuilder(address) { Fragment = string.Empty }.Uri;
        try
        {
            for (var hop = 0; ; hop++)
            {
                if (Problem(at) is { } bad)
                {
                    throw new FetchException(hop == 0 ? bad : $"a redirect led to {Shown(at)}: {bad}");
                }

                if (!mayGo(at, hop))
                {
                    throw new FetchException(hop == 0 ? "not allowed" : $"the redirect to {Shown(at)} was not allowed") { Refused = true };
                }

                using var request = new HttpRequestMessage(HttpMethod.Get, at);
                request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,text/plain;q=0.9,application/pdf;q=0.8,*/*;q=0.5");
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false);
                var status = (int)response.StatusCode;
                if (status is 301 or 302 or 303 or 307 or 308)
                {
                    if (hop >= MostHops)
                    {
                        throw new FetchException(string.Create(CultureInfo.InvariantCulture, $"it redirected more than {MostHops} times"));
                    }

                    if (response.Headers.Location is not { } next || !Uri.TryCreate(at, next.OriginalString, out var target))
                    {
                        throw new FetchException(string.Create(CultureInfo.InvariantCulture, $"{at.Host} answered {status} and said nowhere to go"));
                    }

                    at = new UriBuilder(target) { Fragment = string.Empty }.Uri;
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new FetchException(string.Create(CultureInfo.InvariantCulture, $"{at.Host} answered {status} {response.ReasonPhrase}"));
                }

                return await ReadAsync(at, hop, response.Content, limit.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new FetchException(string.Create(CultureInfo.InvariantCulture, $"no page within {Limit.TotalSeconds} s"));
        }
        catch (HttpRequestException ex)
        {
            throw new FetchException($"could not reach {at.Host}: {SafeText.Line(ex.InnerException?.Message ?? ex.Message)}", ex);
        }
    }

    /// <summary>The page's body, capped, as the reader for its type makes it.</summary>
    private static async Task<FetchedPage> ReadAsync(Uri at, int hops, HttpContent content, CancellationToken cancel)
    {
        var type = content.Headers.ContentType?.MediaType?.Trim().ToLowerInvariantAscii() ?? string.Empty;
        var html = HtmlTypes.Contains(type, StringComparer.Ordinal);
        var pdf = type == "application/pdf";
        var text = TextTypes.Contains(type, StringComparer.Ordinal) || (type.StartsWith("text/", StringComparison.Ordinal) && !html);
        if (!html && !pdf && !text)
        {
            throw new FetchException(type.Length == 0 ? "the page did not say what it is, so it is not read" : $"it is {type}, which DipTick does not read");
        }

        if (content.Headers.ContentLength is > MostBytes)
        {
            throw new FetchException(string.Create(CultureInfo.InvariantCulture, $"it is {content.Headers.ContentLength / 1024} KiB, past the {MostBytes / 1024 / 1024} MiB DipTick fetches"));
        }

        var bytes = await Capped(content, cancel).ConfigureAwait(false);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var fetched = DateTimeOffset.UtcNow;
        if (pdf)
        {
            return new FetchedPage(at, hops, type, at.Segments.LastOrDefault()?.Trim('/') ?? at.Host, string.Empty, false, bytes, hash, bytes.Length, [], fetched);
        }

        var words = Decode(bytes, content.Headers.ContentType);
        if (html)
        {
            var page = Readable.From(at, words);
            return new FetchedPage(at, hops, type, page.Title.Length > 0 ? page.Title : at.Host, page.Text, page.Article, null, hash, bytes.Length, page.Links, fetched);
        }

        return new FetchedPage(at, hops, type, at.Segments.LastOrDefault()?.Trim('/') is { Length: > 0 } name ? name : at.Host, words, false, null, hash, bytes.Length, [], fetched);
    }

    /// <summary>The body, read until it ends or passes the cap; past the cap is refused, never cut.</summary>
    private static async Task<byte[]> Capped(HttpContent content, CancellationToken cancel)
    {
        var body = await content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
        await using var bodyScope = body.ConfigureAwait(false);
        using var kept = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var read = await body.ReadAsync(buffer, cancel).ConfigureAwait(false);
            if (read == 0)
            {
                return kept.ToArray();
            }

            if (kept.Length + read > MostBytes)
            {
                throw new FetchException(string.Create(CultureInfo.InvariantCulture, $"it is longer than the {MostBytes / 1024 / 1024} MiB DipTick fetches"));
            }

            await kept.WriteAsync(buffer.AsMemory(0, read), cancel).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The body as text, in the character set the page names (Windows' code pages included, from .NET's
    /// own provider, without registering it for the whole program), or UTF-8; a byte-order mark wins.
    /// </summary>
    private static string Decode(byte[] bytes, MediaTypeHeaderValue? type)
    {
        var encoding = Encoding.UTF8;
        if (type?.CharSet is { Length: > 0 } charset)
        {
            var name = charset.Trim('"', ' ');
            try
            {
                encoding = CodePagesEncodingProvider.Instance.GetEncoding(name) ?? Encoding.GetEncoding(name);
            }
            catch (ArgumentException)
            {
                // A character set .NET does not know is read as UTF-8.
            }
        }

        using var reader = new StreamReader(new MemoryStream(bytes), encoding, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>An address as a person reads it in a message: its host and path, made safe.</summary>
    private static string Shown(Uri address) => SafeText.Line(address.GetLeftPart(UriPartial.Path));
}
