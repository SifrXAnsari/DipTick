using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DipTick.Models.Files;

/// <summary>Why a file or folder could not be used safely, in words for the model and the chat.</summary>
internal sealed class SafeFileException : Exception
{
    public SafeFileException()
    {
    }

    public SafeFileException(string message)
        : base(message)
    {
    }

    public SafeFileException(string message, Exception inner)
        : base(message, inner)
    {
    }

    /// <summary>Windows' own error number, where Windows refused; 0 otherwise.</summary>
    public int Error { get; init; }
}

/// <summary>One entry of a folder, as <see cref="SafeFiles.List"/> gives it.</summary>
internal sealed record FolderEntry(string Name, bool Folder, bool Link, long Size, DateTime Modified);

/// <summary>
/// Files an agent reads and writes, checked on the handle rather than the path. The gate judged the
/// path as it was written; between that and the use, a folder could be
/// swapped for a junction or a file hard-linked. So a file is opened without following a reparse point
/// (<c>FILE_FLAG_OPEN_REPARSE_POINT</c>) and without letting anyone delete or rename it
/// (no <c>FILE_SHARE_DELETE</c>), and then, on the handle, it must not be a reparse point, must have
/// one name, and its final path (<c>GetFinalPathNameByHandleW</c>: the path "when a path is fully
/// resolved") must be inside the root's own final path. Only then is a byte read or written.
/// </summary>
/// <remarks>
/// A write never truncates by path. An existing file is opened as it is, checked, then cut and
/// written. A new file is made with <c>CREATE_NEW</c> in a parent folder already open and checked,
/// then checked itself, and deleted through its own handle if it landed anywhere else, before a byte
/// is written. A folder is listed through its final path while its handle is held, and its final path
/// is read again after. The root itself is opened as the person named it, links followed: it is the
/// person's choice of where agents may work.
/// </remarks>
internal static partial class SafeFiles
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint DeleteAccess = 0x00010000;
    private const uint ShareRead = 0x1;
    private const uint ShareWrite = 0x2;
    private const uint CreateNew = 1;
    private const uint OpenExisting = 3;
    private const uint OpenReparsePoint = 0x00200000;
    private const uint BackupSemantics = 0x02000000;
    private const uint AttributeDirectory = 0x10;
    private const uint AttributeReparsePoint = 0x400;
    private const int FileDispositionInfo = 4;
    private const int ErrorFileExists = 80;
    private const int ErrorAlreadyExists = 183;

    /// <summary>The most a folder listing gives.</summary>
    public const int MostEntries = 1000;

    /// <summary>A root's own final path: the folder as the person named it, its links followed.</summary>
    /// <exception cref="SafeFileException">It cannot be opened.</exception>
    public static string RootPath(string root)
    {
        ArgumentNullException.ThrowIfNull(root);
        using var handle = Open(Path.GetFullPath(root), GenericRead, ShareRead | ShareWrite, OpenExisting, BackupSemantics);
        return Final(handle);
    }

    /// <summary>A file's bytes, at most <paramref name="most"/> of them, when it passes the checks.</summary>
    /// <exception cref="SafeFileException">It is not a file safely inside the root, cannot be read, or is larger than <paramref name="most"/> and <paramref name="whole"/> is asked.</exception>
    public static (byte[] Bytes, long Size) Read(string path, string root, int most, bool whole = false)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(root);
        var inside = RootPath(root);
        // The handle is let go with the stream; disposing it too is harmless, and says who owns it.
        using var handle = Open(Path.GetFullPath(path), GenericRead, ShareRead, OpenExisting, OpenReparsePoint);
        using var stream = new FileStream(handle, FileAccess.Read);
        Check(handle, inside, folder: false);
        var size = stream.Length;
        if (whole && size > most)
        {
            throw new SafeFileException($"it is {size / 1024} KiB, more than the {most / 1024} KiB read at once");
        }

        var take = (int)Math.Min(size, most);
        var bytes = new byte[take];
        stream.ReadExactly(bytes);
        return (bytes, size);
    }

    /// <summary>
    /// Writes a file: a new one made in a checked parent folder, or with <paramref name="overwrite"/> an
    /// existing one replaced after its checks; the folders under the root that are missing are made on
    /// the way, each checked as it is made.
    /// </summary>
    /// <exception cref="SafeFileException">It is not safely inside the root, exists without overwrite, or cannot be written.</exception>
    public static void Write(string path, string root, ReadOnlySpan<byte> bytes, bool overwrite)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(root);

        // The root is where the person said files may be written: made when it is missing (the chats'
        // own folder, the first time), as they named it.
        try
        {
            Directory.CreateDirectory(Path.GetFullPath(root));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SafeFileException($"the folder {root} could not be made: {SafeText.Line(ex.Message)}", ex);
        }

        var inside = RootPath(root);
        var full = Path.GetFullPath(path);
        var parentPath = Path.GetDirectoryName(full) ?? throw new SafeFileException("the path has no folder");
        MakeFolders(parentPath, Path.GetFullPath(root), inside);

        // The parent held open, with nobody allowed to rename it, while the file is made in it.
        using var parent = Open(parentPath, GenericRead, ShareRead | ShareWrite, OpenExisting, BackupSemantics | OpenReparsePoint);
        Check(parent, inside, folder: true);
        var parentFinal = Final(parent);

        var (made, created) = OpenForWrite(full, overwrite);
        using var file = made;
        using var stream = new FileStream(file, FileAccess.Write);
        try
        {
            Check(file, inside, folder: false);
            var final = Final(file);
            if (!string.Equals(Path.GetDirectoryName(final), parentFinal, StringComparison.OrdinalIgnoreCase))
            {
                throw new SafeFileException("the file landed outside the folder it was made in");
            }
        }
        catch (SafeFileException) when (created)
        {
            // Made somewhere it must not be: gone again through its own handle, before a byte is written.
            byte yes = 1;
            _ = SetFileInformationByHandle(file, FileDispositionInfo, ref yes, 1);
            throw;
        }

        stream.SetLength(0);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    /// <summary>
    /// A file opened to be written: made new (<c>CREATE_NEW</c>, so nothing already there is followed or
    /// cut), or with <paramref name="overwrite"/> an existing one opened as it is; whether it was made.
    /// </summary>
    private static (SafeFileHandle File, bool Created) OpenForWrite(string full, bool overwrite)
    {
        try
        {
            return (Open(full, GenericWrite | DeleteAccess, ShareRead, CreateNew, OpenReparsePoint), true);
        }
        catch (SafeFileException ex) when (ex.Error is ErrorFileExists or ErrorAlreadyExists)
        {
            if (!overwrite)
            {
                throw new SafeFileException("a file of that name is already there; ask to overwrite it, or choose another name", ex);
            }

            return (Open(full, GenericWrite, ShareRead, OpenExisting, OpenReparsePoint), false);
        }
    }

    /// <summary>A folder's entries, at most <see cref="MostEntries"/>, folders first, each by name.</summary>
    /// <exception cref="SafeFileException">It is not a folder safely inside the root, or cannot be listed.</exception>
    public static IReadOnlyList<FolderEntry> List(string path, string root)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(root);
        var inside = RootPath(root);
        using var folder = Open(Path.GetFullPath(path), GenericRead, ShareRead | ShareWrite, OpenExisting, BackupSemantics | OpenReparsePoint);
        Check(folder, inside, folder: true);
        var final = Final(folder);
        var entries = new List<FolderEntry>();
        try
        {
            foreach (var info in new DirectoryInfo(Long(final)).EnumerateFileSystemInfos())
            {
                if (entries.Count >= MostEntries)
                {
                    break;
                }

                var isFolder = (info.Attributes & FileAttributes.Directory) != 0;
                var link = (info.Attributes & FileAttributes.ReparsePoint) != 0;
                entries.Add(new FolderEntry(info.Name, isFolder, link, isFolder ? 0 : ((FileInfo)info).Length, info.LastWriteTime));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SafeFileException($"the folder could not be listed: {SafeText.Line(ex.Message)}", ex);
        }

        // Read again: the folder the names came from is still the one that was checked.
        if (!string.Equals(Final(folder), final, StringComparison.OrdinalIgnoreCase))
        {
            throw new SafeFileException("the folder moved while it was listed");
        }

        return [.. entries.OrderByDescending(e => e.Folder).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Whether a path is the folder or inside it, both as final paths; a separator bounds the folder.</summary>
    internal static bool Within(string path, string folder)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(folder);
        var bare = Path.TrimEndingDirectorySeparator(folder);
        return path.Equals(bare, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(bare + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The folders from the root down to <paramref name="folder"/> that are missing, made one at a time.
    /// The deepest folder already there is checked first, and each folder is held open while the next is
    /// made in it (no <c>FILE_SHARE_DELETE</c>, so it cannot be renamed or swapped for a link meanwhile,
    /// nor any folder above it), so nothing is ever made through a link; each new folder is opened
    /// without following a link and checked inside the root as soon as it is made.
    /// </summary>
    private static void MakeFolders(string folder, string rootAsNamed, string inside)
    {
        if (Directory.Exists(folder))
        {
            return;
        }

        var root = Path.TrimEndingDirectorySeparator(rootAsNamed);
        var missing = new Stack<string>();
        var at = folder;
        for (; at.Length > 0 && !Directory.Exists(at); at = Path.GetDirectoryName(at) ?? string.Empty)
        {
            if (!Within(at, root))
            {
                throw new SafeFileException("the folder is outside every folder agents may use");
            }

            missing.Push(at);
        }

        if (!Within(at, root))
        {
            throw new SafeFileException("the folder is outside every folder agents may use");
        }

        // The root is opened as the person named it, links followed (RootPath); below it, no link is.
        var isRoot = string.Equals(Path.TrimEndingDirectorySeparator(at), root, StringComparison.OrdinalIgnoreCase);
        var held = Open(at, GenericRead, ShareRead | ShareWrite, OpenExisting, isRoot ? BackupSemantics : BackupSemantics | OpenReparsePoint);
        try
        {
            Check(held, inside, folder: true);
            while (missing.Count > 0)
            {
                var next = missing.Pop();
                try
                {
                    Directory.CreateDirectory(next);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new SafeFileException($"a folder could not be made: {SafeText.Line(ex.Message)}", ex);
                }

                var made = Open(next, GenericRead, ShareRead | ShareWrite, OpenExisting, BackupSemantics | OpenReparsePoint);
                held.Dispose();
                held = made;
                Check(held, inside, folder: true);
            }
        }
        finally
        {
            held.Dispose();
        }
    }

    /// <summary>The checks on an open handle: not a reparse point, the kind asked for, one name for a file, and inside the root.</summary>
    private static void Check(SafeFileHandle handle, string inside, bool folder)
    {
        if (!GetFileInformationByHandle(handle, out var info))
        {
            throw new SafeFileException($"it could not be checked: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");
        }

        if ((info.FileAttributes & AttributeReparsePoint) != 0)
        {
            throw new SafeFileException("it is a link (a junction or a symbolic link), which agents never go through");
        }

        var isFolder = (info.FileAttributes & AttributeDirectory) != 0;
        if (isFolder != folder)
        {
            throw new SafeFileException(folder ? "it is a file, not a folder" : "it is a folder, not a file");
        }

        if (!folder && info.NumberOfLinks > 1)
        {
            throw new SafeFileException("the file has another name (a hard link), so it could be a file outside every folder agents may use");
        }

        if (!Within(Final(handle), inside))
        {
            throw new SafeFileException("it leads outside every folder agents may use");
        }
    }

    /// <summary>A handle's final path, normalized with its drive letter, without the <c>\\?\</c> in front.</summary>
    private static unsafe string Final(SafeFileHandle handle)
    {
        var buffer = new char[1024];
        while (true)
        {
            uint length;
            fixed (char* at = buffer)
            {
                length = GetFinalPathNameByHandle(handle, at, (uint)buffer.Length, 0);
            }

            if (length == 0)
            {
                throw new SafeFileException($"its final path could not be read: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");
            }

            if (length < buffer.Length)
            {
                return Short(new string(buffer, 0, (int)length));
            }

            if (length > 32768)
            {
                throw new SafeFileException("its final path is too long");
            }

            buffer = new char[length];
        }
    }

    /// <summary><c>\\?\C:\x</c> as <c>C:\x</c>, and <c>\\?\UNC\server\share</c> as <c>\\server\share</c>.</summary>
    internal static string Short(string final)
    {
        ArgumentNullException.ThrowIfNull(final);
        return final.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) ? @"\\" + final[8..]
            : final.StartsWith(@"\\?\", StringComparison.Ordinal) ? final[4..]
            : final;
    }

    /// <summary>A path in the long form Windows reads past 260 characters.</summary>
    private static string Long(string path) =>
        path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;

    private static SafeFileHandle Open(string path, uint access, uint share, uint disposition, uint flags)
    {
        var handle = CreateFile(Long(path), access, share, 0, disposition, flags, 0);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new SafeFileException(error switch
            {
                2 => "there is no such file",
                3 => "there is no such folder",
                5 => "Windows refused access to it",
                32 => "another program is using it",
                ErrorFileExists or ErrorAlreadyExists => "a file of that name is already there",
                _ => new Win32Exception(error).Message,
            })
            { Error = error };
        }

        return handle;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static unsafe partial uint GetFinalPathNameByHandle(SafeFileHandle file, char* path, uint size, uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(SafeFileHandle file, out HandleInformation information);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, ref byte information, uint size);

    // Field for field as fileapi.h declares BY_HANDLE_FILE_INFORMATION; FILETIME as two DWORDs.
    [StructLayout(LayoutKind.Sequential)]
    private struct HandleInformation
    {
        public uint FileAttributes;
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}
