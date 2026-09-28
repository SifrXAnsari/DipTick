using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ModelContextProtocol.Authentication;

namespace DipTick.Models.Connectors;

/// <summary>Why a connector could not be used, in words; <see cref="NeedsSignIn"/> when signing in would help.</summary>
internal sealed class ConnectorException : Exception
{
    public ConnectorException()
    {
    }

    public ConnectorException(string message)
        : base(message)
    {
    }

    public ConnectorException(string message, Exception inner)
        : base(message, inner)
    {
    }

    /// <summary>It needs you to sign in first: Settings > Connectors > Connect, or <c>diptick connectors signin</c>.</summary>
    public bool NeedsSignIn { get; init; }
}

/// <summary>
/// Signing in to a connector in your browser: the service's page opened only
/// when it is an https address, and its answer taken on this PC's own address, <c>127.0.0.1</c>, on a port
/// opened for that one sign-in (RFC 8252 §7.3, with the IP literal as §8.3 asks). The answer's
/// <c>code</c>, <c>state</c> and <c>iss</c> go back to the SDK, which checks the state and the issuer
/// before the code is used. What the service says when it refuses is not shown: until its issuer is
/// checked, it could be anyone's words (the MCP specification, Authorization Response Validation).
/// </summary>
internal static class LoopbackSignIn
{
    /// <summary>The path the answer comes back on.</summary>
    public const string CallbackPath = "/callback";

    /// <summary>How long a sign-in may take in the browser.</summary>
    public static readonly TimeSpan Wait = TimeSpan.FromMinutes(5);

    /// <summary>The most of a request DipTick reads from the browser.</summary>
    private const int MostRequest = 16 * 1024;

    /// <summary>A port on 127.0.0.1 nobody is listening on now, chosen by Windows.</summary>
    public static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>The address a sign-in's answer comes back to.</summary>
    public static Uri RedirectFor(int port) => new(string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{port}{CallbackPath}"));

    /// <summary>Opens an https address in your default browser, and only an https address.</summary>
    public static bool Open(Uri address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        try
        {
            using var browser = Process.Start(new ProcessStartInfo(address.AbsoluteUri) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// The browser sent to the service's sign-in page, and its answer awaited on the loopback address:
    /// one answer on the callback path, within <see cref="Wait"/>. <paramref name="say"/> is told the page's
    /// address, to copy when the browser did not open. <paramref name="open"/> opens the page: the default
    /// browser, unless the caller gives another way.
    /// </summary>
    /// <exception cref="ConnectorException">The sign-in did not finish, and why.</exception>
    public static async Task<AuthorizationResult?> WaitAsync(AuthorizationCallbackContext context, Action<string> say, CancellationToken cancel, Func<Uri, bool>? open = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(say);
        var page = context.AuthorizationUri;
        var back = context.RedirectUri;
        if (page is null || page.Scheme != Uri.UriSchemeHttps)
        {
            throw new ConnectorException("the service's sign-in page is not an https address, so DipTick does not open it");
        }

        if (back is null || back.Scheme != Uri.UriSchemeHttp || !IPAddress.TryParse(back.Host, out var host) || !IPAddress.IsLoopback(host))
        {
            throw new ConnectorException("the sign-in's answer would not come back to this PC's own address");
        }

        using var listener = new TcpListener(IPAddress.Loopback, back.Port);
        try
        {
            listener.Start(1);
        }
        catch (SocketException ex)
        {
            throw new ConnectorException(string.Create(CultureInfo.InvariantCulture, $"port {back.Port} on this PC is in use, so the sign-in's answer cannot come back ({ex.Message}); 'diptick connectors add' with another --callback-port changes it"), ex);
        }

        try
        {
            say($"Sign in in your browser; DipTick waits up to {Wait.TotalMinutes.ToString(CultureInfo.InvariantCulture)} minutes. If the browser did not open, go to {page.AbsoluteUri}");
            _ = (open ?? Open)(page);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            limit.CancelAfter(Wait);
            for (var tries = 0; tries < 20; tries++)
            {
                using var client = await listener.AcceptTcpClientAsync(limit.Token).ConfigureAwait(false);
                var stream = client.GetStream();
                var target = await RequestTargetAsync(stream, limit.Token).ConfigureAwait(false);
                if (target is null || !target.StartsWith(CallbackPath, StringComparison.Ordinal) || (target.Length > CallbackPath.Length && target[CallbackPath.Length] != '?'))
                {
                    await AnswerAsync(stream, 404, "Not here", "This address only takes a sign-in's answer.", limit.Token).ConfigureAwait(false);
                    continue;
                }

                var query = Query(target.Length > CallbackPath.Length ? target[(CallbackPath.Length + 1)..] : string.Empty);
                if (query.TryGetValue("code", out var code) && code.Length > 0 && query.TryGetValue("state", out var state))
                {
                    await AnswerAsync(stream, 200, "Signed in", "You are signed in. You can close this tab and go back to DipTick.", limit.Token).ConfigureAwait(false);
                    return new AuthorizationResult { Code = code, State = state, Iss = query.GetValueOrDefault("iss") };
                }

                await AnswerAsync(stream, 200, "Not signed in", "The sign-in did not finish. You can close this tab; DipTick says what to do next.", limit.Token).ConfigureAwait(false);
                throw new ConnectorException("the service did not sign you in: it said no, or the sign-in was left. Connect again to retry");
            }

            throw new ConnectorException("the sign-in's answer never came");
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new ConnectorException(string.Create(CultureInfo.InvariantCulture, $"{Wait.TotalMinutes} minutes passed without the sign-in's answer; Connect again to retry"));
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>The target of the browser's request line ("GET /callback?… HTTP/1.1"), with the rest of the request read and left; null when it is not a GET.</summary>
    private static async Task<string?> RequestTargetAsync(NetworkStream stream, CancellationToken cancel)
    {
        var read = new List<byte>();
        var buffer = new byte[1024];
        while (read.Count < MostRequest)
        {
            var n = await stream.ReadAsync(buffer, cancel).ConfigureAwait(false);
            if (n == 0)
            {
                break;
            }

            read.AddRange(buffer.AsSpan(0, n));
            if (EndsHeaders(read))
            {
                break;
            }
        }

        var text = Encoding.ASCII.GetString([.. read]);
        var line = text.Split("\r\n", 2)[0];
        var parts = line.Split(' ');
        return parts.Length == 3 && parts[0] == "GET" && parts[2].StartsWith("HTTP/", StringComparison.Ordinal) ? parts[1] : null;
    }

    private static bool EndsHeaders(List<byte> read)
    {
        var n = read.Count;
        return n >= 4 && read[n - 4] == '\r' && read[n - 3] == '\n' && read[n - 2] == '\r' && read[n - 1] == '\n';
    }

    /// <summary>A query's names and values, decoded; a name given twice keeps its first value.</summary>
    internal static Dictionary<string, string> Query(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var at = pair.IndexOf('=', StringComparison.Ordinal);
            var name = Decode(at < 0 ? pair : pair[..at]);
            var value = at < 0 ? string.Empty : Decode(pair[(at + 1)..]);
            values.TryAdd(name, value);
        }

        return values;
    }

    private static string Decode(string text) => Uri.UnescapeDataString(text.Replace('+', ' '));

    /// <summary>A small page of DipTick's own words: nothing from the request is shown in it.</summary>
    private static async Task AnswerAsync(NetworkStream stream, int status, string title, string words, CancellationToken cancel)
    {
        var body = Encoding.UTF8.GetBytes($"<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>DipTick: {title}</title></head><body style=\"font-family:system-ui,sans-serif;margin:3em\"><h1>{title}</h1><p>{words}</p></body></html>");
        var head = Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture, $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Not Found")}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nReferrer-Policy: no-referrer\r\nConnection: close\r\n\r\n"));
        await stream.WriteAsync(head, cancel).ConfigureAwait(false);
        await stream.WriteAsync(body, cancel).ConfigureAwait(false);
        await stream.FlushAsync(cancel).ConfigureAwait(false);
    }
}
