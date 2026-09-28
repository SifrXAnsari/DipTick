namespace DipTick.Models.Chat.Store;

/// <summary>A session as the list shows it; <paramref name="Pinned"/> keeps it at the top of the rail.</summary>
internal sealed record SessionInfo(
    string Id,
    string Title,
    string Person,
    string Mode,
    DateTimeOffset Created,
    DateTimeOffset Updated,
    bool Open,
    int Messages,
    int? LeasePid,
    bool Pinned = false);

/// <summary>
/// One stored line: who wrote it, its number once final, its state, the step of its round it came in,
/// and for an answer what it used: tokens read and written (estimated when the provider did not say)
/// and its cost in millionths of the currency, where a price is known.
/// </summary>
internal sealed record StoredMessage(
    long Id,
    int? Seq,
    int Round,
    string Author,
    string Kind,
    string Status,
    string Text,
    DateTimeOffset Started,
    DateTimeOffset? Ended,
    int Step = 0,
    int? TokensIn = null,
    int? TokensOut = null,
    bool TokensEstimated = false,
    long? CostMicros = null,
    long? ReplyTo = null);

/// <summary>
/// One tool call an agent made: its answer's number once it has
/// one, who made it, the connector and tool, a SHA-256 of its arguments (never the arguments), how it
/// ended, and the audit line that allowed or refused it.
/// </summary>
internal sealed record StoredToolCall(int? Seq, string Seat, string? Connector, string Tool, string ArgsDigest, string Status, string? AuditHash, DateTimeOffset Started, DateTimeOffset? Ended);

/// <summary>
/// A source of a chat's answers: its number in the chat, the answer it came
/// with, its address and title, whether DipTick fetched it or a provider cited it, when, the SHA-256
/// of what was read, and the readable text, capped.
/// </summary>
internal sealed record StoredSource(int Id, int? Seq, string Address, string Title, string Kind, DateTimeOffset? Fetched, string? Sha256, string? Text);

/// <summary>How a chat came by a source.</summary>
internal static class SourceKind
{
    /// <summary>DipTick fetched the page itself.</summary>
    public const string Fetched = "fetched";

    /// <summary>A provider's own tool read the page, and an answer cites it.</summary>
    public const string Cited = "cited";

    /// <summary>A provider's own tool found or read the page; DipTick did not fetch it.</summary>
    public const string Provider = "provider";

    /// <summary>A search gave it, with passages; nobody fetched it.</summary>
    public const string Search = "search";

    /// <summary>The kind in a person's words, for the Sources under an answer.</summary>
    public static string Words(string kind) => kind switch
    {
        Fetched => "fetched by DipTick",
        Cited => "cited by the provider",
        Provider => "read by the provider",
        Search => "a search result, not fetched",
        _ => kind,
    };
}

/// <summary>A file brought into a chat: by the person or an agent, its name, media type, size, SHA-256, its bytes, and where it was saved.</summary>
internal sealed record StoredAttachment(long Id, int? Seq, string Author, string Name, string Type, long Size, string Sha256, byte[]? Bytes, string? SavedPath, DateTimeOffset Created);

/// <summary>A skill switched on in a chat, with the hash of the skill the person approved.</summary>
internal sealed record ChatSkill(string Skill, string Hash);

/// <summary>
/// An agent's seat at a chat's table from a line on: the name it is addressed by, the model it answers
/// with, where its words go, and the set-up agent it is (<paramref name="Profile"/>; empty in a chat kept
/// before profiles, where it is the seat's own id). <paramref name="Seated"/> false: it left the
/// table from that line. Never a key.
/// </summary>
internal sealed record SeatRecord(
    string Seat,
    string Name,
    string Provider,
    string Model,
    string Host,
    bool Remote,
    IReadOnlyList<string> Lane,
    decimal? PriceIn,
    decimal? PriceOut,
    string Profile = "",
    bool Seated = true)
{
    /// <summary>The set-up agent it is: its profile, or in a chat kept before profiles, its own id.</summary>
    public string Agent => Profile.Length > 0 ? Profile : Seat;
}

/// <summary>
/// An act an agent's answer put in a chat: the answer's number, who wrote it, its
/// kind, to whom or what, its words, whether it counted and why not, and the audit line that records
/// it; no act has an effect without that line.
/// </summary>
internal sealed record StoredAct(int Seq, string Seat, string Kind, string Target, string Text, bool Accepted, string? Code, string AuditHash);

/// <summary>The states a stored line can be in.</summary>
internal static class MessageStatus
{
    public const string Streaming = "streaming";
    public const string Done = "done";
    public const string Passed = "passed";
    public const string Cut = "cut";
    public const string Stopped = "stopped";
    public const string Failed = "failed";
    public const string Denied = "denied";
    public const string Repeat = "repeat";
    public const string Refused = "refused";
}

/// <summary>Who wrote a line, apart from a seat id.</summary>
internal static class Author
{
    public const string Person = "person";
    public const string DipTick = "diptick";
}

/// <summary>What kind of line it is.</summary>
internal static class MessageKind
{
    public const string Say = "say";

    /// <summary>The person passing the floor back to the agents with no new words (<c>chat pass</c>).</summary>
    public const string Pass = "pass";

    public const string Answer = "answer";
    public const string Notice = "notice";

    /// <summary>A tool call an agent made in its turn, and what came back, folded under its answer.</summary>
    public const string Tool = "tool";
}

/// <summary>
/// Where sessions live: every line of every chat, so a session can be
/// brought back after the window closes. The audit log holds no content; this holds nothing
/// else. Never a key, a key variable's value, a request body or a nonce.
/// </summary>
/// <remarks>
/// A turn's line is begun as <c>streaming</c> with no number, its text updated as it arrives, and
/// numbered when it ends, so numbers follow the order answers finished. No line's text changes
/// after it is final. A session that opens with <c>streaming</c> lines left by a crash cuts them
/// and says so. One writer at a time: a round takes the session's lease, renewed every 30 s,
/// and a lease past its time is taken over.
/// </remarks>
internal interface IChatStore : IDisposable
{
    /// <summary>The file, or "memory".</summary>
    string Location { get; }

    /// <summary>A new open session; returns its id (yyyyMMdd-HHmmss-xxxx, UTC).</summary>
    string Create(string person, string mode, string? title, IReadOnlyList<SeatRecord> seats);

    /// <summary>Sessions, newest first; closed ones only when asked for.</summary>
    IReadOnlyList<SessionInfo> List(bool includeClosed);

    /// <summary>By id, or "last" for the newest open session; null when there is none.</summary>
    SessionInfo? Find(string idOrLast);

    /// <summary>Every line of a session in the order it was written.</summary>
    IReadOnlyList<StoredMessage> Messages(string sessionId);

    /// <summary>A change to the table from this line on: an agent sits down, leaves, or answers with another model.</summary>
    void RecordSeat(string sessionId, int fromSeq, SeatRecord seat);

    /// <summary>The ids of every seat the session has ever had, in order of name.</summary>
    IReadOnlyList<string> SeatIds(string sessionId);

    /// <summary>Who sits at the chat's table now, in the order they sat down.</summary>
    IReadOnlyList<SeatRecord> Table(string sessionId);

    /// <summary>The chat's mode from now on, in the mode's grammar.</summary>
    void SetMode(string sessionId, string mode);

    /// <summary>The chat's own instructions for its agents; empty for none.</summary>
    string Brief(string sessionId);

    void SetBrief(string sessionId, string brief);

    /// <summary>Keeps an act an agent's answer put in the chat, as it was judged.</summary>
    void RecordAct(string sessionId, StoredAct act);

    /// <summary>Every act kept for the chat, oldest first.</summary>
    IReadOnlyList<StoredAct> Acts(string sessionId);

    /// <summary>What answers have cost, in millionths of the currency: one chat's, or every chat's; since a moment, or ever. Answers with no price count nothing.</summary>
    long Spent(string? sessionId, DateTimeOffset? since);

    /// <summary>Opens the next round; returns its number.</summary>
    int OpenRound(string sessionId, string mode, IReadOnlyList<IReadOnlyList<string>> plan);

    void CloseRound(string sessionId, int round, string endReason);

    /// <summary>A line that has started; returns its row id. Status <c>streaming</c> unless it is already whole.</summary>
    long Begin(string sessionId, int round, int step, string author, string kind, string text, string status = MessageStatus.Streaming);

    /// <summary>More of a streaming line's text.</summary>
    void Progress(long messageId, string text);

    /// <summary>A line is final: its status, its whole text, what it used and cost, and the briefing its writer was given; returns its number.</summary>
    int End(long messageId, string status, string text, int? tokensIn = null, int? tokensOut = null, bool tokensEstimated = false, string? auditHash = null, long? costMicros = null, string? briefing = null);

    /// <summary>A line from DipTick itself, whole at once; returns its number.</summary>
    int Notice(string sessionId, int round, string text);

    void Title(string sessionId, string title);

    /// <summary>Pins a session to the top of the list, or lets it go back among the rest.</summary>
    void Pin(string sessionId, bool pinned);

    void Close(string sessionId);

    /// <summary>Removes a session and every line of it.</summary>
    void Delete(string sessionId);

    /// <summary>Takes the lease for <paramref name="ttl"/>; returns null when taken, else the pid that holds it.</summary>
    int? Lease(string sessionId, int pid, TimeSpan ttl);

    void Renew(string sessionId, int pid, TimeSpan ttl);

    void Release(string sessionId, int pid);

    /// <summary>Cuts the streaming lines a crash left, with a notice each; returns the numbers given.</summary>
    IReadOnlyList<int> Recover(string sessionId);

    /// <summary>The chat's own rules, oldest first.</summary>
    IReadOnlyList<Harness.ChatRule> ChatRules(string sessionId);

    /// <summary>Keeps one of the chat's own rules, in place of any it had for the same rule, with the audit line that allowed the change.</summary>
    void SetChatRule(string sessionId, Harness.ChatRule rule, string auditHash);

    /// <summary>Takes one of the chat's own rules away; false when it had none.</summary>
    bool ClearChatRule(string sessionId, string rule);

    /// <summary>A tool call's line, whole at once, under the answer <paramref name="answerId"/> (a row id); returns its number.</summary>
    int ToolLine(string sessionId, int round, int step, string seat, string text, string status, long answerId);

    /// <summary>Keeps a tool call's record.</summary>
    void RecordToolCall(string sessionId, StoredToolCall call);

    /// <summary>Every tool call kept for the chat, oldest first.</summary>
    IReadOnlyList<StoredToolCall> ToolCalls(string sessionId);

    /// <summary>Keeps a source; returns its number in the chat, the same number when the address was already one.</summary>
    int AddSource(string sessionId, StoredSource source);

    /// <summary>Every source of the chat, by number; with <paramref name="withText"/>, their text too.</summary>
    IReadOnlyList<StoredSource> Sources(string sessionId, bool withText = false);

    /// <summary>Gives sources found during an answer the answer's number, once it has one.</summary>
    void NumberSources(string sessionId, IReadOnlyList<int> ids, int seq);

    /// <summary>Keeps a file brought into the chat; returns its id.</summary>
    long AddAttachment(string sessionId, StoredAttachment attachment);

    /// <summary>Gives files brought in before their line was numbered the number of the line they came with.</summary>
    void NumberAttachments(string sessionId, IReadOnlyList<long> ids, int seq);

    /// <summary>Every file of the chat, oldest first; with <paramref name="withBytes"/>, their bytes too.</summary>
    IReadOnlyList<StoredAttachment> Attachments(string sessionId, bool withBytes = false);

    /// <summary>The connectors on in the chat.</summary>
    IReadOnlyList<string> ChatConnectors(string sessionId);

    /// <summary>Switches a connector on or off in the chat.</summary>
    void SetChatConnector(string sessionId, string connector, bool on);

    /// <summary>The skills on in the chat, each with the hash the person approved.</summary>
    IReadOnlyList<ChatSkill> ChatSkills(string sessionId);

    /// <summary>Switches a skill on (with the hash approved) or off (null) in the chat.</summary>
    void SetChatSkill(string sessionId, string skill, string? hash);
}
