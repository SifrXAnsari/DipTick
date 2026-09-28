using System.Runtime.InteropServices;

namespace DipTick.Views;

/// <summary>
/// Windows' double-click time: the one setting a person has for "quick succession", and so the gap
/// within which three presses of Esc count as one quit, in the window and in the terminal alike.
/// </summary>
internal static partial class DoubleClick
{
    /// <summary>
    /// The person's double-click time in milliseconds, held to 200 to 5000. GetDoubleClickTime (user32):
    /// "The maximum return value is 5000 milliseconds."
    /// </summary>
    public static long Gap() => Math.Clamp(GetDoubleClickTime(), 200u, 5000u);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint GetDoubleClickTime();
}
