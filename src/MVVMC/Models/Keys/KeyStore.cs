using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace DipTick.Models.Keys;

/// <summary>Where saved API keys live: one per host, never in DipTick's own files.</summary>
internal interface IKeyStore
{
    /// <summary>The key saved for <paramref name="host"/>, or null when there is none.</summary>
    /// <exception cref="KeyStoreException">The store could not be read.</exception>
    string? Read(string host);

    /// <exception cref="KeyStoreException">The store refused it.</exception>
    void Write(string host, string key);

    /// <summary>True when a key was there and is gone; false when there was none.</summary>
    /// <exception cref="KeyStoreException">The store refused it.</exception>
    bool Delete(string host);
}

/// <summary>The key store refused, or could not be reached. The message never carries a key.</summary>
internal sealed class KeyStoreException : Exception
{
    public KeyStoreException()
    {
    }

    public KeyStoreException(string message)
        : base(message)
    {
    }

    public KeyStoreException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>
/// Windows Credential Manager: one generic credential per host, for this Windows account on this
/// PC. It shows in Control Panel, Credential Manager, Windows Credentials, where it can be removed.
/// </summary>
/// <remarks>
/// From wincred.h (learn.microsoft.com): <c>CREDENTIALW</c>'s fields in order, <c>CRED_TYPE_GENERIC</c>
/// (1), <c>CRED_PERSIST_LOCAL_MACHINE</c> (2: "visible to other logon sessions of this same user on
/// this same computer and not visible to logon sessions for this user on other computers"), a blob
/// of at most <c>CRED_MAX_CREDENTIAL_BLOB_SIZE</c> (5 × 512 bytes); <c>CredReadW</c>'s result freed
/// with <c>CredFree</c>; <c>CredDeleteW</c>'s flags reserved and 0; a missing credential is
/// <c>ERROR_NOT_FOUND</c> (1168, System Error Codes 1000-1299).
/// </remarks>
internal sealed partial class WindowsKeyStore : IKeyStore
{
    /// <summary>Every DipTick credential's name starts with this; the host follows.</summary>
    public const string Prefix = "DipTick:key:";

    /// <summary>The most bytes a credential's blob holds.</summary>
    public const int MaxBlob = 5 * 512;

    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;
    private const int ErrorNoSuchLogonSession = 1312;
    private const int MaxComment = 255;

    public static WindowsKeyStore Instance { get; } = new();

    /// <summary>The credential's name for a host, e.g. DipTick:key:api.openai.com.</summary>
    public static string Target(string host) => Prefix + host;

    public string? Read(string host)
    {
        var bytes = ReadTarget(Target(host), $"the saved key for {host}");
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

    public void Write(string host, string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var bytes = Encoding.UTF8.GetBytes(key);
        try
        {
            if (bytes.Length is 0 or > MaxBlob)
            {
                throw new KeyStoreException($"a key must be 1 to {MaxBlob} bytes.");
            }

            WriteTarget(Target(host), bytes, $"API key DipTick sends to {host}", $"the key for {host}");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public bool Delete(string host) => DeleteTarget(Target(host), $"the saved key for {host}");

    /// <summary>
    /// A credential's blob by its full name, or null when there is none; the caller zeroes it when done.
    /// <paramref name="what"/> names it in a failure.
    /// </summary>
    /// <exception cref="KeyStoreException">The store could not be read.</exception>
    internal unsafe byte[]? ReadTarget(string name, string what)
    {
        ArgumentNullException.ThrowIfNull(name);
        nint found;
        fixed (char* target = name)
        {
            if (!CredRead(target, CredTypeGeneric, 0, out found))
            {
                var error = Marshal.GetLastPInvokeError();
                return error == ErrorNotFound ? null : throw new KeyStoreException(Describe(error, $"{what} could not be read"));
            }
        }

        try
        {
            var credential = (Credential*)found;
            var size = (int)credential->CredentialBlobSize;
            if (size == 0 || credential->CredentialBlob == 0)
            {
                return null;
            }

            if (size > MaxBlob)
            {
                throw new KeyStoreException($"{what} is larger than Credential Manager holds; save it again.");
            }

            var bytes = new byte[size];
            Marshal.Copy(credential->CredentialBlob, bytes, 0, size);
            return bytes;
        }
        finally
        {
            CredFree(found);
        }
    }

    /// <summary>A credential written by its full name: at most <see cref="MaxBlob"/> bytes, with a comment saying what it is.</summary>
    /// <exception cref="KeyStoreException">The store refused it.</exception>
    internal unsafe void WriteTarget(string name, ReadOnlySpan<byte> blob, string comment, string what)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(comment);
        if (blob.Length is 0 or > MaxBlob)
        {
            throw new KeyStoreException($"{what} must be 1 to {MaxBlob} bytes.");
        }

        if (comment.Length > MaxComment)
        {
            comment = comment[..MaxComment];
        }

        fixed (char* target = name)
        fixed (char* note = comment)
        fixed (char* user = "DipTick")
        fixed (byte* bytes = blob)
        {
            var credential = new Credential
            {
                Type = CredTypeGeneric,
                TargetName = (nint)target,
                Comment = (nint)note,
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = (nint)bytes,
                Persist = CredPersistLocalMachine,
                UserName = (nint)user,
            };
            if (!CredWrite(&credential, 0))
            {
                throw new KeyStoreException(Describe(Marshal.GetLastPInvokeError(), $"{what} could not be saved"));
            }
        }
    }

    /// <summary>True when a credential was there by its full name and is gone; false when there was none.</summary>
    /// <exception cref="KeyStoreException">The store refused it.</exception>
    internal unsafe bool DeleteTarget(string name, string what)
    {
        ArgumentNullException.ThrowIfNull(name);
        fixed (char* target = name)
        {
            if (CredDelete(target, CredTypeGeneric, 0))
            {
                return true;
            }
        }

        var error = Marshal.GetLastPInvokeError();
        return error == ErrorNotFound ? false : throw new KeyStoreException(Describe(error, $"{what} could not be removed"));
    }
    private static string Describe(int error, string what) => error == ErrorNoSuchLogonSession
        ? $"{what}: Windows Credential Manager is not available in this session. Name a variable instead (main.keyVariable)."
        : $"{what}: {new Win32Exception(error).Message}";

    /// <summary>
    /// <c>CREDENTIALW</c>, field for field. <c>LastWritten</c> is a FILETIME, two DWORDs, kept as two
    /// <see cref="uint"/>s so the layout matches on every platform.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public nint TargetName;
        public nint Comment;
        public uint LastWrittenLow;
        public uint LastWrittenHigh;
        public uint CredentialBlobSize;
        public nint CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public nint Attributes;
        public nint TargetAlias;
        public nint UserName;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "CredReadW", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool CredRead(char* targetName, uint type, uint flags, out nint credential);

    [LibraryImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool CredWrite(Credential* credential, uint flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredDeleteW", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool CredDelete(char* targetName, uint type, uint flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredFree")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void CredFree(nint buffer);
}
