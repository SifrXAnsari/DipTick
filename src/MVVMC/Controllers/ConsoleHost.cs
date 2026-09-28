using System.Runtime.InteropServices;

namespace DipTick.Controllers;

/// <summary>How DipTick came to be running, as its console tells it.</summary>
internal enum ConsoleStart
{
    /// <summary>In a terminal, or with its output going to a file or a pipe: the command line as always.</summary>
    Terminal,

    /// <summary>In a console made for it alone (older Windows, from Explorer or a shortcut).</summary>
    Alone,

    /// <summary>With no console and nothing where its output would go (the detached policy, from Explorer).</summary>
    None,
}

/// <summary>
/// Whether DipTick was started from a terminal, and what to do when it was not. diptick.exe stays
/// a console program, so a shell waits for it and its verbs print where they were run. Started
/// from Explorer or a shortcut, it should look like any other windowed program: no console window
/// behind the window.
/// </summary>
/// <remarks>
/// <para>
/// On Windows 11 24H2 (build 26100) and later, the manifest's <c>consoleAllocationPolicy</c>
/// <c>detached</c> keeps Windows from making a console for it at all ("Console Allocation Policy",
/// Microsoft Learn): there is no console, and no standard handles either.
/// </para>
/// <para>
/// On older Windows, a console program started outside a terminal is given a console of its own,
/// and it is the only process in it: GetConsoleProcessList returns 1 (Raymond Chen, The Old New
/// Thing, 2016). <see cref="Detach"/> lets go of it before the window opens, and Windows closes it.
/// </para>
/// <para>
/// The terminal screen is the one verb that needs a console wherever it is started: in one made for
/// it alone it stays, and with none it makes its own (<see cref="Open"/>).
/// </para>
/// </remarks>
internal static partial class ConsoleHost
{
    // processenv.h: the standard handles' ids, and a handle that names nothing.
    private const int StdInput = -10;
    private const int StdOutput = -11;
    private const int StdError = -12;
    private const nint InvalidHandle = -1;

    // winuser.h: MB_OK, MB_ICONERROR, MB_SETFOREGROUND.
    private const uint MbOk = 0x00000000;
    private const uint MbIconError = 0x00000010;
    private const uint MbSetForeground = 0x00010000;

    /// <summary>
    /// How DipTick was started: from a terminal; into a console made for it alone (older Windows);
    /// or with no console and no standard handles at all (the detached policy). A program that
    /// started it with its output going to a file or a pipe is not a person, and counts as a terminal.
    /// </summary>
    public static ConsoleStart Started()
    {
        if (GetConsoleWindow() == 0)
        {
            return Has(StdInput) || Has(StdOutput) || Has(StdError) ? ConsoleStart.Terminal : ConsoleStart.None;
        }

        return Alone() ? ConsoleStart.Alone : ConsoleStart.Terminal;
    }

    /// <summary>
    /// A console of DipTick's own, for the terminal screen when Windows gave it none: what a shortcut
    /// to <c>diptick tui</c> gets where the detached policy does not apply. AllocConsole sets the
    /// standard handles to the new console, and fails only for a process that has one already
    /// ("AllocConsole", Microsoft Learn), so nothing may touch <see cref="Console"/> before it. False
    /// when Windows would not make one.
    /// </summary>
    public static bool Open() => AllocConsole();

    /// <summary>
    /// Lets go of the console, when DipTick is the only process in it: Windows then closes it. A
    /// console shared with a shell is never let go. Nothing happens when there is no console.
    /// </summary>
    public static void Detach()
    {
        if (GetConsoleWindow() != 0 && Alone())
        {
            // Nothing is written to it after this, so a failure changes nothing that matters.
            _ = FreeConsole();
        }
    }

    /// <summary>Says <paramref name="text"/> in a Windows message box: the one place left to say it with no console.</summary>
    public static void Tell(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        _ = MessageBox(0, text, "DipTick", MbOk | MbIconError | MbSetForeground);
    }

    /// <summary>Whether DipTick is the only process in its console: the console was made for it, not a terminal's.</summary>
    private static bool Alone()
    {
        // A buffer of two tells one from more: a larger list returns its size and fills nothing.
        Span<uint> list = stackalloc uint[2];
        return GetConsoleProcessList(ref MemoryMarshal.GetReference(list), (uint)list.Length) == 1;
    }

    private static bool Has(int which) => GetStdHandle(which) is var handle && handle != 0 && handle != InvalidHandle;

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint GetConsoleWindow();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint GetConsoleProcessList(ref uint processList, uint processCount);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FreeConsole();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllocConsole();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint GetStdHandle(int which);

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int MessageBox(nint owner, string text, string caption, uint type);
}
