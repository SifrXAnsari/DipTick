namespace DipTick.ViewModels;

/// <summary>What one press of Esc comes to, counted toward the three that quit.</summary>
internal enum EscapeTurn
{
    /// <summary>The first of a run: Esc does what it always does.</summary>
    Once,

    /// <summary>The second: the screen says the third will quit.</summary>
    Twice,

    /// <summary>The third: quit, and stop everything.</summary>
    Quit,
}

/// <summary>
/// Esc three times quits DipTick and stops everything, the same in the window and in the terminal:
/// each press within <c>gap</c> of the one before counts toward three, the gap being the person's
/// own double-click time. The screen tells a press from the key repeating and never passes a repeat
/// here; this counts presses and nothing else.
/// </summary>
internal sealed class EscapeCounter
{
    private long _last;
    private int _count;

    /// <summary>A press at <paramref name="now"/> (milliseconds), with presses <paramref name="gap"/> or less apart counting on.</summary>
    public EscapeTurn Press(long now, long gap)
    {
        _count = _count > 0 && now - _last <= gap ? _count + 1 : 1;
        _last = now;
        if (_count >= 3)
        {
            _count = 0;
            return EscapeTurn.Quit;
        }

        return _count == 2 ? EscapeTurn.Twice : EscapeTurn.Once;
    }

    /// <summary>The run is over: the time ran out, and the next press is a first again.</summary>
    public void Reset() => _count = 0;
}
