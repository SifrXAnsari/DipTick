using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using NSec.Cryptography;

namespace DipTick.Models;

/// <summary>
/// The password in the master config, stored only as an Argon2id hash in PHC string form:
/// <c>$argon2id$v=19$m=65536,t=3,p=1$&lt;salt&gt;$&lt;hash&gt;</c>, base64 without padding.
/// </summary>
/// <remarks>
/// <para>
/// Cost 64 MiB, 3 passes, parallelism 1. That is above OWASP's floor of m=19456, t=2, p=1
/// (Password Storage Cheat Sheet, Argon2id). Parallelism is 1 because NSec accepts no other
/// value (nsec.rocks, Argon2Parameters: "Any value other than 1 is not supported"). The cost is read back
/// from the stored string, so raising it later does not lock anyone out.
/// </para>
/// <para>
/// Length: at least 15 characters, which NIST SP 800-63B-4 §3.1.1.2 requires of a password
/// used on its own; at least 64 must be accepted (same section), and this takes 1,024. No
/// composition rules, which the same section forbids.
/// </para>
/// </remarks>
internal static class PasswordHash
{
    public const int MinimumLength = 15;
    public const int MaximumLength = 1024;

    private const string Prefix = "$argon2id$v=19$";
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const long MemoryKib = 64 * 1024;
    private const long Passes = 3;

    /// <summary>Why a password cannot be used, or null if it can.</summary>
    public static string? Refuse(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        var length = new StringInfo(password).LengthInTextElements;
        if (length < MinimumLength)
        {
            return $"A password needs at least {MinimumLength.ToString(CultureInfo.InvariantCulture)} characters.";
        }

        return length > MaximumLength
            ? $"A password can have at most {MaximumLength.ToString(CultureInfo.InvariantCulture)} characters."
            : null;
    }

    public static string Hash(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Derive(password, salt, MemoryKib, Passes);
        try
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{Prefix}m={MemoryKib},t={Passes},p=1${Encode(salt)}${Encode(hash)}");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(hash);
        }
    }

    /// <summary>True only if <paramref name="stored"/> is well formed and matches.</summary>
    public static bool Verify(string password, string stored)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (!TryParse(stored, out var memoryKib, out var passes, out var salt, out var expected))
        {
            return false;
        }

        var actual = Derive(password, salt, memoryKib, passes);
        try
        {
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(actual);
        }
    }

    /// <summary>Whether a stored value has the shape this class writes.</summary>
    public static bool IsWellFormed(string stored) => TryParse(stored, out _, out _, out _, out _);

    private static byte[] Derive(string password, byte[] salt, long memoryKib, long passes)
    {
        var cost = new Argon2Parameters
        {
            DegreeOfParallelism = 1,
            MemorySize = memoryKib,
            NumberOfPasses = passes,
        };
        var bytes = Encoding.UTF8.GetBytes(password);
        try
        {
            return PasswordBasedKeyDerivationAlgorithm.Argon2id(in cost).DeriveBytes(bytes, salt, HashSize);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static bool TryParse(string? stored, out long memoryKib, out long passes, out byte[] salt, out byte[] hash)
    {
        memoryKib = 0;
        passes = 0;
        salt = [];
        hash = [];
        if (stored is null || !stored.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var parts = stored[Prefix.Length..].Split('$');
        if (parts.Length != 3)
        {
            return false;
        }

        var cost = parts[0].Split(',');
        if (cost.Length != 3
            // Floor: OWASP's minimum, m=19456 KiB with t=2. Ceiling: 1 GiB and 10 passes, so a
            // crafted hash cannot make every login take minutes or exhaust memory.
            || !TryNumber(cost[0], "m=", out memoryKib) || memoryKib < 19456 || memoryKib > 1024L * 1024
            || !TryNumber(cost[1], "t=", out passes) || passes < 2 || passes > 10
            || cost[2] != "p=1")
        {
            return false;
        }

        return TryDecode(parts[1], out salt) && salt.Length == SaltSize
            && TryDecode(parts[2], out hash) && hash.Length == HashSize;
    }

    private static bool TryNumber(string text, string prefix, out long value)
    {
        value = 0;
        return text.StartsWith(prefix, StringComparison.Ordinal)
            && long.TryParse(text.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    private static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=');

    private static bool TryDecode(string text, out byte[] bytes)
    {
        var padded = text + new string('=', (4 - (text.Length % 4)) % 4);
        bytes = new byte[padded.Length];
        if (Convert.TryFromBase64String(padded, bytes, out var written))
        {
            bytes = bytes[..written];
            return true;
        }

        bytes = [];
        return false;
    }
}
