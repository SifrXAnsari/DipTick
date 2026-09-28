using DipTick.Models.Keys;

namespace DipTick.Models.Chat.Store;

/// <summary>Which store a run gets, how a session is named, and the seat record for the <c>main</c> section.</summary>
internal static class ChatStores
{
    /// <summary>The session's mode when the main model answers alone, in the mode's grammar.</summary>
    public const string SoloMain = "solo main";

    /// <summary>The id the main model answers under in the store; reserved, so no seat can take it.</summary>
    public const string MainSeat = "main";

    /// <summary>
    /// The main model's former id. A session kept under it goes on under it, because the store takes
    /// a line only from a seat the session has.
    /// </summary>
    public const string FormerMainSeat = "ai";

    /// <summary>The longest a session's title is, from its first words or given.</summary>
    public const int TitleLength = 60;

    /// <summary>The sessions of a process that keeps none (<c>chat.save</c> false): they last as long as it does.</summary>
    private static readonly Lazy<MemoryChatStore> Unkept = new(() => new MemoryChatStore());

    /// <summary>
    /// Private chats: in this process's memory only, whatever
    /// <c>chat.save</c> says, and apart from every other chat. A private tab's chat leaves it when
    /// the tab closes; the rest go when the process ends.
    /// </summary>
    private static readonly Lazy<MemoryChatStore> Private = new(() => new MemoryChatStore());

    /// <summary>
    /// Where this run's sessions are: <c>sessions.db</c> in the data folder, or, with
    /// <c>chat.save</c> false, the memory of this process, so a window's chats last as long as
    /// the window; a private chat's, this process's memory apart from the rest. A dry run reads
    /// the same store; the verbs write nothing to it then.
    /// </summary>
    public static IChatStore Open(MasterConfig config, bool isPrivate = false)
    {
        ArgumentNullException.ThrowIfNull(config);
        return isPrivate ? Private.Value : config.Chat.Save ? SqliteChatStore.ForThisUser() : Unkept.Value;
    }

    /// <summary>Whether a line's author is the main model, under its id now or its former one.</summary>
    public static bool IsMainSeat(string author) =>
        string.Equals(author, MainSeat, StringComparison.Ordinal) || string.Equals(author, FormerMainSeat, StringComparison.Ordinal);

    /// <summary>The id the main model answers under in this session: its former one when the session was kept under it.</summary>
    public static string MainSeatIn(IReadOnlyList<string> seatIds)
    {
        ArgumentNullException.ThrowIfNull(seatIds);
        return !seatIds.Contains(MainSeat, StringComparer.Ordinal) && seatIds.Contains(FormerMainSeat, StringComparer.Ordinal) ? FormerMainSeat : MainSeat;
    }

    /// <summary>A session's name from its first words: one line, cut to <see cref="TitleLength"/>.</summary>
    public static string TitleFrom(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var line = SafeText.Line(text.Split('\n')[0]).Trim();
        return line.Length <= TitleLength ? line : line[..(TitleLength - 1)] + "…";
    }

    /// <summary>
    /// The <c>main</c> section as it stands, for a session's seat history. Never its key. Its host is
    /// empty while it has no address; it is remote unless the address is this PC's own (localhost,
    /// 127.x, ::1), as <see cref="Uri.IsLoopback"/> reads it: a private chat's promise rests on it.
    /// </summary>
    public static SeatRecord MainSeatRecord(MasterConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var endpoint = config.MainProvider.Length == 0 ? null : Engines.Endpoint(config.MainProvider, config.MainEndpoint);
        var host = endpoint is null ? string.Empty : ApiKeys.HostOf(endpoint);
        var remote = endpoint is not null && !endpoint.IsLoopback;
        return new SeatRecord(MainSeat, MainSeat, config.MainProvider, config.MainModel, host, remote, ["model.call"], null, null);
    }
}
