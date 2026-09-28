using System.Globalization;
using DipTick.Controllers;
using DipTick.Models;
using DipTick.Models.Chat;
using DipTick.ViewModels;

namespace DipTick.Views;

/// <summary>
/// The questions in the plain console, one at a time: numbered choices, a default in brackets that
/// Enter takes, <c>b</c> to go back, and <c>q</c> or <c>exit</c> to stop, at any question: every
/// prompt has an exit. Works the same with answers piped from a script, except that a pipe never
/// gives a yes.
/// </summary>
/// <param name="output">Where questions go: stderr, so stdout stays data only.</param>
/// <param name="keyboard">A person at a keyboard, rather than answers from a pipe.</param>
internal sealed class ConsoleAsker(TextReader input, TextWriter output, bool keyboard) : IAsker
{
    private const int Page = 15;

    public bool CanConfirm => keyboard;

    public Asked<int> Choose(Question question, IReadOnlyList<Choice> choices, int? preset)
    {
        ArgumentNullException.ThrowIfNull(question);
        ArgumentNullException.ThrowIfNull(choices);
        Header(question);
        output.WriteLine(question.Text);
        var width = choices.Max(c => c.Label.Length);
        for (var i = 0; i < choices.Count; i++)
        {
            output.WriteLine($"  {(i + 1).ToString(CultureInfo.InvariantCulture),2}  {choices[i].Label.PadRight(width)}  {choices[i].Detail}".TrimEnd());
        }

        while (true)
        {
            var hint = preset is { } p ? $"[{(p + 1).ToString(CultureInfo.InvariantCulture)}]" : $"1-{choices.Count.ToString(CultureInfo.InvariantCulture)}";
            var line = Prompt($"Number {hint} (b back, q or exit stops): ");
            if (line is null || Stops(line))
            {
                return Asked<int>.Stop;
            }

            if (line is "b" or "B")
            {
                return Asked<int>.Back;
            }

            if (line.Length == 0 && preset is { } chosen)
            {
                return Asked<int>.Of(chosen);
            }

            if (int.TryParse(line, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= 1 && n <= choices.Count)
            {
                return Asked<int>.Of(n - 1);
            }

            output.WriteLine($"  Type a number from 1 to {choices.Count.ToString(CultureInfo.InvariantCulture)}.");
        }
    }

    public Asked<string> Text(Question question, string preset, Func<string, string?> check)
    {
        ArgumentNullException.ThrowIfNull(question);
        ArgumentNullException.ThrowIfNull(check);
        Header(question);
        while (true)
        {
            var hint = preset.Length > 0 ? $" [{SafeText.Line(preset)}]" : " (Enter alone goes back; exit stops)";
            var line = Prompt($"{question.Text}{hint}: ");
            if (line is null || ChatViewModel.IsExitWord(line))
            {
                return Asked<string>.Stop;
            }

            var value = line.Trim();
            if (value.Length == 0)
            {
                if (preset.Length == 0)
                {
                    return Asked<string>.Back;
                }

                value = preset;
            }

            if (check(value) is { } why)
            {
                output.WriteLine($"  That will not do: {why}.");
                continue;
            }

            return Asked<string>.Of(value);
        }
    }

    public Asked<string> Secret(Question question, Func<string, string?> check)
    {
        ArgumentNullException.ThrowIfNull(question);
        ArgumentNullException.ThrowIfNull(check);
        Header(question);
        while (true)
        {
            string? value;
            if (keyboard)
            {
                // Esc, or Enter on nothing, goes back; exit stops.
                value = ConsoleInput.ReadSecret($"{question.Text} (not shown; Esc goes back, exit stops): ", output);
                if (value is null)
                {
                    return Asked<string>.Back;
                }

                if (ChatViewModel.IsExitWord(value))
                {
                    return Asked<string>.Stop;
                }
            }
            else
            {
                output.Write($"{question.Text}: ");
                value = input.ReadLine();
                output.WriteLine("(read from the pipe; not shown)");
                if (value is null || ChatViewModel.IsExitWord(value))
                {
                    return Asked<string>.Stop;
                }
            }

            if (value.Length == 0)
            {
                return Asked<string>.Back;
            }

            if (check(value) is { } why)
            {
                output.WriteLine($"  That will not do: {why}.");
                if (!keyboard)
                {
                    return Asked<string>.Stop;
                }

                continue;
            }

            return Asked<string>.Of(value);
        }
    }

    public Asked<bool> YesNo(Question question, bool preset)
    {
        ArgumentNullException.ThrowIfNull(question);
        Header(question);
        while (true)
        {
            var line = Prompt($"{question.Text} {(preset ? "[Y/n]" : "[y/N]")} (b back, q or exit stops): ");
            switch (line?.ToUpperInvariant())
            {
                case null or "Q" or "EXIT" or "QUIT":
                    return Asked<bool>.Stop;
                case "B":
                    return Asked<bool>.Back;
                case "":
                    return Asked<bool>.Of(preset);
                case "Y" or "YES":
                    return Asked<bool>.Of(true);
                case "N" or "NO":
                    return Asked<bool>.Of(false);
                default:
                    output.WriteLine("  Type y or n.");
                    break;
            }
        }
    }

    public Asked<string> PickModel(Question question, IReadOnlyList<ModelInfo> models, string preset)
    {
        ArgumentNullException.ThrowIfNull(question);
        ArgumentNullException.ThrowIfNull(models);
        Header(question);
        output.WriteLine(question.Text);
        var shown = models;
        while (true)
        {
            var page = shown.Take(Page).ToList();
            var width = page.Count == 0 ? 0 : Math.Min(48, page.Max(m => m.Id.Length));
            for (var i = 0; i < page.Count; i++)
            {
                var m = page[i];
                var when = m.Created is { } created ? created.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : string.Empty;
                output.WriteLine(SafeText.Line($"  {(i + 1).ToString(CultureInfo.InvariantCulture),2}  {m.Id.PadRight(width)}  {when,-10}  {m.Name}").TrimEnd());
            }

            if (shown.Count > page.Count)
            {
                output.WriteLine($"  and {(shown.Count - page.Count).ToString(CultureInfo.InvariantCulture)} more; type part of a name to narrow the list.");
            }

            var hint = preset.Length > 0 ? $" [{SafeText.Line(preset)}]" : string.Empty;
            var line = Prompt($"Number, part of a name, or a name not listed{hint} (b back, q or exit stops): ");
            if (line is null || Stops(line))
            {
                return Asked<string>.Stop;
            }

            var text = line.Trim();
            if (text is "b" or "B")
            {
                return Asked<string>.Back;
            }

            if (text.Length == 0)
            {
                if (preset.Length > 0 && ModelLister.UsableId(preset))
                {
                    return Asked<string>.Of(preset);
                }

                continue;
            }

            if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= 1 && n <= page.Count)
            {
                return Asked<string>.Of(page[n - 1].Id);
            }

            if (models.FirstOrDefault(m => string.Equals(m.Id, text, StringComparison.OrdinalIgnoreCase)) is { } exact)
            {
                return Asked<string>.Of(exact.Id);
            }

            var matches = models.Where(m => m.Id.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count > 0)
            {
                output.WriteLine($"  {matches.Count.ToString(CultureInfo.InvariantCulture)} with '{SafeText.Line(text)}' in the name:");
                shown = matches;
                continue;
            }

            if (!ModelLister.UsableId(text))
            {
                output.WriteLine("  That will not do: a model's name has no spaces and only plain characters.");
                continue;
            }

            var use = YesNo(new Question(question.Title, $"'{text}' is not in the list. Use it anyway?"), false);
            if (use.Reply != Reply.Answered)
            {
                return new Asked<string>(use.Reply, string.Empty);
            }

            if (use.Value)
            {
                return Asked<string>.Of(text);
            }

            shown = models;
        }
    }

    /// <summary>
    /// A long list, a page at a time, each group headed as it starts: a number picks from the page,
    /// words narrow the list, Enter takes the one marked [preset]. An item that cannot be picked is
    /// shown as not yet, and picking it says why.
    /// </summary>
    public Asked<string> Pick(Question question, IReadOnlyList<PickItem> items, string preset)
    {
        ArgumentNullException.ThrowIfNull(question);
        ArgumentNullException.ThrowIfNull(items);
        Header(question);
        output.WriteLine(question.Text);
        IReadOnlyList<PickItem> shown = items;
        var first = 0;
        while (true)
        {
            var page = shown.Skip(first).Take(Page).ToList();
            var width = page.Count == 0 ? 0 : Math.Min(34, page.Max(i => i.Label.Length));
            string? group = null;
            for (var n = 0; n < page.Count; n++)
            {
                var item = page[n];
                if (item.Group.Length > 0 && item.Group != group)
                {
                    group = item.Group;
                    output.WriteLine($"  {group}");
                }

                var label = item.Label.Length <= width ? item.Label.PadRight(width) : item.Label[..(width - 1)] + "…";
                var detail = item.Unavailable.Length > 0 ? "not yet" : item.Detail;
                output.WriteLine(SafeText.Line($"   {(n + 1).ToString(CultureInfo.InvariantCulture),2}  {label}  {detail}").TrimEnd());
            }

            if (first + page.Count < shown.Count)
            {
                output.WriteLine($"  {(shown.Count - first - page.Count).ToString(CultureInfo.InvariantCulture)} more: Enter shows the next page, or type part of a name.");
            }

            var hint = preset.Length > 0 && items.Any(i => i.Id == preset) ? $" [{SafeText.Line(items.First(i => i.Id == preset).Label)}]" : string.Empty;
            var line = Prompt($"Number, or part of a name{hint} (b back, q or exit stops): ");
            if (line is null || Stops(line))
            {
                return Asked<string>.Stop;
            }

            var text = line.Trim();
            if (text is "b" or "B")
            {
                return Asked<string>.Back;
            }

            if (text.Length == 0)
            {
                if (preset.Length > 0 && items.FirstOrDefault(i => i.Id == preset) is { Unavailable.Length: 0 })
                {
                    return Asked<string>.Of(preset);
                }

                first = first + Page < shown.Count ? first + Page : 0;
                continue;
            }

            if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number >= 1 && number <= page.Count)
            {
                var chosen = page[number - 1];
                if (chosen.Unavailable.Length > 0)
                {
                    output.WriteLine(SafeText.Line($"  Not yet: {chosen.Unavailable}"));
                    continue;
                }

                return Asked<string>.Of(chosen.Id);
            }

            var exact = items.FirstOrDefault(i => string.Equals(i.Id, text, StringComparison.OrdinalIgnoreCase));
            if (exact is { Unavailable.Length: 0 })
            {
                return Asked<string>.Of(exact.Id);
            }

            var matches = PickItem.Narrow(items, text);
            if (matches.Count == 0)
            {
                output.WriteLine($"  Nothing matches '{SafeText.Line(text)}'.");
                continue;
            }

            output.WriteLine($"  {matches.Count.ToString(CultureInfo.InvariantCulture)} with '{SafeText.Line(text)}':");
            shown = matches;
            first = 0;
        }
    }

    public void Tell(string title, string text)
    {
        output.WriteLine();
        output.WriteLine(title);
        output.WriteLine(text);
    }

    public T Busy<T>(string what, Func<Task<T>> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        output.Write($"{what} ");
        try
        {
            var result = work().GetAwaiter().GetResult();
            output.WriteLine("done.");
            return result;
        }
        catch
        {
            output.WriteLine();
            throw;
        }
    }

    /// <summary>q, exit or quit: the words that stop at a question.</summary>
    private static bool Stops(string line) => line.Trim() is "q" or "Q" || ChatViewModel.IsExitWord(line);

    private void Header(Question question)
    {
        output.WriteLine();
        output.WriteLine(question.Title);
        foreach (var line in Wrap(question.Note, Width() - 4))
        {
            output.WriteLine($"  {line}");
        }
    }

    private string? Prompt(string text)
    {
        output.Write(text);
        output.Flush();
        var line = input.ReadLine();
        if (!keyboard && line is not null)
        {
            // Piped answers are echoed, so a script's log reads like the conversation it was.
            output.WriteLine(SafeText.Line(line));
        }

        return line?.Trim();
    }

    private static int Width()
    {
        try
        {
            return Console.IsErrorRedirected ? 100 : Math.Max(40, Console.WindowWidth);
        }
        catch (IOException)
        {
            return 100;
        }
    }

    /// <summary>Words wrapped to <paramref name="width"/>; a line break in the text starts a new line.</summary>
    internal static IEnumerable<string> Wrap(string text, int width)
    {
        foreach (var paragraph in text.Split('\n'))
        {
            var rest = paragraph.TrimEnd();
            while (rest.Length > width)
            {
                var cut = rest.LastIndexOf(' ', width);
                cut = cut <= 0 ? width : cut;
                yield return rest[..cut];
                rest = rest[cut..].TrimStart();
            }

            if (rest.Length > 0)
            {
                yield return rest;
            }
        }
    }
}
