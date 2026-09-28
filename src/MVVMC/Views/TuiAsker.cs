using DipTick.Models.Chat;
using DipTick.ViewModels;
using Terminal.Gui.App;

namespace DipTick.Views;

/// <summary>
/// The questions in the chat screen's prompt zone: a panel above the composer, never a window
/// floating over the text. Esc goes back a step.
/// </summary>
/// <remarks>
/// Called from worker threads only: each question is handed to the screen's thread with
/// <see cref="IApplication.Invoke(Action)"/> and the worker waits for the answer, while the screen
/// keeps running. Called on the screen's own thread it would wait for itself.
/// </remarks>
/// <param name="status">Shows a line in the status bar while something runs, or clears it with null.</param>
internal sealed class TuiAsker(IApplication app, PromptZone zone, Action<string?> status) : IAsker
{
    public bool CanConfirm => true;

    public Asked<int> Choose(Question question, IReadOnlyList<Choice> choices, int? preset)
    {
        ArgumentNullException.ThrowIfNull(question);
        var answer = Ask(new Prompt(question.Title, question.Note, question.Text, PromptKind.Choose, choices, preset ?? 0, Now: question.Now));
        return answer.Reply == Reply.Answered ? Asked<int>.Of(answer.Index) : new Asked<int>(answer.Reply, 0);
    }

    public Asked<string> Text(Question question, string preset, Func<string, string?> check)
    {
        ArgumentNullException.ThrowIfNull(question);
        return Typed(new Prompt(question.Title, question.Note, question.Text, PromptKind.Text, Text: preset, Check: check));
    }

    public Asked<string> Secret(Question question, Func<string, string?> check)
    {
        ArgumentNullException.ThrowIfNull(question);
        return Typed(new Prompt(question.Title, question.Note, question.Text, PromptKind.Text, Secret: true, Check: check));
    }

    public Asked<bool> YesNo(Question question, bool preset)
    {
        ArgumentNullException.ThrowIfNull(question);
        var answer = Ask(new Prompt(question.Title, question.Note, question.Text, PromptKind.Choose, [new Choice("Yes"), new Choice("No")], preset ? 0 : 1));
        return answer.Reply == Reply.Answered ? Asked<bool>.Of(answer.Index == 0) : new Asked<bool>(answer.Reply, false);
    }

    public Asked<string> PickModel(Question question, IReadOnlyList<ModelInfo> models, string preset)
    {
        ArgumentNullException.ThrowIfNull(question);
        return Typed(new Prompt(question.Title, question.Note, question.Text, PromptKind.Model, Text: preset, Models: models));
    }

    public Asked<string> Pick(Question question, IReadOnlyList<PickItem> items, string preset)
    {
        ArgumentNullException.ThrowIfNull(question);
        return Typed(new Prompt(question.Title, question.Note, question.Text, PromptKind.Pick, Text: preset, Items: items));
    }

    public void Tell(string title, string text) => Ask(new Prompt(title, string.Empty, text, PromptKind.Tell));

    public T Busy<T>(string what, Func<Task<T>> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        status(what);
        try
        {
            return work().GetAwaiter().GetResult();
        }
        finally
        {
            status(null);
        }
    }

    /// <summary>
    /// An approval, in the warning colour: Allow once, or Deny, which is where the mark starts, so
    /// Enter without looking says no. Esc denies too.
    /// </summary>
    public bool? Approve(string question)
    {
        ArgumentNullException.ThrowIfNull(question);

        // The gate words its question for a console prompt; here the choices say it instead.
        var shown = question.TrimEnd();
        if (shown.EndsWith("[y/N]", StringComparison.Ordinal))
        {
            shown = shown[..^"[y/N]".Length].TrimEnd();
        }

        var answer = Ask(new Prompt("approval required", string.Empty, shown, PromptKind.Choose, [new Choice("Allow once"), new Choice("Deny")], 1, Alert: true));
        return answer.Reply == Reply.Answered && answer.Index == 0;
    }

    /// <summary>
    /// An approval in a chat: Allow once, Allow for this chat, or Deny, which is
    /// where the mark starts. Esc denies.
    /// </summary>
    public Models.Harness.Approval? ApproveInChat(string question)
    {
        ArgumentNullException.ThrowIfNull(question);
        var shown = question.TrimEnd();
        if (shown.EndsWith("[y/N]", StringComparison.Ordinal))
        {
            shown = shown[..^"[y/N]".Length].TrimEnd();
        }

        var answer = Ask(new Prompt("approval required", string.Empty, shown, PromptKind.Choose, [new Choice("Allow once"), new Choice("Allow for this chat", "until this chat's rules change"), new Choice("Deny")], 2, Alert: true));
        return answer.Reply != Reply.Answered ? Models.Harness.Approval.Deny : answer.Index switch
        {
            0 => Models.Harness.Approval.Once,
            1 => Models.Harness.Approval.ForChat,
            _ => Models.Harness.Approval.Deny,
        };
    }

    private Asked<string> Typed(Prompt prompt)
    {
        var answer = Ask(prompt);
        return answer.Reply == Reply.Answered ? Asked<string>.Of(answer.Text) : new Asked<string>(answer.Reply, string.Empty);
    }

    private PromptResult Ask(Prompt prompt)
    {
        var result = new PromptResult(Reply.Stop, 0, string.Empty);
        using var done = new ManualResetEventSlim();
        app.Invoke(() =>
        {
            zone.Ask(prompt, answer =>
            {
                result = answer;
                done.Set();
            });

            // Work handed to the screen's thread runs after that loop's repaint: repaint now.
            app.LayoutAndDraw();
        });
        done.Wait();
        return result;
    }
}
