using DipTick.Models.Chat;
using DipTick.ViewModels;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace DipTick.Views;

/// <summary>
/// The screen of <c>diptick tui</c>: full screen, foreground, near-black, one accent. Top to bottom:
/// a one-line header naming the provider and model; the transcript; the prompt zone when a question
/// is open; the command list when <c>/</c> is typed; the composer, a rounded box holding the draft
/// and the line being typed; the status line; and the F-key bar, the window's keys at the bottom of
/// the terminal. Settings and Help take the transcript's place. Esc three times quits and stops
/// everything, as in the window. Draws the <see cref="ChatViewModel"/> and sends it keys; decides
/// nothing itself.
/// </summary>
/// <remarks>
/// Built on Terminal.Gui v2 (2.5.0, MIT), on its Windows console driver (<see cref="TappedConsole"/>),
/// which speaks the Win32 console API, so it works in both Windows Terminal and the old console
/// host. Every visible part is drawn in DipTick's own colours, not the library's stock look. Screen
/// changes from another thread go through <see cref="IApplication.Invoke(Action)"/>.
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Reliability",
    "CA2000:Dispose objects before losing scope",
    Justification = "Every view made here is added to the window or to a panel in it, which owns it: Terminal.Gui disposes a view's " +
                    "subviews with it, and the window is disposed by its using. CA2000 does not follow ownership through Add.")]
internal static partial class ChatView
{
    /// <summary>The most draft lines shown in the composer; older ones are counted, not drawn.</summary>
    private const int MostDraftRows = 6;

    /// <summary>F1 to F10, in order: the bar's keys.</summary>
    private static readonly Key[] FKeys = [Key.F1, Key.F2, Key.F3, Key.F4, Key.F5, Key.F6, Key.F7, Key.F8, Key.F9, Key.F10];

    public static void Run(ChatViewModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        // Anything typed or pasted before the screen was up is thrown away: the person has not
        // seen what it would be sent to, so it is not theirs to send yet.
        _ = FlushConsoleInputBuffer(GetStdHandle(StdInputHandle));

        // Terminal.Gui's Windows console driver (it hears Shift+Enter), with DipTick watching its key records (Esc).
        using var tap = new KeyTap();
        using IApplication app = Application.Create();
        TappedConsole.Install(app, tap);
        app.Init();

        // No frame round the whole screen: the terminal is the frame.
        using var window = new Window { Title = model.Title, BorderStyle = LineStyle.None };
        window.SetScheme(Theme.Base);

        var header = new RowsView { X = 0, Y = 0, Width = Dim.Fill(), Height = 1 };
        var transcript = new RowsView { X = 0, Y = 1, Width = Dim.Fill(), Height = Dim.Fill(5), FollowTail = true };
        var settings = new SettingsPanel(app) { X = 0, Y = 1, Width = Dim.Fill(), Height = Dim.Fill(5) };
        var help = new DocsPanel { X = 0, Y = 1, Width = Dim.Fill(), Height = Dim.Fill(5) };
        var zone = new PromptZone { X = 0, Width = Dim.Fill() };

        var palette = new View { X = 0, Width = Dim.Fill(), Visible = false, BorderStyle = LineStyle.Rounded, CanFocus = false };
        palette.SetScheme(Theme.Faint);
        var paletteRows = new RowsView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
        palette.Add(paletteRows);

        // The composer: a rounded box holding the draft of earlier lines and the line being typed.
        // Focusable, or the line inside it could never have the keyboard: focus reaches a view only
        // through ancestors that can take it.
        var composer = new View { X = 0, Width = Dim.Fill(), BorderStyle = LineStyle.Rounded, CanFocus = true };
        composer.SetScheme(Theme.Faint);
        var draft = new RowsView { X = 0, Y = 0, Width = Dim.Fill(), Height = 0 };
        var caret = new Label { X = 0, Text = "›" };
        caret.SetScheme(Theme.Uniform(Theme.Accent));

        // Explicit colours for the line the person types in, in every state, so what is typed can
        // always be seen.
        var input = new TextField { X = 2, Width = Dim.Fill() };
        input.SetScheme(Theme.Base);
        composer.Add(draft, caret, input);

        var status = new RowsView { X = 0, Y = Pos.AnchorEnd(2), Width = Dim.Fill(), Height = 1 };
        var keyBar = new TermKeyBar { X = 0, Y = Pos.AnchorEnd(1), Width = Dim.Fill(), Height = 1 };
        window.Add(header, transcript, settings, help, zone, palette, composer, status, keyBar);
        var composerModel = model.Composer;

        // Find (F3): only the lines that hold these words, until F3 or Esc shows them all.
        string? finding = null;

        // Scrolling (F6): the arrows move the chat instead of what was typed before.
        var scrolling = false;

        // The chat whose draft the composer holds; null before the first draw.
        string? draftFor = null;
        var escapes = new EscapeCounter();

        void Layout()
        {
            var width = Math.Max(20, Theme.ScreenWidth() - 6);
            var draftRows = DraftRows(composerModel.Draft, width);
            draft.Height = draftRows.Count;
            draft.Rows = draftRows;
            caret.Y = draftRows.Count;
            input.Y = draftRows.Count;
            var composerHeight = draftRows.Count + 3;
            composer.Height = composerHeight;
            composer.Y = Pos.AnchorEnd(2 + composerHeight);

            var zoneRows = zone.Visible ? zone.Rows : 0;
            zone.Height = zoneRows;
            zone.Y = Pos.AnchorEnd(2 + composerHeight + zoneRows);

            var listRows = zone.Visible ? [] : PaletteRows(composerModel, width);
            palette.Visible = listRows.Count > 0;
            palette.Height = listRows.Count + 2;
            palette.Y = Pos.AnchorEnd(2 + composerHeight + zoneRows + listRows.Count + 2);
            paletteRows.Rows = listRows;

            var below = 2 + composerHeight + zoneRows;
            transcript.Visible = !settings.Visible && !help.Visible;
            transcript.Height = Dim.Fill(below);
            settings.Height = Dim.Fill(below);
            help.Height = Dim.Fill(below);
            window.SetNeedsLayout();
        }

        string Screen() => settings.Visible ? "Settings" : help.Visible ? "Help" : "Chat";

        void Redraw()
        {
            var width = transcript.Viewport.Width > 0 ? transcript.Viewport.Width : Theme.ScreenWidth();
            transcript.Rows = [.. model.RenderRows(width, finding).Select(TranscriptSpans)];
            header.Rows = [finding is { } words ? [.. Headline(model), new Span($"  · showing lines with '{words}' · F3 shows all", Theme.Warning)] : Headline(model)];
            status.Rows = [StatusSpans(scrolling ? "scrolling │ ↑/↓, PgUp/PgDn move the chat · F6 or Esc goes back to typing" : model.Status)];
            window.Title = model.Title;
            keyBar.Show(KeysFor(Screen()));
            ShowDraft();
        }

        // The composer holds the draft of the chat on screen: when the chat changes, its own draft
        // comes back (the view model keeps what is typed, as it is typed, and quitting keeps it).
        void ShowDraft()
        {
            var chat = model.SessionId ?? string.Empty;
            if (chat == draftFor)
            {
                return;
            }

            draftFor = chat;
            var lines = model.Draft.Split('\n');
            composerModel.Clear();
            foreach (var line in lines.Take(lines.Length - 1))
            {
                composerModel.NewLine(line);
            }

            input.Text = lines[^1];
            input.MoveEnd();
            Layout();
        }

        void Focus()
        {
            if (zone.Open)
            {
                return;
            }

            if (settings.Visible)
            {
                settings.SetFocus();
            }
            else
            {
                input.SetFocus();
            }
        }

        // Work handed to the screen's thread runs after that loop's repaint, and the loop then waits
        // for the next key; so every such change repaints before it returns.
        void Repaint()
        {
            Redraw();
            app.LayoutAndDraw();
        }

        var asker = new TuiAsker(app, zone, note =>
        {
            model.Working = note;
            app.Invoke(Repaint);
        });
        model.Asker = asker;
        model.ConfirmHook = asker.Approve;
        model.ApproveHook = asker.ApproveInChat;

        // /copy: the terminal library's clipboard, on the screen's thread; false when it cannot reach one.
        model.ClipboardHook = text =>
        {
            var copied = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            app.Invoke(() => copied.TrySetResult(app.Clipboard?.TrySetClipboardData(text) == true));
            return copied.Task;
        };
        model.Changed += () => app.Invoke(Repaint);
        model.QuitRequested += () => app.Invoke(app.RequestStop);
        zone.Changed += () =>
        {
            Layout();
            Focus();
        };

        TaskCompletionSource? settingsOpen = null;
        settings.Closed += () =>
        {
            Layout();
            Focus();
            Repaint();
            settingsOpen?.TrySetResult();
        };

        void OpenSettings()
        {
            help.Visible = false;
            settings.Open(new SettingsViewModel(model));
            Layout();
            Repaint();
        }

        void ToggleSettings()
        {
            if (settings.Visible)
            {
                settings.Close();
            }
            else
            {
                OpenSettings();
            }
        }

        model.SettingsHook = () =>
        {
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            app.Invoke(() =>
            {
                settingsOpen = closed;
                OpenSettings();
                app.LayoutAndDraw();
            });
            return closed.Task;
        };

        // Help: DipTick's own pages, open at one; closing it gives the chat back.
        void ShowPage(string page)
        {
            settings.Visible = false;
            help.Open(page);
            Layout();
            Repaint();
            help.SetFocus();
        }

        help.Closed += () =>
        {
            Layout();
            Focus();
            Repaint();
        };
        model.DocsHook = page => app.Invoke(() =>
        {
            ShowPage(page);
            app.LayoutAndDraw();
        });

        // F3 in Help: which words, asked in the prompt zone; the lines that hold them fill the list.
        void SearchPages() => _ = Task.Run(() =>
        {
            if (model.Asker is not { } ask)
            {
                return;
            }

            var words = ask.Text(new Question("Search the pages", "Which words?", "Every page is searched; Enter opens the one chosen, Esc shows the pages again."), string.Empty, _ => null);
            app.Invoke(() =>
            {
                if (words.Reply == Reply.Answered)
                {
                    help.Search(words.Value);
                }

                help.SetFocus();
                Repaint();
            });
        });

        // F7 in Help: every page as a Markdown file, in a folder asked for (diptick docs export).
        void SavePages() => _ = Task.Run(() =>
        {
            if (model.Asker is not { } ask)
            {
                return;
            }

            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var folder = ask.Text(new Question("Save the pages as files", "In which folder?", "Each page becomes one Markdown file there."), Path.Combine(documents, "DipTick pages"), text => text.Trim().Length == 0 ? "a folder's path" : null);
            var said = folder.Reply == Reply.Answered ? model.Capture(false, "docs", "export", folder.Value.Trim()).Text : null;

            // What was written, said above the page, where the person is looking.
            app.Invoke(() =>
            {
                if (said is { Length: > 0 })
                {
                    help.Note(said);
                }

                help.SetFocus();
                Repaint();
            });
        });

        // F3 on the chat: which words, asked in the prompt zone; F3 again shows every line.
        void Find()
        {
            if (finding is not null)
            {
                finding = null;
                Repaint();
                return;
            }

            if (model.Asker is not { } ask)
            {
                return;
            }

            _ = Task.Run(() =>
            {
                var words = ask.Text(new Question("Find in this chat", "Which words?", "Only the lines that hold them are shown; F3 again, or Esc, shows them all."), string.Empty, _ => null);
                app.Invoke(() =>
                {
                    finding = words.Reply == Reply.Answered && words.Value.Trim().Length > 0 ? words.Value.Trim() : null;
                    Repaint();
                });
            });
        }

        // F3 on Settings: which words, then the settings of any section that hold them.
        void FindSetting() => _ = Task.Run(() =>
        {
            if (model.Asker is not { } ask)
            {
                return;
            }

            var words = ask.Text(new Question("Find a setting", "Its name, or what it does?", "Every section is searched."), string.Empty, _ => null);
            app.Invoke(() =>
            {
                if (words.Reply == Reply.Answered)
                {
                    settings.Find(words.Value);
                }

                Focus();
                Repaint();
            });
        });

        void Quit(bool ask) => _ = Task.Run(() => model.QuitAsync(ask));

        // Each screen's keys, the window's own where a terminal can do them.
        TermKey?[] KeysFor(string screen) => screen switch
        {
            "Settings" =>
            [
                new("Help", settings.HelpSelected),
                new("Change", settings.ChangeSelected),
                new("Find", FindSetting),
                null,
                new("Reload", () =>
                {
                    model.Reload();
                    settings.Reload();
                }),
                new("Pane", settings.NextPane),
                new("Show file", settings.ShowFile),
                new("Default", settings.DefaultSelected),
                new("Close", settings.Close),
                new("Quit", () => Quit(ask: true)),
            ],
            "Help" =>
            [
                new("Contents", help.Contents),
                new("Back", help.Back),
                new("Search", SearchPages),
                new("Pages", help.Pages),
                null,
                new("Pane", help.NextPane),
                new("Save as files", SavePages),
                null,
                new("Settings", OpenSettings),
                new("Quit", () => Quit(ask: true)),
            ],
            _ =>
            [
                new("Help", () => ShowPage(Controllers.Docs.For("Chat").Id)),
                new("Window", () => _ = Task.Run(model.OpenWindowAsync)),
                new("Find", Find),
                new("Agents", () => _ = Task.Run(model.TableMenuAsync)),
                new("Retry", () => _ = Task.Run(() => model.RetryAsync())),
                new("Pane", () =>
                {
                    scrolling = !scrolling;
                    Repaint();
                }),
                new("Attach", () => _ = Task.Run(model.AskAttachAsync)),
                new("Stop", model.Stop),
                new("Settings", ToggleSettings),
                new("Quit", () => Quit(ask: true)),
            ],
        };

        void Send(string text)
        {
            composerModel.Clear();
            input.Text = string.Empty;
            Layout();

            // Off the screen's thread, so a long answer never freezes typing; every change it
            // makes comes back through Changed and Invoke.
            _ = Task.Run(() => model.SubmitAsync(text));
        }

        input.TextChanged += (_, _) =>
        {
            var typed = input.Text ?? string.Empty;
            composerModel.Typed(typed);

            // Written through as it is typed, so a quit, however sudden, keeps it.
            if (draftFor is not null)
            {
                model.Draft = composerModel.Compose(typed);
            }

            Layout();
        };

        input.KeyDown += (_, key) =>
        {
            var line = input.Text ?? string.Empty;

            if (scrolling && (key == Key.CursorUp || key == Key.CursorDown))
            {
                transcript.ScrollBy(key == Key.CursorUp ? -1 : 1);
                key.Handled = true;
            }
            else if (key == Key.Enter)
            {
                Send(composerModel.Marked ?? composerModel.Compose(line));
                key.Handled = true;
            }
            else if (key == Key.Enter.WithCtrl || key == Key.J.WithCtrl || key == Key.Enter.WithShift)
            {
                // Ctrl+J arrives as Ctrl+Enter (it is a line feed). Shift+Enter arrives as itself on
                // the Windows console driver.
                composerModel.NewLine(line);
                input.Text = string.Empty;
                Layout();
                key.Handled = true;
            }
            else if (key == Key.Tab && composerModel.Complete() is { } completed)
            {
                input.Text = completed;
                input.MoveEnd();
                composerModel.Typed(completed);
                Layout();
                key.Handled = true;
            }
            else if ((key == Key.CursorUp || key == Key.CursorDown) && composerModel.ListOpen)
            {
                composerModel.Move(key == Key.CursorUp ? -1 : 1);
                Layout();
                key.Handled = true;
            }
            else if (key == Key.CursorUp || key == Key.CursorDown)
            {
                // History only on a plain line: with a draft, the arrows would lose it.
                if (composerModel.Draft.Count == 0 && model.Recall(key == Key.CursorUp ? -1 : 1) is { } recalled)
                {
                    input.Text = recalled;
                    input.MoveEnd();
                }

                key.Handled = true;
            }
            else if (key == Key.Backspace && line.Length == 0 && composerModel.TakeBack() is { } previous)
            {
                input.Text = previous;
                input.MoveEnd();
                Layout();
                key.Handled = true;
            }
            else if (key == Key.Esc && composerModel.ListOpen)
            {
                composerModel.CloseList();
                Layout();
                key.Handled = true;
            }
            else if (key == Key.PageUp || key == Key.PageDown)
            {
                transcript.ScrollBy((key == Key.PageUp ? -1 : 1) * Math.Max(1, transcript.Viewport.Height - 1));
                key.Handled = true;
            }
        };

        // Enter from anywhere else (a mouse, a hot key) sends the same way the key does.
        input.Accepting += (_, e) =>
        {
            e.Handled = true;
            Send(composerModel.Compose(input.Text ?? string.Empty));
        };

        // A paste with line breaks in the composer never sends itself: a text field keeps only a
        // paste's first line, and sending it line by line would be one model call, and one charge,
        // per line. One of chat.pasteLines lines or more is kept in a file and the composer shows
        // the file's name, as Hermes Agent does (Pastes); a shorter one goes into the draft whole.
        // What was typed before it stays.
        void PasteIn(string text)
        {
            var typed = input.Text ?? string.Empty;
            string? reference = null;
            try
            {
                reference = model.KeepPaste(text);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _ = model.Say(Role.System, $"The paste stays in the composer: it could not be kept in {ChatViewModel.PasteFolder} ({ex.Message}).");
            }

            input.Text = reference is { } named
                ? typed.Length == 0 || typed.EndsWith(' ') ? typed + named : typed + " " + named
                : composerModel.Paste(typed, text);
            input.MoveEnd();
            Layout();
        }

        // A terminal that brackets its pastes hands them over whole (Terminal.Gui's Paste); the
        // console host and Windows Terminal type them, and the console's reader gathers them (KeyTap).
        app.Paste += (_, e) =>
        {
            if (input.HasFocus && ComposerViewModel.IsMultiLine(e.Text))
            {
                PasteIn(e.Text);
                e.Handled = true;
            }
        };
        tap.Pasted += text => app.Invoke(() => PasteIn(text));
        input.HasFocusChanged += (_, _) => tap.Collecting = input.HasFocus;

        // Before any view has a key: Esc counted toward the three that quit (a held Esc's repeats
        // never get here, KeyTap), and the F-keys, the screen's own, whatever else is open.
        app.Keyboard.KeyDown += (_, key) =>
        {
            if (key == Key.Esc)
            {
                tap.EscapeSeen();
                var gap = DoubleClick.Gap();
                switch (escapes.Press(Environment.TickCount64, gap))
                {
                    case EscapeTurn.Quit:
                        key.Handled = true;
                        keyBar.Warn("Quitting: stopping everything…");
                        Quit(ask: false);
                        return;
                    case EscapeTurn.Twice:
                        keyBar.Warn("Esc once more quits DipTick and stops everything, the local models included.");
                        app.AddTimeout(TimeSpan.FromMilliseconds(gap), () =>
                        {
                            escapes.Reset();
                            keyBar.Warn(null);
                            return false;
                        });
                        break;
                    default:
                        break;
                }

                return;
            }

            var number = Array.IndexOf(FKeys, key) + 1;
            if (number > 0)
            {
                // A key with nothing to do on this screen is taken all the same: nothing else acts on it.
                _ = keyBar.Press(number);
                key.Handled = true;
                Repaint();
            }
        };

        window.KeyDown += (_, key) =>
        {
            if (key == Key.Esc)
            {
                // Esc closes what is open, nearest first; with nothing open it stops the answer. One
                // press never leaves: three quick ones do, above. Settings and Help take their own
                // Esc when they have the keyboard; this is their Esc when a click left it elsewhere.
                if (help.Visible)
                {
                    help.Close();
                }
                else if (settings.Visible)
                {
                    settings.Back();
                }
                else if (scrolling)
                {
                    scrolling = false;
                    Repaint();
                }
                else if (finding is not null)
                {
                    finding = null;
                    Repaint();
                }
                else
                {
                    model.Stop();
                }

                key.Handled = true;
            }
            else if (key == Key.Q.WithCtrl)
            {
                // A quit like any other: asked first while an answer runs, the local models let go.
                Quit(ask: true);
                key.Handled = true;
            }
        };

        // Each key's place on the bar, sized once for every screen's labels.
        keyBar.Measure([KeysFor("Chat"), KeysFor("Settings"), KeysFor("Help")]);

        _ = Task.Run(model.StartAsync);
        Layout();
        Redraw();
        input.SetFocus();
        app.Run(window);
    }

    /// <summary>
    /// The header: the name in the accent, "Private: nothing kept" in words while the chat is private
    /// (the notice that says more scrolls away; this line stays), then the
    /// chat's table, each agent in its colour with its model, and who answers when nobody is named.
    /// </summary>
    internal static IReadOnlyList<Span> Headline(ChatViewModel model)
    {
        List<Span> line = [new Span(" DipTick  ", Theme.Accent)];
        if (model.IsPrivate)
        {
            line.Add(new Span("Private: nothing kept  ", Theme.Warning));
        }

        var table = model.Table;
        if (table.Count == 0)
        {
            return model.SetUpAgents.Count == 0
                ? [.. line, new Span("no model yet", Theme.Warning), new Span("  · /settings, Models", Theme.Dim)]
                : [.. line, new Span("nobody at this table", Theme.Warning), new Span("  · F4 or /seats add <agent>", Theme.Dim)];
        }

        for (var i = 0; i < table.Count; i++)
        {
            var chip = table[i];
            line.Add(new Span(chip.Name, Theme.Seat(chip.Color)));
            var tools = model.TakesTools(chip) == false ? " (no tools)" : string.Empty;
            line.Add(new Span($" · {(chip.Model.Length == 0 ? "no model" : chip.Model)}{tools}{(i < table.Count - 1 ? "   " : string.Empty)}", chip.SetUp ? Theme.Dim : Theme.Warning));
        }

        line.Add(new Span($"  · {model.AnswerModeWords}", Theme.Text));

        // Whether agents here may search and fetch; /research switches it.
        line.Add(new Span($"  · {model.ResearchWords}", model.Research == Models.Research.ResearchState.On ? Theme.Text : Theme.Dim));
        line.Add(new Span("  · F4 the table · /help", Theme.Dim));
        return line;
    }

    /// <summary>The status line: its first part in the accent when ready, the warning colour when busy; the rest dim.</summary>
    internal static IReadOnlyList<Span> StatusSpans(string status)
    {
        var parts = status.Split(" │ ");
        var first = parts[0] == "ready" ? Theme.Accent : Theme.Warning;
        var spans = new List<Span> { new(" " + parts[0], first) };
        foreach (var part in parts.Skip(1))
        {
            spans.Add(new Span(" │ ", Theme.Dim));
            spans.Add(new Span(part, part is "DRY RUN" or "auto-yes" ? Theme.Warning : Theme.Dim));
        }

        return spans;
    }

    /// <summary>
    /// One transcript row: › for the person in the accent, ┊ for an agent and · for DipTick, dim; an
    /// answer's head (its agent's name and model) and the questions it asked in the agent's colour, what
    /// it used dim.
    /// </summary>
    internal static IReadOnlyList<Span> TranscriptSpans(TranscriptRow row)
    {
        var (mark, markLook, textLook) = row.Role switch
        {
            Role.Person => (" › ", Theme.Accent, Theme.Text),
            Role.Agent when row.Part == RowPart.Head => (" ┊ ", Theme.Seat(row.Color), Theme.Seat(row.Color)),
            Role.Agent when row.Part == RowPart.Note => (" ┊ ", Theme.Dim, row.Color.Length > 0 ? Theme.Seat(row.Color) : Theme.Dim),
            Role.Agent => (" ┊ ", Theme.Dim, Theme.Text),
            Role.Tool => (" » ", Theme.Dim, row.Color.Length > 0 ? Theme.Seat(row.Color) : Theme.Dim),
            _ => (" · ", Theme.Dim, Theme.Dim),
        };
        return [new Span(row.First ? mark : "   ", markLook), new Span(" " + row.Text, textLook)];
    }

    /// <summary>The draft as rows: the last few lines, marked ┆, and a count of any above them.</summary>
    internal static List<IReadOnlyList<Span>> DraftRows(IReadOnlyList<string> draft, int width)
    {
        var rows = new List<IReadOnlyList<Span>>();
        var skipped = Math.Max(0, draft.Count - MostDraftRows);
        if (skipped > 0)
        {
            rows.Add(Theme.Row($"┆ ({skipped} more line{(skipped == 1 ? string.Empty : "s")} above)", Theme.Dim));
        }

        foreach (var line in draft.Skip(skipped))
        {
            var shown = Models.SafeText.Line(line);
            rows.Add([new Span("┆ ", Theme.Dim), new Span(shown.Length <= width - 2 ? shown : shown[..Math.Max(1, width - 3)] + "…", Theme.Text)]);
        }

        return rows;
    }

    /// <summary>The command list as rows: each command and its help, the marked one with ›.</summary>
    internal static List<IReadOnlyList<Span>> PaletteRows(ComposerViewModel composer, int width)
    {
        var rows = new List<IReadOnlyList<Span>>();
        if (!composer.ListOpen)
        {
            return rows;
        }

        var matches = composer.Matches;
        var first = Math.Clamp(composer.Mark - (ComposerViewModel.MostRows - 1), 0, Math.Max(0, matches.Count - ComposerViewModel.MostRows));
        var nameWidth = matches.Max(m => m.Name.Length);
        for (var i = first; i < Math.Min(matches.Count, first + ComposerViewModel.MostRows); i++)
        {
            var on = i == composer.Mark;
            rows.Add(
            [
                new Span(on ? "› " : "  ", Theme.Accent),
                new Span(matches[i].Name.PadRight(nameWidth), on ? Theme.Accent : Theme.Text),
                new Span("  " + matches[i].Help, Theme.Dim),
            ]);
        }

        rows.Add(Theme.Row(" Tab completes · Enter runs · Esc closes", Theme.Dim));
        return rows;
    }

    // STD_INPUT_HANDLE is (DWORD)-10 (processenv.h, GetStdHandle); FlushConsoleInputBuffer
    // discards every input record in the console's buffer (learn.microsoft.com, wincon.h).
    private const int StdInputHandle = -10;

    [System.Runtime.InteropServices.LibraryImport("kernel32.dll", SetLastError = true)]
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    private static partial nint GetStdHandle(int handle);

    [System.Runtime.InteropServices.LibraryImport("kernel32.dll", SetLastError = true)]
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool FlushConsoleInputBuffer(nint console);
}
