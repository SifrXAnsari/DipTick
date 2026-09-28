using System.Text;

namespace DipTick.Controllers;

/// <summary>Reading from the person at the console, the two ways every verb needs.</summary>
internal static class ConsoleInput
{
    /// <summary>
    /// One line from stdin. From a keyboard, nothing is echoed; piped, the line is read as is,
    /// so a script can pass a secret without it ever landing in an argument list.
    /// </summary>
    public static string? ReadSecret(string prompt, TextWriter output)
    {
        if (Console.IsInputRedirected)
        {
            return Console.In.ReadLine();
        }

        output.Write(prompt);
        var text = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            switch (key.Key)
            {
                case ConsoleKey.Enter:
                    output.WriteLine();
                    return text.ToString();
                case ConsoleKey.Escape:
                    output.WriteLine();
                    return null;
                case ConsoleKey.Backspace:
                    if (text.Length > 0)
                    {
                        text.Length--;
                    }

                    break;
                default:
                    if (!char.IsControl(key.KeyChar))
                    {
                        text.Append(key.KeyChar);
                    }

                    break;
            }
        }
    }

    /// <summary>
    /// For the Gate: yes, no, or null when there is no keyboard to ask. A pipe never answers a
    /// harness question, and the record then says nobody was asked rather than "no".
    /// </summary>
    public static bool? Ask(string question, TextWriter output) =>
        Console.IsInputRedirected ? null : Confirm(question, output);

    /// <summary>A yes from the keyboard only. A pipe never answers a harness question.</summary>
    public static bool Confirm(string question, TextWriter output)
    {
        if (Console.IsInputRedirected)
        {
            return false;
        }

        output.Write(question);
        var answer = Console.ReadLine()?.Trim();
        return string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase)
            || string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase);
    }
}
