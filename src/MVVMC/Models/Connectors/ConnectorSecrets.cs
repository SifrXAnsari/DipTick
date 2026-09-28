using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DipTick.Models.Keys;
using DipTick.Models.Tools;
using ModelContextProtocol.Authentication;

namespace DipTick.Models.Connectors;

/// <summary>
/// A connector's secrets, in Windows Credential Manager and nowhere else:
/// a header's value (<c>DipTick:connector:&lt;name&gt;:header:&lt;header&gt;</c>), your client's secret
/// (<c>…:client-secret</c>), and what signing in gave (<c>…:tokens</c>): the access and refresh tokens,
/// the client ID and secret they were issued to, and the issuer they belong to. A set of tokens is longer
/// than one credential holds (5 × 512 bytes, wincred.h), so it is kept in numbered parts, the first
/// naming how many.
/// </summary>
internal static class ConnectorSecrets
{
    /// <summary>Every connector credential's name starts with this.</summary>
    public const string Prefix = "DipTick:connector:";

    /// <summary>The most parts a set of tokens may take: 16 × 2,560 bytes is far more than any service issues.</summary>
    private const int MostParts = 16;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private static WindowsKeyStore Store => WindowsKeyStore.Instance;

    /// <summary>A header's value kept for a connector, or null.</summary>
    /// <exception cref="KeyStoreException">The store could not be read.</exception>
    public static string? Header(string connector, string header) => ReadText(Name(connector, $"header:{header.ToLowerInvariantAscii()}"), $"{connector}'s {header}");

    /// <exception cref="KeyStoreException">The store refused it.</exception>
    public static void SetHeader(string connector, string header, string value) =>
        WriteText(Name(connector, $"header:{header.ToLowerInvariantAscii()}"), value, $"{header} DipTick sends to the {connector} connector", $"{connector}'s {header}");

    /// <summary>Your client's secret for a connector's sign-in, or null.</summary>
    /// <exception cref="KeyStoreException">The store could not be read.</exception>
    public static string? ClientSecret(string connector) => ReadText(Name(connector, "client-secret"), $"{connector}'s client secret");

    /// <exception cref="KeyStoreException">The store refused it.</exception>
    public static void SetClientSecret(string connector, string secret) =>
        WriteText(Name(connector, "client-secret"), secret, $"The client secret DipTick signs in to the {connector} connector with", $"{connector}'s client secret");

    /// <summary>What signing in to a connector gave, or null when it is not signed in or a part is missing.</summary>
    /// <exception cref="KeyStoreException">The store could not be read.</exception>
    public static TokenContainer? Tokens(string connector)
    {
        var count = ReadText(Name(connector, "tokens"), $"{connector}'s sign-in");
        if (count is null || !int.TryParse(count, NumberStyles.None, CultureInfo.InvariantCulture, out var parts) || parts is < 1 or > MostParts)
        {
            return null;
        }

        var json = new List<byte>();
        try
        {
            for (var i = 1; i <= parts; i++)
            {
                var part = Store.ReadTarget(Name(connector, string.Create(CultureInfo.InvariantCulture, $"tokens:{i}")), $"{connector}'s sign-in");
                if (part is null)
                {
                    return null;
                }

                json.AddRange(part);
                CryptographicOperations.ZeroMemory(part);
            }

            var bytes = json.ToArray();
            try
            {
                return JsonSerializer.Deserialize<TokenContainer>(bytes, Json);
            }
            catch (JsonException)
            {
                return null;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
        finally
        {
            json.Clear();
        }
    }

    /// <summary>What signing in gave, kept in parts: the parts first, then how many, then any left from a longer set removed.</summary>
    /// <exception cref="KeyStoreException">The store refused it.</exception>
    public static void SetTokens(string connector, TokenContainer tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(tokens, Json);
        try
        {
            var parts = (bytes.Length + WindowsKeyStore.MaxBlob - 1) / WindowsKeyStore.MaxBlob;
            if (parts > MostParts)
            {
                throw new KeyStoreException($"the {connector} connector's sign-in is larger than DipTick keeps.");
            }

            for (var i = 0; i < parts; i++)
            {
                var start = i * WindowsKeyStore.MaxBlob;
                var length = Math.Min(WindowsKeyStore.MaxBlob, bytes.Length - start);
                Store.WriteTarget(Name(connector, string.Create(CultureInfo.InvariantCulture, $"tokens:{i + 1}")), bytes.AsSpan(start, length), $"Part {i + 1} of the sign-in DipTick keeps for the {connector} connector", $"{connector}'s sign-in");
            }

            WriteText(Name(connector, "tokens"), parts.ToString(CultureInfo.InvariantCulture), $"How many parts the sign-in DipTick keeps for the {connector} connector has", $"{connector}'s sign-in");
            for (var i = parts + 1; i <= MostParts; i++)
            {
                if (!Store.DeleteTarget(Name(connector, string.Create(CultureInfo.InvariantCulture, $"tokens:{i}")), $"{connector}'s sign-in"))
                {
                    break;
                }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    /// <summary>A connector's sign-in removed; whether there was one.</summary>
    /// <exception cref="KeyStoreException">The store refused it.</exception>
    public static bool ClearTokens(string connector)
    {
        var had = Store.DeleteTarget(Name(connector, "tokens"), $"{connector}'s sign-in");
        for (var i = 1; i <= MostParts; i++)
        {
            had |= Store.DeleteTarget(Name(connector, string.Create(CultureInfo.InvariantCulture, $"tokens:{i}")), $"{connector}'s sign-in");
        }

        return had;
    }

    /// <summary>Every secret of a connector removed: its sign-in, its client secret and the headers named.</summary>
    /// <exception cref="KeyStoreException">The store refused it.</exception>
    public static void ClearAll(string connector, IEnumerable<string> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        _ = ClearTokens(connector);
        _ = Store.DeleteTarget(Name(connector, "client-secret"), $"{connector}'s client secret");
        foreach (var header in headers)
        {
            _ = Store.DeleteTarget(Name(connector, $"header:{header.ToLowerInvariantAscii()}"), $"{connector}'s {header}");
        }
    }

    private static string Name(string connector, string part) => $"{Prefix}{connector}:{part}";

    private static string? ReadText(string name, string what)
    {
        var bytes = Store.ReadTarget(name, what);
        if (bytes is null)
        {
            return null;
        }

        try
        {
            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static void WriteText(string name, string value, string comment, string what)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        try
        {
            Store.WriteTarget(name, bytes, comment, what);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}

/// <summary>
/// The SDK's token cache, kept in Credential Manager (<see cref="ConnectorSecrets"/>) so a sign-in lasts
/// past this run; read once a connection, as the SDK asks on every request.
/// </summary>
internal sealed class CredentialTokenCache(string connector) : ITokenCache
{
    private readonly Lock _lock = new();
    private TokenContainer? _known;
    private bool _read;

    public ValueTask StoreTokensAsync(TokenContainer tokens, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        ConnectorSecrets.SetTokens(connector, tokens);
        lock (_lock)
        {
            _known = tokens;
            _read = true;
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<TokenContainer?> GetTokensAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (!_read)
            {
                _known = ConnectorSecrets.Tokens(connector);
                _read = true;
            }

            return ValueTask.FromResult(_known);
        }
    }
}
