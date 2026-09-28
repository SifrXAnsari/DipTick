using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DipTick.Models.Protocol;

/// <summary>
/// Reads the one <c>diptick-acts</c> block at the end of a finished answer, and nothing else.
/// Pure: text in, <see cref="ActBlock"/> out.
/// </summary>
/// <remarks>
/// <para>
/// Prose is never read. Only one place counts: the last fenced block, opened at column 0 by
/// exactly <c>```diptick-acts</c>, closed, with only whitespace after it. A block anywhere
/// else, including an earlier <c>diptick-acts</c> block, is prose.
/// </para>
/// <para>
/// The JSON is strict: no comments, no trailing commas, depth at most 4, no duplicated key,
/// top level exactly <c>{"acts":[...]}</c> with at most 20 acts, no unknown act and no unknown
/// field (a <c>"seat"</c> field is unknown: authorship comes from the turn). A format error voids
/// the whole block; the rules that need the chat's state (phase, versions, quotes, flaws,
/// evidence) are checked later and void one act at a time.
/// </para>
/// </remarks>
internal static partial class ActParser
{
    public const string Fence = "diptick-acts";
    public const int MaxBlockBytes = 16 * 1024;
    public const int MaxActs = 20;
    public const int MaxBoxes = 5;
    public const int MaxAsks = 2;

    private static readonly JsonDocumentOptions Strict = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 4,
    };

    // Every field each act may carry, and which are required.
    private static readonly Dictionary<string, (string[] Required, string[] Optional)> Fields = new(StringComparer.Ordinal)
    {
        ["propose"] = (["act", "title", "clauses"], ["items", "basedOn"]),
        ["critique"] = (["act", "p", "v", "h", "where", "quote", "flaw"], []),
        ["amend"] = (["act", "p", "v", "h", "clauses", "why"], ["items"]),
        ["agree"] = (["act", "p", "v", "h", "where", "quote", "risk"], []),
        ["object"] = (["act", "p", "v", "h", "where", "reason", "redraft"], ["change"]),
        ["box"] = (["act", "text"], ["owner"]),
        ["tick"] = (["act", "b", "msg", "quote"], []),
        ["confirm"] = (["act", "t", "quote", "checked"], []),
        ["dispute"] = (["act", "t", "reason"], []),
        ["ask"] = (["act", "to", "q"], []),
    };

    /// <summary>The block at the end of <paramref name="answer"/>, if the answer finished.</summary>
    /// <param name="answer">The seat's own stored answer, exactly as streamed.</param>
    /// <param name="finished">Whether the answer ended done (or with [hand back]); a cut, stopped, failed or refused answer's acts are void.</param>
    public static ActBlock Parse(string answer, bool finished)
    {
        ArgumentNullException.ThrowIfNull(answer);
        var (found, body) = FindBlock(answer.Replace("\r\n", "\n", StringComparison.Ordinal));
        if (!found)
        {
            return ActBlock.None;
        }

        if (!finished)
        {
            return ActBlock.Void(Codes.CutAnswer);
        }

        if (body is null)
        {
            return ActBlock.Void(Codes.BadJson);
        }

        if (Encoding.UTF8.GetByteCount(body) > MaxBlockBytes)
        {
            return ActBlock.Void(Codes.TooLarge);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body, Strict);
        }
        catch (JsonException)
        {
            return ActBlock.Void(Codes.BadJson);
        }

        using (document)
        {
            if (DuplicateKey(document.RootElement))
            {
                return ActBlock.Void(Codes.BadJson);
            }

            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 1
                || !root.TryGetProperty("acts", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                return ActBlock.Void(Codes.BadJson);
            }

            if (list.GetArrayLength() > MaxActs)
            {
                return ActBlock.Void(Codes.TooLarge);
            }

            var acts = new List<ParsedAct>();
            foreach (var item in list.EnumerateArray())
            {
                var (act, error) = Read(item);
                if (error is not null)
                {
                    return ActBlock.Void(error);
                }

                acts.Add(new ParsedAct(act, act!.Kind, null));
            }

            return new ActBlock(true, null, Limit(Conflicts(acts)));
        }
    }

    /// <summary>
    /// An answer as it is quoted to another agent: its words with the
    /// act block taken off, whole or broken, since a block must never be passed on to be read again;
    /// DipTick says what it read of the block in its own marker instead. A block DipTick would not
    /// read (not last, words after it) is prose and stays.
    /// </summary>
    public static string Prose(string answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        var text = answer.Replace("\r\n", "\n", StringComparison.Ordinal);
        var (found, _, openLine) = Locate(text);
        return found ? string.Join('\n', text.Split('\n')[..openLine]).TrimEnd() : text;
    }

    /// <summary>
    /// Where the block is: not there (<c>Found</c> false), there but broken (<c>Body</c> null:
    /// our fence opened and never closed), or its text. Only the last top-level fenced block
    /// counts, only if it is ours and nothing but whitespace follows it. Fences open and close at
    /// column 0 only; a closing fence is the opener's character, at least as many times, alone.
    /// </summary>
    private static (bool Found, string? Body) FindBlock(string text)
    {
        var (found, body, _) = Locate(text);
        return (found, body);
    }

    /// <summary><see cref="FindBlock"/>, with the line the block opens on.</summary>
    private static (bool Found, string? Body, int OpenLine) Locate(string text)
    {
        var lines = text.Split('\n');
        string? fence = null;
        var openedAt = -1;
        var info = string.Empty;
        (int Open, int Close, string Info)? last = null;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (fence is null)
            {
                var m = Opener().Match(line);
                if (m.Success)
                {
                    fence = m.Groups["fence"].Value;
                    info = m.Groups["info"].Value.Trim();
                    openedAt = i;
                }
            }
            else
            {
                var bare = line.TrimEnd();
                if (bare.Length >= fence.Length && bare.All(c => c == fence[0]))
                {
                    last = (openedAt, i, info);
                    fence = null;
                }
            }
        }

        if (fence is not null)
        {
            return string.Equals(info, Fence, StringComparison.Ordinal) ? (true, null, openedAt) : (false, null, -1);
        }

        if (last is not { } block || !string.Equals(block.Info, Fence, StringComparison.Ordinal))
        {
            return (false, null, -1);
        }

        for (var i = block.Close + 1; i < lines.Length; i++)
        {
            if (lines[i].Trim().Length > 0)
            {
                return (false, null, -1);
            }
        }

        return (true, string.Join('\n', lines[(block.Open + 1)..block.Close]), block.Open);
    }

    private static bool DuplicateKey(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!seen.Add(property.Name) || DuplicateKey(property.Value))
                    {
                        return true;
                    }
                }

                return false;
            case JsonValueKind.Array:
                return element.EnumerateArray().Any(DuplicateKey);
            default:
                return false;
        }
    }

    private static (Act? Act, string? Error) Read(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("act", out var kindElement) || kindElement.ValueKind != JsonValueKind.String)
        {
            return (null, Codes.BadJson);
        }

        var kind = kindElement.GetString()!;
        if (!Fields.TryGetValue(kind, out var spec))
        {
            return (null, Codes.UnknownAct);
        }

        var names = item.EnumerateObject().Select(p => p.Name).ToList();
        if (names.Any(n => !spec.Required.Contains(n) && !spec.Optional.Contains(n)))
        {
            return (null, Codes.UnknownField);
        }

        if (spec.Required.Any(r => !names.Contains(r)))
        {
            return (null, Codes.MissingField);
        }

        try
        {
            Act act = kind switch
            {
                "propose" => new ProposeAct(Text(item, "title", 120), List(item, "clauses", 1, 40, 600), Optional(item, "items") is { } items ? List(items, 0, 20, 200) : [], BasedOn(item)),
                "critique" => new CritiqueAct(Ref(item), Where(item), Text(item, "quote", 400), Text(item, "flaw", 400)),
                "amend" => new AmendAct(Ref(item), List(item, "clauses", 1, 40, 600), Optional(item, "items") is { } amended ? List(amended, 0, 20, 200) : [], Text(item, "why", 400)),
                "agree" => new AgreeAct(Ref(item), Where(item), Text(item, "quote", 400), Text(item, "risk", 400)),
                "object" => new ObjectAct(Ref(item), Where(item), Text(item, "reason", 400), Optional(item, "change") is { } change ? Text(change, 400) : null, Bool(item, "redraft")),
                "box" => new BoxAct(Text(item, "text", 200), Optional(item, "owner") is { } owner ? Id(owner, SeatPattern()) : null),
                "tick" => new TickAct(Id(item.GetProperty("b"), BoxPattern()), Msg(item), Text(item, "quote", 400)),
                "confirm" => new ConfirmAct(Id(item.GetProperty("t"), TickPattern()), Text(item, "quote", 400), Text(item, "checked", 300)),
                "dispute" => new DisputeAct(Id(item.GetProperty("t"), TickPattern()), Text(item, "reason", 300)),
                "ask" => new AskAct(List(item, "to", 1, 5, 12).Select(t => Id(t, AddresseePattern())).ToList(), Text(item, "q", 400)),
                _ => throw new FormatException(Codes.UnknownAct),
            };

            if (act is ProposeAct p && p.Title.Length + p.Clauses.Sum(c => c.Length) + p.Items.Sum(i => i.Length) > 8000)
            {
                return (null, Codes.TooLarge);
            }

            return (act, null);
        }
        catch (FormatException ex)
        {
            return (null, ex.Message.Length > 0 && ex.Message.All(c => char.IsAsciiLetterLower(c) || c == '-') ? ex.Message : Codes.BadJson);
        }
    }

    /// <summary>Two votes on one proposal, or agreement to two proposals, in one block: every vote is rejected.</summary>
    private static List<ParsedAct> Conflicts(List<ParsedAct> acts)
    {
        var votes = acts.Where(a => a.Act is AgreeAct or ObjectAct).ToList();
        var byProposal = votes.GroupBy(a => a.Act switch { AgreeAct g => g.Ref.P, ObjectAct o => o.Ref.P, _ => string.Empty });
        var agreedTo = votes.Where(a => a.Act is AgreeAct).Select(a => ((AgreeAct)a.Act!).Ref.P).Distinct(StringComparer.Ordinal).Count();
        if (byProposal.Any(g => g.Count() > 1) || agreedTo > 1)
        {
            return acts.Select(a => a.Act is AgreeAct or ObjectAct ? a with { Rejected = Codes.Conflict } : a).ToList();
        }

        return acts;
    }

    /// <summary>At most 5 boxes and 2 questions per answer; the ones after are rejected.</summary>
    private static List<ParsedAct> Limit(List<ParsedAct> acts)
    {
        var boxes = 0;
        var asks = 0;
        return acts.Select(a => a.Act switch
        {
            BoxAct when ++boxes > MaxBoxes => a with { Rejected = Codes.TooLarge },
            AskAct when ++asks > MaxAsks => a with { Rejected = Codes.TooLarge },
            _ => a,
        }).ToList();
    }

    /// <summary>
    /// A string field, cleaned: invisible characters removed, the envelope brackets
    /// made plain, the block's own name defused, and its length capped.
    /// </summary>
    private static string Text(JsonElement item, string name, int max) => Text(item.GetProperty(name), max);

    private static string Text(JsonElement value, int max)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new FormatException(Codes.BadJson);
        }

        var text = SafeText.ForModel(value.GetString())
            .Replace('⟦', '[').Replace('⟧', ']').Replace('〚', '[').Replace('〛', ']')
            .Replace(Fence, "diptick_acts", StringComparison.OrdinalIgnoreCase)
            .Trim();
        return text.Length > max ? throw new FormatException(Codes.TooLarge) : text;
    }

    private static List<string> List(JsonElement item, string name, int min, int max, int each) => List(item.GetProperty(name), min, max, each);

    private static List<string> List(JsonElement value, int min, int max, int each)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException(Codes.BadJson);
        }

        var count = value.GetArrayLength();
        if (count > max)
        {
            throw new FormatException(Codes.TooLarge);
        }

        if (count < min)
        {
            throw new FormatException(Codes.MissingField);
        }

        return value.EnumerateArray().Select(e => Text(e, each)).ToList();
    }

    private static JsonElement? Optional(JsonElement item, string name) => item.TryGetProperty(name, out var value) ? value : null;

    private static bool Bool(JsonElement item, string name) =>
        item.GetProperty(name).ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new FormatException(Codes.BadJson),
        };

    private static VersionRef Ref(JsonElement item)
    {
        var p = Id(item.GetProperty("p"), ProposalPattern());
        var v = item.GetProperty("v");
        if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var version) || version < 1 || version > 999)
        {
            throw new FormatException(Codes.BadJson);
        }

        return new VersionRef(p, version, Id(item.GetProperty("h"), HashPattern()));
    }

    /// <summary>"P1.v2#51c0e7aa" in a basedOn list: strings, so the block stays within depth 4.</summary>
    private static List<VersionRef> BasedOn(JsonElement item)
    {
        if (Optional(item, "basedOn") is not { } list)
        {
            return [];
        }

        return List(list, 1, 8, 80).Select(text =>
        {
            var m = RefPattern().Match(text);
            return m.Success
                ? new VersionRef(m.Groups["p"].Value, int.Parse(m.Groups["v"].Value, CultureInfo.InvariantCulture), m.Groups["h"].Value)
                : throw new FormatException(Codes.BadJson);
        }).ToList();
    }

    private static string Where(JsonElement item) => Id(item.GetProperty("where"), WherePattern());

    private static string Msg(JsonElement item)
    {
        var msg = item.GetProperty("msg");
        return msg.ValueKind switch
        {
            JsonValueKind.Number when msg.TryGetInt32(out var seq) && seq > 0 => seq.ToString(CultureInfo.InvariantCulture),
            JsonValueKind.String when msg.GetString() == "this" => "this",
            _ => throw new FormatException(Codes.BadJson),
        };
    }

    private static string Id(JsonElement value, Regex pattern)
    {
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } text || !pattern.IsMatch(text))
        {
            throw new FormatException(Codes.BadJson);
        }

        return text;
    }

    private static string Id(string text, Regex pattern) => pattern.IsMatch(text) ? text : throw new FormatException(Codes.BadJson);

    [GeneratedRegex("^(?<fence>`{3,}|~{3,})(?<info>[^`]*)$", RegexOptions.CultureInvariant)]
    private static partial Regex Opener();

    [GeneratedRegex("^P[1-9][0-9]{0,3}$", RegexOptions.CultureInvariant)]
    private static partial Regex ProposalPattern();

    [GeneratedRegex("^[0-9a-f]{8,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex HashPattern();

    [GeneratedRegex("^[CK][1-9][0-9]{0,1}$", RegexOptions.CultureInvariant)]
    private static partial Regex WherePattern();

    [GeneratedRegex("^B[1-9][0-9]{0,3}$", RegexOptions.CultureInvariant)]
    private static partial Regex BoxPattern();

    [GeneratedRegex("^T[1-9][0-9]{0,4}$", RegexOptions.CultureInvariant)]
    private static partial Regex TickPattern();

    [GeneratedRegex("^[a-z][a-z0-9-]{0,11}$", RegexOptions.CultureInvariant)]
    private static partial Regex SeatPattern();

    [GeneratedRegex("^([a-z][a-z0-9-]{0,11})$", RegexOptions.CultureInvariant)]
    private static partial Regex AddresseePattern();

    [GeneratedRegex("^(?<p>P[1-9][0-9]{0,3})\\.v(?<v>[1-9][0-9]{0,2})#(?<h>[0-9a-f]{8,64})$", RegexOptions.CultureInvariant)]
    private static partial Regex RefPattern();
}
