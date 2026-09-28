using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using DipTick.Models.Chat;

namespace DipTick.Models.Tools;

/// <summary>One step of a check: what it was, whether it worked, how long it took, and what it found.</summary>
internal sealed record ProbeStep(string Name, bool Ok, TimeSpan Took, string Detail);

/// <summary>A check of one host and port: each step in order, stopping at the first that fails.</summary>
internal sealed record ProbeResult(string Host, int Port, string Why, IReadOnlyList<ProbeStep> Steps)
{
    public bool Ok => Steps.Count > 0 && Steps.All(s => s.Ok);

    /// <summary>The check in lines, for a person and for a model.</summary>
    public string Words
    {
        get
        {
            var text = new StringBuilder(string.Create(CultureInfo.InvariantCulture, $"{Host}:{Port} ({Why}): {(Ok ? "reachable" : "not reachable")}"));
            foreach (var step in Steps)
            {
                text.Append(CultureInfo.InvariantCulture, $"\n  {(step.Ok ? "ok  " : "FAIL")} {step.Name,-10} {step.Took.TotalMilliseconds,6:0} ms  {step.Detail}");
            }

            return text.ToString();
        }
    }
}

/// <summary>A host DipTick itself talks to, and why: a provider, the search service, a connector, or the person's list.</summary>
internal sealed record KnownHost(string Host, int Port, bool Https, string Why);

/// <summary>
/// The network check: is this PC online, and can DipTick reach each service it
/// uses: the name looked up, the connection, TLS where the service speaks it, and the HTTP status, each
/// timed. One host and one port at a time; for an agent (<c>net_check</c>), only hosts DipTick already
/// talks to or the person's list names, on their own port, through <c>net.probe</c>. Never a scan.
/// </summary>
internal static class NetworkCheck
{
    private static readonly TimeSpan StepLimit = TimeSpan.FromSeconds(10);

    /// <summary>Whether Windows says any network is up, and which interfaces are.</summary>
    public static (bool Online, IReadOnlyList<string> Interfaces) Here()
    {
        try
        {
            var up = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                .Select(n => $"{n.Name} ({n.NetworkInterfaceType})")
                .ToList();
            return (NetworkInterface.GetIsNetworkAvailable(), up);
        }
        catch (NetworkInformationException)
        {
            return (false, []);
        }
    }

    /// <summary>
    /// The hosts DipTick talks to: the model that answers you and each agent's provider, the search
    /// service, each connector, and the hosts in the permissions' list; each once.
    /// </summary>
    public static IReadOnlyList<KnownHost> Known(MasterConfig config, IEnumerable<Uri>? more = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        var hosts = new List<KnownHost>();
        void Add(Uri? uri, string why)
        {
            if (uri is null || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            {
                return;
            }

            var host = uri.IdnHost.TrimEnd('.');
            if (!hosts.Any(h => string.Equals(h.Host, host, StringComparison.OrdinalIgnoreCase) && h.Port == uri.Port))
            {
                hosts.Add(new KnownHost(host, uri.Port, uri.Scheme == Uri.UriSchemeHttps, why));
            }
        }

        if (config.MainProvider.Length > 0)
        {
            Add(Engines.Endpoint(config.MainProvider, config.MainEndpoint), $"{Engines.DisplayName(config.MainProvider, config.MainEndpoint)}, the model that answers you");
        }

        foreach (var seat in config.Seats.Where(s => s.Provider.Length > 0))
        {
            Add(Engines.Endpoint(seat.Provider, seat.Endpoint), $"{Engines.DisplayName(seat.Provider, seat.Endpoint)}, {seat.Id}'s provider");
        }

        foreach (var service in Research.SearchService.Hosts(config))
        {
            Add(service, "the search service");
        }

        foreach (var uri in more ?? [])
        {
            Add(uri, "a connector");
        }

        // A wildcard names no host to check: "*" is any public site, "*.example.com" any of its names.
        foreach (var entry in config.Permissions.Hosts.Where(h => h != "*" && !h.StartsWith("*.", StringComparison.Ordinal)))
        {
            var (name, port) = Harness.Policy.SplitHost(entry);
            if (!hosts.Any(h => string.Equals(h.Host, name, StringComparison.OrdinalIgnoreCase) && h.Port == port))
            {
                hosts.Add(new KnownHost(name, port, true, "in your list of web hosts"));
            }
        }

        return hosts;
    }

    /// <summary>
    /// Looks the name up, connects, speaks TLS where <paramref name="https"/>, and asks <c>HEAD /</c> for the
    /// HTTP status, each within ten seconds; stops at the first step that fails, and says why.
    /// </summary>
    public static async Task<ProbeResult> ProbeAsync(string host, int port, bool https, string why, CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(host);
        var steps = new List<ProbeStep>();
        var clock = Stopwatch.StartNew();
        IPAddress[] addresses;
        try
        {
            using var limit = Limit(cancel);
            addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, limit.Token).ConfigureAwait(false);
            steps.Add(new ProbeStep("name", addresses.Length > 0, clock.Elapsed, addresses.Length == 0 ? "no address" : string.Join(", ", addresses.Take(3).Select(a => a.ToString()))));
            if (addresses.Length == 0)
            {
                return new ProbeResult(host, port, why, steps);
            }
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ArgumentException)
        {
            steps.Add(new ProbeStep("name", false, clock.Elapsed, Why(ex)));
            return new ProbeResult(host, port, why, steps);
        }

        using var client = new TcpClient();
        clock.Restart();
        try
        {
            using var limit = Limit(cancel);
            await client.ConnectAsync(addresses, port, limit.Token).ConfigureAwait(false);
            steps.Add(new ProbeStep("connect", true, clock.Elapsed, client.Client.RemoteEndPoint?.ToString() ?? string.Empty));
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            steps.Add(new ProbeStep("connect", false, clock.Elapsed, Why(ex)));
            return new ProbeResult(host, port, why, steps);
        }

        var authority = Authority(host, port, https);
        if (!https)
        {
            await AskAsync(client.GetStream(), authority, steps, clock, cancel).ConfigureAwait(false);
            return new ProbeResult(host, port, why, steps);
        }

        var tls = new SslStream(client.GetStream(), leaveInnerStreamOpen: true);
        await using (tls.ConfigureAwait(false))
        {
            clock.Restart();
            try
            {
                using var limit = Limit(cancel);
                await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, limit.Token).ConfigureAwait(false);
                var certificate = tls.RemoteCertificate is { } c ? $"{c.Subject}, until {c.GetExpirationDateString()}" : "no certificate";
                steps.Add(new ProbeStep("tls", true, clock.Elapsed, $"{tls.SslProtocol}; {certificate}"));
            }
            catch (Exception ex) when (ex is AuthenticationException or IOException or OperationCanceledException)
            {
                steps.Add(new ProbeStep("tls", false, clock.Elapsed, Why(ex)));
                return new ProbeResult(host, port, why, steps);
            }

            await AskAsync(tls, authority, steps, clock, cancel).ConfigureAwait(false);
        }

        return new ProbeResult(host, port, why, steps);
    }

    /// <summary>
    /// What the request's Host field says: the address's authority, with its port when that is not the
    /// scheme's own (RFC 9110, 7.2; RFC 3986, 3.2.3), and an IPv6 address in brackets.
    /// </summary>
    internal static string Authority(string host, int port, bool https)
    {
        ArgumentNullException.ThrowIfNull(host);
        var name = host.Contains(':', StringComparison.Ordinal) && !host.StartsWith('[') ? $"[{host}]" : host;
        return port == (https ? 443 : 80) ? name : string.Create(CultureInfo.InvariantCulture, $"{name}:{port}");
    }

    /// <summary>The last step: <c>HEAD /</c> asked, and the status line that came back, or why none did.</summary>
    private static async Task AskAsync(Stream stream, string authority, List<ProbeStep> steps, Stopwatch clock, CancellationToken cancel)
    {
        clock.Restart();
        try
        {
            using var limit = Limit(cancel);
            var ask = Encoding.ASCII.GetBytes($"HEAD / HTTP/1.1\r\nHost: {authority}\r\nUser-Agent: DipTick\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(ask, limit.Token).ConfigureAwait(false);
            var buffer = new byte[256];
            var read = await stream.ReadAsync(buffer, limit.Token).ConfigureAwait(false);
            var first = Encoding.ASCII.GetString(buffer, 0, read).Split("\r\n")[0];
            var ok = first.StartsWith("HTTP/", StringComparison.Ordinal);
            steps.Add(new ProbeStep("http", ok, clock.Elapsed, ok ? first : "no HTTP answer"));
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
        {
            steps.Add(new ProbeStep("http", false, clock.Elapsed, Why(ex)));
        }
    }

    private static CancellationTokenSource Limit(CancellationToken cancel)
    {
        var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        limit.CancelAfter(StepLimit);
        return limit;
    }

    private static string Why(Exception ex) => ex is OperationCanceledException ? "no answer within 10 s" : Models.SafeText.Line(ex.Message);
}
