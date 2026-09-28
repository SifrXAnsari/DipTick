using System.Globalization;

namespace DipTick.Models.Chat.Store;

/// <summary>
/// The store with nothing on disk: for <c>--dry-run</c> (which leaves sessions.db
/// unchanged), for <c>chat.save = false</c>, and for private chats. It keeps the same rules as the
/// file: numbers on ending, cuts on recovery, one lease.
/// </summary>
internal sealed partial class MemoryChatStore : IChatStore
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<long, (string SessionId, Line Line)> _lines = [];
    private long _nextId;

    public string Location => "memory";

    public string Create(string person, string mode, string? title, IReadOnlyList<SeatRecord> seats)
    {
        ArgumentNullException.ThrowIfNull(person);
        ArgumentNullException.ThrowIfNull(mode);
        ArgumentNullException.ThrowIfNull(seats);
        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow;
            var id = SqliteChatStore.NewId(now);
            while (_sessions.ContainsKey(id))
            {
                id = SqliteChatStore.NewId(now);
            }

            var session = new Session(id, person, mode, now) { Title = Cut(title ?? string.Empty) };
            foreach (var seat in seats)
            {
                session.Sit(seat);
            }

            _sessions[id] = session;
            return id;
        }
    }

    public IReadOnlyList<SessionInfo> List(bool includeClosed)
    {
        lock (_lock)
        {
            return [.. _sessions.Values.Where(s => includeClosed || s.Open).OrderByDescending(s => s.Updated).ThenByDescending(s => s.Id, StringComparer.Ordinal).Select(Info)];
        }
    }

    public SessionInfo? Find(string idOrLast)
    {
        ArgumentNullException.ThrowIfNull(idOrLast);
        lock (_lock)
        {
            if (string.Equals(idOrLast, "last", StringComparison.OrdinalIgnoreCase))
            {
                var open = List(includeClosed: false);
                return open.Count > 0 ? open[0] : null;
            }

            return _sessions.TryGetValue(idOrLast, out var session) ? Info(session) : null;
        }
    }

    public IReadOnlyList<StoredMessage> Messages(string sessionId)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        lock (_lock)
        {
            // Nothing for a session that is not there, as the file answers: a deleted session has no lines.
            return _sessions.TryGetValue(sessionId, out var session)
                ? [.. session.Lines.Select(l => new StoredMessage(l.Id, l.Seq, l.Round, l.Author, l.Kind, l.Status, l.Text, l.Started, l.Ended, l.Step, l.TokensIn, l.TokensOut, l.TokensEstimated, l.CostMicros, l.ReplyTo))]
                : [];
        }
    }

    /// <summary>
    /// A copy of a chat to run a dry round on (<c>--dry-run</c>): its table, mode, brief, lines and
    /// acts, under the same id, in this store. Nothing done here reaches the chat it was copied from.
    /// </summary>
    public void Seed(SessionInfo info, IReadOnlyList<SeatRecord> table, string brief, IReadOnlyList<StoredMessage> lines, IReadOnlyList<StoredAct> acts)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(brief);
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(acts);
        lock (_lock)
        {
            var session = new Session(info.Id, info.Person, info.Mode, info.Created) { Title = info.Title, Brief = brief, Pinned = info.Pinned };
            foreach (var seat in table)
            {
                session.Sit(seat);
            }

            foreach (var line in lines)
            {
                var copy = new Line(++_nextId, line.Round, line.Step, line.Author, line.Kind, line.Text, line.Started)
                {
                    Status = line.Status,
                    Seq = line.Seq,
                    Ended = line.Ended,
                    TokensIn = line.TokensIn,
                    TokensOut = line.TokensOut,
                    TokensEstimated = line.TokensEstimated,
                    CostMicros = line.CostMicros,
                    ReplyTo = line.ReplyTo,
                };
                session.Lines.Add(copy);
                _lines[copy.Id] = (info.Id, copy);
            }

            session.LastSeq = lines.Select(l => l.Seq ?? 0).DefaultIfEmpty(0).Max();
            session.Rounds = lines.Select(l => l.Round).DefaultIfEmpty(0).Max();
            session.Acts.AddRange(acts);
            _sessions[info.Id] = session;
        }
    }

    public IReadOnlyList<string> SeatIds(string sessionId)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        lock (_lock)
        {
            return [.. Get(sessionId).Seats.Select(s => s.Seat).Order(StringComparer.Ordinal)];
        }
    }

    public void RecordSeat(string sessionId, int fromSeq, SeatRecord seat)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(seat);
        lock (_lock)
        {
            Get(sessionId).Sit(seat);
        }
    }

    public IReadOnlyList<SeatRecord> Table(string sessionId)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        lock (_lock)
        {
            return [.. Get(sessionId).Seats.Where(s => s.Seated)];
        }
    }

    public void SetMode(string sessionId, string mode)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(mode);
        lock (_lock)
        {
            var session = Get(sessionId);
            session.Mode = mode;
            session.Updated = DateTimeOffset.UtcNow;
        }
    }

    public string Brief(string sessionId)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        lock (_lock)
        {
            return Get(sessionId).Brief;
        }
    }

    public void SetBrief(string sessionId, string brief)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(brief);
        lock (_lock)
        {
            var session = Get(sessionId);
            session.Brief = brief;
            session.Updated = DateTimeOffset.UtcNow;
        }
    }

    public void RecordAct(string sessionId, StoredAct act)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(act);
        lock (_lock)
        {
            Get(sessionId).Acts.Add(act);
        }
    }

    public long Spent(string? sessionId, DateTimeOffset? since)
    {
        lock (_lock)
        {
            return _sessions.Values
                .Where(s => sessionId is null || s.Id == sessionId)
                .SelectMany(s => s.Lines)
                .Where(l => l.CostMicros is not null && (since is null || l.Ended >= since))
                .Sum(l => l.CostMicros!.Value);
        }
    }

    public IReadOnlyList<StoredAct> Acts(string sessionId)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        lock (_lock)
        {
            return _sessions.TryGetValue(sessionId, out var session) ? [.. session.Acts] : [];
        }
    }

    public int OpenRound(string sessionId, string mode, IReadOnlyList<IReadOnlyList<string>> plan)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(mode);
        ArgumentNullException.ThrowIfNull(plan);
        lock (_lock)
        {
            var session = Get(sessionId);
            session.Updated = DateTimeOffset.UtcNow;
            return ++session.Rounds;
        }
    }

    public void CloseRound(string sessionId, int round, string endReason)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(endReason);
        lock (_lock)
        {
            Get(sessionId).Updated = DateTimeOffset.UtcNow;
        }
    }

    public long Begin(string sessionId, int round, int step, string author, string kind, string text, string status = MessageStatus.Streaming)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(author);
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(status);
        lock (_lock)
        {
            var session = Get(sessionId);
            if (author != Author.Person && author != Author.DipTick && !session.Seats.Any(s => s.Seat == author))
            {
                throw new InvalidOperationException($"unknown author {author}");
            }

            var now = DateTimeOffset.UtcNow;
            var line = new Line(++_nextId, round, step, author, kind, text, now) { Status = status };
            if (status != MessageStatus.Streaming)
            {
                line.Seq = ++session.LastSeq;
                line.Ended = now;
            }

            session.Lines.Add(line);
            session.Updated = now;
            _lines[line.Id] = (sessionId, line);
            return line.Id;
        }
    }

    public void Progress(long messageId, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        lock (_lock)
        {
            if (_lines.TryGetValue(messageId, out var found) && found.Line.Status == MessageStatus.Streaming)
            {
                found.Line.Text = text;
            }
        }
    }

    public int End(long messageId, string status, string text, int? tokensIn = null, int? tokensOut = null, bool tokensEstimated = false, string? auditHash = null, long? costMicros = null, string? briefing = null)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(text);
        if (status == MessageStatus.Streaming)
        {
            throw new ArgumentException("A line ends in a final state, not streaming.", nameof(status));
        }

        lock (_lock)
        {
            if (!_lines.TryGetValue(messageId, out var found))
            {
                throw new InvalidOperationException($"No line {messageId} to end.");
            }

            var session = Get(found.SessionId);
            var line = found.Line;
            line.Status = status;
            line.Text = text;
            line.TokensIn = tokensIn;
            line.TokensOut = tokensOut;
            line.TokensEstimated = tokensEstimated;
            line.CostMicros = costMicros;
            line.Seq ??= ++session.LastSeq;
            line.Ended = DateTimeOffset.UtcNow;
            session.Updated = line.Ended.Value;
            return line.Seq.Value;
        }
    }

    public int Notice(string sessionId, int round, string text)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(text);
        var id = Begin(sessionId, round, 0, Author.DipTick, MessageKind.Notice, text, MessageStatus.Done);
        lock (_lock)
        {
            return _lines[id].Line.Seq!.Value;
        }
    }

    public void Title(string sessionId, string title)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(title);
        lock (_lock)
        {
            var session = Get(sessionId);
            session.Title = Cut(title);
            session.Updated = DateTimeOffset.UtcNow;
        }
    }

    public void Pin(string sessionId, bool pinned)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        lock (_lock)
        {
            Get(sessionId).Pinned = pinned;
        }
    }

    public void Close(string sessionId)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        lock (_lock)
        {
            var session = Get(sessionId);
            session.Open = false;
            session.LeasePid = null;
            session.LeaseUntil = null;
            session.Updated = DateTimeOffset.UtcNow;
        }
    }

    public void Delete(string sessionId)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        lock (_lock)
        {
            if (_sessions.Remove(sessionId, out var session))
            {
                foreach (var line in session.Lines)
                {
                    _lines.Remove(line.Id);
                }
            }
        }
    }

    public int? Lease(string sessionId, int pid, TimeSpan ttl)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        lock (_lock)
        {
            var session = Get(sessionId);
            if (!session.Open)
            {
                throw new InvalidOperationException($"No open session {sessionId}.");
            }

            var now = DateTimeOffset.UtcNow;
            if (session.LeasePid is { } other && other != pid && session.LeaseUntil is { } until && until > now)
            {
                return other;
            }

            session.LeasePid = pid;
            session.LeaseUntil = now + ttl;
            return null;
        }
    }

    public void Renew(string sessionId, int pid, TimeSpan ttl)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        lock (_lock)
        {
            if (_sessions.TryGetValue(sessionId, out var session) && session.LeasePid == pid)
            {
                session.LeaseUntil = DateTimeOffset.UtcNow + ttl;
            }
        }
    }

    public void Release(string sessionId, int pid)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        lock (_lock)
        {
            if (_sessions.TryGetValue(sessionId, out var session) && session.LeasePid == pid)
            {
                session.LeasePid = null;
                session.LeaseUntil = null;
            }
        }
    }

    public IReadOnlyList<int> Recover(string sessionId)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        List<Line> left;
        lock (_lock)
        {
            left = [.. Get(sessionId).Lines.Where(l => l.Status == MessageStatus.Streaming)];
        }

        var cut = new List<int>();
        foreach (var line in left)
        {
            var seq = End(line.Id, MessageStatus.Cut, line.Text);
            cut.Add(seq);
            Notice(sessionId, 0, string.Create(CultureInfo.InvariantCulture, $"· #{seq} was interrupted: DipTick was closed while it was still arriving."));
        }

        return cut;
    }

    public void Dispose()
    {
    }

    private static string Cut(string text) => text.Length <= 60 ? text : text[..60];

    private static SessionInfo Info(Session s) => new(s.Id, s.Title, s.Person, s.Mode, s.Created, s.Updated, s.Open, s.Lines.Count(l => l.Seq is not null),
        s.LeasePid is { } pid && s.LeaseUntil is { } until && until > DateTimeOffset.UtcNow ? pid : null, s.Pinned);

    private Session Get(string sessionId) => _sessions.TryGetValue(sessionId, out var session) ? session : throw new InvalidOperationException($"No session {sessionId}.");

    private sealed class Session(string id, string person, string mode, DateTimeOffset created)
    {
        public string Id { get; } = id;

        public string Person { get; } = person;

        public string Mode { get; set; } = mode;

        public string Brief { get; set; } = string.Empty;

        public DateTimeOffset Created { get; } = created;

        /// <summary>Every seat the chat has had, each as its newest record, in the order they first sat down.</summary>
        public List<SeatRecord> Seats { get; } = [];

        public List<StoredAct> Acts { get; } = [];

        /// <summary>The chat's own rules, by rule.</summary>
        public List<Harness.ChatRule> Rules { get; } = [];

        public List<StoredToolCall> ToolCalls { get; } = [];

        public List<StoredSource> Sources { get; } = [];

        public List<StoredAttachment> Files { get; } = [];

        public List<string> Connectors { get; } = [];

        public List<ChatSkill> Skills { get; } = [];

        /// <summary>A seat's newest record: a change keeps its place; a new seat sits last.</summary>
        public void Sit(SeatRecord seat)
        {
            var at = Seats.FindIndex(s => s.Seat == seat.Seat);
            if (at < 0)
            {
                Seats.Add(seat);
            }
            else
            {
                Seats[at] = seat;
            }
        }

        public string Title { get; set; } = string.Empty;

        public DateTimeOffset Updated { get; set; } = created;

        public bool Open { get; set; } = true;

        public bool Pinned { get; set; }

        public int? LeasePid { get; set; }

        public DateTimeOffset? LeaseUntil { get; set; }

        public int LastSeq { get; set; }

        public int Rounds { get; set; }

        public List<Line> Lines { get; } = [];
    }

    private sealed class Line(long id, int round, int step, string author, string kind, string text, DateTimeOffset started)
    {
        public long Id { get; } = id;

        public int Round { get; } = round;

        public int Step { get; } = step;

        public int? TokensIn { get; set; }

        public int? TokensOut { get; set; }

        public bool TokensEstimated { get; set; }

        public long? CostMicros { get; set; }

        public string Author { get; } = author;

        public string Kind { get; } = kind;

        public DateTimeOffset Started { get; } = started;

        public string Text { get; set; } = text;

        public string Status { get; set; } = MessageStatus.Streaming;

        public int? Seq { get; set; }

        public DateTimeOffset? Ended { get; set; }

        /// <summary>For a tool call's line, the row id of the answer it was made in.</summary>
        public long? ReplyTo { get; set; }
    }
}
