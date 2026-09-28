using System.Collections.Concurrent;
using System.Reflection;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;

namespace DipTick.Views;

/// <summary>
/// Which of the console's own records Terminal.Gui gets, and when.
/// </summary>
/// <remarks>
/// <para>
/// Esc held down is not passed on. Terminal.Gui 2.5.0's Windows driver reads key-up records and
/// drops them unseen (WindowsInputProcessor: "TG v2 does not support keyup events"), so the screen
/// alone cannot tell a held Esc from presses. Here a press counts once the key has come up since the
/// last one, as in the window. A held key repeats after the keyboard's delay, about a second at the
/// longest, then about 2.5 times a second at the slowest (SystemParametersInfo, SPI_GETKEYBOARDDELAY
/// and SPI_GETKEYBOARDSPEED, "may vary depending on the hardware"); an Esc down with no up since,
/// later than <see cref="RepeatWithin"/>, is a press whose up went to another window.
/// </para>
/// <para>
/// A key right behind an Esc waits until the screen has had the Esc. Terminal.Gui holds an Esc 50 ms
/// as the possible start of an escape sequence and reads a key inside that time with it, so both are
/// lost (InputProcessorImpl, v2.5.0); handing a lone Esc over was measured at 104 ms in the console host
/// and 146 ms in Windows Terminal. The wait ends at <see cref="MostHeld"/> ms.
/// </para>
/// <para>
/// Only the Esc key itself is looked at: VK_ESCAPE. A terminal's replies to Terminal.Gui's own
/// questions arrive as key records too, with no key code (VK 0, scan 0; a DA1 reply and a cell-size
/// report in Windows Terminal), and pass as they came.
/// </para>
/// <para>
/// A paste of more than one line goes to the screen whole (<see cref="Pasted"/>) while the composer
/// has the keyboard, instead of as keys. A paste arrives as keys all at once: the console host's own
/// paste, and Windows Terminal's, which sends the text as typed, its line breaks as carriage
/// returns, when the program has not asked for bracketed paste (ControlCore::PasteText,
/// release-1.24); Terminal.Gui's Windows driver does not ask. Typed keys come apart: the console is
/// read every 20 ms, so a person's keys land one or two to a pass.
/// </para>
/// </remarks>
internal sealed class KeyTap : IDisposable
{
    /// <summary>Milliseconds within which an Esc down, with no up since the last, is the keyboard repeating it.</summary>
    internal const long RepeatWithin = 1500;

    /// <summary>The longest a key behind an Esc waits for the screen to have the Esc, in milliseconds.</summary>
    internal const int MostHeld = 500;

    /// <summary>Keys read in one pass before an Enter that make the pass a paste, with no key after the Enter: a block copied with its last line break.</summary>
    internal const int PastedAfter = 3;

    /// <summary>Milliseconds a burst of keys may pause and still be one paste: a paste can reach the console in pieces.</summary>
    internal const int PasteSettles = 30;

    private readonly ManualResetEventSlim _settled = new(initialState: true);

    // Esc presses passed on that the screen has not yet had; written on both threads.
    private int _pending;

    // Written on the screen's thread, read on the input thread.
    private bool _collecting;

    // Read and written on the input thread only.
    private bool _escDown;
    private long _escAt;

    /// <summary>Raised on the input thread with a paste of more than one line, in place of its keys, while <see cref="Collecting"/>.</summary>
    public event Action<string>? Pasted;

    /// <summary>Whether a paste goes to the screen whole: set by the screen while its composer has the keyboard.</summary>
    public bool Collecting
    {
        get => Volatile.Read(ref _collecting);
        set => Volatile.Write(ref _collecting, value);
    }

    /// <summary>
    /// Whether <paramref name="record"/> goes on to Terminal.Gui, on the input thread at
    /// <paramref name="now"/> (milliseconds): false only for Esc repeating while held down.
    /// </summary>
    public bool Pass(in WindowsConsole.InputRecord record, long now)
    {
        if (record.EventType != WindowsConsole.EventType.Key || record.KeyEvent.wVirtualKeyCode != VK.ESCAPE)
        {
            return true;
        }

        if (!record.KeyEvent.bKeyDown)
        {
            _escDown = false;
            return true;
        }

        var repeat = _escDown && now - _escAt < RepeatWithin;
        _escDown = true;
        _escAt = now;
        if (repeat)
        {
            return false;
        }

        _ = Interlocked.Increment(ref _pending);
        _settled.Reset();
        return true;
    }

    /// <summary>Whether <paramref name="record"/> must not reach Terminal.Gui before an Esc ahead of it has: a key down that is not Esc.</summary>
    public static bool WaitsForEscape(in WindowsConsole.InputRecord record) =>
        record.EventType == WindowsConsole.EventType.Key && record.KeyEvent.bKeyDown && record.KeyEvent.wVirtualKeyCode != VK.ESCAPE;

    /// <summary>Whether <paramref name="record"/> is a key going down.</summary>
    public static bool IsKeyDown(in WindowsConsole.InputRecord record) =>
        record.EventType == WindowsConsole.EventType.Key && record.KeyEvent.bKeyDown;

    /// <summary>How many keys go down in <paramref name="pass"/>.</summary>
    public static int KeyDowns(IReadOnlyList<WindowsConsole.InputRecord> pass)
    {
        ArgumentNullException.ThrowIfNull(pass);
        return pass.Count(r => IsKeyDown(r));
    }

    /// <summary>
    /// The text of a paste of more than one line, when <paramref name="pass"/> (the records read in
    /// one pass over the console) holds one; null for typed keys. It is one when an Enter in it has a
    /// key after it, or <see cref="PastedAfter"/> keys or more before it. Its text is each key's
    /// character, an Enter a line break; a key with no character (an arrow, Shift) adds nothing.
    /// </summary>
    public static string? PastedText(IReadOnlyList<WindowsConsole.InputRecord> pass)
    {
        ArgumentNullException.ThrowIfNull(pass);
        var downs = pass.Where(r => IsKeyDown(r)).ToList();
        var lines = false;
        for (var i = 0; i < downs.Count && !lines; i++)
        {
            lines = downs[i].KeyEvent.wVirtualKeyCode == VK.RETURN && (i < downs.Count - 1 || i >= PastedAfter);
        }

        if (!lines)
        {
            return null;
        }

        var text = new System.Text.StringBuilder();
        foreach (var down in downs)
        {
            if (down.KeyEvent.wVirtualKeyCode == VK.RETURN)
            {
                _ = text.Append('\r');
            }
            else if (down.KeyEvent.UnicodeChar != '\0')
            {
                _ = text.Append(down.KeyEvent.UnicodeChar);
            }
        }

        // A line break may come as Enter and then a line feed of its own: one break either way.
        return text.ToString().Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    }

    /// <summary>On the input thread: hands a paste to the screen.</summary>
    public void Paste(string text) => Pasted?.Invoke(text);

    /// <summary>On the input thread: waits while an Esc passed on is not yet the screen's, at most <see cref="MostHeld"/> ms.</summary>
    public void Settle(CancellationToken token)
    {
        try
        {
            if (_settled.Wait(MostHeld, token))
            {
                return;
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // Terminal.Gui kept an Esc after all: the keys behind it go on.
        _ = Interlocked.Exchange(ref _pending, 0);
        _settled.Set();
    }

    /// <summary>On the screen's thread: the screen has had an Esc, so the keys behind it may follow.</summary>
    public void EscapeSeen()
    {
        if (Interlocked.Decrement(ref _pending) <= 0)
        {
            _ = Interlocked.Exchange(ref _pending, 0);
            _settled.Set();
        }
    }

    public void Dispose() => _settled.Dispose();
}

/// <summary>
/// Terminal.Gui's own Windows console driver, which DipTick's terminal screen runs on, with each
/// input record shown to a <see cref="KeyTap"/> before Terminal.Gui has it. It is the Windows driver
/// rather than 2.5.0's default for Windows, its ANSI driver, because the ANSI driver hears
/// Shift+Enter as Enter and the Windows driver does not (measured in the console host with the
/// console's own key records).
/// </summary>
/// <remarks>
/// Terminal.Gui 2.5.0 builds a driver from the factory its application holds, and otherwise only
/// its own three, by name. A driver added to its <see cref="DriverRegistry"/>, whose documentation
/// says it "can be used to add custom drivers", passes the name check and then fails with "Unknown
/// driver name" (ApplicationImpl.CreateDriver, v2.5.0, the same on develop; read 2026-09-27). The
/// factory is given only through constructors it keeps internal, so <see cref="Install"/> sets it
/// on the field those constructors set.
/// </remarks>
internal sealed class TappedConsole(KeyTap tap) : WindowsComponentFactory
{
    /// <summary>Where ApplicationImpl keeps the factory it builds its driver from (Terminal.Gui 2.5.0).</summary>
    internal const string FactoryField = "_componentFactory";

    /// <summary>Has <paramref name="app"/> build its driver from this one, with <paramref name="tap"/> watching; its Init then starts it.</summary>
    public static void Install(IApplication app, KeyTap tap)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(tap);
        var field = app.GetType().GetField(FactoryField, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Terminal.Gui's application no longer has '{FactoryField}' to build its driver from; TappedConsole must follow the version in use.");
        field.SetValue(app, new TappedConsole(tap));
    }

    public override IInput<WindowsConsole.InputRecord> CreateInput() =>
        new TappedInput((InputImpl<WindowsConsole.InputRecord>)base.CreateInput(), tap);

    /// <summary>
    /// The Windows driver's own input, read the way Terminal.Gui 2.5.0's InputImpl.Run reads it (its
    /// source: while there is input, read and queue it; then wait 20 ms; stop on either token), with
    /// the tap deciding which records go into the queue, and when.
    /// </summary>
    private sealed class TappedInput(InputImpl<WindowsConsole.InputRecord> inner, KeyTap tap) : IInput<WindowsConsole.InputRecord>
    {
        private ConcurrentQueue<WindowsConsole.InputRecord>? _queue;

        public CancellationTokenSource? ExternalCancellationTokenSource
        {
            get => inner.ExternalCancellationTokenSource;
            set => inner.ExternalCancellationTokenSource = value;
        }

        public void Initialize(ConcurrentQueue<WindowsConsole.InputRecord> inputQueue)
        {
            _queue = inputQueue;
            inner.Initialize(inputQueue);
        }

        public void Run(CancellationToken runCancellationToken)
        {
            if (_queue is not { } queue)
            {
                throw new InvalidOperationException("The console's input was run before it was initialized.");
            }

            using var linked = ExternalCancellationTokenSource is { } external
                ? CancellationTokenSource.CreateLinkedTokenSource(runCancellationToken, external.Token)
                : null;
            var token = linked?.Token ?? runCancellationToken;
            do
            {
                // One pass over what the console holds; a burst of keys (a paste, which can reach
                // the console in pieces) is read on until it pauses.
                var pass = new List<WindowsConsole.InputRecord>();
                Drain(pass);
                while (KeyTap.KeyDowns(pass) > 1 && !token.WaitHandle.WaitOne(KeyTap.PasteSettles) && inner.Peek())
                {
                    Drain(pass);
                }

                if (tap.Collecting && KeyTap.PastedText(pass) is { } pasted)
                {
                    tap.Paste(pasted);
                    continue;
                }

                foreach (var record in pass)
                {
                    if (KeyTap.WaitsForEscape(record))
                    {
                        tap.Settle(token);
                    }

                    queue.Enqueue(record);
                }
            }
            while (!token.WaitHandle.WaitOne(20));
        }

        /// <summary>Reads what the console holds into <paramref name="pass"/>, but for Esc repeating while held down.</summary>
        private void Drain(List<WindowsConsole.InputRecord> pass)
        {
            while (inner.Peek())
            {
                foreach (var record in inner.Read())
                {
                    if (tap.Pass(record, Environment.TickCount64))
                    {
                        pass.Add(record);
                    }
                }
            }
        }

        public void Dispose() => inner.Dispose();
    }
}
