using DipTick.Models.Chat;

namespace DipTick.ViewModels;

/// <summary>How a question ended: answered, or the person went back a step, or stopped.</summary>
internal enum Reply
{
    Answered,
    Back,
    Stop,
}

/// <summary>An answer, or the person going back or stopping instead.</summary>
internal readonly record struct Asked<T>(Reply Reply, T Value)
{
    public static Asked<T> Back => new(Reply.Back, default!);

    public static Asked<T> Stop => new(Reply.Stop, default!);

    public static Asked<T> Of(T value) => new(Reply.Answered, value);
}

/// <summary>One choice in a list: what it is, and a few words more.</summary>
internal sealed record Choice(string Label, string Detail = "");

/// <summary>
/// One item of a long list to pick from by typing part of it (the providers): its id, what it is
/// called, the group it sits in, a few words more, and why it cannot be picked yet (empty: it can).
/// An item that cannot be picked is shown, dimmed, so the list is whole; picking it says why.
/// </summary>
internal sealed record PickItem(string Id, string Label, string Group = "", string Detail = "", string Unavailable = "")
{
    /// <summary>Whether it answers a search: its name, id, group or words hold the text.</summary>
    public bool Matches(string words) =>
        Label.Contains(words, StringComparison.OrdinalIgnoreCase)
        || Id.Contains(words, StringComparison.OrdinalIgnoreCase)
        || Group.Contains(words, StringComparison.OrdinalIgnoreCase)
        || Detail.Contains(words, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// What a search keeps, the closest first: an item whose name or id is the text typed whole, then
    /// one whose name or id starts with it, then one whose name or id holds it, then one whose group
    /// or words do; in the list's own order within each. No text keeps the whole list as it is.
    /// </summary>
    public static List<PickItem> Narrow(IReadOnlyList<PickItem> items, string words)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(words);
        var text = words.Trim();
        return text.Length == 0 ? [.. items] : [.. items.Where(i => i.Matches(text)).OrderBy(i => i.Closeness(text))];
    }

    private int Closeness(string text) =>
        string.Equals(Id, text, StringComparison.OrdinalIgnoreCase) || string.Equals(Label, text, StringComparison.OrdinalIgnoreCase) ? 0
        : Label.StartsWith(text, StringComparison.OrdinalIgnoreCase) || Id.StartsWith(text, StringComparison.OrdinalIgnoreCase) ? 1
        : Label.Contains(text, StringComparison.OrdinalIgnoreCase) || Id.Contains(text, StringComparison.OrdinalIgnoreCase) ? 2
        : 3;
}

/// <summary>
/// A question as the person sees it: <paramref name="Title"/> says where they are ("Setup · 2 of 6"),
/// <paramref name="Text"/> is the question, <paramref name="Note"/> why it is asked or what came before.
/// <paramref name="Now"/> puts it before a question already open, which comes back after it: for
/// the one question that concerns everything open (quitting).
/// </summary>
internal sealed record Question(string Title, string Text, string Note = "", bool Now = false);

/// <summary>
/// What a flow may ask a person, whatever the screen: the plain console, the chat screen's
/// dialogs, or the window's prompt card. Every answer can be Back or Stop instead.
/// </summary>
internal interface IAsker
{
    /// <summary>
    /// Whether a person is there to give a yes. False for answers piped from a script: a pipe never
    /// gives a yes, so a flow needs --yes there instead.
    /// </summary>
    bool CanConfirm { get; }

    /// <summary>One of <paramref name="choices"/>, by index.</summary>
    Asked<int> Choose(Question question, IReadOnlyList<Choice> choices, int? preset);

    /// <summary>
    /// A line of text, trimmed. Empty keeps <paramref name="preset"/>. <paramref name="check"/> says
    /// why an answer will not do, and the question is asked again.
    /// </summary>
    Asked<string> Text(Question question, string preset, Func<string, string?> check);

    /// <summary>Text that is never shown as it is typed: a key, a password.</summary>
    Asked<string> Secret(Question question, Func<string, string?> check);

    Asked<bool> YesNo(Question question, bool preset);

    /// <summary>A model from the platform's list, or a name the list does not show.</summary>
    Asked<string> PickModel(Question question, IReadOnlyList<ModelInfo> models, string preset);

    /// <summary>
    /// One item of a long list, narrowed by typing, by its id; <paramref name="preset"/> is marked
    /// first. Only an item that can be picked is ever the answer.
    /// </summary>
    Asked<string> Pick(Question question, IReadOnlyList<PickItem> items, string preset);

    /// <summary>Something to read, acknowledged before going on.</summary>
    void Tell(string title, string text);

    /// <summary>Runs <paramref name="work"/> while telling the person what is happening.</summary>
    T Busy<T>(string what, Func<Task<T>> work);
}
