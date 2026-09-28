using System.Buffers;
using DipTick.Models.Tools;
using ModelContextProtocol.Client;

namespace DipTick.Models.Connectors;

/// <summary>
/// What starting a local connector means on this PC: the program found, its
/// arguments, or for a batch file cmd.exe's own command line, and the variables it gets: the SDK's own
/// curated set (<c>StdioClientTransportOptions.GetDefaultEnvironmentVariables</c>, "aligned with the
/// defaults used by the TypeScript and Python MCP SDKs") and the connector's <c>env</c>, nothing else of
/// DipTick's, so none of your keys in DipTick's own variables reaches it.
/// </summary>
internal sealed record Launch(string Program, IReadOnlyList<string> Arguments, string? CommandLine, IReadOnlyDictionary<string, string> Variables)
{
    /// <summary>What cmd.exe reads as its own in a command line: an argument holding one is not passed (the class of CVE-2024-24576).</summary>
    private static readonly SearchValues<char> CmdSpecial = SearchValues.Create("&|<>^%\"!\r\n");

    /// <summary>What makes an argument need quotes on cmd.exe's line.</summary>
    private static readonly SearchValues<char> Blank = SearchValues.Create(" \t");

    /// <summary>How the connector would start, or why it cannot: a variable not set, the program not found, or a batch file's argument cmd.exe would read as its own.</summary>
    public static (Launch? Launch, string? Problem) For(Connector connector, Func<string, string?> variable)
    {
        ArgumentNullException.ThrowIfNull(connector);
        ArgumentNullException.ThrowIfNull(variable);
        var missing = new List<string>();
        var command = ConnectorFile.Expand(connector.Command ?? string.Empty, variable, missing);
        var arguments = connector.Args.Select(a => ConnectorFile.Expand(a, variable, missing)).ToList();
        var variables = StdioClientTransportOptions.GetDefaultEnvironmentVariables()
            .Where(v => v.Value is not null)
            .ToDictionary(v => v.Key, v => v.Value!, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in connector.Env)
        {
            variables[name] = ConnectorFile.Expand(value, variable, missing);
        }

        if (missing.Count > 0)
        {
            return (null, $"it names {string.Join(", ", missing.Distinct(StringComparer.Ordinal))}, which {(missing.Count == 1 ? "is" : "are")} not set");
        }

        var path = variables.GetValueOrDefault("PATH") ?? variables.GetValueOrDefault("Path") ?? variable("PATH");
        if (Find(command, path, variable("PATHEXT")) is not { } program)
        {
            return (null, $"{SafeText.Line(command)} is not found (a full path, or a name on PATH; never the current folder)");
        }

        var extension = Path.GetExtension(program).ToLowerInvariantAscii();
        if (extension is ".cmd" or ".bat")
        {
            if (program.AsSpan().ContainsAny(CmdSpecial) || arguments.Any(a => a.AsSpan().ContainsAny(CmdSpecial)))
            {
                return (null, "it is a batch file, which runs through cmd.exe, and its path or an argument holds one of & | < > ^ % \" ! or a line break, which cmd.exe would read as its own; DipTick does not pass it");
            }

            var cmd = Path.Combine(System.Environment.SystemDirectory, "cmd.exe");
            var inner = string.Join(' ', [$"\"{program}\"", .. arguments.Select(a => a.Length == 0 || a.AsSpan().ContainsAny(Blank) ? $"\"{a}\"" : a)]);
            return (new Launch(cmd, [], $"\"{cmd}\" /d /s /c \"{inner}\"", variables), null);
        }

        return extension is ".exe" or ".com"
            ? (new Launch(program, arguments, null, variables), null)
            : (null, $"{SafeText.Line(program)} is not a program DipTick starts: an .exe, .com, .cmd or .bat");
    }

    /// <summary>
    /// The program: a full path as it is, with PATHEXT's extensions tried when it has none; a bare name
    /// looked for in each folder of PATH that is itself a full path. Never the current folder, and never
    /// a relative path.
    /// </summary>
    internal static string? Find(string command, string? path, string? pathExt)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Length == 0)
        {
            return null;
        }

        if (Path.IsPathFullyQualified(command))
        {
            return Path.HasExtension(command) ? (File.Exists(command) ? Path.GetFullPath(command) : null) : Extensions(pathExt).Select(e => command + e).FirstOrDefault(File.Exists);
        }

        if (command.Contains('\\', StringComparison.Ordinal) || command.Contains('/', StringComparison.Ordinal))
        {
            return null;
        }

        foreach (var folder in (path ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var trimmed = folder.Trim('"');
            if (!Path.IsPathFullyQualified(trimmed))
            {
                continue;
            }

            var candidate = Path.Combine(trimmed, command);
            if (Path.HasExtension(command))
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                continue;
            }

            if (Extensions(pathExt).Select(e => candidate + e).FirstOrDefault(File.Exists) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// The extensions tried, as PATHEXT lists them, in lower case: Windows compares names without case, and
    /// a program found so is shown as it is usually spelled (pwsh.exe, not pwsh.EXE).
    /// </summary>
    private static IEnumerable<string> Extensions(string? pathExt) =>
        (string.IsNullOrWhiteSpace(pathExt) ? ".COM;.EXE;.BAT;.CMD" : pathExt).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(e => e.StartsWith('.')).Select(e => e.ToLowerInvariantAscii());
}
