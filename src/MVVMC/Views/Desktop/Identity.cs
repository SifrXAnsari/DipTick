using System.Runtime.CompilerServices;

namespace DipTick.Views.Desktop;

/// <summary>
/// A number of an object's own for as long as it lives: what a part of a window was drawn from, told
/// apart from what replaced it. Unlike a hash code, no two live objects share one.
/// </summary>
internal static class Identity
{
    private static readonly ConditionalWeakTable<object, StrongBox<long>> Numbers = new();
    private static long _last;

    public static long Of(object item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return Numbers.GetValue(item, _ => new StrongBox<long>(Interlocked.Increment(ref _last))).Value;
    }
}
