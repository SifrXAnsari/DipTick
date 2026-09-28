using System.ComponentModel;
using System.Runtime.InteropServices;
using DipTick.Models.Sandbox;

namespace DipTick.Controllers;

/// <summary>
/// The window as a program of its own, for <c>diptick window</c> at a terminal and for /window and
/// F2 in the terminal screen: a second diptick.exe, started detached, so the terminal stays free
/// and closing it leaves the window open. Nothing is inherited: no console, no handles, and a
/// process group of its own, so Ctrl+C in the terminal never reaches it. With no console it runs
/// as a start from Explorer does (<see cref="ConsoleHost"/>), so a password, when one is set, is
/// asked for in the window.
/// </summary>
internal static partial class WindowStart
{
    // WinBase.h: CreateProcessW's creation flags ("Process Creation Flags", Microsoft Learn).
    private const uint DetachedProcess = 0x00000008;
    private const uint CreateNewProcessGroup = 0x00000200;

    /// <summary>
    /// The window's own command line: <paramref name="arguments"/> as they were given, the verb
    /// among them, and <c>--wait</c>, so the process started runs the window itself rather than
    /// starting another.
    /// </summary>
    public static string CommandLine(string program, IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(arguments);
        return JobProcess.CommandLine(program, [.. arguments, "--wait"]);
    }

    /// <summary>
    /// Starts the window with <paramref name="arguments"/>, and returns once Windows has started it:
    /// null, or why it could not.
    /// </summary>
    public static unsafe string? Start(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var program = Environment.ProcessPath;
        if (string.IsNullOrEmpty(program))
        {
            return "DipTick could not find its own program file to start the window from.";
        }

        // CreateProcessW may write into the command line it is given, so it gets a copy of its own.
        var line = (CommandLine(program, arguments) + '\0').ToCharArray();
        var startup = new StartupInfo { Size = (uint)sizeof(StartupInfo) };
        ProcessInformation started;
        fixed (char* application = program)
        fixed (char* command = line)
        {
            if (!CreateProcess(application, command, 0, 0, 0, DetachedProcess | CreateNewProcessGroup, null, null, &startup, &started))
            {
                return $"Windows would not start the window: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}";
            }
        }

        // DipTick does not wait for the window or ask how it ended: the handles go at once.
        _ = CloseHandle(started.Thread);
        _ = CloseHandle(started.Process);
        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public uint Size;
        public nint Reserved;
        public nint Desktop;
        public nint Title;
        public uint X;
        public uint Y;
        public uint XSize;
        public uint YSize;
        public uint XCountChars;
        public uint YCountChars;
        public uint FillAttribute;
        public uint Flags;
        public ushort ShowWindow;
        public ushort Reserved2Size;
        public nint Reserved2;
        public nint StdInput;
        public nint StdOutput;
        public nint StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint Process;
        public nint Thread;
        public uint ProcessId;
        public uint ThreadId;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool CreateProcess(char* application, char* commandLine, nint processAttributes, nint threadAttributes, int inheritHandles, uint flags, char* environment, char* folder, StartupInfo* startup, ProcessInformation* started);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
