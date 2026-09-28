using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DipTick.Models.Harness;

/// <summary>What the file system says about one file that a path string cannot.</summary>
internal static partial class FileFacts
{
    /// <summary>
    /// How many names the file has, or null when it cannot be opened to ask. More than one means
    /// a hard link: the same file reachable under another name, possibly outside every root, so a
    /// write "inside" a root could land outside it.
    /// </summary>
    /// <remarks>
    /// GetFileInformationByHandle and BY_HANDLE_FILE_INFORMATION (learn.microsoft.com, fileapi.h):
    /// the count is <c>nNumberOfLinks</c>; FAT always reports 1, NTFS can report more.
    /// </remarks>
    public static uint? LinkCount(string path)
    {
        try
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return GetFileInformationByHandle(handle, out var info) ? info.NumberOfLinks : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

    // Field for field as fileapi.h declares it. FILETIME is two DWORDs, so it is written as two
    // uints: a C# long would be 8-aligned and shift every field after the first.
    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
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
