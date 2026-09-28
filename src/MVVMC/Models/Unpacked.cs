namespace DipTick.Models;

/// <summary>
/// Where diptick.exe, one self-contained file, unpacks the native libraries it carries the first
/// time a version starts: the folder DOTNET_BUNDLE_EXTRACT_BASE_DIR names, else the temporary
/// folder's .net folder, then the program's own name ("Create a single file for application
/// deployment", Microsoft Learn). The program loads those libraries from there on every start
/// without reading them again, so the folder is guarded as DipTick's own data is: no agent's file
/// capability reaches it, and no connector is given it.
/// </summary>
internal static class Unpacked
{
    /// <summary>The folder every version's libraries are unpacked under.</summary>
    public static string Folder()
    {
        var chosen = Environment.GetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR");
        var root = string.IsNullOrWhiteSpace(chosen) ? Path.Combine(Path.GetTempPath(), ".net") : chosen;
        return Path.Combine(Path.GetFullPath(root), "diptick");
    }
}
