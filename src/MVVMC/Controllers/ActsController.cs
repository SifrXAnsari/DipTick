using System.Text.Json;
using DipTick.Models;
using DipTick.Models.Protocol;

namespace DipTick.Controllers;

/// <summary>
/// <c>diptick acts check [-|&lt;file&gt;] [--phase &lt;step&gt;] [--proposal &lt;file&gt;] [--unfinished]</c>:
/// what DipTick would count in one agent's answer, with no network and no chat.
/// </summary>
/// <remarks>
/// For tuning the protocol card on real model answers for free, and for anyone who wants to see
/// exactly why an act was not counted. <c>--proposal</c> is a JSON file
/// <c>{"id","v","parent","title","clauses","items"}</c>; votes and critiques on it are checked
/// against its hash and the flaw rules.
/// </remarks>
internal static class ActsController
{
    public static int Run(IReadOnlyList<string> args, Cli cli)
    {
        if (args.Count < 2 || args[1] != "check")
        {
            return cli.Fail("usage: diptick acts check [-|<file>] [--phase draft|review|synth|vote|revise|work] [--proposal <file>] [--unfinished]", Exit.Usage);
        }

        string? source = null;
        Phase? phase = null;
        string? proposalFile = null;
        var finished = true;
        for (var i = 2; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--phase" when i + 1 < args.Count && Enum.TryParse<Phase>(args[i + 1], ignoreCase: true, out var p):
                    phase = p;
                    i++;
                    break;
                case "--proposal" when i + 1 < args.Count:
                    proposalFile = args[++i];
                    break;
                case "--unfinished":
                    finished = false;
                    break;
                default:
                    if (source is not null || args[i].StartsWith("--", StringComparison.Ordinal))
                    {
                        return cli.Fail($"acts check does not take '{args[i]}'.", Exit.Usage);
                    }

                    source = args[i];
                    break;
            }
        }

        string answer;
        try
        {
            answer = source is null or "-" ? Console.In.ReadToEnd() : File.ReadAllText(source);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return cli.Fail($"cannot read the answer: {ex.Message}");
        }

        ProposalVersion? version = null;
        if (proposalFile is not null)
        {
            version = ReadProposal(proposalFile, out var why);
            if (version is null)
            {
                return cli.Fail(why!, Exit.Usage);
            }
        }

        var block = ActParser.Parse(answer, finished);
        IReadOnlyList<ParsedAct> acts = block.Acts;
        string? missing = null;
        if (phase is { } step && block.BlockError is null)
        {
            (acts, missing) = PhaseTable.Apply(step, acts);
        }

        if (version is not null)
        {
            acts = acts.Select(a => a.Accepted ? Against(a, version) : a).ToList();
        }

        if (cli.WriteJson(new
        {
            found = block.Found,
            blockError = block.BlockError,
            proposal = version?.Label,
            missing,
            acts = acts.Select(a => new { act = a.Kind, accepted = a.Accepted, code = a.Rejected }),
        }))
        {
            return block.BlockError is null && missing is null && acts.All(a => a.Accepted) ? Exit.Ok : Exit.Failed;
        }

        if (!block.Found)
        {
            cli.Data("No diptick-acts block at the end of this answer: nothing is counted.");
            return Exit.Failed;
        }

        if (block.BlockError is { } error)
        {
            cli.Data($"The whole block is void: {error}.");
            return Exit.Failed;
        }

        foreach (var a in acts)
        {
            cli.Data($"  {(a.Accepted ? "counted " : "rejected")}  {a.Kind,-8}  {Describe(a.Act)}{(a.Accepted ? string.Empty : $"  ({a.Rejected})")}");
        }

        if (missing is not null)
        {
            cli.Data($"This step still needs {missing}.");
        }

        return missing is null && acts.All(a => a.Accepted) ? Exit.Ok : Exit.Failed;
    }

    /// <summary>A vote or critique checked against the one version the person supplied.</summary>
    private static ParsedAct Against(ParsedAct a, ProposalVersion version)
    {
        (VersionRef? r, string? where, string? quote, string? flaw) = a.Act switch
        {
            AgreeAct g => ((VersionRef?)g.Ref, g.Where, g.Quote, g.Risk),
            CritiqueAct c => (c.Ref, c.Where, c.Quote, c.Flaw),
            ObjectAct o => (o.Ref, o.Where, null, null),
            _ => ((VersionRef?)null, (string?)null, (string?)null, (string?)null),
        };

        if (r is not { } reference)
        {
            return a;
        }

        if (reference.P != version.Id || reference.V != version.V)
        {
            return a with { Rejected = Codes.StaleVersion };
        }

        if (!version.Matches(reference))
        {
            return a with { Rejected = Codes.UnseenHash };
        }

        if (version.Body.At(where!) is null)
        {
            return a with { Rejected = Codes.BadWhere };
        }

        return quote is null ? a : FlawRules.Check(version.Body, where!, quote, flaw!, []) is { } code ? a with { Rejected = code } : a;
    }

    private static string Describe(Act? act) => SafeText.Line(act switch
    {
        ProposeAct p => $"\"{p.Title}\" · {p.Clauses.Count} clauses, {p.Items.Count} items",
        CritiqueAct c => $"{c.Ref.P} v{c.Ref.V} at {c.Where}: {c.Flaw}",
        AmendAct m => $"{m.Ref.P} v{m.Ref.V}: {m.Why}",
        AgreeAct g => $"{g.Ref.P} v{g.Ref.V} #{g.Ref.H} at {g.Where} · risk: {g.Risk}",
        ObjectAct o => $"{o.Ref.P} v{o.Ref.V} at {o.Where}: {o.Reason}{(o.Redraft ? " · redraft" : string.Empty)}",
        BoxAct b => b.Text,
        TickAct t => $"{t.B} (message {t.Msg})",
        ConfirmAct c => $"{c.T}: {c.Checked}",
        DisputeAct d => $"{d.T}: {d.Reason}",
        AskAct q => $"to {string.Join(", ", q.To)}: {q.Q}",
        _ => string.Empty,
    });

    private static ProposalVersion? ReadProposal(string path, out string? why)
    {
        why = null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var r = doc.RootElement;
            var body = new ProposalBody(
                r.GetProperty("title").GetString() ?? string.Empty,
                r.GetProperty("clauses").EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToList(),
                r.TryGetProperty("items", out var items) ? items.EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToList() : []);
            return new ProposalVersion(r.GetProperty("id").GetString() ?? "P1", r.GetProperty("v").GetInt32(), r.TryGetProperty("parent", out var parent) ? parent.GetString() ?? string.Empty : string.Empty, body);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            why = $"the proposal file is not {{\"id\",\"v\",\"parent\",\"title\",\"clauses\",\"items\"}}: {ex.Message}";
            return null;
        }
    }
}
