using DipTick.Models;
using DipTick.Models.Chat.Store;
using DipTick.Models.Skills;
using DipTick.Models.Tools;

namespace DipTick.Controllers;

/// <summary>
/// <c>chat skills</c>: which skills are on in a chat. Only an approved skill can
/// be switched on; its hash is kept with the chat, and one changed since is offered to nobody.
/// </summary>
internal static partial class ChatController
{
    private const string SkillsUsage = "usage: diptick chat skills [--session <id|last>] [add|remove <name>]";

    private static int Skills(IChatStore store, Cli cli, VerbArgs words)
    {
        var session = words.Option("--session") ?? "last";
        var rest = words.Words();
        if (words.Problem is not null || rest.Count is not (0 or 2) || (rest.Count == 2 && rest[0] is not ("add" or "remove")))
        {
            return cli.Fail(SkillsUsage, Exit.Usage);
        }

        if (store.Find(session) is not { } found)
        {
            return cli.Fail(NoSuch(session));
        }

        var on = store.ChatSkills(found.Id);
        var (skills, _) = SkillStore.All(cli.Config);
        if (rest.Count == 0)
        {
            if (cli.WriteJson(new
            {
                session = found.Id,
                on = on.Select(s => s.Skill),
                skills = skills.Select(s => new { name = s.Name, on = on.Any(o => o.Skill == s.Name), state = SkillStore.StateOf(cli.Config, s).ToString().ToLowerInvariantAscii() }),
            }))
            {
                return Exit.Ok;
            }

            if (skills.Count == 0)
            {
                cli.Note("No skills yet: 'diptick skills add <folder|zip>' adds one.");
                return Exit.Ok;
            }

            foreach (var skill in skills)
            {
                cli.Data(SafeText.Line($"  {(on.Any(o => o.Skill == skill.Name) ? "on " : "off")}  {skill.Name,-24} {SkillStore.Words(SkillStore.StateOf(cli.Config, skill))}"));
            }

            return Exit.Ok;
        }

        var name = rest[1];
        var adding = rest[0] == "add";
        string? hash = null;
        if (adding)
        {
            if (skills.FirstOrDefault(s => s.Name == name) is not { } skill)
            {
                return cli.Fail($"no skill {SafeText.Line(name)}; 'diptick skills' lists them.", Exit.Usage);
            }

            if (SkillStore.StateOf(cli.Config, skill) != SkillState.Approved)
            {
                return cli.Fail($"{name} is {SkillStore.Words(SkillStore.StateOf(cli.Config, skill))}; only an approved skill goes in a chat.");
            }

            hash = skill.Hash;
        }

        if (cli.Flags.DryRun)
        {
            cli.Note($"[dry run] would switch {name} {(adding ? "on" : "off")} in {found.Id}; nothing changed.");
            return Exit.Ok;
        }

        store.SetChatSkill(found.Id, name, hash);
        if (!cli.WriteJson(new { session = found.Id, skill = name, on = adding }))
        {
            cli.Note(adding ? $"{name} is on in this chat: agents here are told of it, and read it when they need it." : $"{name} is off in this chat.");
        }

        return Exit.Ok;
    }
}
