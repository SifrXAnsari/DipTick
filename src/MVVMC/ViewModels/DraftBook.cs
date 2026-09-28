namespace DipTick.ViewModels;

/// <summary>
/// The unsent drafts of the kept chats, by chat id: one book for every tab of a run. A chat is open
/// in one tab at a time, and its draft goes with it from tab to tab, back with a closed tab, and on
/// to the next run. The draft of a chat nobody has spoken in yet, and a private chat's, are their
/// tab's own and never in here. The book starts with what the last run kept; the last run's new
/// chat's draft waits in it for the one tab that takes it.
/// </summary>
internal sealed class DraftBook
{
    private readonly Dictionary<string, string> _drafts = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();
    private string _newChat;

    /// <param name="kept">What the last run kept, by chat id; "" is the chat nobody had spoken in.</param>
    public DraftBook(IReadOnlyDictionary<string, string>? kept = null)
    {
        _newChat = kept?.GetValueOrDefault(string.Empty) ?? string.Empty;
        foreach (var (chat, text) in kept ?? new Dictionary<string, string>())
        {
            if (chat.Length > 0)
            {
                _drafts[chat] = text;
            }
        }
    }

    /// <summary>The draft of a kept chat, or "" when it has none.</summary>
    public string Get(string chat)
    {
        ArgumentNullException.ThrowIfNull(chat);
        lock (_lock)
        {
            return _drafts.GetValueOrDefault(chat, string.Empty);
        }
    }

    /// <summary>A kept chat's draft as it is now typed.</summary>
    public void Set(string chat, string text)
    {
        ArgumentException.ThrowIfNullOrEmpty(chat);
        ArgumentNullException.ThrowIfNull(text);
        lock (_lock)
        {
            _drafts[chat] = text;
        }
    }

    /// <summary>A deleted chat takes its draft with it.</summary>
    public void Remove(string chat)
    {
        ArgumentNullException.ThrowIfNull(chat);
        lock (_lock)
        {
            _drafts.Remove(chat);
        }
    }

    /// <summary>Every kept chat's draft as it stands.</summary>
    public IReadOnlyDictionary<string, string> All()
    {
        lock (_lock)
        {
            return new Dictionary<string, string>(_drafts, StringComparer.Ordinal);
        }
    }

    /// <summary>The last run's new chat's draft while no tab has taken it; "" once one has.</summary>
    public string NewChatWaiting
    {
        get
        {
            lock (_lock)
            {
                return _newChat;
            }
        }
    }

    /// <summary>Every draft's text, the new chat's still waiting included: what a paste file may be named in.</summary>
    public IReadOnlyList<string> Texts()
    {
        lock (_lock)
        {
            return [.. _drafts.Values, _newChat];
        }
    }

    /// <summary>
    /// The last run's new chat's draft, once: the tab that shows the new chat first takes it, and any
    /// other tab gets "". A run that brings its tabs back takes it to nothing, each tab having its own.
    /// </summary>
    public string TakeNewChat()
    {
        lock (_lock)
        {
            var draft = _newChat;
            _newChat = string.Empty;
            return draft;
        }
    }
}
