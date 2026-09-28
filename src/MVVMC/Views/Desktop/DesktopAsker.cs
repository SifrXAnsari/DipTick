using Avalonia.Threading;
using DipTick.Models.Chat;
using DipTick.ViewModels;

namespace DipTick.Views.Desktop;

/// <summary>
/// The questions in the window's prompt card, through the same <see cref="IAsker"/> the console and
/// the terminal use, so setup and settings run unchanged.
/// </summary>
/// <remarks>
/// Called from worker threads only: each question is posted to the UI thread and the worker waits
/// for the answer while the window keeps running. An approval nobody answers in
/// <see cref="ApprovalTimeout"/> is denied.
/// </remarks>
internal sealed class DesktopAsker(PromptCard card, Action<string?> status) : IAsker
{
    /// <summary>Hermes' approval timeout: after this, an unanswered approval is a no.</summary>
    public static readonly TimeSpan ApprovalTimeout = TimeSpan.FromSeconds(300);

    public bool CanConfirm => true;

    /// <summary>Raised when an approval timed out, with the question, so the transcript can say so.</summary>
    public event Action<string>? TimedOut;

    public Asked<int> Choose(Question question, IReadOnlyList<Choice> choices, int? preset)
    {
        ArgumentNullException.ThrowIfNull(question);
        var answer = Ask(new Prompt(question.Title, question.Note, question.Text, PromptKind.Choose, choices, preset ?? 0, Now: question.Now), null);
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
        var answer = Ask(new Prompt(question.Title, question.Note, question.Text, PromptKind.Choose, [new Choice("Yes"), new Choice("No")], preset ? 0 : 1), null);
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

    public void Tell(string title, string text) => Ask(new Prompt(title, string.Empty, text, PromptKind.Tell), null);

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
    /// An approval, in the warning colour: Allow once, or Deny, where the mark starts, so Enter
    /// without looking says no. Esc denies; so does silence.
    /// </summary>
    public bool? Approve(string question)
    {
        ArgumentNullException.ThrowIfNull(question);
        var shown = question.TrimEnd();
        if (shown.EndsWith("[y/N]", StringComparison.Ordinal))
        {
            shown = shown[..^"[y/N]".Length].TrimEnd();
        }

        var answer = Ask(new Prompt("approval required", string.Empty, shown, PromptKind.Choose, [new Choice("Allow once"), new Choice("Deny")], 1, Alert: true), ApprovalTimeout, out var timedOut);
        if (timedOut)
        {
            TimedOut?.Invoke(shown);
        }

        return answer.Reply == Reply.Answered && answer.Index == 0;
    }

    /// <summary>
    /// An approval in a chat: Allow once, Allow for this chat, or Deny, where the mark
    /// starts. Esc denies; so does silence, after <see cref="ApprovalTimeout"/>.
    /// </summary>
    public Models.Harness.Approval? ApproveInChat(string question)
    {
        ArgumentNullException.ThrowIfNull(question);
        var shown = question.TrimEnd();
        if (shown.EndsWith("[y/N]", StringComparison.Ordinal))
        {
            shown = shown[..^"[y/N]".Length].TrimEnd();
        }

        var answer = Ask(new Prompt("approval required", string.Empty, shown, PromptKind.Choose, [new Choice("Allow once"), new Choice("Allow for this chat", "until this chat's rules change"), new Choice("Deny")], 2, Alert: true), ApprovalTimeout, out var timedOut);
        if (timedOut)
        {
            TimedOut?.Invoke(shown);
        }

        return answer.Reply != Reply.Answered ? Models.Harness.Approval.Deny : answer.Index switch
        {
            0 => Models.Harness.Approval.Once,
            1 => Models.Harness.Approval.ForChat,
            _ => Models.Harness.Approval.Deny,
        };
    }

    private Asked<string> Typed(Prompt prompt)
    {
        var answer = Ask(prompt, null);
        return answer.Reply == Reply.Answered ? Asked<string>.Of(answer.Text) : new Asked<string>(answer.Reply, string.Empty);
    }

    private PromptResult Ask(Prompt prompt, TimeSpan? timeout) => Ask(prompt, timeout, out _);

    /// <param name="timedOut">True when nobody answered within <paramref name="timeout"/>: the card was closed for them.</param>
    /// <remarks>
    /// The answer arrives through a task, not a wait handle: after a timeout the card still calls
    /// back as it closes, and a handle would already be disposed by then.
    /// </remarks>
    private PromptResult Ask(Prompt prompt, TimeSpan? timeout, out bool timedOut)
    {
        timedOut = false;
        var answered = new TaskCompletionSource<PromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(() => card.Ask(prompt, answer => answered.TrySetResult(answer)));
        if (timeout is { } limit && !answered.Task.Wait(limit))
        {
            Dispatcher.UIThread.Post(() => card.Withdraw(prompt));
            timedOut = true;
            return new PromptResult(Reply.Stop, 0, string.Empty);
        }

        return answered.Task.GetAwaiter().GetResult();
    }
}
