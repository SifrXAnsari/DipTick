using System.ComponentModel;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DipTick.Models.Sandbox;

/// <summary>
/// What a sandboxed start adds: the AppContainer the process becomes,
/// by its SID; the capabilities it is given, as SID strings (none: no network); and the job's limits:
/// how many processes at once, how much committed memory, and how much CPU time, where there is a cap
/// (a connector has none: it lives as long as DipTick). Every UI restriction is on.
/// </summary>
internal sealed record SandboxSpec(nint AppContainerSid, int MostProcesses, long MostMemory, TimeSpan? MostCpu, IReadOnlyList<string>? Capabilities = null);

/// <summary>Why a program could not be started, in words; never anything the program printed.</summary>
internal sealed class LaunchException : Exception
{
    public LaunchException()
    {
    }

    public LaunchException(string message)
        : base(message)
    {
    }

    public LaunchException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>
/// A program DipTick starts itself (a local connector, or the code a run starts): inside a
/// Job object from its first instruction, so closing the job, or DipTick ending, ends it and everything
/// it started; with only its three pipes inherited, no window, and the environment it is given and no
/// other.
/// </summary>
/// <remarks>
/// From the Windows SDK's headers (10.0.26100) and Microsoft Learn: <c>CreateProcessW</c> with
/// <c>EXTENDED_STARTUPINFO_PRESENT</c> (0x00080000) and an attribute list holding
/// <c>PROC_THREAD_ATTRIBUTE_HANDLE_LIST</c> (0x20002: "a list of handles to be inherited by the child
/// process", created inheritable, with bInheritHandles TRUE) and <c>PROC_THREAD_ATTRIBUTE_JOB_LIST</c>
/// (0x2000D: "job handles to be assigned to the child process", Windows 10 and later);
/// <c>CREATE_UNICODE_ENVIRONMENT</c> (0x400) with a block "sorted alphabetically by name ...
/// case-insensitive, Unicode order, without regard to locale", ended by two nulls; <c>CREATE_NO_WINDOW</c>
/// (0x08000000); <c>STARTF_USESTDHANDLES</c> (0x100). The job's
/// <c>JOBOBJECT_EXTENDED_LIMIT_INFORMATION</c> (class 9) sets <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>
/// (0x2000) and <c>JOB_OBJECT_LIMIT_DIE_ON_UNHANDLED_EXCEPTION</c> (0x400), so a crash ends quietly
/// instead of waiting on an error dialog nobody sees. The program is always named in full
/// (<c>lpApplicationName</c>), as the page's security remarks ask.
/// </remarks>
internal sealed partial class JobProcess : IDisposable
{
    /// <summary>The most a command line may be, in characters, with its terminating null (CreateProcessW).</summary>
    public const int MostCommandLine = 32_767;

    /// <summary>The last lines the program wrote to its error stream, kept to say why it stopped.</summary>
    private const int ErrorLinesKept = 40;

    /// <summary>The most of its error stream kept whole, in characters; the rest is counted.</summary>
    public const int MostErrorText = 256 * 1024;

    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateNoWindow = 0x08000000;
    private const uint StartfUseStdHandles = 0x00000100;
    private const nint ProcThreadAttributeHandleList = 0x20002;
    private const nint ProcThreadAttributeJobList = 0x2000D;
    private const nint ProcThreadAttributeSecurityCapabilities = 0x20009;
    private const int JobObjectBasicUiRestrictions = 4;
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitJobTime = 0x00000004;
    private const uint JobObjectLimitActiveProcess = 0x00000008;
    private const uint JobObjectLimitJobMemory = 0x00000200;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const uint JobObjectLimitDieOnUnhandledException = 0x00000400;

    /// <summary>Every <c>JOB_OBJECT_UILIMIT_*</c> flag, 0x1 to 0x80: handles, the clipboard both ways, system parameters, display settings, global atoms, desktops, exiting Windows.</summary>
    private const uint JobObjectUiLimitAll = 0x000000FF;
    private const uint WaitObject0 = 0;
    private const uint StillActive = 259;

    /// <summary><c>SE_GROUP_ENABLED</c> (TOKEN_GROUPS, Microsoft Learn): "The SID is enabled for access checks."</summary>
    private const uint SeGroupEnabled = 0x00000004;

    private readonly SafeFileHandle _job;
    private readonly SafeProcessHandle _process;
    private readonly Queue<string> _errors = new();
    private readonly StringBuilder _errorText = new();
    private long _errorDropped;
    private readonly Lock _errorLock = new();
    private readonly Stream _error;
    private readonly Task _errorReader;
    private int _disposed;

    private JobProcess(SafeFileHandle job, SafeProcessHandle process, int id, Stream input, Stream output, Stream error, Action<string>? errorLine)
    {
        _job = job;
        _process = process;
        Id = id;
        Input = input;
        Output = output;
        _error = error;
        _errorReader = Task.Run(() => ReadErrors(error, errorLine));
    }

    /// <summary>The program's standard input: what DipTick writes to it.</summary>
    public Stream Input { get; }

    /// <summary>The program's standard output: what DipTick reads from it.</summary>
    public Stream Output { get; }

    /// <summary>Its process id, for the record.</summary>
    public int Id { get; }

    /// <summary>Whether it is still running.</summary>
    public bool Running => GetExitCodeProcess(_process, out var code) && code == StillActive;

    /// <summary>Its exit code, once it has ended; null while it runs.</summary>
    public int? ExitCode => GetExitCodeProcess(_process, out var code) && code != StillActive ? unchecked((int)code) : null;

    /// <summary>What it wrote to its error stream, at most <see cref="MostErrorText"/> characters, and how many more there were.</summary>
    public (string Text, long Dropped) ErrorText
    {
        get
        {
            lock (_errorLock)
            {
                return (_errorText.ToString(), _errorDropped);
            }
        }
    }

    /// <summary>The last lines it wrote to its error stream, oldest first.</summary>
    public IReadOnlyList<string> ErrorLines
    {
        get
        {
            lock (_errorLock)
            {
                return [.. _errors];
            }
        }
    }

    /// <summary>
    /// Starts <paramref name="program"/>, a full path to an .exe or .com, with <paramref name="arguments"/>
    /// quoted as the C runtime reads them back, in <paramref name="workingFolder"/>, with exactly
    /// <paramref name="environment"/>. Each line it writes to its error stream goes to
    /// <paramref name="errorLine"/> as well as being kept.
    /// </summary>
    /// <exception cref="LaunchException">It could not be started, and why.</exception>
    public static JobProcess Start(string program, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment, string workingFolder, Action<string>? errorLine = null, SandboxSpec? sandbox = null)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(arguments);
        return StartLine(program, CommandLine(program, arguments), environment, workingFolder, errorLine, sandbox);
    }

    /// <summary>
    /// As <see cref="Start"/>, with the command line already made: for cmd.exe, which reads its line by
    /// its own rules rather than the C runtime's, so its caller makes the line and refuses what cmd.exe
    /// would read as its own.
    /// </summary>
    /// <exception cref="LaunchException">It could not be started, and why.</exception>
    public static JobProcess StartLine(string program, string commandLine, IReadOnlyDictionary<string, string> environment, string workingFolder, Action<string>? errorLine = null, SandboxSpec? sandbox = null)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(commandLine);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(workingFolder);
        if (!Path.IsPathFullyQualified(program) || !File.Exists(program))
        {
            throw new LaunchException($"{program} is not a program's full path");
        }

        if (commandLine.Length + 1 > MostCommandLine)
        {
            throw new LaunchException(string.Create(CultureInfo.InvariantCulture, $"its command line is {commandLine.Length} characters; Windows takes at most {MostCommandLine - 1}"));
        }

        var job = NewJob(sandbox);
        AnonymousPipeServerStream? input = null, output = null, error = null;
        try
        {
            // Each pipe's end for the child is inheritable; DipTick's own end is a copy that is not (the
            // stream duplicates it so, in .NET's AnonymousPipeServerStream.Windows.cs), and only the
            // child's ends are listed to be inherited.
            input = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
            output = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
            error = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
            var (process, id) = Create(program, commandLine, environment, workingFolder, job, input.ClientSafePipeHandle, output.ClientSafePipeHandle, error.ClientSafePipeHandle, sandbox);

            // The child holds its own ends now; DipTick keeps only its own, so an end of file reaches both sides.
            input.DisposeLocalCopyOfClientHandle();
            output.DisposeLocalCopyOfClientHandle();
            error.DisposeLocalCopyOfClientHandle();
            return new JobProcess(job, process, id, input, output, error, errorLine);
        }
        catch
        {
            Close(input);
            Close(output);
            Close(error);
            job.Dispose();
            throw;
        }
    }

    /// <summary>
    /// A pipe given up: the child's end first, which the stream leaves open once its handle has been
    /// taken (<c>ClientSafePipeHandle</c>), then DipTick's own.
    /// </summary>
    private static void Close(AnonymousPipeServerStream? pipe)
    {
        pipe?.DisposeLocalCopyOfClientHandle();
        pipe?.Dispose();
    }

    /// <summary>
    /// A command line as the C runtime and <c>CommandLineToArgvW</c> read it back: an argument with no
    /// space, tab, line break or quote as it is; any other in quotes, a quote inside made \", and the
    /// backslashes before a quote, or before the closing one, doubled.
    /// </summary>
    public static string CommandLine(string program, IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(arguments);
        var line = new StringBuilder();
        Quote(line, program, always: true);
        foreach (var argument in arguments)
        {
            line.Append(' ');
            Quote(line, argument, always: false);
        }

        return line.ToString();
    }

    /// <summary>Waits for it to end, at most <paramref name="wait"/>; whether it did.</summary>
    public bool WaitForExit(TimeSpan wait) => WaitForSingleObject(_process, (uint)Math.Clamp(wait.TotalMilliseconds, 0, uint.MaxValue - 1)) == WaitObject0;

    /// <summary>Ends it and everything it started, at once.</summary>
    public void Kill()
    {
        if (!_job.IsInvalid && !_job.IsClosed)
        {
            _ = TerminateJobObject(_job, 1);
        }
    }

    /// <summary>Its pipes closed and its job closed, which ends everything still running in it.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Input.Dispose();
        Output.Dispose();
        _job.Dispose();
        _process.Dispose();

        // Closing the job ended everything that held the pipe's other end, so the reader reaches its end
        // and closes the stream itself; closing it here too covers a reader that has not.
        _ = _errorReader.Wait(TimeSpan.FromSeconds(2));
        _error.Dispose();
    }

    private void ReadErrors(Stream error, Action<string>? errorLine)
    {
        using var reader = new StreamReader(error, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false);
        try
        {
            while (reader.ReadLine() is { } line)
            {
                var shown = SafeText.Line(line.Length > 500 ? line[..500] + "…" : line);
                lock (_errorLock)
                {
                    _errors.Enqueue(shown);
                    while (_errors.Count > ErrorLinesKept)
                    {
                        _errors.Dequeue();
                    }

                    if (_errorText.Length + line.Length + 1 <= MostErrorText)
                    {
                        _errorText.Append(line).Append('\n');
                    }
                    else
                    {
                        _errorDropped += line.Length + 1;
                    }
                }

                errorLine?.Invoke(shown);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The pipe closed under the reader as the program ended: there is nothing more to read.
        }
    }

    private static void Quote(StringBuilder line, string argument, bool always)
    {
        if (!always && argument.Length > 0 && argument.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0)
        {
            line.Append(argument);
            return;
        }

        line.Append('"');
        var slashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                slashes++;
                continue;
            }

            if (c == '"')
            {
                line.Append('\\', (slashes * 2) + 1).Append('"');
            }
            else
            {
                line.Append('\\', slashes).Append(c);
            }

            slashes = 0;
        }

        line.Append('\\', slashes * 2).Append('"');
    }

    /// <summary>
    /// A job that ends everything in it when its last handle closes, and lets a crash end quietly; for a
    /// sandbox, with its limits on processes, memory and CPU time, and every UI restriction.
    /// </summary>
    private static unsafe SafeFileHandle NewJob(SandboxSpec? sandbox)
    {
        var job = CreateJobObject(0, null);
        if (job.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            job.Dispose();
            throw new LaunchException($"a Job object could not be made: {new Win32Exception(error).Message}");
        }

        var limits = new ExtendedLimitInformation { BasicLimitInformation = new BasicLimitInformation { LimitFlags = JobObjectLimitKillOnJobClose | JobObjectLimitDieOnUnhandledException } };
        if (sandbox is not null)
        {
            limits.BasicLimitInformation.LimitFlags |= JobObjectLimitActiveProcess | JobObjectLimitJobMemory;
            limits.BasicLimitInformation.ActiveProcessLimit = (uint)Math.Max(1, sandbox.MostProcesses);
            limits.JobMemoryLimit = (nuint)Math.Max(1, sandbox.MostMemory);
            if (sandbox.MostCpu is { } cpu)
            {
                limits.BasicLimitInformation.LimitFlags |= JobObjectLimitJobTime;
                limits.BasicLimitInformation.PerJobUserTimeLimit = cpu.Ticks;
            }
        }

        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, &limits, (uint)sizeof(ExtendedLimitInformation)))
        {
            var error = Marshal.GetLastPInvokeError();
            job.Dispose();
            throw new LaunchException($"the Job object's limits could not be set: {new Win32Exception(error).Message}");
        }

        if (sandbox is not null)
        {
            var ui = JobObjectUiLimitAll;
            if (!SetInformationJobObject(job, JobObjectBasicUiRestrictions, &ui, sizeof(uint)))
            {
                var error = Marshal.GetLastPInvokeError();
                job.Dispose();
                throw new LaunchException($"the Job object's UI restrictions could not be set: {new Win32Exception(error).Message}");
            }
        }

        return job;
    }

    private static unsafe (SafeProcessHandle Process, int Id) Create(string program, string commandLine, IReadOnlyDictionary<string, string> environment, string workingFolder, SafeFileHandle job, SafeHandle childIn, SafeHandle childOut, SafeHandle childErr, SandboxSpec? sandbox)
    {
        var count = sandbox is null ? 2u : 3u;
        nuint size = 0;
        _ = InitializeProcThreadAttributeList(0, count, 0, ref size);
        var list = Marshal.AllocHGlobal((nint)size);
        var inherited = (nint*)NativeMemory.Alloc(3, (nuint)sizeof(nint));
        var jobs = (nint*)NativeMemory.Alloc(1, (nuint)sizeof(nint));
        var capabilities = (SecurityCapabilities*)NativeMemory.AllocZeroed(1, (nuint)sizeof(SecurityCapabilities));
        var wanted = sandbox?.Capabilities ?? [];
        var granted = (SidAndAttributes*)NativeMemory.AllocZeroed((nuint)Math.Max(1, wanted.Count), (nuint)sizeof(SidAndAttributes));
        var sids = new List<nint>();
        var initialized = false;
        var refs = new bool[4];
        try
        {
            if (!InitializeProcThreadAttributeList(list, count, 0, ref size))
            {
                throw new LaunchException($"the start's attributes could not be made: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");
            }

            initialized = true;

            // The handles stay alive until the list is gone: their values are what the list holds.
            childIn.DangerousAddRef(ref refs[0]);
            childOut.DangerousAddRef(ref refs[1]);
            childErr.DangerousAddRef(ref refs[2]);
            job.DangerousAddRef(ref refs[3]);
            inherited[0] = childIn.DangerousGetHandle();
            inherited[1] = childOut.DangerousGetHandle();
            inherited[2] = childErr.DangerousGetHandle();
            jobs[0] = job.DangerousGetHandle();
            if (!UpdateProcThreadAttribute(list, 0, ProcThreadAttributeHandleList, inherited, (nuint)(3 * sizeof(nint)), 0, 0)
                || !UpdateProcThreadAttribute(list, 0, ProcThreadAttributeJobList, jobs, (nuint)sizeof(nint), 0, 0))
            {
                throw new LaunchException($"the start's attributes could not be set (a Job object for a new process needs Windows 10): {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");
            }

            // A sandbox: the process starts as the AppContainer, with the capabilities it is given and no
            // others; each is enabled for access checks (SE_GROUP_ENABLED), as Microsoft Learn's "Launch an
            // AppContainer" builds them.
            if (sandbox is not null)
            {
                capabilities->AppContainerSid = sandbox.AppContainerSid;
                for (var i = 0; i < wanted.Count; i++)
                {
                    var sid = new SecurityIdentifier(wanted[i]);
                    var binary = new byte[sid.BinaryLength];
                    sid.GetBinaryForm(binary, 0);
                    var native = (nint)NativeMemory.Alloc((nuint)binary.Length);
                    sids.Add(native);
                    Marshal.Copy(binary, 0, native, binary.Length);
                    granted[i].Sid = native;
                    granted[i].Attributes = SeGroupEnabled;
                }

                if (wanted.Count > 0)
                {
                    capabilities->Capabilities = (nint)granted;
                    capabilities->CapabilityCount = (uint)wanted.Count;
                }

                if (!UpdateProcThreadAttribute(list, 0, ProcThreadAttributeSecurityCapabilities, capabilities, (nuint)sizeof(SecurityCapabilities), 0, 0))
                {
                    throw new LaunchException($"the sandbox could not be set for the start: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");
                }
            }

            var info = new StartupInfoEx
            {
                StartupInfo = new StartupInfo
                {
                    Size = (uint)sizeof(StartupInfoEx),
                    Flags = StartfUseStdHandles,
                    StdInput = inherited[0],
                    StdOutput = inherited[1],
                    StdError = inherited[2],
                },
                AttributeList = list,
            };
            var block = EnvironmentBlock(environment);
            var line = (commandLine + '\0').ToCharArray();
            ProcessInformation created;
            fixed (char* application = program)
            fixed (char* command = line)
            fixed (char* env = block)
            fixed (char* folder = workingFolder)
            {
                if (!CreateProcess(application, command, 0, 0, 1, ExtendedStartupInfoPresent | CreateUnicodeEnvironment | CreateNoWindow, env, folder, &info, &created))
                {
                    throw new LaunchException($"{Path.GetFileName(program)} could not be started: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");
                }
            }

            _ = CloseHandle(created.Thread);
            return (new SafeProcessHandle(created.Process, ownsHandle: true), unchecked((int)created.ProcessId));
        }
        finally
        {
            if (initialized)
            {
                DeleteProcThreadAttributeList(list);
            }

            Marshal.FreeHGlobal(list);
            NativeMemory.Free(inherited);
            NativeMemory.Free(jobs);
            NativeMemory.Free(capabilities);
            foreach (var sid in sids)
            {
                NativeMemory.Free((void*)sid);
            }

            NativeMemory.Free(granted);
            if (refs[0])
            {
                childIn.DangerousRelease();
            }

            if (refs[1])
            {
                childOut.DangerousRelease();
            }

            if (refs[2])
            {
                childErr.DangerousRelease();
            }

            if (refs[3])
            {
                job.DangerousRelease();
            }
        }
    }

    /// <summary>The environment as Windows takes it: name=value strings, sorted by name without regard to case or locale, ended by two nulls.</summary>
    internal static char[] EnvironmentBlock(IReadOnlyDictionary<string, string> environment)
    {
        var text = new StringBuilder();
        foreach (var (name, value) in environment.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (name.Length == 0 || name.Contains('=', StringComparison.Ordinal) || name.Contains('\0', StringComparison.Ordinal) || value.Contains('\0', StringComparison.Ordinal))
            {
                throw new LaunchException($"'{SafeText.Line(name)}' cannot be passed as an environment variable");
            }

            text.Append(name).Append('=').Append(value).Append('\0');
        }

        if (text.Length == 0)
        {
            text.Append('\0');
        }

        text.Append('\0');
        return text.ToString().ToCharArray();
    }

    /// <summary><c>SECURITY_CAPABILITIES</c>: the AppContainer's SID, its capabilities, their count, reserved.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityCapabilities
    {
        public nint AppContainerSid;
        public nint Capabilities;
        public uint CapabilityCount;
        public uint Reserved;
    }

    /// <summary><c>SID_AND_ATTRIBUTES</c>: a SID and its attributes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes
    {
        public nint Sid;
        public uint Attributes;
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
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public nint AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint Process;
        public nint Thread;
        public uint ProcessId;
        public uint ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial SafeFileHandle CreateJobObject(nint attributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool SetInformationJobObject(SafeFileHandle job, int infoClass, void* info, uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateJobObject(SafeFileHandle job, uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InitializeProcThreadAttributeList(nint list, uint count, uint flags, ref nuint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool UpdateProcThreadAttribute(nint list, uint flags, nint attribute, void* value, nuint size, nint previous, nint returnSize);

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void DeleteProcThreadAttributeList(nint list);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool CreateProcess(char* application, char* commandLine, nint processAttributes, nint threadAttributes, int inheritHandles, uint flags, char* environment, char* folder, StartupInfoEx* startup, ProcessInformation* created);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);
}
