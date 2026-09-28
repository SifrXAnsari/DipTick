using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using DipTick.Models.Chat;

namespace DipTick.Models.Providers;

/// <summary>
/// A Google Compute Engine machine's own account: a token from the machine's metadata server, asked only
/// when <c>providers.googleMachineAccount</c> is on (it is off by default), no sign-in file is there, and
/// the firmware says the machine is one of Google's.
/// </summary>
/// <remarks>
/// <para>
/// From Google's pages (docs.cloud.google.com): a Compute Engine VM on Windows shows manufacturer "Google"
/// and model "Google Compute Engine" (<c>wmic computersystem get model,manufacturer</c>); those are SMBIOS
/// type 1's Manufacturer and Product Name (offsets 4 and 5, each a string's number, 0 for none: Linux's
/// <c>dmi_scan.c</c>), read here with <c>GetSystemFirmwareTable('RSMB')</c>, whose buffer is
/// <c>RawSMBIOSData</c>: four bytes, a DWORD length, then the table (Microsoft Learn). The token:
/// <c>computeMetadata/v1/instance/service-accounts/default/token</c> with <c>Metadata-Flavor: Google</c>,
/// answering <c>access_token</c>, <c>expires_in</c> and <c>token_type</c>, with the instance's own scopes.
/// Google's own .NET library checks that the answer carries <c>Metadata-Flavor: Google</c> too.
/// </para>
/// <para>
/// The server is reached at its address, 169.254.169.254 (link-local: it cannot be routed off the
/// machine's own network), over plain http as Google documents it, with no proxy, no redirect and no
/// cookies, and DipTick's connection goes to that address and port only. Agents' fetches still refuse it
/// (SafeConnect). The token is kept in memory until a minute before it runs out, never written.
/// </para>
/// </remarks>
internal sealed partial class MachineAccount
{
    /// <summary>The metadata server's own address, as Google documents it.</summary>
    public static readonly Uri Server = new("http://169.254.169.254/");

    /// <summary>The machine's real metadata server, and its firmware as SMBIOS gives it.</summary>
    public static MachineAccount Real { get; } = new(Server, Firmware);

    private const string TokenPath = "computeMetadata/v1/instance/service-accounts/default/token";

    /// <summary>The most a token's answer may be: a few hundred bytes, in practice.</summary>
    private const int MostAnswer = 64 * 1024;

    /// <summary>'RSMB': the raw SMBIOS table provider, as GetSystemFirmwareTable takes its signature.</summary>
    private const uint RawSmbios = 0x52534D42;

    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly Uri _server;
    private readonly Lazy<bool> _onGoogle;
    private readonly Lock _lock = new();
    private (string Token, DateTimeOffset Until)? _token;

    /// <param name="server">The metadata server: Google's own address, or another given by the caller.</param>
    /// <param name="firmware">The machine's manufacturer and model, as its firmware says them; read once.</param>
    internal MachineAccount(Uri server, Func<(string? Maker, string? Product)> firmware)
    {
        _server = server;
        _onGoogle = new Lazy<bool>(
            () =>
            {
                var (maker, product) = firmware();
                return string.Equals(maker, "Google", StringComparison.Ordinal) && string.Equals(product, "Google Compute Engine", StringComparison.Ordinal);
            },
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>Whether the firmware says this is a Google Compute Engine machine: read from this PC once, nothing sent.</summary>
    public bool OnComputeEngine() => _onGoogle.Value;

    /// <summary>
    /// A token for the machine's own account, from its metadata server; the one kept while it is good for a
    /// minute more. Two asking at once may both ask the server: each gets a good token, and the later is kept.
    /// </summary>
    /// <exception cref="ChatException">The server did not give one, and why.</exception>
    public async Task<string> TokenAsync(CancellationToken cancel)
    {
        lock (_lock)
        {
            if (_token is { } kept && kept.Until - TimeSpan.FromMinutes(1) > DateTimeOffset.UtcNow)
            {
                return kept.Token;
            }
        }

        var (token, lasts) = await AskAsync(cancel).ConfigureAwait(false);
        lock (_lock)
        {
            _token = (token, DateTimeOffset.UtcNow + lasts);
        }

        return token;
    }

    /// <summary>The manufacturer and model in SMBIOS type 1, from the raw table GetSystemFirmwareTable gives; nulls when they are not there.</summary>
    internal static (string? Maker, string? Product) SystemInformation(ReadOnlySpan<byte> raw)
    {
        if (raw.Length < 8)
        {
            return (null, null);
        }

        var length = BinaryPrimitives.ReadUInt32LittleEndian(raw[4..8]);
        var table = raw[8..(int)Math.Min(raw.Length, 8L + length)];
        var at = 0;
        while (at + 4 <= table.Length)
        {
            var type = table[at];
            var size = table[at + 1];
            if (size < 4 || at + size > table.Length)
            {
                break;
            }

            // A structure's strings follow its formatted part, each ended by a null, the whole by two.
            var strings = at + size;
            var end = strings;
            while (end + 1 < table.Length && !(table[end] == 0 && table[end + 1] == 0))
            {
                end++;
            }

            if (type == 1 && size >= 6)
            {
                var area = table[strings..Math.Min(end, table.Length)];
                return (StringAt(area, table[at + 4]), StringAt(area, table[at + 5]));
            }

            // Type 127 ends the table.
            if (type == 127)
            {
                break;
            }

            at = end + 2;
        }

        return (null, null);
    }

    /// <summary>A structure's string by its number, counted from 1; null for 0 or one that is not there.</summary>
    private static string? StringAt(ReadOnlySpan<byte> area, byte number)
    {
        if (number == 0)
        {
            return null;
        }

        var index = 1;
        foreach (var range in area.Split((byte)0))
        {
            if (index++ == number)
            {
                var text = Encoding.Latin1.GetString(area[range]).Trim();
                return text.Length > 0 ? text : null;
            }
        }

        return null;
    }

    /// <summary>This machine's firmware, as SMBIOS says it; nulls when it cannot be read.</summary>
    private static (string? Maker, string? Product) Firmware()
    {
        var size = GetSystemFirmwareTable(RawSmbios, 0, null, 0);
        if (size == 0)
        {
            return (null, null);
        }

        var buffer = new byte[size];
        return GetSystemFirmwareTable(RawSmbios, 0, buffer, size) == size ? SystemInformation(buffer) : (null, null);
    }

    private async Task<(string Token, TimeSpan Lasts)> AskAsync(CancellationToken cancel)
    {
        using var http = NewClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_server, TokenPath));
        request.Headers.Add("Metadata-Flavor", "Google");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
            if (!response.Headers.TryGetValues("Metadata-Flavor", out var flavor) || !flavor.Contains("Google", StringComparer.Ordinal))
            {
                throw new ChatException("What answered at the metadata server's address is not Google's metadata server (no Metadata-Flavor: Google), so no token was taken from it.");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new ChatException(string.Create(CultureInfo.InvariantCulture, $"This machine's metadata server gave no token ({(int)response.StatusCode}): the machine may have no service account, or none DipTick can use."));
            }

            var body = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
            await using var bodyScope = body.ConfigureAwait(false);
            using var kept = new MemoryStream();
            var buffer = new byte[4096];
            int read;
            while ((read = await body.ReadAsync(buffer, cancel).ConfigureAwait(false)) > 0)
            {
                if (kept.Length + read > MostAnswer)
                {
                    throw new ChatException("This machine's metadata server answered with more than a token.");
                }

                await kept.WriteAsync(buffer.AsMemory(0, read), cancel).ConfigureAwait(false);
            }

            return Token(kept.ToArray());
        }
        catch (HttpRequestException ex)
        {
            throw new ChatException($"Could not reach this machine's metadata server: {OpenAiCompatibleEngine.Clip(ex.Message)}", ex);
        }
    }

    /// <summary>A token's answer read: <c>access_token</c>, a Bearer <c>token_type</c>, and <c>expires_in</c> seconds.</summary>
    /// <exception cref="ChatException">It is not such an answer.</exception>
    internal static (string Token, TimeSpan Lasts) Token(byte[] answer)
    {
        try
        {
            using var doc = JsonDocument.Parse(answer);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("access_token", out var token) && token.ValueKind == JsonValueKind.String && token.GetString() is { Length: > 0 } text
                && root.TryGetProperty("token_type", out var type) && type.ValueKind == JsonValueKind.String && string.Equals(type.GetString(), "Bearer", StringComparison.OrdinalIgnoreCase)
                && root.TryGetProperty("expires_in", out var expires) && expires.ValueKind == JsonValueKind.Number && expires.TryGetInt32(out var seconds) && seconds > 0)
            {
                return (text, TimeSpan.FromSeconds(seconds));
            }
        }
        catch (JsonException ex)
        {
            throw new ChatException("This machine's metadata server's answer was not JSON.", ex);
        }

        throw new ChatException("This machine's metadata server's answer held no token.");
    }

    /// <summary>A client that connects to the metadata server's address and port and nowhere else: no proxy, no redirect, no cookies.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The handler is owned by the HttpClient from the moment it is constructed with disposeHandler: true, and the " +
                        "caller disposes the client; the catch disposes the handler if the client never took it. CA2000 does not " +
                        "follow ownership through that constructor.")]
    private HttpClient NewClient()
    {
        var server = _server;
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            UseCookies = false,
            ConnectTimeout = Wait,
            ConnectCallback = (context, cancel) => ConnectAsync(context, server, cancel),
        };
        try
        {
            return new HttpClient(handler, disposeHandler: true) { Timeout = Wait };
        }
        catch
        {
            handler.Dispose();
            throw;
        }
    }

    /// <summary>The connection, to the metadata server's own address and port only: a request for anywhere else is refused before any connection is made.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The socket is owned by the NetworkStream from the moment it is constructed with ownsSocket: true, and the " +
                        "handler disposes the stream with its connection; the catch disposes the socket if the stream never took it. " +
                        "CA2000 does not follow ownership through that constructor.")]
    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, Uri server, CancellationToken cancel)
    {
        if (!string.Equals(context.DnsEndPoint.Host, server.Host, StringComparison.OrdinalIgnoreCase) || context.DnsEndPoint.Port != server.Port || !IPAddress.TryParse(server.Host, out var address))
        {
            throw new HttpRequestException("DipTick connects only to the metadata server's own address.");
        }

        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            socket.NoDelay = true;
            await socket.ConnectAsync(new IPEndPoint(address, server.Port), cancel).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint GetSystemFirmwareTable(uint provider, uint table, byte[]? buffer, uint size);
}
