using System.Text.Json;
using DipTick.Models.Chat;
using DipTick.Models.Skills;

namespace DipTick.ViewModels;

/// <summary>
/// Skills in the chat: which are on in the chat on screen, switched on or off
/// from the rail, <c>/skill</c> or Settings; each change a <c>diptick chat skills</c> verb, run in this process.
/// </summary>
internal sealed partial class ChatViewModel
{
    /// <summary>The skills on in the chat on screen as last read; null until read again.</summary>
    private IReadOnlyList<string>? _skillsOn;

    /// <summary>The skills in DipTick's folder, as they are on disk now.</summary>
    public IReadOnlyList<Skill> Skills => SkillStore.All(Config).Skills;

    /// <summary>A mark of the skills folder and its approvals as they stand, for a screen to know when to draw them again.</summary>
    public string SkillsStamp
    {
        get
        {
            var folder = SkillStore.FolderFor(Config);
            var approvals = SkillStore.LockFor(Config);
            var a = folder is not null && Directory.Exists(folder) ? Directory.GetLastWriteTimeUtc(folder).Ticks : 0;
            var b = approvals is not null && File.Exists(approvals) ? File.GetLastWriteTimeUtc(approvals).Ticks : 0;
            return $"{a}:{b}";
        }
    }

    /// <summary>The skills on in the chat on screen (<c>chat skills</c>); none for a chat nobody has spoken in.</summary>
    public IReadOnlyList<string> SkillsOn
    {
        get
        {
            lock (_lock)
            {
                if (_skillsOn is { } known)
                {
                    return known;
                }
            }

            IReadOnlyList<string> on = [];
            if (_sessionId is { } id)
            {
                var (json, _, code) = Json(Private("chat", "skills", $"--session={id}"));
                if (code == Controllers.Exit.Ok)
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(json);
                        on = [.. doc.RootElement.GetProperty("on").EnumerateArray().Select(n => n.GetString()).OfType<string>()];
                    }
                    catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
                    {
                        on = [];
                    }
                }
            }

            lock (_lock)
            {
                _skillsOn = on;
            }

            return on;
        }
    }

    /// <summary>A skill on or off in the chat on screen (<c>chat skills add|remove</c>), making the chat first when nobody has spoken in it. Runs on a worker thread.</summary>
    public async Task SetSkillAsync(string name, bool on)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (EnsureKept() is not { } id)
        {
            return;
        }

        await Task.Run(() => Run(false, Private("chat", "skills", $"--session={id}", on ? "add" : "remove", name))).ConfigureAwait(false);
        lock (_lock)
        {
            _skillsOn = null;
        }

        Changed?.Invoke();
    }

    /// <summary>/skills: every skill and whether it is on here (<c>chat skills</c>), or the list alone in a chat nobody has spoken in.</summary>
    private Task SkillsCommandAsync() =>
        _sessionId is { } id ? Verb(Private("chat", "skills", $"--session={id}")) : Verb("skills");

    /// <summary>/skill &lt;name&gt; switches a skill on in this chat; /skill off &lt;name&gt; switches it off.</summary>
    private async Task SkillCommandAsync(string arg)
    {
        var parts = arg.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        switch (parts)
        {
            case ["off", var name]:
                await SetSkillAsync(name, false).ConfigureAwait(false);
                break;
            case ["on", var name]:
                await SetSkillAsync(name, true).ConfigureAwait(false);
                break;
            case [var name]:
                await SetSkillAsync(name, true).ConfigureAwait(false);
                break;
            default:
                await Say(Role.System, "/skill <name> puts an approved skill in this chat; /skill off <name> takes it out; /skills lists them.").ConfigureAwait(false);
                break;
        }
    }
}
