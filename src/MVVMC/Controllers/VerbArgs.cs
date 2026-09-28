namespace DipTick.Controllers;

/// <summary>
/// A verb's own words, split where the person typed a lone <c>--</c> (RunFlags notes where it
/// stood). Before it, a word starting with <c>--</c> is one of the verb's options, and one the verb
/// does not know is refused. From it on, every word is text, whatever it looks like: so
/// <c>chat say -- --session x</c> says "--session x" and names no session.
/// </summary>
internal sealed class VerbArgs
{
    private readonly List<(string Word, bool Literal)> _words = [];

    /// <param name="start">The first word that belongs to the verb (after its name and subcommand).</param>
    public VerbArgs(IReadOnlyList<string> args, int start, int? literalFrom)
    {
        ArgumentNullException.ThrowIfNull(args);
        for (var i = start; i < args.Count; i++)
        {
            _words.Add((args[i], literalFrom is { } from && i >= from));
        }
    }

    /// <summary>The first thing wrong with the words (a value missing, an option not known), or null.</summary>
    public string? Problem { get; private set; }

    /// <summary>True when the only word left is a lone <c>-</c> before any <c>--</c>: read standard input.</summary>
    public bool Dash => _words is [("-", false)];

    /// <summary>Takes <c>--name value</c> or <c>--name=value</c> from before the boundary; the last one given wins.</summary>
    public string? Option(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        string? value = null;
        for (var i = 0; i < _words.Count && !_words[i].Literal; i++)
        {
            var word = _words[i].Word;
            if (word == name)
            {
                if (i + 1 < _words.Count && !_words[i + 1].Literal)
                {
                    value = _words[i + 1].Word;
                    _words.RemoveRange(i, 2);
                }
                else
                {
                    Problem ??= $"{name} needs a value.";
                    _words.RemoveAt(i);
                }

                i--;
            }
            else if (word.StartsWith(name + "=", StringComparison.Ordinal))
            {
                value = word[(name.Length + 1)..];
                _words.RemoveAt(i);
                i--;
            }
        }

        return value;
    }

    /// <summary>Takes every <c>--name value</c> and <c>--name=value</c> from before the boundary, in order: an option given as often as needed (<c>--attach</c>).</summary>
    public IReadOnlyList<string> All(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var values = new List<string>();
        for (var i = 0; i < _words.Count && !_words[i].Literal; i++)
        {
            var word = _words[i].Word;
            if (word == name)
            {
                if (i + 1 < _words.Count && !_words[i + 1].Literal)
                {
                    values.Add(_words[i + 1].Word);
                    _words.RemoveRange(i, 2);
                }
                else
                {
                    Problem ??= $"{name} needs a value.";
                    _words.RemoveAt(i);
                }

                i--;
            }
            else if (word.StartsWith(name + "=", StringComparison.Ordinal))
            {
                values.Add(word[(name.Length + 1)..]);
                _words.RemoveAt(i);
                i--;
            }
        }

        return values;
    }

    /// <summary>
    /// Takes <c>--name a b</c>, an option with <paramref name="count"/> values, from before the boundary;
    /// null when it is not given, and a problem when its values are not all there.
    /// </summary>
    public IReadOnlyList<string>? Options(string name, int count)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        List<string>? values = null;
        for (var i = 0; i < _words.Count && !_words[i].Literal; i++)
        {
            if (_words[i].Word != name)
            {
                continue;
            }

            var available = 0;
            while (available < count && i + 1 + available < _words.Count && !_words[i + 1 + available].Literal)
            {
                available++;
            }

            if (available < count)
            {
                Problem ??= $"{name} needs {count} values.";
                _words.RemoveRange(i, 1 + available);
            }
            else
            {
                values = [.. _words.Skip(i + 1).Take(count).Select(w => w.Word)];
                _words.RemoveRange(i, 1 + count);
            }

            i--;
        }

        return values;
    }

    /// <summary>Takes a switch (<c>--name</c>, no value) from before the boundary.</summary>
    public bool Switch(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var found = false;
        for (var i = 0; i < _words.Count && !_words[i].Literal; i++)
        {
            if (_words[i].Word == name)
            {
                found = true;
                _words.RemoveAt(i);
                i--;
            }
        }

        return found;
    }

    /// <summary>The words left, as text. An option the verb did not take, before the boundary, is a problem.</summary>
    public List<string> Words()
    {
        foreach (var (word, literal) in _words)
        {
            if (!literal && word.Length > 2 && word.StartsWith("--", StringComparison.Ordinal))
            {
                Problem ??= $"unknown option {word}; words that start with -- go after a lone --.";
            }
        }

        return [.. _words.Select(w => w.Word)];
    }
}
