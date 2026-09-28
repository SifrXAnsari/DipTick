using System.Globalization;
using System.Runtime.Versioning;
using DipTick.Controllers;

// Windows only: the harness leans on DPAPI for the audit key and on the Win32 console and file
// APIs for its checks. Said once here rather than on every class that does.
[assembly: SupportedOSPlatform("windows")]

namespace DipTick;

internal static class Program
{
    // Single-threaded apartment, for the window (diptick window): the Win32 backend expects it.
    [STAThread]
    private static int Main(string[] args)
    {
        // How DipTick was started decides where it speaks. From a terminal, the command line as
        // always. From Explorer, the Start menu or a shortcut, diptick alone and diptick tui are the
        // terminal screen, which keeps a console made for it or makes one when Windows made none;
        // anything else is the window, or a message box. The window a terminal starts has no
        // console at all (WindowStart), and so starts here as from Explorer.
        var terminalScreen = CommandController.VerbOf(args) is "tui" or null;
        switch (ConsoleHost.Started())
        {
            case ConsoleStart.Terminal:
            case ConsoleStart.Alone when terminalScreen:
            case ConsoleStart.None when terminalScreen && ConsoleHost.Open():
                return InTerminal(args);
            default:
                return Windowed(args);
        }
    }

    /// <summary>The command line: Ctrl+C ends a run with STATUS_CONTROL_C_EXIT, after the verb has stopped cleanly where it can.</summary>
    private static int InTerminal(string[] args)
    {
        using var consoleBreak = new ConsoleBreak();
        var code = CommandController.Run(args, Console.Out, Console.Error, consoleBreak);
        return consoleBreak.Pressed ? ConsoleBreak.ExitCode : code;
    }

    /// <summary>
    /// Started with no console: the window, and anything that fails before a window can say so is
    /// said in a message box, since nothing else would show it. What a verb prints has nowhere to go.
    /// </summary>
    private static int Windowed(string[] args)
    {
        using var said = new StringWriter(CultureInfo.InvariantCulture);
        var code = CommandController.Run(args, TextWriter.Null, said, windowed: true);
        if (code != Exit.Ok && said.ToString().Trim() is { Length: > 0 } text)
        {
            // In a box of its own, the "diptick: " a terminal line starts with says nothing.
            ConsoleHost.Tell(string.Join('\n', text.ReplaceLineEndings("\n").Split('\n').Select(l => l.StartsWith("diptick: ", StringComparison.Ordinal) ? l["diptick: ".Length..] : l)));
        }

        return code;
    }
}
