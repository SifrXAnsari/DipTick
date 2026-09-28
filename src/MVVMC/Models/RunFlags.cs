using System.Globalization;

namespace DipTick.Models;

internal enum OutputFormat
{
    Text,
    Json,
    None,
}

internal enum Verbosity
{
    Quiet,
    Normal,
    Verbose,
    Debug,
}

internal enum ColorMode
{
    Auto,
    Always,
    Never,
}

/// <summary>Where a resolved setting came from, strongest last.</summary>
internal enum Layer
{
    Default,
    File,
    Variable,
    Flag,
}

/// <summary>The global switches as typed, before any other layer is consulted.</summary>
internal sealed class FlagValues
{
    public bool? DryRun { get; set; }

    public bool? Yes { get; set; }

    public bool? Interactive { get; set; }

    public OutputFormat? Output { get; set; }

    public Verbosity? Verbosity { get; set; }

    public ColorMode? Color { get; set; }

    public int? TimeoutSeconds { get; set; }

    /// <summary>A path, or "none" for no file at all.</summary>
    public string? Config { get; set; }

    public bool Help { get; set; }

    public bool Version { get; set; }

    /// <summary>
    /// Where a lone <c>--</c> stood among the words that are not switches, or null when none was
    /// given. From that index on every word is text: a verb reads none of them as its option.
    /// </summary>
    public int? LiteralFrom { get; set; }
}

/// <summary>
/// Every global switch after all four layers: flag, then <c>DIPTICK_*</c> variable, then the
/// master config's <c>cli</c> section, then the built-in default.
/// Every one can be turned off from the command line, with <c>--no-x</c> or a word like
/// <c>none</c> or <c>never</c>.
/// </summary>
internal sealed record RunFlags(
    bool DryRun,
    bool Yes,
    bool Interactive,
    OutputFormat Output,
    Verbosity Verbosity,
    ColorMode Color,
    bool ColorOut,
    bool ColorErr,
    int NetTimeoutSeconds,
    int MainTimeoutSeconds,
    IReadOnlyDictionary<string, Layer> Layers)
{
    public const string Prefix = "DIPTICK_";

    /// <summary>For callers with no command line: every default, nothing interactive.</summary>
    public static RunFlags Default { get; } = new(
        false, false, false, OutputFormat.Text, Verbosity.Normal, ColorMode.Never, false, false, 60, 120,
        new Dictionary<string, Layer>());

    /// <summary>
    /// Takes the global switches off <paramref name="args"/> wherever they appear and returns the
    /// rest in order. The last of <c>--x</c> / <c>--no-x</c> wins; <c>--</c> ends switches. A
    /// switch this does not know is left for the verb, which refuses what it does not know either.
    /// </summary>
    public static (FlagValues Flags, List<string> Remaining, List<string> Errors) Parse(IReadOnlyList<string> args)
    {
        var flags = new FlagValues();
        var rest = new List<string>();
        var errors = new List<string>();
        var literal = false;
        var sawDryRunOn = false;
        var sawYesOff = false;
        var sawInteractiveOff = false;

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (literal || !arg.StartsWith('-') || arg == "-")
            {
                rest.Add(arg);
                continue;
            }

            var eq = arg.IndexOf('=', StringComparison.Ordinal);
            var name = eq < 0 ? arg : arg[..eq];
            var stuck = eq < 0 ? null : arg[(eq + 1)..];

            string? Value()
            {
                if (stuck is not null)
                {
                    return stuck;
                }

                if (i + 1 < args.Count)
                {
                    return args[++i];
                }

                errors.Add($"{name} needs a value.");
                return null;
            }

            bool? Toggle(bool on)
            {
                if (stuck is null)
                {
                    return on;
                }

                if (ParseBool(stuck) is { } b)
                {
                    return on ? b : !b;
                }

                errors.Add($"{name}={stuck}: say true or false.");
                return null;
            }

            switch (name)
            {
                case "--":
                    // The boundary is noted for the verb: what follows is text, never its option.
                    flags.LiteralFrom ??= rest.Count;
                    literal = true;
                    break;
                case "--dry-run" or "-n":
                    flags.DryRun = Toggle(true) ?? flags.DryRun;
                    sawDryRunOn |= flags.DryRun == true;
                    break;
                case "--no-dry-run":
                    flags.DryRun = false;
                    break;
                case "--yes" or "-y":
                    flags.Yes = Toggle(true) ?? flags.Yes;
                    sawYesOff |= flags.Yes == false;
                    break;
                case "--no-yes":
                    flags.Yes = false;
                    sawYesOff = true;
                    break;
                case "--interactive" or "-i":
                    flags.Interactive = Toggle(true) ?? flags.Interactive;
                    sawInteractiveOff |= flags.Interactive == false;
                    break;
                case "--no-interactive" or "--no-input":
                    flags.Interactive = false;
                    sawInteractiveOff = true;
                    break;
                case "--output":
                    if (Value() is { } o)
                    {
                        flags.Output = ParseOutput(o) ?? Bad<OutputFormat>(errors, "--output", o, "text, json or none");
                    }

                    break;
                case "--json":
                    flags.Output = OutputFormat.Json;
                    break;
                case "--no-json":
                    flags.Output = OutputFormat.Text;
                    break;
                case "--verbosity":
                    if (Value() is { } v)
                    {
                        flags.Verbosity = ParseVerbosity(v) ?? Bad<Verbosity>(errors, "--verbosity", v, "quiet, normal, verbose or debug");
                    }

                    break;
                case "--quiet" or "-q" or "--silent":
                    flags.Verbosity = Verbosity.Quiet;
                    break;
                case "--verbose":
                    flags.Verbosity = Verbosity.Verbose;
                    break;
                case "--debug" or "-d":
                    flags.Verbosity = Verbosity.Debug;
                    break;
                case "--no-quiet" or "--no-silent" or "--no-verbose" or "--no-debug":
                    flags.Verbosity = Verbosity.Normal;
                    break;
                case "--color" or "--colour":
                    if (Value() is { } c)
                    {
                        flags.Color = ParseColor(c) ?? Bad<ColorMode>(errors, "--color", c, "auto, always or never");
                    }

                    break;
                case "--no-color" or "--no-colour":
                    flags.Color = ColorMode.Never;
                    break;
                case "--config":
                    if (Value() is { } path)
                    {
                        flags.Config = path;
                    }

                    break;
                case "--no-config":
                    flags.Config = "none";
                    break;
                case "--timeout":
                    if (Value() is { } t)
                    {
                        flags.TimeoutSeconds = ParseTimeout(t, allowZero: true) ?? Bad<int>(errors, "--timeout", t, "whole seconds, 0 to 600 (0 waits for ever)");
                    }

                    break;
                case "--no-timeout":
                    flags.TimeoutSeconds = 0;
                    break;
                case "--help" or "-h":
                    flags.Help = true;
                    break;
                case "--version":
                    flags.Version = true;
                    break;
                default:
                    rest.Add(arg);
                    break;
            }
        }

        // Safety wins a conflict, wherever each switch sits: an untrusted value placed at the end
        // of a line ("... --no-dry-run") cannot undo a --dry-run, a --no-yes or a --no-interactive
        // given anywhere on it. Pass untrusted values after "--".
        if (sawDryRunOn)
        {
            flags.DryRun = true;
        }

        if (sawYesOff)
        {
            flags.Yes = false;
        }

        if (sawInteractiveOff)
        {
            flags.Interactive = false;
        }

        return (flags, rest, errors);
    }

    /// <summary>Folds the four layers into one answer, and names the layer each came from.</summary>
    public static (RunFlags Flags, List<string> Errors) Resolve(FlagValues typed, MasterConfig config)
    {
        ArgumentNullException.ThrowIfNull(typed);
        ArgumentNullException.ThrowIfNull(config);

        var errors = new List<string>();
        var layers = new Dictionary<string, Layer>(StringComparer.Ordinal);

        T Pick<T>(string key, T? flag, string variable, Func<string, T?> parse, string expected, T? file, bool fileSet, T fallback)
            where T : struct
        {
            if (flag is { } f)
            {
                layers[key] = Layer.Flag;
                return f;
            }

            var raw = Environment.GetEnvironmentVariable(Prefix + variable);
            if (!string.IsNullOrEmpty(raw))
            {
                if (parse(raw.Trim()) is { } fromVariable)
                {
                    layers[key] = Layer.Variable;
                    return fromVariable;
                }

                errors.Add($"{Prefix}{variable}={raw}: expected {expected}.");
            }

            if (fileSet && file is { } fromFile)
            {
                layers[key] = Layer.File;
                return fromFile;
            }

            layers[key] = Layer.Default;
            return fallback;
        }

        bool IsFile(string name) => config.Settings.Any(s => s.Name == name && !s.FromDefault);

        var dryRun = Pick("cli.dryRun", typed.DryRun, "DRY_RUN", ParseBool, "true or false", config.CliDryRun, IsFile("cli.dryRun"), false);
        var yes = Pick("cli.yes", typed.Yes, "YES", ParseBool, "true or false", config.CliYes, IsFile("cli.yes"), false);

        var interactiveAuto = !Console.IsInputRedirected;
        var interactive = Pick(
            "cli.interactive", typed.Interactive, "INTERACTIVE",
            s => s.Equals("auto", StringComparison.OrdinalIgnoreCase) ? interactiveAuto : ParseBool(s),
            "auto, true or false", config.CliInteractive, config.CliInteractive is not null, interactiveAuto);

        var output = Pick("output.format", typed.Output, "OUTPUT", ParseOutput, "text, json or none", ParseOutput(config.OutputFormat), IsFile("output.format"), OutputFormat.Text);
        var verbosity = Pick("cli.verbosity", typed.Verbosity, "VERBOSITY", ParseVerbosity, "quiet, normal, verbose or debug", ParseVerbosity(config.CliVerbosity), IsFile("cli.verbosity"), Verbosity.Normal);

        // Colour: NO_COLOR sits below the config file and above auto (no-color.org: "user-level
        // configuration files and per-instance command-line arguments should override it").
        var color = Pick("cli.color", typed.Color, "COLOR", ParseColor, "auto, always or never", ParseColor(config.CliColor), IsFile("cli.color") && ParseColor(config.CliColor) != ColorMode.Auto, ColorMode.Auto);
        if (color == ColorMode.Auto && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR")))
        {
            color = ColorMode.Never;
            layers["cli.color"] = Layer.Variable;
        }

        var dumb = string.Equals(Environment.GetEnvironmentVariable("TERM"), "dumb", StringComparison.Ordinal);
        var colorOut = color == ColorMode.Always || (color == ColorMode.Auto && !Console.IsOutputRedirected && !dumb);
        var colorErr = color == ColorMode.Always || (color == ColorMode.Auto && !Console.IsErrorRedirected && !dumb);

        // Only a flag or a variable can say 0 (wait for ever); the file's range starts at 1.
        var timeout = Pick("cli.timeoutSeconds", typed.TimeoutSeconds, "TIMEOUT", s => ParseTimeout(s, allowZero: true), "whole seconds, 0 to 600", config.CliTimeoutSeconds, IsFile("cli.timeoutSeconds"), 60);
        var aiTimeout = layers["cli.timeoutSeconds"] is Layer.Flag or Layer.Variable ? timeout : config.MainTimeoutSeconds;

        return (new RunFlags(dryRun, yes, interactive, output, verbosity, color, colorOut, colorErr, timeout, aiTimeout, layers), errors);
    }

    public static bool? ParseBool(string text) => text.Trim().ToUpperInvariant() switch
    {
        "1" or "TRUE" or "YES" or "ON" => true,
        "0" or "FALSE" or "NO" or "OFF" => false,
        _ => null,
    };

    public static OutputFormat? ParseOutput(string text) => text.Trim().ToUpperInvariant() switch
    {
        "TEXT" => OutputFormat.Text,
        "JSON" => OutputFormat.Json,
        "NONE" or "OFF" => OutputFormat.None,
        _ => null,
    };

    public static Verbosity? ParseVerbosity(string text) => text.Trim().ToUpperInvariant() switch
    {
        "QUIET" or "SILENT" => Models.Verbosity.Quiet,
        "NORMAL" => Models.Verbosity.Normal,
        "VERBOSE" => Models.Verbosity.Verbose,
        "DEBUG" => Models.Verbosity.Debug,
        _ => null,
    };

    public static ColorMode? ParseColor(string text) => text.Trim().ToUpperInvariant() switch
    {
        "AUTO" => ColorMode.Auto,
        "ALWAYS" => ColorMode.Always,
        "NEVER" or "NONE" or "OFF" => ColorMode.Never,
        _ => null,
    };

    public static int? ParseTimeout(string text, bool allowZero) =>
        int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n <= 600 && (n >= 1 || (allowZero && n == 0))
            ? n
            : null;

    private static T? Bad<T>(List<string> errors, string flag, string value, string expected)
        where T : struct
    {
        errors.Add($"{flag} {value}: expected {expected}.");
        return null;
    }
}
