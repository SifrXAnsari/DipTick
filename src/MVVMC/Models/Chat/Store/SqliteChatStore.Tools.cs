using System.Globalization;
using DipTick.Models.Harness;

namespace DipTick.Models.Chat.Store;

/// <summary>
/// What a chat keeps beside its lines, in the tables schema 3 made
/// for them: the chat's own rules, its tool calls, its sources, its files, and the connectors and
/// skills switched on in it.
/// </summary>
internal sealed partial class SqliteChatStore
{
    /// <summary>The most text a source keeps of a page: what an agent read of it, capped.</summary>
    public const int MostSourceText = 256 * 1024;

    public IReadOnlyList<ChatRule> ChatRules(string sessionId)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT rule, verdict FROM chat_policy WHERE session_id = $id ORDER BY set_utc, rule";
        command.Parameters.AddWithValue("$id", sessionId);
        using var reader = command.ExecuteReader();
        var rules = new List<ChatRule>();
        while (reader.Read())
        {
            rules.Add(new ChatRule(reader.GetString(0), reader.GetString(1) switch { "allow" => Verdict.Allow, "ask" => Verdict.Ask, _ => Verdict.Deny }));
        }

        return rules;
    }

    public void SetChatRule(string sessionId, ChatRule rule, string auditHash)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(auditHash);
        using var connection = Open();
        Run(connection, null, """
            INSERT OR REPLACE INTO chat_policy (session_id, rule, verdict, set_utc, audit_hash)
            VALUES ($id, $rule, $verdict, $now, $hash)
            """,
            ("$id", sessionId), ("$rule", rule.Rule), ("$verdict", VerdictWord(rule.Verdict)), ("$now", Stamp(DateTimeOffset.UtcNow)), ("$hash", auditHash));
    }

    public bool ClearChatRule(string sessionId, string rule)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(rule);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM chat_policy WHERE session_id = $id AND rule = $rule";
        command.Parameters.AddWithValue("$id", sessionId);
        command.Parameters.AddWithValue("$rule", rule);
        return command.ExecuteNonQuery() > 0;
    }

    public int ToolLine(string sessionId, int round, int step, string seat, string text, string status, long answerId)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(seat);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(status);
        using var connection = Open();
        using var tx = connection.BeginTransaction();
        var now = Stamp(DateTimeOffset.UtcNow);
        var seq = Convert.ToInt32(Scalar(connection, tx, "SELECT coalesce(max(seq), 0) + 1 FROM message WHERE session_id = $id", ("$id", sessionId)), CultureInfo.InvariantCulture);
        Run(connection, tx, """
            INSERT INTO message (session_id, seq, round_n, step, author, kind, status, text, reply_to, started_utc, ended_utc)
            VALUES ($id, $seq, $round, $step, $seat, 'tool', $status, $text, $reply, $now, $now)
            """,
            ("$id", sessionId), ("$seq", seq), ("$round", round), ("$step", step), ("$seat", seat), ("$status", status), ("$text", text), ("$reply", answerId), ("$now", now));
        Touch(connection, tx, sessionId);
        tx.Commit();
        return seq;
    }

    public void RecordToolCall(string sessionId, StoredToolCall call)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(call);
        using var connection = Open();
        Run(connection, null, """
            INSERT INTO tool_call (session_id, seq, seat, connector, tool, args_digest, status, audit_hash, started_utc, ended_utc)
            VALUES ($id, $seq, $seat, $connector, $tool, $digest, $status, $hash, $started, $ended)
            """,
            ("$id", sessionId), ("$seq", call.Seq), ("$seat", call.Seat), ("$connector", call.Connector), ("$tool", call.Tool), ("$digest", call.ArgsDigest),
            ("$status", call.Status), ("$hash", call.AuditHash), ("$started", Stamp(call.Started)), ("$ended", call.Ended is { } ended ? Stamp(ended) : null));
    }

    public IReadOnlyList<StoredToolCall> ToolCalls(string sessionId)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT seq, seat, connector, tool, args_digest, status, audit_hash, started_utc, ended_utc FROM tool_call WHERE session_id = $id ORDER BY id";
        command.Parameters.AddWithValue("$id", sessionId);
        using var reader = command.ExecuteReader();
        var calls = new List<StoredToolCall>();
        while (reader.Read())
        {
            calls.Add(new StoredToolCall(
                reader.IsDBNull(0) ? null : reader.GetInt32(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                Parse(reader.GetString(7)),
                reader.IsDBNull(8) ? null : Parse(reader.GetString(8))));
        }

        return calls;
    }

    public int AddSource(string sessionId, StoredSource source)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(source);
        using var connection = Open();
        using var tx = connection.BeginTransaction();

        // An address already kept keeps its number; what was read of it now replaces what was read before.
        if (Scalar(connection, tx, "SELECT id FROM source WHERE session_id = $id AND address = $address", ("$id", sessionId), ("$address", source.Address)) is long known)
        {
            Run(connection, tx, """
                UPDATE source SET title = CASE WHEN $title = '' THEN title ELSE $title END, fetched_utc = coalesce($fetched, fetched_utc),
                                  sha256 = coalesce($sha, sha256), text = coalesce($text, text),
                                  kind = CASE WHEN $kind = 'fetched' THEN 'fetched' ELSE kind END
                WHERE session_id = $id AND id = $n
                """,
                ("$id", sessionId), ("$n", known), ("$title", source.Title), ("$fetched", source.Fetched is { } f ? Stamp(f) : null), ("$sha", source.Sha256),
                ("$text", source.Text is { } t ? Cut(t, MostSourceText) : null), ("$kind", source.Kind));
            tx.Commit();
            return (int)known;
        }

        var number = Convert.ToInt32(Scalar(connection, tx, "SELECT coalesce(max(id), 0) + 1 FROM source WHERE session_id = $id", ("$id", sessionId)), CultureInfo.InvariantCulture);
        Run(connection, tx, """
            INSERT INTO source (session_id, id, seq, address, title, kind, fetched_utc, sha256, text)
            VALUES ($id, $n, $seq, $address, $title, $kind, $fetched, $sha, $text)
            """,
            ("$id", sessionId), ("$n", number), ("$seq", source.Seq), ("$address", source.Address), ("$title", source.Title), ("$kind", source.Kind),
            ("$fetched", source.Fetched is { } fetched ? Stamp(fetched) : null), ("$sha", source.Sha256), ("$text", source.Text is { } text ? Cut(text, MostSourceText) : null));
        tx.Commit();
        return number;
    }

    public IReadOnlyList<StoredSource> Sources(string sessionId, bool withText = false)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        using var connection = Open();
        using var command = connection.CreateCommand();
        if (withText)
        {
            command.CommandText = "SELECT id, seq, address, title, kind, fetched_utc, sha256, text FROM source WHERE session_id = $id ORDER BY id";
        }
        else
        {
            command.CommandText = "SELECT id, seq, address, title, kind, fetched_utc, sha256, NULL FROM source WHERE session_id = $id ORDER BY id";
        }

        command.Parameters.AddWithValue("$id", sessionId);
        using var reader = command.ExecuteReader();
        var sources = new List<StoredSource>();
        while (reader.Read())
        {
            sources.Add(new StoredSource(
                reader.GetInt32(0),
                reader.IsDBNull(1) ? null : reader.GetInt32(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : Parse(reader.GetString(5)),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7)));
        }

        return sources;
    }

    public void NumberSources(string sessionId, IReadOnlyList<int> ids, int seq)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(ids);
        using var connection = Open();
        using var tx = connection.BeginTransaction();

        // A source first found for an earlier answer keeps that answer's number.
        foreach (var id in ids.Distinct())
        {
            Run(connection, tx, "UPDATE source SET seq = $seq WHERE session_id = $session AND id = $id AND seq IS NULL", ("$session", sessionId), ("$id", id), ("$seq", seq));
        }

        tx.Commit();
    }

    public long AddAttachment(string sessionId, StoredAttachment attachment)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(attachment);
        using var connection = Open();
        using var tx = connection.BeginTransaction();
        Run(connection, tx, """
            INSERT INTO attachment (session_id, seq, author, name, type, size, sha256, bytes, saved_path, created_utc)
            VALUES ($id, $seq, $author, $name, $type, $size, $sha, $bytes, $saved, $created)
            """,
            ("$id", sessionId), ("$seq", attachment.Seq), ("$author", attachment.Author), ("$name", attachment.Name), ("$type", attachment.Type),
            ("$size", attachment.Size), ("$sha", attachment.Sha256), ("$bytes", attachment.Bytes), ("$saved", attachment.SavedPath), ("$created", Stamp(attachment.Created)));
        var id = Convert.ToInt64(Scalar(connection, tx, "SELECT last_insert_rowid()"), CultureInfo.InvariantCulture);
        tx.Commit();
        return id;
    }

    public void NumberAttachments(string sessionId, IReadOnlyList<long> ids, int seq)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(ids);
        using var connection = Open();
        using var tx = connection.BeginTransaction();
        foreach (var id in ids)
        {
            Run(connection, tx, "UPDATE attachment SET seq = $seq WHERE session_id = $session AND id = $id", ("$session", sessionId), ("$id", id), ("$seq", seq));
        }

        tx.Commit();
    }

    public IReadOnlyList<StoredAttachment> Attachments(string sessionId, bool withBytes = false)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        using var connection = Open();
        using var command = connection.CreateCommand();
        if (withBytes)
        {
            command.CommandText = "SELECT id, seq, author, name, type, size, sha256, bytes, saved_path, created_utc FROM attachment WHERE session_id = $id ORDER BY id";
        }
        else
        {
            command.CommandText = "SELECT id, seq, author, name, type, size, sha256, NULL, saved_path, created_utc FROM attachment WHERE session_id = $id ORDER BY id";
        }

        command.Parameters.AddWithValue("$id", sessionId);
        using var reader = command.ExecuteReader();
        var files = new List<StoredAttachment>();
        while (reader.Read())
        {
            files.Add(new StoredAttachment(
                reader.GetInt64(0),
                reader.IsDBNull(1) ? null : reader.GetInt32(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt64(5),
                reader.GetString(6),
                reader.IsDBNull(7) ? null : (byte[])reader.GetValue(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                Parse(reader.GetString(9))));
        }

        return files;
    }

    public IReadOnlyList<string> ChatConnectors(string sessionId)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT connector FROM chat_connector WHERE session_id = $id ORDER BY on_utc, connector";
        command.Parameters.AddWithValue("$id", sessionId);
        using var reader = command.ExecuteReader();
        var on = new List<string>();
        while (reader.Read())
        {
            on.Add(reader.GetString(0));
        }

        return on;
    }

    public void SetChatConnector(string sessionId, string connector, bool on)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(connector);
        using var connection = Open();
        if (on)
        {
            Run(connection, null, "INSERT OR IGNORE INTO chat_connector (session_id, connector, on_utc) VALUES ($id, $name, $now)", ("$id", sessionId), ("$name", connector), ("$now", Stamp(DateTimeOffset.UtcNow)));
        }
        else
        {
            Run(connection, null, "DELETE FROM chat_connector WHERE session_id = $id AND connector = $name", ("$id", sessionId), ("$name", connector));
        }
    }

    public IReadOnlyList<ChatSkill> ChatSkills(string sessionId)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT skill, hash FROM chat_skill WHERE session_id = $id ORDER BY on_utc, skill";
        command.Parameters.AddWithValue("$id", sessionId);
        using var reader = command.ExecuteReader();
        var on = new List<ChatSkill>();
        while (reader.Read())
        {
            on.Add(new ChatSkill(reader.GetString(0), reader.GetString(1)));
        }

        return on;
    }

    public void SetChatSkill(string sessionId, string skill, string? hash)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(skill);
        using var connection = Open();
        if (hash is not null)
        {
            Run(connection, null, "INSERT OR REPLACE INTO chat_skill (session_id, skill, hash, on_utc) VALUES ($id, $skill, $hash, $now)", ("$id", sessionId), ("$skill", skill), ("$hash", hash), ("$now", Stamp(DateTimeOffset.UtcNow)));
        }
        else
        {
            Run(connection, null, "DELETE FROM chat_skill WHERE session_id = $id AND skill = $skill", ("$id", sessionId), ("$skill", skill));
        }
    }

    private static string VerdictWord(Verdict verdict) => verdict switch
    {
        Verdict.Allow => "allow",
        Verdict.Ask => "ask",
        _ => "deny",
    };
}
