using System.Globalization;
using DipTick.Models;
using DipTick.Models.Skills;

namespace DipTick.ViewModels;

/// <summary>
/// Settings, Skills: each skill in DipTick's folder with its state and one menu
/// (Show, Approve, On in this chat, Remove), and Add a skill…. Each change is a <c>diptick skills</c> or
/// <c>chat skills</c> verb, run in this process; the verb shows the skill and asks your yes.
/// </summary>
internal sealed partial class SettingsViewModel
{
    private List<SettingRow> SkillRows()
    {
        var (skills, problems) = SkillStore.All(chat.Config);
        var rows = new List<SettingRow>();
        foreach (var skill in skills)
        {
            var state = SkillStore.StateOf(chat.Config, skill);
            var scripts = skill.HasScripts ? " It holds scripts, which run only in DipTick's sandbox, asked each time." : string.Empty;
            rows.Add(new SettingRow(skill.Name, SkillStore.Words(state), $"{SafeText.Line(skill.Description)}{scripts} Change opens its menu: Show, Approve, On in this chat, Remove.", RowAction.Skill, Raw: skill.Name));
        }

        rows.Add(new SettingRow("Add a skill…", string.Empty, "A folder with a SKILL.md, or a zip of one, in the open Agent Skills format (from ~\\.claude\\skills, say). It is copied into DipTick's own folder, shown to you whole, and used only after your yes.", RowAction.AddSkill));
        if (SkillStore.FolderFor(chat.Config) is { } folder)
        {
            foreach (var problem in problems)
            {
                rows.Add(new SettingRow("A skill that could not be read", folder, problem, RowAction.OpenPath));
            }
        }

        return rows;
    }

    /// <summary>One skill's menu. Runs on a worker thread.</summary>
    private async Task SkillMenuAsync(IAsker asker, string name)
    {
        if (SkillStore.Find(chat.Config, name) is not { } skill)
        {
            return;
        }

        var state = SkillStore.StateOf(chat.Config, skill);
        var on = chat.SkillsOn.Contains(name, StringComparer.Ordinal);
        var actions = new List<(Choice Choice, Func<Task> Run)>
        {
            (new Choice("Show", "all of it: what it is for, its files, its instructions"), () => ShowSkill(asker, skill)),
        };
        if (state != SkillState.Approved)
        {
            actions.Add((new Choice("Approve…", "shows it whole and asks your yes"), () => Verb("skills", "approve", name)));
        }
        else
        {
            actions.Add((new Choice(on ? "Off in this chat" : "On in this chat", on ? "agents here are no longer told of it" : "agents here are told of it, and read it when they need it"), () => chat.SetSkillAsync(name, !on)));
        }

        actions.Add((new Choice("Remove", "DipTick's copy of it goes, and its approval"), () => Verb("skills", "remove", name)));
        var picked = asker.Choose(new Question($"Settings · Skills · {name}", SkillStore.Words(state), SafeText.Line(skill.Description)), [.. actions.Select(a => a.Choice)], 0);
        if (picked.Reply == Reply.Answered)
        {
            await actions[picked.Value].Run().ConfigureAwait(false);
        }
    }

    private static Task ShowSkill(IAsker asker, Skill skill)
    {
        var files = string.Join(", ", skill.Files.Take(40).Select(SafeText.Line)) + (skill.Files.Count > 40 ? ", …" : string.Empty);
        asker.Tell(
            $"Settings · Skills · {skill.Name}",
            string.Create(CultureInfo.InvariantCulture, $"{SafeText.Line(skill.Description)}\n\nFiles ({skill.Files.Count}): {files}\n{(skill.HasScripts ? "It holds scripts: they run only in DipTick's sandbox, through run_code or 'diptick run --skill', and you are asked each time.\n" : string.Empty)}\n{SafeText.Block(skill.Body)}"));
        return Task.CompletedTask;
    }

    /// <summary>Add a skill…: a folder or a zip, then <c>skills add</c>, which shows it whole and asks your yes.</summary>
    private void AddSkill(IAsker asker)
    {
        var path = asker.Text(
            new Question("Settings · Skills · Add a skill", "Which folder or zip?", "A folder holding a SKILL.md, or a .zip of one. It is copied into DipTick's own skills folder."),
            string.Empty,
            text => Directory.Exists(text.Trim().Trim('"')) || File.Exists(text.Trim().Trim('"')) ? null : "a folder or a zip that is there");
        if (path.Reply == Reply.Answered)
        {
            chat.Run(false, "skills", "add", path.Value.Trim().Trim('"'));
        }
    }
}
