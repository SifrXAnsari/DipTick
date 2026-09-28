using System.Text.RegularExpressions;
using DipTick.Models.Chat.Store;

namespace DipTick.Models.Tools;

/// <summary>Where an address a chat has seen came from.</summary>
internal enum SeenFrom
{
    /// <summary>The person wrote it.</summary>
    Person,

    /// <summary>A search result gave it.</summary>
    Search,

    /// <summary>A page DipTick fetched in this chat linked to it.</summary>
    Page,
}

/// <summary>
/// The web addresses a chat has seen, and where each came from: an agent may
/// fetch only an address that appeared in the person's messages, in a search result, or in a page
/// already fetched in the chat, so a model cannot send data out in an address it made up. Anthropic
/// applies the same rule to its own fetch tool. Addresses are kept without their fragment.
/// </summary>
internal sealed partial class Seen
{
    private readonly Dictionary<string, SeenFrom> _addresses = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    /// <summary>What a chat's kept lines and sources say it has seen: the person's addresses and every source's.</summary>
    public static Seen From(IReadOnlyList<StoredMessage> lines, IReadOnlyList<StoredSource> sources)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(sources);
        var seen = new Seen();
        foreach (var line in lines.Where(l => l.Author == Author.Person))
        {
            seen.AddAll(line.Text, SeenFrom.Person);
        }

        foreach (var source in sources)
        {
            seen.Add(source.Address, SeenFrom.Page);
        }

        return seen;
    }

    /// <summary>Every https address in <paramref name="text"/>, as seen from <paramref name="from"/>.</summary>
    public void AddAll(string text, SeenFrom from)
    {
        ArgumentNullException.ThrowIfNull(text);
        foreach (Match match in Address().Matches(text))
        {
            Add(match.Value.TrimEnd('.', ',', ';', ':', ')', ']', '"', '\''), from);
        }
    }

    /// <summary>One address as seen from <paramref name="from"/>; the first place it was seen is kept.</summary>
    public void Add(string address, SeenFrom from)
    {
        if (Normal(address) is not { } key)
        {
            return;
        }

        lock (_lock)
        {
            _addresses.TryAdd(key, from);
        }
    }

    /// <summary>Where the chat first saw this address, or null when it never did.</summary>
    public SeenFrom? Origin(string address)
    {
        if (Normal(address) is not { } key)
        {
            return null;
        }

        lock (_lock)
        {
            return _addresses.TryGetValue(key, out var from) ? from : null;
        }
    }

    /// <summary>An address as kept: absolute, http or https, without its fragment; null when it is not one.</summary>
    public static string? Normal(string address)
    {
        if (!Uri.TryCreate(address?.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return null;
        }

        return new UriBuilder(uri) { Fragment = string.Empty }.Uri.AbsoluteUri;
    }

    [GeneratedRegex(@"https?://[^\s<>""'`\]\)]+", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex Address();
}
