using System.Diagnostics;
using System.Globalization;
using System.Text;
using DipTick.Models.Connectors;

namespace DipTick.Models.Sandbox;

/// <summary>An interpreter the sandbox runs: its language, its program, what goes before the script, and the script's extension.</summary>
internal sealed record Interpreter(string Language, string Program, IReadOnlyList<string> Arguments, string Extension);

/// <summary>
/// One run in the sandbox: its language, exit code (null when it was ended), what it wrote to its output and
/// errors (each cut at <see cref="CodeSandbox.MostOutput"/>, the rest counted), how long it took, why it
/// was ended if it was, its folder, and the files it made there.
/// </summary>
internal sealed record CodeRun(string Language, int? ExitCode, string Output, long OutputDropped, string Errors, long ErrorsDropped, TimeSpan Took, string? Ended, string Folder, IReadOnlyList<(string Name, long Size)> Made);

/// <summary>
/// A skill a run is given: its name, its folder and the hash approved for
/// it; to run one of its scripts, the script's path in it, with / between parts, and its arguments.
/// </summary>
internal sealed record SkillRun(string Name, string Folder, string Hash, string? Script, IReadOnlyList<string> Args);

/// <summary>Why code could not be run, in words.</summary>
internal sealed class SandboxException : Exception
{
    public SandboxException()
    {
    }

    public SandboxException(string message)
        : base(message)
    {
    }

    public SandboxException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>
/// DipTick's own sandbox: code runs as DipTick's AppContainer, with no
/// capabilities, inside a Job object that holds at most <see cref="MostProcesses"/> processes,
/// <see cref="MostMemory"/> of committed memory and <see cref="MostCpu"/> of CPU time, every UI
/// restriction on; with a wall clock of its own; writing only in its run's folder; and with an
/// environment of its own, nothing of DipTick's. The interpreters are those already installed where the
/// container can read them: PowerShell 7, Python, Node.
/// </summary>
internal static class CodeSandbox
{
    public const int MostProcesses = 16;

    public const long MostMemory = 1L << 30;

    /// <summary>The most one run may write to its output, and to its errors, kept whole.</summary>
    public const int MostOutput = 256 * 1024;

    /// <summary>The most code one run takes.</summary>
    public const int MostCode = 256 * 1024;

    /// <summary>
    /// The exit status of every process of a job whose CPU time ran out: the job limits' page names
    /// <c>ERROR_NOT_ENOUGH_QUOTA</c> (1816), and a process ended so reports its NT form,
    /// <c>STATUS_QUOTA_EXCEEDED</c> (0xC0000044), in practice; both are read so.
    /// </summary>
    private const int NotEnoughQuota = 1816;

    private const int QuotaExceeded = unchecked((int)0xC0000044);

    public static readonly TimeSpan MostCpu = TimeSpan.FromSeconds(60);

    public static readonly TimeSpan DefaultWall = TimeSpan.FromSeconds(60);

    public static readonly TimeSpan MostWall = TimeSpan.FromMinutes(10);

    /// <summary>The languages the sandbox knows, whether or not this PC has them.</summary>
    public static IReadOnlyList<string> Languages { get; } = ["pwsh", "python", "node"];

    /// <summary>Whether code can run here at all: Windows 10 or later, and DipTick's AppContainer made.</summary>
    public static bool Available => OperatingSystem.IsWindowsVersionAtLeast(10) && AppContainer.Sid != 0;

    /// <summary>Where runs that keep nothing go: <c>sandbox</c> in DipTick's data folder, each run removed after it ends.</summary>
    public static string ScratchFolder => Path.Combine(Harness.AuditLog.DataFolder(), "sandbox");

    /// <summary>
    /// Runs left in <see cref="ScratchFolder"/> by an ending that could not remove them (a file still held),
    /// removed once they are a day old; a younger one may be another run's, still going.
    /// </summary>
    public static void SweepLeftOvers()
    {
        if (!Directory.Exists(ScratchFolder))
        {
            return;
        }

        foreach (var folder in Directory.EnumerateDirectories(ScratchFolder, "run-*"))
        {
            try
            {
                if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(folder) > TimeSpan.FromDays(1))
                {
                    Directory.Delete(folder, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Still held: the next sweep tries again.
            }
        }
    }

    /// <summary>The languages this PC can run in the sandbox now.</summary>
    public static IReadOnlyList<string> Present() => [.. Languages.Where(l => Find(l).Found is not null)];

    /// <summary>A language's interpreter, or why it cannot run in the sandbox.</summary>
    public static (Interpreter? Found, string? Problem) Find(string language)
    {
        ArgumentNullException.ThrowIfNull(language);
        var (name, before, extension) = language switch
        {
            "pwsh" => ("pwsh", (string[])["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File"], ".ps1"),
            "python" => ("python", ["-I", "-B", "-X", "utf8"], ".py"),
            "node" => ("node", [], ".js"),
            _ => (string.Empty, [], string.Empty),
        };
        if (name.Length == 0)
        {
            return (null, $"{SafeText.Line(language)} is not one of {string.Join(", ", Languages)}");
        }

        if (Launch.Find(name, Environment.GetEnvironmentVariable("PATH"), ".EXE") is not { } program)
        {
            return (null, $"{name} is not installed on this PC (not found on PATH)");
        }

        return Unreadable(name, program, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) is { } why
            ? (null, why)
            : (new Interpreter(language, program, before, extension), null);
    }

    /// <summary>
    /// Why an interpreter found at <paramref name="program"/> cannot run in the sandbox, or null: one
    /// from the Microsoft Store (its WindowsApps alias) cannot be started from it, and a folder under
    /// the person's own profile cannot be read from it, and DipTick opens neither to it.
    /// </summary>
    internal static string? Unreadable(string name, string program, string profile)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(profile);
        if (program.Contains("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase))
        {
            return $"{name} here comes from the Microsoft Store, whose programs the sandbox cannot start; install {name} for all users";
        }

        return profile.Length > 0 && program.StartsWith(Path.TrimEndingDirectorySeparator(profile) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? $"{name} is installed in your user folder ({program}), which the sandbox cannot read; install it for all users (in Program Files)"
            : null;
    }

    /// <summary>
    /// Runs <paramref name="code"/> in the sandbox, in <paramref name="folder"/> (made, and given to the
    /// container), for at most <paramref name="wall"/>; or, with <paramref name="skill"/>'s script, that
    /// script with its arguments. A skill's files are copied into the run's <c>.skill</c> folder, read-only
    /// to the container and named by SKILL_DIR, once the copy's hash is the one approved. <paramref name="cancel"/>
    /// ends it at once.
    /// </summary>
    /// <exception cref="SandboxException">It could not be run, and why.</exception>
    public static async Task<CodeRun> RunAsync(string language, string code, TimeSpan wall, string folder, CancellationToken cancel, SkillRun? skill = null)
    {
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(folder);
        if (!Available)
        {
            throw new SandboxException(AppContainer.Problem ?? "the sandbox needs Windows 10 or later");
        }

        if (Find(language) is not ({ } interpreter, null))
        {
            throw new SandboxException(Find(language).Problem ?? "no interpreter");
        }

        if (Encoding.UTF8.GetByteCount(code) > MostCode)
        {
            throw new SandboxException("the code is larger than 256 KiB");
        }

        wall = wall <= TimeSpan.Zero ? DefaultWall : wall > MostWall ? MostWall : wall;
        string target;
        string? skillCopy = null;
        try
        {
            Directory.CreateDirectory(folder);
            AppContainer.Grant(folder);

            // Its home, and the temporary folder Windows gives a container (%LOCALAPPDATA%\Packages\<name>\AC\Temp),
            // made inside the run's folder, so a program's own files and temporary files work and stay with the run.
            Directory.CreateDirectory(TemporaryFolder(folder));
            if (skill is not null)
            {
                skillCopy = CopySkill(skill, folder);
            }

            if (skill?.Script is { } relative)
            {
                target = Path.Combine(skillCopy!, relative.Replace('/', Path.DirectorySeparatorChar));
            }
            else
            {
                target = Path.Combine(folder, "script" + interpreter.Extension);
                await File.WriteAllTextAsync(target, code, new UTF8Encoding(false), cancel).ConfigureAwait(false);
            }

            if (interpreter.Language == "pwsh")
            {
                await File.WriteAllTextAsync(Path.Combine(folder, PowerShellStart), PowerShellStartText(target), new UTF8Encoding(false), cancel).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            throw new SandboxException($"its folder could not be made ready: {SafeText.Line(ex.Message)}", ex);
        }

        var clock = Stopwatch.StartNew();
        JobProcess process;
        try
        {
            process = JobProcess.Start(interpreter.Program, Arguments(interpreter, folder, target, skill?.Args ?? []), Variables(interpreter, folder, skillCopy), folder, errorLine: null, new SandboxSpec(AppContainer.Sid, MostProcesses, MostMemory, MostCpu));
        }
        catch (LaunchException ex)
        {
            throw new SandboxException($"{interpreter.Language} could not be started in the sandbox: {ex.Message}", ex);
        }

        using (process)
        {
            // No input: the program reads the end of it at once.
            await process.Input.DisposeAsync().ConfigureAwait(false);
            var output = new StringBuilder();
            long dropped = 0;
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            limit.CancelAfter(wall);

            // A read of a pipe does not end on a token: at the wall clock, or at a stop, the job is ended,
            // which closes the pipe and so ends the read, whether the program writes or not.
            using (limit.Token.Register(process.Kill))
            {
                try
                {
                    using var reader = new StreamReader(process.Output, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false);
                    var buffer = new char[4096];
                    int read;
                    while ((read = await reader.ReadAsync(buffer.AsMemory(), limit.Token).ConfigureAwait(false)) > 0)
                    {
                        var room = Math.Max(0, MostOutput - output.Length);
                        output.Append(buffer, 0, Math.Min(room, read));
                        dropped += Math.Max(0, read - room);
                    }

                    // Its output closed; the program itself may run on, until the same wall clock.
                    while (process.Running)
                    {
                        await Task.Delay(100, limit.Token).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException)
                {
                    // The wall clock or a stop, said below; or its pipe broke as its job ended.
                }
            }

            string? ended = null;
            if (limit.IsCancellationRequested)
            {
                process.Kill();
                ended = cancel.IsCancellationRequested ? "stopped" : string.Create(CultureInfo.InvariantCulture, $"ended at its {wall.TotalSeconds:0} seconds");
            }

            _ = process.WaitForExit(TimeSpan.FromSeconds(5));
            var exit = ended is null ? process.ExitCode : null;
            if (exit is NotEnoughQuota or QuotaExceeded)
            {
                ended = string.Create(CultureInfo.InvariantCulture, $"ended when its {MostCpu.TotalSeconds:0} seconds of CPU ran out");
                exit = null;
            }

            var (errors, errorsDropped) = process.ErrorText;
            return new CodeRun(interpreter.Language, exit, SafeText.ForModel(output.ToString()), dropped, SafeText.ForModel(errors), errorsDropped, clock.Elapsed, ended, folder, Made(folder, target));
        }
    }

    /// <summary>The run's own home, inside its folder: its profile, its settings and its temporary files; not what it made.</summary>
    internal const string Home = ".sandbox";

    /// <summary>What starts a PowerShell run (<see cref="PowerShellStartText"/>).</summary>
    internal const string PowerShellStart = ".start.ps1";

    /// <summary>Where a run's copy of a skill is, inside its folder: read-only to the run, named by SKILL_DIR.</summary>
    internal const string SkillFolder = ".skill";

    /// <summary>
    /// PowerShell's start: its location made the run's folder, as the drive <c>Run:</c>, then the script at
    /// <paramref name="target"/> run as <c>pwsh -File</c> runs one, with the arguments the start was given.
    /// The container cannot list the folders above the run's, and PowerShell lists each one to set a location
    /// by its path (to set its letters' case), so its own start leaves the location empty and relative paths
    /// and Start-Process fail; a drive rooted at the folder needs none of them. The script
    /// is dot-sourced, as -File runs it (about_Pwsh), from a script block that has no file, so its
    /// <c>$MyInvocation</c>, and the errors it writes, name no line of the start's; its arguments pass through
    /// <c>@args</c>, which keeps a parameter's name a name. A dot-sourced script's <c>exit n</c> ends only the
    /// script and leaves <c>$?</c> false, as a script that does not parse does, which is not run at all. So the
    /// run ends as -File's does: n after <c>exit n</c>, 1 when the script does not parse or an error stops it,
    /// otherwise 0, whatever the last program it ran returned. The target's path is written into the start,
    /// quoted twice (<see cref="Quoted"/>).
    /// </summary>
    internal static string PowerShellStartText(string target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var inner = ". '" + Quoted(target) + "' @args; if (-not $?) { if ($LASTEXITCODE) { exit $LASTEXITCODE }; exit 1 }";
        return "$null = New-PSDrive -Name Run -PSProvider FileSystem -Root $PSScriptRoot -Scope Global\n" +
            "Set-Location -LiteralPath Run:\\\n" +
            ". ([scriptblock]::Create('" + Quoted(inner) + "')) @args\n";
    }

    /// <summary>
    /// Text as it stands inside PowerShell's single quotes: each character PowerShell reads as a single quote
    /// doubled, which the tokenizer reads back as one (' and U+2018 to U+201B: CharTraits.IsSingleQuote,
    /// PowerShell's own source).
    /// </summary>
    internal static string Quoted(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var quoted = new StringBuilder(text.Length + 8);
        foreach (var c in text)
        {
            quoted.Append(c);
            if (c is '\'' or '‘' or '’' or '‚' or '‛')
            {
                quoted.Append(c);
            }
        }

        return quoted.ToString();
    }

    /// <summary>The temporary folder Windows gives the container, with its home in the run's folder.</summary>
    internal static string TemporaryFolder(string folder) => Path.Combine(folder, Home, "Packages", AppContainer.Name, "AC", "Temp");

    /// <summary>
    /// What the interpreter is started with: its own switches, then the script and its arguments; for
    /// PowerShell, its start, which runs the script with them.
    /// </summary>
    internal static IReadOnlyList<string> Arguments(Interpreter interpreter, string folder, string target, IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(interpreter);
        ArgumentNullException.ThrowIfNull(arguments);
        return interpreter.Language == "pwsh"
            ? [.. interpreter.Arguments, Path.Combine(folder, PowerShellStart), .. arguments]
            : [.. interpreter.Arguments, target, .. arguments];
    }

    /// <summary>
    /// The skill copied into the run's own folder, file by file as it is on disk (never through a link), into
    /// <see cref="SkillFolder"/>, made read-only to the container first so each file takes that; then the
    /// copy's hash is the one approved, or nothing runs.
    /// </summary>
    /// <exception cref="SandboxException">It changed since its yes, or holds more than a skill may.</exception>
    private static string CopySkill(SkillRun skill, string folder)
    {
        var copy = Path.Combine(folder, SkillFolder);
        Directory.CreateDirectory(copy);
        AppContainer.ReadOnly(copy);
        var root = Path.GetFullPath(skill.Folder);
        foreach (var path in Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false }))
        {
            var target = Path.Combine(copy, Path.GetRelativePath(root, path));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(path, target);
        }

        string hash;
        try
        {
            (_, hash) = Skills.SkillStore.Contents(copy);
        }
        catch (Skills.SkillException ex)
        {
            throw new SandboxException($"the skill {skill.Name} {ex.Message}", ex);
        }

        return hash == skill.Hash
            ? copy
            : throw new SandboxException($"the skill {skill.Name} changed since your yes: 'diptick skills approve {skill.Name}' shows it again");
    }

    /// <summary>
    /// A run's environment, and nothing of DipTick's: Windows' own folder, PATH to the interpreter and
    /// System32, a home of its own and the container's temporary folder in the run's folder, SKILL_DIR for a
    /// skill's copy; telemetry off, since the network is shut anyway; and colour off, since what a run says is
    /// read as text by a model and the chat: NO_COLOR, which PowerShell 7.2 and later read as
    /// <c>$PSStyle.OutputRendering = PlainText</c> (Microsoft Learn, about_ANSI_Terminals, read 2026-09-28),
    /// as the no-color.org convention asks of other programs. Without it, PowerShell's errors come out
    /// coloured.
    /// </summary>
    private static Dictionary<string, string> Variables(Interpreter interpreter, string folder, string? skillCopy)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var home = Path.Combine(folder, Home);
        var temporary = TemporaryFolder(folder);
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SystemRoot"] = windows,
            ["windir"] = windows,
            ["PATH"] = $"{Path.GetDirectoryName(interpreter.Program)};{Environment.SystemDirectory}",
            ["PATHEXT"] = ".COM;.EXE;.BAT;.CMD",
            ["TEMP"] = temporary,
            ["TMP"] = temporary,
            ["USERPROFILE"] = home,
            ["HOME"] = home,
            ["LOCALAPPDATA"] = home,
            ["APPDATA"] = home,
            ["POWERSHELL_TELEMETRY_OPTOUT"] = "1",
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            ["NO_COLOR"] = "1",
        };
        if (skillCopy is not null)
        {
            variables["SKILL_DIR"] = skillCopy;
        }

        return variables;
    }

    /// <summary>The files a run made in its folder, but its script, PowerShell's start, its own home's and a skill's copy: at most 100, by name and size.</summary>
    private static List<(string Name, long Size)> Made(string folder, string script)
    {
        var home = Path.Combine(folder, Home) + Path.DirectorySeparatorChar;
        var skill = Path.Combine(folder, SkillFolder) + Path.DirectorySeparatorChar;
        var start = Path.Combine(folder, PowerShellStart);
        try
        {
            return [.. Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })
                .Where(f => !string.Equals(f, script, StringComparison.OrdinalIgnoreCase) && !string.Equals(f, start, StringComparison.OrdinalIgnoreCase)
                    && !f.StartsWith(home, StringComparison.OrdinalIgnoreCase) && !f.StartsWith(skill, StringComparison.OrdinalIgnoreCase))
                .Take(100)
                .Select(f => (Path.GetRelativePath(folder, f), new FileInfo(f).Length))];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
