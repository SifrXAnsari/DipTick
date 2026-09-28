using System.Net;
using System.Net.Sockets;
using System.Reflection;
using DipTick.Models.Harness;

namespace DipTick.Models.Research;

/// <summary>
/// The connection every page DipTick fetches goes through: the name
/// looked up by DipTick itself, every address it gives refused that is this PC's, a private network's or
/// link-local (<see cref="Policy.IsLocal(IPAddress)"/>), and the first public one connected. The gate
/// judged the address as it was written; this judges where it leads, so a name that resolves to
/// 127.0.0.1 or 169.254.169.254 is refused where it connects.
/// </summary>
/// <remarks>
/// No proxy, so what is checked is what is reached; no cookies, so one site cannot recognise a visit
/// from another chat; no redirect followed by the client, so every hop goes back to the caller and
/// through the gate; no timeout of its own, since the caller's token decides.
/// </remarks>
internal static class SafeConnect
{
    /// <summary>What a fetched page is told DipTick is: its name and version, nothing about the person.</summary>
    public static string UserAgent { get; } = "DipTick/" + (Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0");

    /// <summary>A client for fetching pages for agents, which connects only to public addresses.</summary>
    public static HttpClient NewClient() => NewClient(allowLocal: false);

    /// <summary>
    /// As <see cref="NewClient()"/>; with <paramref name="allowLocal"/>, this PC and a private network may
    /// be reached too, for a connector you said may reach them. No proxy, no
    /// cookies and no redirect either way.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The handler is owned by the HttpClient from the moment it is constructed with disposeHandler: true, and the " +
                        "caller disposes the client; the catch disposes the handler if the client never took it. CA2000 does not " +
                        "follow ownership through that constructor.")]
    public static HttpClient NewClient(bool allowLocal)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = allowLocal ? null : ConnectAsync,
        };
        try
        {
            var client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            return client;
        }
        catch
        {
            handler.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Looks the name up, keeps only the public addresses, and connects to the first that answers; a
    /// name with no public address is refused before any connection is made.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The socket is owned by the NetworkStream from the moment it is constructed with ownsSocket: true, and the " +
                        "handler disposes the stream with its connection; the catch disposes the socket if the stream never took it. " +
                        "CA2000 does not follow ownership through that constructor.")]
    internal static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(context);
        var host = context.DnsEndPoint.Host;
        var addresses = IPAddress.TryParse(host.Trim('[', ']'), out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(host, cancel).ConfigureAwait(false);
        var open = Public(addresses);
        if (open.Count == 0)
        {
            throw new HttpRequestException(addresses.Length == 0
                ? $"{host} has no address"
                : $"{host} leads to this PC, a private network or a link-local address, which DipTick never fetches from");
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        try
        {
            socket.NoDelay = true;
            await socket.ConnectAsync([.. open], context.DnsEndPoint.Port, cancel).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>The addresses a fetch may reach: every one that is not this PC's, a private network's or link-local.</summary>
    internal static IReadOnlyList<IPAddress> Public(IEnumerable<IPAddress> addresses)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        return [.. addresses.Where(a => !Policy.IsLocal(a))];
    }
}
