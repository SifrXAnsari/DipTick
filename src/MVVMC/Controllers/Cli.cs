using System.Text.Json;
using DipTick.Models;

namespace DipTick.Controllers;

/// <summary>Exit codes.</summary>
internal static class Exit
{
    /// <summary>Done; also --help, --version, and a dry run in which nothing would be refused.</summary>
    public const int Ok = 0;

    /// <summary>Failed, or a check said no.</summary>
    public const int Failed = 1;

    /// <summary>Usage: an unknown verb, flag or key, a bad value, or contradictory flags.</summary>
    public const int Usage = 2;

    /// <summary>Nothing was done because there was no yes. The message names the flag that would give one.</summary>
    public const int NoYes = 3;
}

/// <summary>What a person's confirmation came to.</summary>
internal enum Answer
{
    Yes,
    No,

    /// <summary>A yes was needed and nobody could be asked: exit 3, naming the flag.</summary>
    NoOneToAsk,
}

/// <summary>
/// One run of the command line: its resolved flags, its config, and its two streams. Data goes
/// to stdout and nothing else does; messages, prompts and errors go to stderr, so
/// <c>--output json</c> is always parseable.
/// </summary>
internal sealed class Cli(RunFlags flags, MasterConfig config, TextWriter output, TextWriter error, string? chosenConfig = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,

        // Terminal and pipe output, never HTML: keep apostrophes and non-ASCII readable.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions EventOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public RunFlags Flags { get; } = flags;

    public MasterConfig Config { get; } = config;

    /// <summary>Where a lone <c>--</c> stood in the verb's words: from there on, every word is text.</summary>
    public int? LiteralFrom { get; init; }

    /// <summary>The command line as it was given, flags and all: what <c>diptick window</c> starts the window's own process with.</summary>
    public IReadOnlyList<string> Raw { get; init; } = [];

    /// <summary>
    /// The person at a screen that runs this verb in its own process (<c>diptick ui</c>): its
    /// questions go there, as they would to the terminal. Null on the command line, where the
    /// keyboard answers. Only the screen sets it; nothing typed on a command line can.
    /// </summary>
    public Func<string, bool?>? Ask { get; init; }

    /// <summary>
    /// A screen's approval with three answers, Deny, Allow once and Allow for this chat, or null where
    /// the screen has only yes and no, and on the command line.
    /// </summary>
    public Func<string, Models.Harness.Approval?>? Approve { get; init; }

    /// <summary>Ends the verb's work from outside: the screen's Esc. On the command line, Ctrl+C.</summary>
    public CancellationToken Stop { get; init; }

    /// <summary>
    /// True when no terminal started DipTick (Explorer, a shortcut; <see cref="ConsoleHost"/>): the
    /// window is the only place a person can be asked anything, and nothing printed is seen.
    /// </summary>
    public bool Windowed { get; init; }

    /// <summary>Ctrl+C on the command line, or null where a screen runs the verb (<see cref="ConsoleBreak"/>).</summary>
    public ConsoleBreak? Break { get; init; }

    /// <summary>
    /// Said by a verb that stops cleanly at <see cref="Stop"/>: the first Ctrl+C asks it to stop,
    /// rather than ending DipTick at once. Nothing where a screen runs the verb.
    /// </summary>
    public void StopsCleanly() => Break?.StopsCleanly();

    /// <summary>The config path the person named with --config or DIPTICK_CONFIG, or null.</summary>
    public string? ChosenConfig { get; } = chosenConfig;

    public TextWriter Out { get; } = output;

    public TextWriter Err { get; } = error;

    public bool Json => Flags.Output == OutputFormat.Json;

    public string Who => Config.UserName.Length > 0 ? Config.UserName : Environment.UserName;

    /// <summary>A line of data, unless the output is none.</summary>
    public void Data(string line)
    {
        if (Flags.Output != OutputFormat.None)
        {
            Out.WriteLine(line);
        }
    }

    /// <summary>With --output json, writes <paramref name="value"/> and returns true; otherwise false.</summary>
    public bool WriteJson(object value)
    {
        if (!Json)
        {
            return false;
        }

        Out.WriteLine(JsonSerializer.Serialize(value, JsonOptions));
        return true;
    }

    /// <summary>
    /// One event as one line of JSON (JSON Lines), flushed at once, so whoever reads sees it as it
    /// happens. Agents answering at once write their events from their own threads; one line never
    /// breaks into another.
    /// </summary>
    public void WriteEvent(object value)
    {
        var line = JsonSerializer.Serialize(value, EventOptions);
        lock (_writing)
        {
            Out.WriteLine(line);
            Out.Flush();
        }
    }

    /// <summary>Text to the output from several threads at once, each piece whole.</summary>
    public void WriteText(string text)
    {
        lock (_writing)
        {
            Out.Write(text);
            Out.Flush();
        }
    }

    private readonly Lock _writing = new();

    /// <summary>
    /// A harness question for the person, as a gate asks it: the screen's when a screen runs this
    /// verb, else the keyboard's when there is one, else nobody (null).
    /// </summary>
    public bool? Question(string question) =>
        Ask is { } screen ? screen(question)
        : Flags.Interactive ? ConsoleInput.Ask(question, Err)
        : null;

    /// <summary>
    /// The flags as a gate judges them: a screen that runs this verb (<see cref="Ask"/>) asks its
    /// person through <see cref="Question"/>, so a request that needs a yes is asked there, and not
    /// denied for want of a keyboard. The verb itself still never reads the console (--no-input).
    /// </summary>
    public RunFlags GateFlags => Ask is null ? Flags : Flags with { Interactive = true };

    /// <summary>A message for the person. Quiet keeps it back; errors and prompts are never kept back.</summary>
    public void Note(string line)
    {
        if (Flags.Verbosity != Verbosity.Quiet)
        {
            Err.WriteLine(line);
        }
    }

    public void Verbose(string line)
    {
        if (Flags.Verbosity >= Verbosity.Verbose)
        {
            Err.WriteLine(line);
        }
    }

    public int Fail(string message, int code = Exit.Failed)
    {
        Err.WriteLine($"diptick: {message}");
        return code;
    }

    /// <summary>
    /// A moderate confirmation for something the person asked for. Dry run wins over everything
    /// (nothing changes, nothing is asked); then --yes; then a question on the keyboard; with no
    /// keyboard, nobody to ask. Never a default answer, never going ahead without asking.
    /// </summary>
    public Answer Confirm(string question)
    {
        if (Flags.DryRun)
        {
            return Answer.No;
        }

        if (Flags.Yes)
        {
            return Answer.Yes;
        }

        // A screen running this verb asks its person, where the terminal would ask.
        if (Ask is { } screen)
        {
            return screen(question + " [y/N] ") switch
            {
                true => Answer.Yes,
                false => Answer.No,
                null => Answer.NoOneToAsk,
            };
        }

        if (!Flags.Interactive || Console.IsInputRedirected)
        {
            return Answer.NoOneToAsk;
        }

        return ConsoleInput.Confirm(question + " [y/N] ", Err) ? Answer.Yes : Answer.No;
    }

    /// <summary>The exit for an answer that was not yes, with the flag that would have given one.</summary>
    public int NotConfirmed(Answer answer, string what) => answer == Answer.NoOneToAsk
        ? Fail($"{what} needs a yes and there is no one to ask. Pass --yes, or run it at a keyboard.", Exit.NoYes)
        : Fail($"{what}: not confirmed; nothing changed.", Exit.NoYes);
}
