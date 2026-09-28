using System.Globalization;

namespace DipTick.Models.Harness;

internal enum Verdict
{
    /// <summary>Do it.</summary>
    Allow,

    /// <summary>Do it only if a person says yes now.</summary>
    Ask,

    /// <summary>Allowed, but --dry-run is on: report it, do nothing.</summary>
    DryRun,

    /// <summary>Never.</summary>
    Deny,
}

/// <summary>A decision, and the reason, which goes to the audit log and to the person but never to the model.</summary>
/// <param name="NeedsPerson">Denied only because a yes was needed and nobody could be asked (exit 3, not 1).</param>
internal readonly record struct Decision(Verdict Verdict, string Reason, bool NeedsPerson = false);

/// <summary>
/// Decides one request against the permissions and the run's flags. Pure but for looking at the
/// target's own path on disk, so the same inputs give the same answer and a decision can be
/// printed with <c>diptick policy check</c> before anything runs. It never throws: anything it
/// cannot judge is denied.
/// </summary>
/// <remarks>
/// Order, first match wins: a target with control characters, an unknown or badly scoped
/// target, or a protected file is denied; a deny rule denies; an ask rule asks; an allow rule
/// allows; nothing matching denies. Then the flags: anything consequential under
/// <c>--dry-run</c> becomes DryRun and nobody is asked; an Ask with <c>--yes</c> becomes Allow
/// only for what is not consequential (a model cannot talk a <c>--yes</c> into a write, a delete
/// or a fetch that carries data out); an Ask with no one to ask is denied, and says so.
/// </remarks>
internal static class Policy
{
    private const int MaxTarget = 2048;

    public static Decision Decide(Capability cap, string? target, Permissions permissions, RunFlags flags) =>
        Decide(cap, target, permissions, flags, seat: null);

    /// <summary>
    /// As above, for one agent's seat: its lane narrows what it may ask for at all (anything outside
    /// the lane is denied whatever the permissions say), and its own ask list turns an allow into
    /// an ask. A seat can only ever get less than the permissions give.
    /// <paramref name="chat"/> is the chat's own rules, read last.
    /// <paramref name="vouchedBy"/> says where a target came from when that answers the settings' own
    /// ask (an address the person wrote, or a search or a fetched page gave);
    /// never an agent's own ask list, the chat's ask, or a deny.
    /// </summary>
    public static Decision Decide(Capability cap, string? target, Permissions permissions, RunFlags flags, SeatConfig? seat, IReadOnlyList<ChatRule>? chat = null, string? vouchedBy = null)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        ArgumentNullException.ThrowIfNull(flags);

        if (seat is not null && !seat.Lane.Any(r => Capabilities.Matches(r, cap, target)))
        {
            return new Decision(Verdict.Deny, $"outside {seat.Id}'s lane");
        }

        try
        {
            // The seat's own ask list turns what the permissions allow into a question; deny still wins,
            // and where an address came from never answers the agent's own ask.
            var asks = seat?.Ask.Where(r => Capabilities.Matches(r, cap, target)).ToList() ?? [];
            return asks.Count == 0
                ? DecideCore(cap, target, permissions, flags, chat ?? [], vouchedBy)
                : DecideCore(cap, target, permissions with { Ask = [.. permissions.Ask, .. asks] }, flags, chat ?? [], null);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or UriFormatException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new Decision(Verdict.Deny, "the target could not be judged");
        }
    }

    /// <summary>
    /// Whether an agent could ever be allowed this capability, for some target: it is in the agent's
    /// lane, no rule with no target denies it (the settings' or the chat's),
    /// and some rule allows or asks for it, for every target or one. A tool nothing could allow is not
    /// offered to the model at all.
    /// </summary>
    public static bool CouldAllow(Capability cap, Permissions permissions, SeatConfig? seat, IReadOnlyList<ChatRule>? chat)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        if (seat is not null && !seat.Lane.Any(r => Capabilities.SpeaksOf(r, cap)))
        {
            return false;
        }

        if (permissions.Deny.Any(r => Capabilities.Matches(r, cap)) || (chat ?? []).Any(r => r.Verdict == Verdict.Deny && Capabilities.Matches(r.Rule, cap)))
        {
            return false;
        }

        return permissions.Allow.Concat(permissions.Ask).Any(r => Capabilities.SpeaksOf(r, cap));
    }

    private static Decision DecideCore(Capability cap, string? target, Permissions permissions, RunFlags flags, IReadOnlyList<ChatRule> chat, string? vouchedBy)
    {
        if (target is not null && (target.Length > MaxTarget || SafeText.HasControls(target)))
        {
            return new Decision(Verdict.Deny, target.Length > MaxTarget ? "the target is too long" : "the target holds control characters");
        }

        if (ScopeProblem(cap, target, permissions) is { } scope)
        {
            return new Decision(Verdict.Deny, scope);
        }

        Decision decision;
        if (permissions.Deny.FirstOrDefault(r => Capabilities.Matches(r, cap, target)) is { } denied)
        {
            return new Decision(Verdict.Deny, $"denied by rule '{denied}'");
        }
        else if (permissions.Ask.FirstOrDefault(r => Capabilities.Matches(r, cap, target)) is { } asked)
        {
            decision = new Decision(Verdict.Ask, $"needs a yes, by rule '{asked}'");
        }
        else if (permissions.Allow.FirstOrDefault(r => Capabilities.Matches(r, cap, target)) is { } allowed)
        {
            decision = new Decision(Verdict.Allow, $"allowed by rule '{allowed}'");
        }
        else
        {
            return new Decision(Verdict.Deny, "no rule allows it");
        }

        // An address with provenance answers the settings' own ask, before
        // the chat's rules are read, so a chat that asks for every fetch still asks.
        if (decision.Verdict == Verdict.Ask && vouchedBy is not null)
        {
            decision = new Decision(Verdict.Allow, $"{decision.Reason}, answered by where the address came from ({vouchedBy})");
        }

        // The chat's own rules: they tighten anything, and loosen only an ask, and
        // only in this chat. The order stays deny, then ask, then allow: a chat's allow can never
        // carve an exception out of a deny, the settings' or its own.
        if (chat.FirstOrDefault(r => r.Verdict == Verdict.Deny && Capabilities.Matches(r.Rule, cap, target)) is { } chatDenied)
        {
            return new Decision(Verdict.Deny, $"denied in this chat by '{chatDenied.Rule}'");
        }

        if (decision.Verdict == Verdict.Allow && chat.FirstOrDefault(r => r.Verdict == Verdict.Ask && Capabilities.Matches(r.Rule, cap, target)) is { } chatAsked)
        {
            decision = new Decision(Verdict.Ask, $"needs a yes in this chat, by '{chatAsked.Rule}'");
        }
        else if (decision.Verdict == Verdict.Ask && chat.FirstOrDefault(r => r.Verdict == Verdict.Allow && Capabilities.Matches(r.Rule, cap, target)) is { } chatAllowed)
        {
            decision = new Decision(Verdict.Allow, $"allowed for this chat by '{chatAllowed.Rule}' ({decision.Reason})");
        }

        // Running code never goes without a person: a rule that allows it, the
        // settings' or a chat's, is read as asking.
        if (cap == Capability.ShellRun && decision.Verdict == Verdict.Allow)
        {
            decision = new Decision(Verdict.Ask, $"{decision.Reason}; running code always asks");
        }

        var consequential = Capabilities.ChangesState(cap);

        // Dry run first: nothing consequential happens, so nobody needs asking.
        if (consequential && flags.DryRun)
        {
            return new Decision(Verdict.DryRun, decision.Reason + "; --dry-run, nothing done");
        }

        if (decision.Verdict == Verdict.Ask)
        {
            if (flags.Yes && !consequential)
            {
                return new Decision(Verdict.Allow, decision.Reason + "; --yes answered");
            }

            if (!flags.Interactive)
            {
                return new Decision(Verdict.Deny, decision.Reason + ", and there is no one to ask", NeedsPerson: true);
            }
        }

        return decision;
    }

    private static string? ScopeProblem(Capability cap, string? target, Permissions permissions) =>
        Capabilities.ScopeOf(cap) switch
        {
            Scope.Path => PathProblem(target, permissions),
            Scope.Host => HostProblem(target, permissions.Hosts),
            _ => null,
        };

    /// <summary>
    /// A path must be absolute, plain, under a root, reach it through no link, not be a file with
    /// a second name, and not be one of DipTick's own files. Plain means no device or
    /// <c>\\?\</c> prefix, no alternate data stream, no <c>..</c> left after normalising. No link
    /// means no reparse point on any existing part of the path: a junction is how a path inside a
    /// root ends up outside it, and a hard link is how a file inside a root is also a file
    /// outside it.
    /// </summary>
    private static string? PathProblem(string? target, Permissions permissions)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return "no path given";
        }

        if (target.StartsWith(@"\\", StringComparison.Ordinal) || target.StartsWith("//", StringComparison.Ordinal))
        {
            return "device and network paths are refused";
        }

        if (!Path.IsPathFullyQualified(target))
        {
            return "the path is not absolute";
        }

        var full = Path.GetFullPath(target);

        // A colon anywhere after the drive's is a stream name: file.txt:hidden.
        if (full.IndexOf(':', 2) >= 0)
        {
            return "alternate data streams are refused";
        }

        // DipTick's own files are never an agent's, whatever the roots say: the master config
        // (anywhere, under any folder), the config in use, and the data folder with the audit log.
        if (string.Equals(Path.GetFileName(full), MasterConfig.FileName, StringComparison.OrdinalIgnoreCase)
            || permissions.Protected.Any(p => Within(full, Path.GetFullPath(p))))
        {
            return "DipTick's own files are refused";
        }

        var root = permissions.Roots.Select(r => Path.GetFullPath(r)).FirstOrDefault(r => Within(full, r));
        if (root is null)
        {
            return permissions.Roots.Count == 0 ? "no folder is open to agents" : "the path is outside every allowed folder";
        }

        var rootLength = Path.TrimEndingDirectorySeparator(root).Length;
        for (var at = full; at.Length >= rootLength; at = Path.GetDirectoryName(at) ?? string.Empty)
        {
            try
            {
                if (File.Exists(at) || Directory.Exists(at))
                {
                    if ((File.GetAttributes(at) & FileAttributes.ReparsePoint) != 0)
                    {
                        return "the path goes through a link";
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return "the path cannot be checked";
            }

            if (at.Length <= rootLength)
            {
                break;
            }
        }

        if (File.Exists(full))
        {
            var links = FileFacts.LinkCount(full);
            if (links is null)
            {
                return "the file cannot be checked";
            }

            if (links > 1)
            {
                return "the file has another name (a hard link)";
            }
        }

        return null;
    }

    /// <summary>
    /// Whether <paramref name="path"/> is <paramref name="root"/> or inside it. A root that already
    /// ends in a separator (a drive, <c>C:\</c>) is a prefix as it stands.
    /// </summary>
    private static bool Within(string path, string root)
    {
        var bare = Path.TrimEndingDirectorySeparator(root);
        if (path.Equals(bare, StringComparison.OrdinalIgnoreCase) || path.Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var prefix = Path.EndsInDirectorySeparator(bare) ? bare : bare + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// https only, no credentials in the URL, and a host (and port) the list names. An entry with
    /// no port allows only https's own, 443; <c>host:port</c> allows that port.
    /// </summary>
    private static string? HostProblem(string? target, IReadOnlyList<string> hosts)
    {
        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return "only https URLs are fetched";
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return "a URL carrying credentials is refused";
        }

        string host;
        try
        {
            host = uri.IdnHost.TrimEnd('.');
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or UriFormatException)
        {
            return "the host name is not valid";
        }

        var port = uri.Port;

        // "*" is any public site on https's own port: never this PC, a private network or a link-local
        // address, by name or by number. What a name resolves to is checked again at connect time.
        if (hosts.Contains("*") && port == 443 && !IsLocal(host))
        {
            return null;
        }

        var ok = hosts.Any(entry =>
        {
            if (entry == "*")
            {
                return false;
            }

            var (name, allowedPort) = SplitHost(entry);
            if (port != allowedPort)
            {
                return false;
            }

            return name.StartsWith("*.", StringComparison.Ordinal)
                ? host.EndsWith(name[1..], StringComparison.OrdinalIgnoreCase)
                : string.Equals(name, host, StringComparison.OrdinalIgnoreCase);
        });
        return ok ? null : hosts.Count == 0 ? "no host is open to agents" : $"host '{host}:{port.ToString(CultureInfo.InvariantCulture)}' is not allowed";
    }

    /// <summary>
    /// Whether a host names this PC, a private network or a link-local address: localhost and its
    /// kind, and the IPv4 and IPv6 ranges RFC 1918, RFC 6890 and RFC 4193 set aside (127/8, 10/8,
    /// 172.16/12, 192.168/16, 169.254/16, 100.64/10, 0/8, ::1, fc00::/7, fe80::/10). A name that only
    /// resolves to one is caught where it connects (<see cref="Research.SafeConnect"/>).
    /// </summary>
    public static bool IsLocal(string host)
    {
        ArgumentNullException.ThrowIfNull(host);
        var name = host.Trim('[', ']').TrimEnd('.');
        if (name.Equals("localhost", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".local", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".internal", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return System.Net.IPAddress.TryParse(name, out var address) && IsLocal(address);
    }

    /// <summary>Whether an address is this PC's, a private network's or link-local (the ranges <see cref="IsLocal(string)"/> names).</summary>
    public static bool IsLocal(System.Net.IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (System.Net.IPAddress.IsLoopback(address) || address.Equals(System.Net.IPAddress.Any) || address.Equals(System.Net.IPAddress.IPv6Any))
        {
            return true;
        }

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] is 0 or 10 or 127
                || (b[0] == 172 && b[1] is >= 16 and <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254)
                || (b[0] == 100 && b[1] is >= 64 and <= 127);
        }

        // IPv6: unique local fc00::/7, link-local fe80::/10, site-local fec0::/10 (deprecated, still refused).
        var v6 = address.GetAddressBytes();
        return (v6[0] & 0xFE) == 0xFC || (v6[0] == 0xFE && (v6[1] & 0xC0) is 0x80 or 0xC0);
    }

    /// <summary>An allow-list entry as name and port; 443 when it names none.</summary>
    public static (string Name, int Port) SplitHost(string entry)
    {
        var colon = entry.LastIndexOf(':');
        if (colon > 0 && int.TryParse(entry.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var port))
        {
            return (entry[..colon], port);
        }

        return (entry, 443);
    }
}
