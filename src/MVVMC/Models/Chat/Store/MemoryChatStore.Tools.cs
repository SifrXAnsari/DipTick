using DipTick.Models.Harness;

namespace DipTick.Models.Chat.Store;

/// <summary>What a chat keeps beside its lines, in memory: the same rules as the file's tables (<see cref="SqliteChatStore"/>).</summary>
internal sealed partial class MemoryChatStore
{
    /// <summary>
    /// For a dry run's copy (<see cref="Seed"/>): the chat's own rules and the connectors and skills
    /// on in it, so the copy decides and offers what the chat itself would.
    /// </summary>
    public void SeedRules(string sessionId, IReadOnlyList<ChatRule> rules, IReadOnlyList<string> connectors, IReadOnlyList<ChatSkill> skills)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(connectors);
        ArgumentNullException.ThrowIfNull(skills);
        lock (_lock)
        {
            var session = Get(sessionId);
            session.Rules.AddRange(rules);
            session.Connectors.AddRange(connectors);
            session.Skills.AddRange(skills);
        }
    }

    public IReadOnlyList<ChatRule> ChatRules(string sessionId)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        lock (_lock)
        {
            return _sessions.TryGetValue(sessionId, out var session) ? [.. session.Rules] : [];
        }
    }

    public void SetChatRule(string sessionId, ChatRule rule, string auditHash)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(auditHash);
        lock (_lock)
        {
            var rules = Get(sessionId).Rules;
            rules.RemoveAll(r => r.Rule == rule.Rule);
            rules.Add(rule);
        }
    }

    public bool ClearChatRule(string sessionId, string rule)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(rule);
        lock (_lock)
        {
            return Get(sessionId).Rules.RemoveAll(r => r.Rule == rule) > 0;
        }
    }

    public int ToolLine(string sessionId, int round, int step, string seat, string text, string status, long answerId)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(seat);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(status);
        var id = Begin(sessionId, round, step, seat, MessageKind.Tool, text, status);
        lock (_lock)
        {
            var line = _lines[id].Line;
            line.ReplyTo = answerId;
            return line.Seq!.Value;
        }
    }

    public void RecordToolCall(string sessionId, StoredToolCall call)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(call);
        lock (_lock)
        {
            Get(sessionId).ToolCalls.Add(call);
        }
    }

    public IReadOnlyList<StoredToolCall> ToolCalls(string sessionId)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        lock (_lock)
        {
            return _sessions.TryGetValue(sessionId, out var session) ? [.. session.ToolCalls] : [];
        }
    }

    public int AddSource(string sessionId, StoredSource source)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(source);
        lock (_lock)
        {
            var sources = Get(sessionId).Sources;
            var text = source.Text is { } t && t.Length > SqliteChatStore.MostSourceText ? t[..SqliteChatStore.MostSourceText] : source.Text;
            var at = sources.FindIndex(s => s.Address == source.Address);
            if (at >= 0)
            {
                var known = sources[at];
                sources[at] = known with
                {
                    Title = source.Title.Length > 0 ? source.Title : known.Title,
                    Fetched = source.Fetched ?? known.Fetched,
                    Sha256 = source.Sha256 ?? known.Sha256,
                    Text = text ?? known.Text,
                    Kind = source.Kind == "fetched" ? "fetched" : known.Kind,
                };
                return known.Id;
            }

            var number = sources.Count == 0 ? 1 : sources.Max(s => s.Id) + 1;
            sources.Add(source with { Id = number, Text = text });
            return number;
        }
    }

    public IReadOnlyList<StoredSource> Sources(string sessionId, bool withText = false)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        lock (_lock)
        {
            return _sessions.TryGetValue(sessionId, out var session)
                ? [.. session.Sources.OrderBy(s => s.Id).Select(s => withText ? s : s with { Text = null })]
                : [];
        }
    }

    public void NumberSources(string sessionId, IReadOnlyList<int> ids, int seq)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(ids);
        lock (_lock)
        {
            var sources = Get(sessionId).Sources;
            for (var i = 0; i < sources.Count; i++)
            {
                if (ids.Contains(sources[i].Id) && sources[i].Seq is null)
                {
                    sources[i] = sources[i] with { Seq = seq };
                }
            }
        }
    }

    public long AddAttachment(string sessionId, StoredAttachment attachment)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(attachment);
        lock (_lock)
        {
            var id = ++_nextId;
            Get(sessionId).Files.Add(attachment with { Id = id });
            return id;
        }
    }

    public void NumberAttachments(string sessionId, IReadOnlyList<long> ids, int seq)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(ids);
        lock (_lock)
        {
            var files = Get(sessionId).Files;
            for (var i = 0; i < files.Count; i++)
            {
                if (ids.Contains(files[i].Id))
                {
                    files[i] = files[i] with { Seq = seq };
                }
            }
        }
    }

    public IReadOnlyList<StoredAttachment> Attachments(string sessionId, bool withBytes = false)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        lock (_lock)
        {
            return _sessions.TryGetValue(sessionId, out var session)
                ? [.. session.Files.Select(f => withBytes ? f : f with { Bytes = null })]
                : [];
        }
    }

    public IReadOnlyList<string> ChatConnectors(string sessionId)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        lock (_lock)
        {
            return _sessions.TryGetValue(sessionId, out var session) ? [.. session.Connectors] : [];
        }
    }

    public void SetChatConnector(string sessionId, string connector, bool on)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(connector);
        lock (_lock)
        {
            var connectors = Get(sessionId).Connectors;
            connectors.Remove(connector);
            if (on)
            {
                connectors.Add(connector);
            }
        }
    }

    public IReadOnlyList<ChatSkill> ChatSkills(string sessionId)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        lock (_lock)
        {
            return _sessions.TryGetValue(sessionId, out var session) ? [.. session.Skills] : [];
        }
    }

    public void SetChatSkill(string sessionId, string skill, string? hash)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(skill);
        lock (_lock)
        {
            var skills = Get(sessionId).Skills;
            skills.RemoveAll(s => s.Skill == skill);
            if (hash is not null)
            {
                skills.Add(new ChatSkill(skill, hash));
            }
        }
    }
}
