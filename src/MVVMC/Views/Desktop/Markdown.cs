using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using DipTick.Models;

namespace DipTick.Views.Desktop;

/// <summary>
/// An agent's answer as it is meant to be read, drawn from the blocks
/// <see cref="MarkdownText"/> reads. Every stretch of prose between code blocks is one selectable
/// text, so a drag selects across its paragraphs, headings and list items; each code block keeps
/// its own box, with a Copy button that shows while the pointer is over it. The right-click menu
/// is the message's (<see cref="TranscriptView"/>), not the theme's.
/// </summary>
internal static class Markdown
{
    private const double BodySize = 15;

    /// <summary>
    /// The answer as a column: prose blocks and code boxes. <paramref name="copy"/> is what a code
    /// box's Copy does. <paramref name="follow"/> opens a link: given for DipTick's own pages; left
    /// out for a model's answer, whose links show where they go beside their words and open nothing,
    /// so words that name one place cannot hide another.
    /// </summary>
    public static StackPanel Render(string text, Look look, Action<string> copy, Action<string>? follow = null)
    {
        ArgumentNullException.ThrowIfNull(look);
        ArgumentNullException.ThrowIfNull(copy);
        var column = new StackPanel { Spacing = 10 };
        InlineCollection? prose = null;
        MdKind? last = null;
        foreach (var block in MarkdownText.Parse(SafeText.Block(text ?? string.Empty)))
        {
            if (block.Kind == MdKind.Code)
            {
                prose = null;
                last = null;
                column.Children.Add(CodeBlock(block.Text, look, copy));
                continue;
            }

            if (prose is null)
            {
                prose = [];
                column.Children.Add(Prose(look, prose));
            }
            else
            {
                // A list's items sit line under line; anything else starts a short blank line below.
                prose.Add(new LineBreak());
                if (!(last == MdKind.Item && block.Kind == MdKind.Item))
                {
                    prose.Add(new LineBreak { FontSize = BodySize * 0.45 });
                }
            }

            Add(prose, block, look, follow);
            last = block.Kind;
        }

        if (column.Children.Count == 0)
        {
            column.Children.Add(Prose(look, []));
        }

        return column;
    }

    /// <summary>One block's runs: a heading larger and heavier, an item after its mark, a paragraph as it is.</summary>
    private static void Add(InlineCollection inlines, MdBlock block, Look look, Action<string>? follow)
    {
        switch (block.Kind)
        {
            case MdKind.Heading:
                var size = block.Level switch { 1 => 20.0, 2 => 17.0, _ => BodySize };
                AddSpans(inlines, block.Text, look, size, heading: true, follow);
                break;
            case MdKind.Item:
                inlines.Add(new Run(block.Mark + "  ") { Foreground = look.Dim });
                AddSpans(inlines, block.Text, look, BodySize, heading: false, follow);
                break;
            default:
                AddSpans(inlines, block.Text, look, BodySize, heading: false, follow);
                break;
        }
    }

    private static void AddSpans(InlineCollection inlines, string text, Look look, double size, bool heading, Action<string>? follow)
    {
        foreach (var span in MarkdownText.Spans(text))
        {
            if (span.Link is { } target)
            {
                inlines.Add(follow is null ? new Run($"{span.Text} ({target})") { FontSize = size, Foreground = look.Ink } : LinkRun(span.Text, target, look, size, follow));
                continue;
            }

            var run = new Run(span.Text) { FontSize = size };
            if (heading || span.Bold)
            {
                run.FontWeight = FontWeight.SemiBold;
            }

            if (span.Italic)
            {
                run.FontStyle = FontStyle.Italic;
            }

            if (span.Code)
            {
                run.FontFamily = look.Mono;
                run.Background = look.Code;
            }

            inlines.Add(run);
        }
    }

    /// <summary>
    /// A link in a page: a button that reads as text, underlined in the accent, saying where it goes
    /// when the pointer is on it. An address outside DipTick says it opens in the browser.
    /// </summary>
    private static InlineUIContainer LinkRun(string text, string target, Look look, double size, Action<string> follow)
    {
        var outside = target.StartsWith("https://", StringComparison.Ordinal);

        // An underline of its own: TextDecorations.Underline is one object for the whole process (a
        // static property in Avalonia 12.1.3), owned by the thread that first read it.
        var underline = new TextDecorationCollection { new TextDecoration { Location = TextDecorationLocation.Underline } };
        var button = new Button
        {
            Classes = { "link" },
            Content = new TextBlock { Text = text, FontSize = size, TextDecorations = underline, Foreground = look.Link },
            Padding = new Thickness(0),
            Margin = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };
        ToolTip.SetTip(button, outside ? $"Opens {target} in your browser" : $"Opens the page '{text}'");
        button.Click += (_, _) => follow(target);
        return new InlineUIContainer(button) { BaselineAlignment = BaselineAlignment.TextBottom };
    }

    /// <summary>A stretch of prose: selectable, and with the theme's own menu taken off, so the message's menu opens instead.</summary>
    private static SelectableTextBlock Prose(Look look, InlineCollection inlines) => new()
    {
        TextWrapping = TextWrapping.Wrap,
        FontSize = BodySize,
        LineSpacing = 5,
        Foreground = look.Ink,
        SelectionBrush = look.Selection,
        ContextFlyout = null,
        Inlines = inlines,
    };

    /// <summary>A code block in its own box; its Copy button shows while the pointer is over the box (App.axaml).</summary>
    private static Border CodeBlock(string code, Look look, Action<string> copy)
    {
        var text = new SelectableTextBlock
        {
            Text = code,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = look.Mono,
            FontSize = 13,
            Foreground = look.Ink,
            SelectionBrush = look.Selection,
            ContextFlyout = null,
        };
        var button = new Button { Content = "Copy", Classes = { "tool" } };
        ToolTip.SetTip(button, "Copy this code");
        button.Click += (_, _) => copy(code);
        var tools = new StackPanel { Classes = { "tools" }, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
        tools.Children.Add(button);
        var grid = new Grid();
        grid.Children.Add(text);
        grid.Children.Add(tools);
        return new Border
        {
            Classes = { "code" },
            Background = look.Code,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10),
            Child = grid,
        };
    }

    /// <summary>The brushes a rendering uses, taken from the theme at the moment of rendering; <paramref name="Link"/> is a link's, the accent.</summary>
    internal sealed record Look(IBrush Ink, IBrush Dim, IBrush Code, IBrush Selection, FontFamily Mono, IBrush Link);
}
