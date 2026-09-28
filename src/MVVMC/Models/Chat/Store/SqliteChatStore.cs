using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace DipTick.Models.Chat.Store;

/// <summary>
/// The session store as one SQLite file, <c>sessions.db</c> in DipTick's data folder (the folder
/// that holds the audit log; never <c>db.connection</c>, which is the database an agent may be
/// given). Microsoft.Data.Sqlite 10 with the bundled e_sqlite3:
/// nothing to install, one writer at a time, WAL on one host. <c>secure_delete</c> is on, so a
/// deleted session's text is overwritten in the file, not left in free pages.
/// </summary>
/// <remarks>
/// One connection per call, pooled: a store is shared between the window's UI thread and the
/// worker that streams an answer, and a SQLite connection may not be. Every write that touches
/// two rows runs in one transaction.
/// </remarks>
internal sealed partial class SqliteChatStore : IChatStore
{
    public const string FileName = "sessions.db";

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly string _connectionString;
    private bool _ready;

    public SqliteChatStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Location = System.IO.Path.GetFullPath(path);

        // Cache=Shared stays off, as Microsoft advises for WAL.
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Location,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
            DefaultTimeout = 30,
            Pooling = true,
        }.ToString();
    }

    public string Location { get; }

    /// <summary>The store in DipTick's data folder for this user.</summary>
    public static SqliteChatStore ForThisUser()
    {
        var folder = Harness.AuditLog.DataFolder();
        Directory.CreateDirectory(folder);
        return new SqliteChatStore(System.IO.Path.Combine(folder, FileName));
    }

    /// <summary>A session id: the UTC time, then four hex digits from the system's random source.</summary>
    public static string NewId(DateTimeOffset now)
    {
        Span<byte> random = stackalloc byte[2];
        RandomNumberGenerator.Fill(random);
        return string.Create(CultureInfo.InvariantCulture, $"{now.UtcDateTime:yyyyMMdd-HHmmss}-{Convert.ToHexStringLower(random)}");
    }

    public string Create(string person, string mode, string? title, IReadOnlyList<SeatRecord> seats)
    {
        ArgumentNullException.ThrowIfNull(person);
        ArgumentNullException.ThrowIfNull(mode);
        ArgumentNullException.ThrowIfNull(seats);
        var now = DateTimeOffset.UtcNow;
        var id = NewId(now);
        using var connection = Open();
        using var tx = connection.BeginTransaction();
        Run(connection, tx, "INSERT INTO session (id, title, person, mode, budget_json, spent_json, created_utc, updated_utc) VALUES ($id, $title, $person, $mode, '{}', '{\"turns\":0,\"tokens\":0,\"cost\":0}', $now, $now)",
            ("$id", id), ("$title", Cut(title ?? string.Empty, 60)), ("$person", person), ("$mode", mode), ("$now", Stamp(now)));
        foreach (var seat in seats)
        {
            InsertSeat(connection, tx, id, 0, seat);
        }

        tx.Commit();
        return id;
    }

    public IReadOnlyList<SessionInfo> List(bool includeClosed)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.id, s.title, s.person, s.mode, s.created_utc, s.updated_utc, s.status, s.lease_pid, s.lease_until_utc,
                   (SELECT count(*) FROM message m WHERE m.session_id = s.id AND m.seq IS NOT NULL), s.pinned
            FROM session s
            WHERE $all OR s.status = 'open'
            ORDER BY s.updated_utc DESC, s.id DESC
            """;
        command.Parameters.AddWithValue("$all", includeClosed);
        using var reader = command.ExecuteReader();
        var rows = new List<SessionInfo>();
        while (reader.Read())
        {
            rows.Add(ReadSession(reader));
        }

        return rows;
    }

    public SessionInfo? Find(string idOrLast)
    {
        ArgumentNullException.ThrowIfNull(idOrLast);
        if (string.Equals(idOrLast, "last", StringComparison.OrdinalIgnoreCase))
        {
            var open = List(includeClosed: false);
            return open.Count > 0 ? open[0] : null;
        }

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.id, s.title, s.person, s.mode, s.created_utc, s.updated_utc, s.status, s.lease_pid, s.lease_until_utc,
                   (SELECT count(*) FROM message m WHERE m.session_id = s.id AND m.seq IS NOT NULL), s.pinned
            FROM session s WHERE s.id = $id
            """;
        command.Parameters.AddWithValue("$id", idOrLast);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadSession(reader) : null;
    }

    public IReadOnlyList<StoredMessage> Messages(string sessionId)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, seq, round_n, author, kind, status, text, started_utc, ended_utc, step, tokens_in, tokens_out, tokens_estimated, cost_micros, reply_to
            FROM message WHERE session_id = $id ORDER BY id
            """;
        command.Parameters.AddWithValue("$id", sessionId);
        using var reader = command.ExecuteReader();
        var rows = new List<StoredMessage>();
        while (reader.Read())
        {
            rows.Add(new StoredMessage(
                reader.GetInt64(0),
                reader.IsDBNull(1) ? null : reader.GetInt32(1),
                reader.GetInt32(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                Parse(reader.GetString(7)),
                reader.IsDBNull(8) ? null : Parse(reader.GetString(8)),
                reader.GetInt32(9),
                reader.IsDBNull(10) ? null : reader.GetInt32(10),
                reader.IsDBNull(11) ? null : reader.GetInt32(11),
                reader.GetInt64(12) != 0,
                reader.IsDBNull(13) ? null : reader.GetInt64(13),
                reader.IsDBNull(14) ? null : reader.GetInt64(14)));
        }

        return rows;
    }

    public IReadOnlyList<SeatRecord> Table(string sessionId)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        using var connection = Open();
        using var command = connection.CreateCommand();

        // Each seat's newest row: whether it still sits, and with what.
        command.CommandText = """
            SELECT s.seat, s.name, s.provider, s.model, s.host, s.remote, s.lane_json, s.price_in, s.price_out, s.profile, s.seated
            FROM seat s
            WHERE s.session_id = $id
              AND s.from_seq = (SELECT max(t.from_seq) FROM seat t WHERE t.session_id = s.session_id AND t.seat = s.seat)
            ORDER BY s.position, s.seat
            """;
        command.Parameters.AddWithValue("$id", sessionId);
        using var reader = command.ExecuteReader();
        var rows = new List<SeatRecord>();
        while (reader.Read())
        {
            var seat = new SeatRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt64(5) != 0,
                JsonSerializer.Deserialize<List<string>>(reader.GetString(6), Json) ?? [],
                reader.IsDBNull(7) ? null : decimal.Parse(reader.GetString(7), CultureInfo.InvariantCulture),
                reader.IsDBNull(8) ? null : decimal.Parse(reader.GetString(8), CultureInfo.InvariantCulture),
                reader.GetString(9),
                reader.GetInt64(10) != 0);
            if (seat.Seated)
            {
                rows.Add(seat);
            }
        }

        return rows;
    }

    public void SetMode(string sessionId, string mode)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(mode);
        using var connection = Open();
        Run(connection, null, "UPDATE session SET mode = $mode, updated_utc = $now WHERE id = $id", ("$id", sessionId), ("$mode", mode), ("$now", Stamp(DateTimeOffset.UtcNow)));
    }

    public string Brief(string sessionId)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        using var connection = Open();
        return Scalar(connection, null, "SELECT brief FROM session WHERE id = $id", ("$id", sessionId)) as string ?? string.Empty;
    }

    public void SetBrief(string sessionId, string brief)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(brief);
        using var connection = Open();
        Run(connection, null, "UPDATE session SET brief = $brief, updated_utc = $now WHERE id = $id", ("$id", sessionId), ("$brief", brief), ("$now", Stamp(DateTimeOffset.UtcNow)));
    }

    public void RecordAct(string sessionId, StoredAct act)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(act);
        using var connection = Open();
        Run(connection, null, """
            INSERT INTO act (session_id, seq, seat, kind, target, text, accepted, code, audit_hash)
            VALUES ($id, $seq, $seat, $kind, $target, $text, $accepted, $code, $hash)
            """,
            ("$id", sessionId), ("$seq", act.Seq), ("$seat", act.Seat), ("$kind", act.Kind), ("$target", act.Target), ("$text", act.Text),
            ("$accepted", act.Accepted), ("$code", act.Code), ("$hash", act.AuditHash));
    }

    public long Spent(string? sessionId, DateTimeOffset? since)
    {
        using var connection = Open();
        return Convert.ToInt64(Scalar(connection, null, """
            SELECT coalesce(sum(cost_micros), 0) FROM message
            WHERE cost_micros IS NOT NULL AND ($id IS NULL OR session_id = $id) AND ($since IS NULL OR ended_utc >= $since)
            """, ("$id", sessionId), ("$since", since is { } from ? Stamp(from) : null)), CultureInfo.InvariantCulture);
    }

    public IReadOnlyList<StoredAct> Acts(string sessionId)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT seq, seat, kind, coalesce(target, ''), coalesce(text, ''), accepted, code, audit_hash FROM act WHERE session_id = $id AND motion_id IS NULL ORDER BY id";
        command.Parameters.AddWithValue("$id", sessionId);
        using var reader = command.ExecuteReader();
        var rows = new List<StoredAct>();
        while (reader.Read())
        {
            rows.Add(new StoredAct(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetInt64(5) != 0, reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetString(7)));
        }

        return rows;
    }

    public void RecordSeat(string sessionId, int fromSeq, SeatRecord seat)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(seat);
        using var connection = Open();
        using var tx = connection.BeginTransaction();
        InsertSeat(connection, tx, sessionId, fromSeq, seat);
        tx.Commit();
    }

    public IReadOnlyList<string> SeatIds(string sessionId)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT seat FROM seat WHERE session_id = $id ORDER BY seat";
        command.Parameters.AddWithValue("$id", sessionId);
        using var reader = command.ExecuteReader();
        var ids = new List<string>();
        while (reader.Read())
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    public int OpenRound(string sessionId, string mode, IReadOnlyList<IReadOnlyList<string>> plan)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(mode);
        ArgumentNullException.ThrowIfNull(plan);
        using var connection = Open();
        using var tx = connection.BeginTransaction();
        var n = Convert.ToInt32(Scalar(connection, tx, "SELECT coalesce(max(n), 0) + 1 FROM round WHERE session_id = $id", ("$id", sessionId)), CultureInfo.InvariantCulture);
        Run(connection, tx, "INSERT INTO round (session_id, n, mode, plan_json, opened_utc) VALUES ($id, $n, $mode, $plan, $now)",
            ("$id", sessionId), ("$n", n), ("$mode", mode), ("$plan", JsonSerializer.Serialize(plan, Json)), ("$now", Stamp(DateTimeOffset.UtcNow)));
        Touch(connection, tx, sessionId);
        tx.Commit();
        return n;
    }

    public void CloseRound(string sessionId, int round, string endReason)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(endReason);
        using var connection = Open();
        using var tx = connection.BeginTransaction();
        Run(connection, tx, "UPDATE round SET closed_utc = $now, end_reason = $why WHERE session_id = $id AND n = $n",
            ("$id", sessionId), ("$n", round), ("$why", endReason), ("$now", Stamp(DateTimeOffset.UtcNow)));
        Touch(connection, tx, sessionId);
        tx.Commit();
    }

    public long Begin(string sessionId, int round, int step, string author, string kind, string text, string status = MessageStatus.Streaming)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(author);
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(status);
        using var connection = Open();
        using var tx = connection.BeginTransaction();
        var now = Stamp(DateTimeOffset.UtcNow);
        var whole = status != MessageStatus.Streaming;
        Run(connection, tx, """
            INSERT INTO message (session_id, seq, round_n, step, author, kind, status, text, started_utc, ended_utc)
            VALUES ($id, CASE WHEN $whole THEN (SELECT coalesce(max(seq), 0) + 1 FROM message WHERE session_id = $id) END, $round, $step, $author, $kind, $status, $text, $now, CASE WHEN $whole THEN $now END)
            """,
            ("$id", sessionId), ("$whole", whole), ("$round", round), ("$step", step), ("$author", author), ("$kind", kind), ("$status", status), ("$text", text), ("$now", now));
        var rowId = Convert.ToInt64(Scalar(connection, tx, "SELECT last_insert_rowid()"), CultureInfo.InvariantCulture);
        Touch(connection, tx, sessionId);
        tx.Commit();
        return rowId;
    }

    public void Progress(long messageId, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        using var connection = Open();
        Run(connection, null, "UPDATE message SET text = $text WHERE id = $id AND status = 'streaming'", ("$id", messageId), ("$text", text));
    }

    public int End(long messageId, string status, string text, int? tokensIn = null, int? tokensOut = null, bool tokensEstimated = false, string? auditHash = null, long? costMicros = null, string? briefing = null)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(text);
        if (status == MessageStatus.Streaming)
        {
            throw new ArgumentException("A line ends in a final state, not streaming.", nameof(status));
        }

        using var connection = Open();
        using var tx = connection.BeginTransaction();
        var sessionId = Scalar(connection, tx, "SELECT session_id FROM message WHERE id = $id", ("$id", messageId)) as string
            ?? throw new InvalidOperationException($"No line {messageId} to end.");
        var existing = Scalar(connection, tx, "SELECT seq FROM message WHERE id = $id", ("$id", messageId));
        var seq = existing is long known
            ? (int)known
            : Convert.ToInt32(Scalar(connection, tx, "SELECT coalesce(max(seq), 0) + 1 FROM message WHERE session_id = $sid", ("$sid", sessionId)), CultureInfo.InvariantCulture);
        Run(connection, tx, """
            UPDATE message SET status = $status, text = $text, seq = $seq, tokens_in = $in, tokens_out = $out, tokens_estimated = $est, audit_hash = $hash,
                               cost_micros = $cost, briefing = $briefing, ended_utc = $now
            WHERE id = $id
            """,
            ("$id", messageId), ("$status", status), ("$text", text), ("$seq", seq), ("$in", tokensIn), ("$out", tokensOut), ("$est", tokensEstimated), ("$hash", auditHash),
            ("$cost", costMicros), ("$briefing", briefing), ("$now", Stamp(DateTimeOffset.UtcNow)));
        Touch(connection, tx, sessionId);
        tx.Commit();
        return seq;
    }

    public int Notice(string sessionId, int round, string text)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(text);
        var id = Begin(sessionId, round, 0, Author.DipTick, MessageKind.Notice, text, MessageStatus.Done);
        using var connection = Open();
        return Convert.ToInt32(Scalar(connection, null, "SELECT seq FROM message WHERE id = $id", ("$id", id)), CultureInfo.InvariantCulture);
    }

    public void Title(string sessionId, string title)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(title);
        using var connection = Open();
        Run(connection, null, "UPDATE session SET title = $title, updated_utc = $now WHERE id = $id", ("$id", sessionId), ("$title", Cut(title, 60)), ("$now", Stamp(DateTimeOffset.UtcNow)));
    }

    public void Pin(string sessionId, bool pinned)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        using var connection = Open();
        Run(connection, null, "UPDATE session SET pinned = $pinned WHERE id = $id", ("$id", sessionId), ("$pinned", pinned));
    }

    public void Close(string sessionId)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        using var connection = Open();
        Run(connection, null, "UPDATE session SET status = 'closed', lease_pid = NULL, lease_until_utc = NULL, updated_utc = $now WHERE id = $id", ("$id", sessionId), ("$now", Stamp(DateTimeOffset.UtcNow)));
    }

    public void Delete(string sessionId)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        using var connection = Open();
        using var tx = connection.BeginTransaction();

        // The cascades take every line with it; secure_delete overwrites what they held. The motion's
        // tables were made without a cascade, so their rows go by name, in the same transaction.
        Run(connection, tx, "DELETE FROM act WHERE session_id = $id", ("$id", sessionId));
        Run(connection, tx, "DELETE FROM proposal WHERE session_id = $id", ("$id", sessionId));
        Run(connection, tx, "DELETE FROM shown WHERE session_id = $id", ("$id", sessionId));
        Run(connection, tx, "DELETE FROM box_event WHERE session_id = $id", ("$id", sessionId));
        Run(connection, tx, "DELETE FROM session WHERE id = $id", ("$id", sessionId));
        tx.Commit();
    }

    public int? Lease(string sessionId, int pid, TimeSpan ttl)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        using var connection = Open();
        using var tx = connection.BeginTransaction(System.Data.IsolationLevel.Serializable);
        var now = DateTimeOffset.UtcNow;
        using var read = connection.CreateCommand();
        read.Transaction = tx;
        read.CommandText = "SELECT lease_pid, lease_until_utc FROM session WHERE id = $id AND status = 'open'";
        read.Parameters.AddWithValue("$id", sessionId);
        using (var reader = read.ExecuteReader())
        {
            if (!reader.Read())
            {
                throw new InvalidOperationException($"No open session {sessionId}.");
            }

            var holder = reader.IsDBNull(0) ? (int?)null : reader.GetInt32(0);
            var until = reader.IsDBNull(1) ? (DateTimeOffset?)null : Parse(reader.GetString(1));
            if (holder is { } other && other != pid && until is { } end && end > now)
            {
                return other;
            }
        }

        Run(connection, tx, "UPDATE session SET lease_pid = $pid, lease_until_utc = $until WHERE id = $id", ("$id", sessionId), ("$pid", pid), ("$until", Stamp(now + ttl)));
        tx.Commit();
        return null;
    }

    public void Renew(string sessionId, int pid, TimeSpan ttl)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        using var connection = Open();
        Run(connection, null, "UPDATE session SET lease_until_utc = $until WHERE id = $id AND lease_pid = $pid", ("$id", sessionId), ("$pid", pid), ("$until", Stamp(DateTimeOffset.UtcNow + ttl)));
    }

    public void Release(string sessionId, int pid)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        using var connection = Open();
        Run(connection, null, "UPDATE session SET lease_pid = NULL, lease_until_utc = NULL WHERE id = $id AND lease_pid = $pid", ("$id", sessionId), ("$pid", pid));
    }

    public IReadOnlyList<int> Recover(string sessionId)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        var left = new List<(long Id, string Text)>();
        using (var connection = Open())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, text FROM message WHERE session_id = $id AND status = 'streaming' ORDER BY id";
            command.Parameters.AddWithValue("$id", sessionId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                left.Add((reader.GetInt64(0), reader.GetString(1)));
            }
        }

        var cut = new List<int>();
        foreach (var (id, text) in left)
        {
            var seq = End(id, MessageStatus.Cut, text);
            cut.Add(seq);
            Notice(sessionId, 0, string.Create(CultureInfo.InvariantCulture, $"· #{seq} was interrupted: DipTick was closed while it was still arriving."));
        }

        return cut;
    }

    /// <summary>Lets the pooled connections go, so the file can be moved or deleted after.</summary>
    public void Dispose()
    {
        using var probe = new SqliteConnection(_connectionString);
        SqliteConnection.ClearPool(probe);
    }

    private static string Stamp(DateTimeOffset when) => when.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string stamp) => DateTimeOffset.Parse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private static string Cut(string text, int most) => text.Length <= most ? text : text[..most];

    private static SessionInfo ReadSession(SqliteDataReader reader)
    {
        var leaseUntil = reader.IsDBNull(8) ? (DateTimeOffset?)null : Parse(reader.GetString(8));
        var live = leaseUntil is { } until && until > DateTimeOffset.UtcNow && !reader.IsDBNull(7);
        return new SessionInfo(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            Parse(reader.GetString(4)),
            Parse(reader.GetString(5)),
            reader.GetString(6) == "open",
            reader.GetInt32(9),
            live ? reader.GetInt32(7) : null,
            reader.GetInt64(10) != 0);
    }

    /// <summary>A seat's row from a line on; a seat keeps the place at the table it first took, and a new one sits last.</summary>
    private static void InsertSeat(SqliteConnection connection, SqliteTransaction tx, string sessionId, int fromSeq, SeatRecord seat)
    {
        Run(connection, tx, """
            INSERT OR REPLACE INTO seat (session_id, seat, from_seq, name, provider, model, host, remote, lane_json, price_in, price_out, profile, seated, position)
            VALUES ($id, $seat, $from, $name, $provider, $model, $host, $remote, $lane, $in, $out, $profile, $seated,
                    coalesce((SELECT position FROM seat WHERE session_id = $id AND seat = $seat LIMIT 1),
                             (SELECT coalesce(max(position), -1) + 1 FROM seat WHERE session_id = $id)))
            """,
            ("$id", sessionId), ("$seat", seat.Seat), ("$from", fromSeq), ("$name", seat.Name), ("$provider", seat.Provider), ("$model", seat.Model),
            ("$host", seat.Host), ("$remote", seat.Remote), ("$lane", JsonSerializer.Serialize(seat.Lane, Json)),
            ("$in", seat.PriceIn?.ToString(CultureInfo.InvariantCulture)), ("$out", seat.PriceOut?.ToString(CultureInfo.InvariantCulture)),
            ("$profile", seat.Profile), ("$seated", seat.Seated));
    }

    private static void Touch(SqliteConnection connection, SqliteTransaction? tx, string sessionId) =>
        Run(connection, tx, "UPDATE session SET updated_utc = $now WHERE id = $id", ("$id", sessionId), ("$now", Stamp(DateTimeOffset.UtcNow)));

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Every SQL text in this file is a constant; values travel as parameters.")]
    private static void Run(SqliteConnection connection, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        command.ExecuteNonQuery();
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Every SQL text in this file is a constant; values travel as parameters.")]
    private static object? Scalar(SqliteConnection connection, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command.ExecuteScalar();
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "The schema is a resource compiled into this exe, not input.")]
    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            connection.Open();
            using (var pragma = connection.CreateCommand())
            {
                // Per connection: WAL is kept by the file, secure_delete is not.
                pragma.CommandText = "PRAGMA secure_delete = ON;";
                pragma.ExecuteNonQuery();
            }

            if (!_ready)
            {
                Migrate(connection);
                using var setup = connection.CreateCommand();
                setup.CommandText = "PRAGMA journal_mode = WAL;" + Schema();
                setup.ExecuteNonQuery();
                _ready = true;
            }

            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Steps a file an earlier DipTick made up to this one's schema before the schema runs. Version 1
    /// had no <c>session.pinned</c>. Version 2 had no chat table or brief of its own, and its message
    /// kinds had no <c>tool</c>. Version 3 kept only sources DipTick fetched or a provider cited. A new
    /// file, version 0, is made whole by the schema.
    /// </summary>
    private static void Migrate(SqliteConnection connection)
    {
        var version = Version(connection, null);
        if (version is 0 or >= 4)
        {
            return;
        }

        if (version < 3)
        {
            if (version == 1 && !HasColumn(connection, null, "session", "pinned"))
            {
                try
                {
                    Run(connection, null, "ALTER TABLE session ADD COLUMN pinned INTEGER NOT NULL DEFAULT 0");
                }
                catch (SqliteException) when (HasColumn(connection, null, "session", "pinned"))
                {
                    // Added by another process between the look and the change.
                }
            }

            StepToThree(connection);
        }

        StepToFour(connection);
    }

    /// <summary>
    /// Version 2 to 3. The new columns are added; the message table's kinds are a CHECK, which ALTER
    /// TABLE cannot change, so the table is made again by SQLite's own steps ("Making Other Kinds Of
    /// Table Schema Changes", sqlite.org/lang_altertable.html): foreign keys off; a transaction; a new
    /// table in the new form; the rows copied; the old table dropped and the new one renamed; its index
    /// and trigger made again; the foreign keys checked; commit; foreign keys on. It runs under BEGIN
    /// IMMEDIATE and reads the version again inside, so two processes opening an old file step it once.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "The columns added are constants of this method and the table's form is read from the schema compiled into this exe; nothing comes from input.")]
    private static void StepToThree(SqliteConnection connection)
    {
        Run(connection, null, "PRAGMA foreign_keys = OFF");
        try
        {
            using var tx = connection.BeginTransaction(deferred: false);
            if (Version(connection, tx) is not (1 or 2))
            {
                return;
            }

            foreach (var (table, column, form) in (ReadOnlySpan<(string, string, string)>)[
                ("seat", "profile", "profile TEXT NOT NULL DEFAULT ''"),
                ("seat", "seated", "seated INTEGER NOT NULL DEFAULT 1"),
                ("seat", "position", "position INTEGER NOT NULL DEFAULT 0"),
                ("session", "brief", "brief TEXT NOT NULL DEFAULT ''")])
            {
                if (!HasColumn(connection, tx, table, column))
                {
                    Run(connection, tx, $"ALTER TABLE {table} ADD COLUMN {form}");
                }
            }

            var schema = Schema();
            Run(connection, tx, MessageTable().Match(schema).Value.Replace("CREATE TABLE IF NOT EXISTS message (", "CREATE TABLE message_new (", StringComparison.Ordinal));
            Run(connection, tx, $"INSERT INTO message_new ({MessageColumns}) SELECT {MessageColumns} FROM message");
            Run(connection, tx, "DROP TABLE message");
            Run(connection, tx, "ALTER TABLE message_new RENAME TO message");
            Run(connection, tx, "CREATE INDEX IF NOT EXISTS message_session ON message (session_id, id)");
            Run(connection, tx, MessageTrigger().Match(schema).Value);
            if (Scalar(connection, tx, "SELECT count(*) FROM pragma_foreign_key_check") is long broken && broken > 0)
            {
                throw new InvalidDataException("sessions.db could not be stepped up: its lines no longer match its chats. Nothing was changed.");
            }

            Run(connection, tx, "PRAGMA user_version = 3");
            tx.Commit();
        }
        finally
        {
            Run(connection, null, "PRAGMA foreign_keys = ON");
        }
    }

    /// <summary>
    /// Version 3 to 4: a source may also be one a provider read or a search gave. Its kinds are a
    /// CHECK too, so the source table is made again by the same steps as <see cref="StepToThree"/>. A
    /// file with no source table has nothing to make again; the schema makes the table whole.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "The table's form is read from the schema compiled into this exe and its columns are a constant of this class; nothing comes from input.")]
    private static void StepToFour(SqliteConnection connection)
    {
        Run(connection, null, "PRAGMA foreign_keys = OFF");
        try
        {
            using var tx = connection.BeginTransaction(deferred: false);
            if (Version(connection, tx) != 3)
            {
                return;
            }

            if (Convert.ToInt32(Scalar(connection, tx, "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'source'"), CultureInfo.InvariantCulture) > 0)
            {
                Run(connection, tx, SourceTable().Match(Schema()).Value.Replace("CREATE TABLE IF NOT EXISTS source (", "CREATE TABLE source_new (", StringComparison.Ordinal));
                Run(connection, tx, $"INSERT INTO source_new ({SourceColumns}) SELECT {SourceColumns} FROM source");
                Run(connection, tx, "DROP TABLE source");
                Run(connection, tx, "ALTER TABLE source_new RENAME TO source");
                if (Scalar(connection, tx, "SELECT count(*) FROM pragma_foreign_key_check") is long broken && broken > 0)
                {
                    throw new InvalidDataException("sessions.db could not be stepped up: its sources no longer match its chats. Nothing was changed.");
                }
            }

            Run(connection, tx, "PRAGMA user_version = 4");
            tx.Commit();
        }
        finally
        {
            Run(connection, null, "PRAGMA foreign_keys = ON");
        }
    }

    /// <summary>Every column of the source table, in its order: what its rebuild copies.</summary>
    private const string SourceColumns = "session_id, id, seq, address, title, kind, fetched_utc, sha256, text";

    [System.Text.RegularExpressions.GeneratedRegex(@"CREATE TABLE IF NOT EXISTS source \(.*?\);", System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex SourceTable();

    /// <summary>Every column of the message table, in its order: what the rebuild copies.</summary>
    private const string MessageColumns =
        "id, session_id, seq, round_n, step, author, kind, addressed, status, visible_to, reply_to, motion_id, text, briefing, " +
        "tokens_in, tokens_out, tokens_estimated, cost_micros, provider_cost, audit_hash, started_utc, ended_utc";

    private static int Version(SqliteConnection connection, SqliteTransaction? tx) =>
        Convert.ToInt32(Scalar(connection, tx, "PRAGMA user_version"), CultureInfo.InvariantCulture);

    private static bool HasColumn(SqliteConnection connection, SqliteTransaction? tx, string table, string column) =>
        Convert.ToInt32(Scalar(connection, tx, "SELECT count(*) FROM pragma_table_info($table) WHERE name = $column", ("$table", table), ("$column", column)), CultureInfo.InvariantCulture) > 0;

    [System.Text.RegularExpressions.GeneratedRegex(@"CREATE TABLE IF NOT EXISTS message \(.*?\n\);", System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex MessageTable();

    [System.Text.RegularExpressions.GeneratedRegex(@"CREATE TRIGGER IF NOT EXISTS message_author .*?END;", System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex MessageTrigger();

    private static string Schema()
    {
        using var stream = typeof(SqliteChatStore).Assembly.GetManifestResourceStream("schema.sql")
            ?? throw new InvalidOperationException("The session store's schema is missing from the build.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
