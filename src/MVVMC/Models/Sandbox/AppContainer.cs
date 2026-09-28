using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace DipTick.Models.Sandbox;

/// <summary>
/// DipTick's AppContainers: the sandbox's own, <see cref="Name"/>, for
/// code a run starts; and one for each local connector (<see cref="ForConnector"/>), so one connector's
/// grants are never another's. Each is a profile for this Windows account, made the first time it is
/// needed. A process started as one reaches no network unless given a capability, none of your folders
/// and none of your credentials; it writes only where DipTick grants it.
/// </summary>
/// <remarks>
/// <para>
/// From Microsoft Learn: <c>CreateAppContainerProfile</c> (userenv.dll) takes a name of "up to 64
/// characters" matching <c>[-_. A-Za-z0-9]+</c>, returns the container's SID (freed with <c>FreeSid</c>),
/// and <c>HRESULT_FROM_WIN32(ERROR_ALREADY_EXISTS)</c> when the profile is there, when
/// <c>DeriveAppContainerSidFromAppContainerName</c> gives it. <c>DeleteAppContainerProfile</c> removes one;
/// "Deleting a non-existent profile returns success". Each SID is kept for the life of the process.
/// </para>
/// <para>
/// The capabilities (<c>WELL_KNOWN_SID_TYPE</c> 85 to 87, which Windows gives as S-1-15-3-1 to -3;
/// App capability declarations): internetClient "Cannot act as a server. No local
/// network access"; privateNetworkClientServer, "inbound and outbound access to home and work networks",
/// which "does not provide access to the Internet".
/// </para>
/// </remarks>
internal static partial class AppContainer
{
    /// <summary>The sandbox's profile, for code a run starts.</summary>
    public const string Name = "DipTick.Sandbox";

    /// <summary>What a connector's profile's name starts with; its name follows (at most 32 characters, so 50 in all).</summary>
    public const string ConnectorPrefix = "DipTick.Connector.";

    /// <summary>The capability to reach the internet, not this PC's own networks.</summary>
    public const string InternetClient = "S-1-15-3-1";

    /// <summary>The capability to reach home and work networks, both ways, not the internet.</summary>
    public const string PrivateNetworkClientServer = "S-1-15-3-3";

    private const int AlreadyExists = unchecked((int)0x800700B7);

    private static readonly ConcurrentDictionary<string, Lazy<(nint Sid, string? Problem)>> Profiles = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The sandbox's SID; zero when it could not be made (<see cref="Problem"/> says why).</summary>
    public static nint Sid => For(Name).Sid;

    /// <summary>Why the sandbox's profile could not be made, or null.</summary>
    public static string? Problem => For(Name).Problem;

    /// <summary>A profile by name, made the first time it is asked for: its SID, or zero and why.</summary>
    public static (nint Sid, string? Problem) For(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Profiles.GetOrAdd(name, n => new Lazy<(nint, string?)>(() => Create(n), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    /// <summary>The profile's name for a connector.</summary>
    public static string ForConnector(string connector)
    {
        ArgumentNullException.ThrowIfNull(connector);
        return ConnectorPrefix + connector;
    }

    /// <summary>
    /// Gives the sandbox Modify on one folder DipTick made, and everything under it; nothing else DipTick
    /// grants it. The folder's other entries stay as they are.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">The folder's access list could not be changed.</exception>
    public static void Grant(string folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        if (Sid == 0)
        {
            throw new InvalidOperationException(Problem ?? "the sandbox could not be made");
        }

        Grant(Sid, folder, write: true);
    }

    /// <summary>
    /// Gives a container Read and execute, or Modify with <paramref name="write"/>, on a folder and
    /// everything under it, as an entry of that folder's own list beside the ones it has.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">The folder's access list could not be changed.</exception>
    public static void Grant(nint sid, string folder, bool write)
    {
        ArgumentNullException.ThrowIfNull(folder);
        var identity = new SecurityIdentifier(sid);
        var info = new DirectoryInfo(folder);
        var security = info.GetAccessControl();
        var rights = (write ? FileSystemRights.Modify : FileSystemRights.ReadAndExecute) | FileSystemRights.Synchronize;
        security.AddAccessRule(new FileSystemAccessRule(identity, rights, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(security);
    }

    /// <summary>Takes back every entry a container has on a folder's own list; the folder's other entries stay.</summary>
    /// <exception cref="UnauthorizedAccessException">The folder's access list could not be changed.</exception>
    public static void Revoke(nint sid, string folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        var identity = new SecurityIdentifier(sid);
        var info = new DirectoryInfo(folder);
        var security = info.GetAccessControl();
        security.PurgeAccessRules(identity);
        info.SetAccessControl(security);
    }

    /// <summary>
    /// Makes a folder inside a run's folder read-only to the sandbox: it stops taking the run's own Modify
    /// from above (the entries it had are kept as its own) and the sandbox's are replaced by Read and
    /// execute. Files put in it afterwards take that from it.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">The folder's access list could not be changed.</exception>
    public static void ReadOnly(string folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        if (Sid == 0)
        {
            throw new InvalidOperationException(Problem ?? "the sandbox could not be made");
        }

        var identity = new SecurityIdentifier(Sid);
        var info = new DirectoryInfo(folder);
        var security = info.GetAccessControl();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: true);
        info.SetAccessControl(security);
        security = info.GetAccessControl();
        security.PurgeAccessRules(identity);
        security.AddAccessRule(new FileSystemAccessRule(identity, FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(security);
    }

    /// <summary>A profile removed, with its own folders; null when it went, or why not.</summary>
    public static string? Delete(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var done = DeleteAppContainerProfile(name);
        if (done >= 0)
        {
            _ = Profiles.TryRemove(name, out _);
            return null;
        }

        return string.Create(CultureInfo.InvariantCulture, $"Windows would not remove the container {name} (0x{done:X8}): {Marshal.GetExceptionForHR(done)?.Message}");
    }

    private static (nint, string?) Create(string name)
    {
        var made = CreateAppContainerProfile(name, name, "Runs a program for DipTick: no network unless its entry grants it, none of your folders but those it names, none of your credentials.", 0, 0, out var sid);
        if (made == AlreadyExists)
        {
            made = DeriveAppContainerSidFromAppContainerName(name, out sid);
        }

        return made < 0
            ? (0, string.Create(CultureInfo.InvariantCulture, $"Windows would not make DipTick's AppContainer {name} (0x{made:X8}): {Marshal.GetExceptionForHR(made)?.Message}"))
            : (sid, null);
    }

    [LibraryImport("userenv.dll", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int CreateAppContainerProfile(string name, string displayName, string description, nint capabilities, uint capabilityCount, out nint sid);

    [LibraryImport("userenv.dll", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int DeriveAppContainerSidFromAppContainerName(string name, out nint sid);

    [LibraryImport("userenv.dll", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int DeleteAppContainerProfile(string name);
}
