namespace DipTick.ViewModels;

/// <summary>One slash command as the command list shows it.</summary>
internal sealed record CommandEntry(string Name, string Help, bool TakesArgument);

/// <summary>
/// The input line's state and rules, with no screen code: the command list that opens on <c>/</c>
/// and its mark, Tab completion, the draft of earlier lines (Ctrl+J), and a paste with line breaks,
/// which goes into the draft whole and is never sent line by line.
/// </summary>
internal sealed class ComposerViewModel(Func<IReadOnlyList<CommandEntry>> commands)
{
    /// <summary>The most rows the command list shows.</summary>
    public const int MostRows = 8;

    private readonly List<string> _draft = [];

    /// <summary>The lines typed before the one being typed, oldest first.</summary>
    public IReadOnlyList<string> Draft => _draft;

    /// <summary>The commands that match what is typed, while the list is open; empty when it is closed.</summary>
    public IReadOnlyList<CommandEntry> Matches { get; private set; } = [];

    /// <summary>Which of <see cref="Matches"/> the mark is on.</summary>
    public int Mark { get; private set; }

    public bool ListOpen => Matches.Count > 0;

    /// <summary>The marked command's name, while the list is open.</summary>
    public string? Marked => ListOpen ? Matches[Mark].Name : null;

    /// <summary>
    /// Called after every change to the line: a <c>/</c> first, no space yet and nothing in the draft
    /// opens the list, narrowed to what is typed; anything else closes it. The mark stays on the same
    /// command while it still matches.
    /// </summary>
    public void Typed(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var marked = Marked;
        var open = _draft.Count == 0 && line.StartsWith('/') && !line.Any(char.IsWhiteSpace);
        Matches = open ? [.. commands().Where(c => c.Name.StartsWith(line, StringComparison.OrdinalIgnoreCase))] : [];
        var at = marked is null ? -1 : Matches.ToList().FindIndex(m => m.Name == marked);
        Mark = at < 0 ? 0 : at;
    }

    /// <summary>Moves the mark, wrapping at either end.</summary>
    public void Move(int step)
    {
        if (Matches.Count > 0)
        {
            Mark = (((Mark + step) % Matches.Count) + Matches.Count) % Matches.Count;
        }
    }

    /// <summary>The line Tab makes: the marked command, with a space when it takes an argument; null with the list closed.</summary>
    public string? Complete()
    {
        if (!ListOpen)
        {
            return null;
        }

        var entry = Matches[Mark];
        return entry.TakesArgument ? entry.Name + " " : entry.Name;
    }

    public void CloseList() => Matches = [];

    /// <summary>Ctrl+J: the line joins the draft, and a new line starts.</summary>
    public void NewLine(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        _draft.Add(line);
        Matches = [];
    }

    /// <summary>Backspace on an empty line: the draft's last line comes back to be edited, or null when there is none.</summary>
    public string? TakeBack()
    {
        if (_draft.Count == 0)
        {
            return null;
        }

        var last = _draft[^1];
        _draft.RemoveAt(_draft.Count - 1);
        return last;
    }

    /// <summary>What Enter sends: the draft and the line, one message.</summary>
    public string Compose(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        return _draft.Count == 0 ? line : string.Join('\n', _draft.Append(line));
    }

    /// <summary>After sending: an empty draft and a closed list.</summary>
    public void Clear()
    {
        _draft.Clear();
        Matches = [];
    }

    /// <summary>
    /// A paste with line breaks: what was typed and the paste's first line become one draft line,
    /// every whole line after it joins the draft, and the paste's last line is returned to stay in
    /// the input, where the person goes on typing. Nothing is sent.
    /// </summary>
    public string Paste(string line, string pasted)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(pasted);
        var lines = pasted.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        _draft.Add(line + lines[0]);
        for (var i = 1; i < lines.Length - 1; i++)
        {
            _draft.Add(lines[i]);
        }

        Matches = [];
        return lines.Length > 1 ? lines[^1] : string.Empty;
    }

    /// <summary>Whether a paste carries line breaks, and so goes to the draft instead of the line.</summary>
    public static bool IsMultiLine(string pasted) => pasted.Contains('\n', StringComparison.Ordinal) || pasted.Contains('\r', StringComparison.Ordinal);

}
