using System.Globalization;
using System.Text.Json;
using DipTick.Models;
using DipTick.Models.Chat;
using DipTick.Models.Harness;

namespace DipTick.ViewModels;

/// <summary>
/// Tools and permissions in the chat: DipTick's own tools as commands, and this chat's own rules,
/// which tighten anything and turn an ask into an allow for the chat alone. Each is a
/// <c>diptick</c> verb, run in this process.
/// </summary>
internal sealed partial class ChatViewModel
{
    /// <summary>This chat's own rules as last read; null until read again.</summary>
    private IReadOnlyList<ChatRule>? _chatRules;

    /// <summary>This chat's own rules (<c>policy chat</c>); none for a chat nobody has spoken in.</summary>
    public IReadOnlyList<ChatRule> ChatRules
    {
        get
        {
            lock (_lock)
            {
                if (_chatRules is { } known)
                {
                    return known;
                }
            }

            IReadOnlyList<ChatRule> rules = [];
            if (_sessionId is { } id)
            {
                var (json, _, code) = Json(Private("policy", "chat", $"--session={id}"));
                if (code == Controllers.Exit.Ok)
                {
                    rules = ReadRules(json);
                }
            }

            lock (_lock)
            {
                _chatRules = rules;
            }

            return rules;
        }
    }

    /// <summary>
    /// The permissions chip's words: the preset the settings match, or Custom; and
    /// when this chat has rules of its own, that it does.
    /// </summary>
    public string PermissionsWords
    {
        get
        {
            var preset = Presets.Match(Config.Permissions) is { } name ? char.ToUpperInvariant(name[0]) + name[1..] : "Custom";
            var own = ChatRules.Count;
            return own == 0 ? preset : $"{preset} · {own.ToString(CultureInfo.InvariantCulture)} for this chat";
        }
    }

    /// <summary>
    /// This chat's permissions, edited in the prompt card: a capability, then what this chat says of it:
    /// allowed here (an ask turned into an allow, never a never loosened), ask here, never here, or as
    /// the settings say. Each is <c>policy set --session</c> or <c>policy unset</c>. Runs on a worker thread.
    /// </summary>
    public async Task ChatRulesAsync()
    {
        if (Asker is not { } asker)
        {
            await Say(Role.System, "'diptick policy set <capability> allow|ask|deny --session <id>' sets this chat's own rules.").ConfigureAwait(false);
            return;
        }

        if (EnsureKept() is not { } id)
        {
            return;
        }

        var rules = ChatRules;
        var p = Config.Permissions;
        var caps = Capabilities.All.ToList();
        string Settled(Capability cap) => p.Deny.Any(r => Capabilities.Matches(r, cap)) ? "never" : p.Ask.Any(r => Capabilities.Matches(r, cap)) ? "ask" : p.Allow.Any(r => Capabilities.Matches(r, cap)) ? "allowed" : "never";
        string Here(Capability cap) => rules.FirstOrDefault(r => Capabilities.IsExactly(r.Rule, cap)) is { } own
            ? own.Verdict switch { Verdict.Allow => "allowed here", Verdict.Ask => "ask here", _ => "never here" }
            : "as the settings";
        var which = asker.Choose(
            new Question("This chat's permissions", "Which?", "A chat can tighten anything, and turn an ask into an allow for itself; nothing it says loosens a never."),
            [.. caps.Select(c => new Choice(Capabilities.Describe(c), $"{Capabilities.Name(c)} · settings: {Settled(c)} · this chat: {Here(c)}"))],
            0);
        if (which.Reply != Reply.Answered)
        {
            return;
        }

        var cap = caps[which.Value];
        var name = Capabilities.Name(cap);
        var what = asker.Choose(
            new Question("This chat's permissions", $"{Capabilities.Describe(cap)}, in this chat?", $"The settings say: {Settled(cap)}."),
            [new Choice("As the settings say"), new Choice("Allowed here", "an ask becomes an allow, in this chat only"), new Choice("Ask here"), new Choice("Never here")],
            0);
        if (what.Reply != Reply.Answered)
        {
            return;
        }

        if (what.Value == 0)
        {
            Run(false, Private("policy", "unset", name, $"--session={id}"));
        }
        else
        {
            Run(false, Private("policy", "set", name, what.Value switch { 1 => "allow", 2 => "ask", _ => "deny" }, $"--session={id}"));
        }

        lock (_lock)
        {
            _chatRules = null;
        }

        Changed?.Invoke();
    }

    /// <summary>/allow, /ask, /deny: this chat's own rule for a capability (or one narrowed to a target).</summary>
    private async Task ChatRuleCommandAsync(string verdict, string arg)
    {
        var rule = arg.Trim();
        if (rule.Length == 0)
        {
            await Say(Role.System, $"/{(verdict == "deny" ? "deny" : verdict)} <capability>: {string.Join(", ", Capabilities.Names)}; for this chat only.").ConfigureAwait(false);
            return;
        }

        if (EnsureKept() is not { } id)
        {
            return;
        }

        Run(false, Private("policy", "set", rule, verdict, $"--session={id}"));
        lock (_lock)
        {
            _chatRules = null;
        }

        Changed?.Invoke();
    }

    /// <summary>/tools: DipTick's own tools, and the time now.</summary>
    private async Task ToolsAsync(string arg)
    {
        _ = arg;
        await Verb("tools", "list").ConfigureAwait(false);
        await Verb("tools", "time").ConfigureAwait(false);
    }

    /// <summary>/calc: exact decimal arithmetic (<c>diptick tools calc</c>).</summary>
    private Task CalcAsync(string arg) => arg.Trim().Length == 0
        ? Say(Role.System, "/calc <arithmetic>, like /calc 17% of 2345.60")
        : Verb("tools", "calc", "--", arg.Trim());

    private static List<ChatRule> ReadRules(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return [.. doc.RootElement.GetProperty("rules").EnumerateArray().Select(r => new ChatRule(
                r.GetProperty("rule").GetString() ?? string.Empty,
                r.GetProperty("verdict").GetString() switch { "allow" => Verdict.Allow, "ask" => Verdict.Ask, _ => Verdict.Deny }))];
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return [];
        }
    }
}
