using System.Reflection;

namespace DipTick.Models;

/// <summary>
/// The starter settings file shipped inside diptick.exe, so it always matches this build: what
/// <c>diptick init</c> writes, and what setup starts from when there is no file yet.
/// </summary>
internal static class StarterConfig
{
    private const string Resource = "diptick.master.json";

    /// <summary>The starter's text.</summary>
    /// <exception cref="InvalidOperationException">This build carries no starter; it was built wrongly.</exception>
    public static string Text()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(Resource)
            ?? throw new InvalidOperationException("this build carries no starter config; it was built wrongly.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Writes the starter to <paramref name="path"/>: beside it first, then moved into place, so a
    /// crash never leaves half a file. Returns null on success, or why not.
    /// </summary>
    public static string? Write(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var temporary = path + ".new";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(temporary, Text(), new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporary, path, overwrite: true);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            File.Delete(temporary);
            return $"{path}: cannot be written ({ex.Message}).";
        }
    }
}
