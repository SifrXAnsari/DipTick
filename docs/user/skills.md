# Skills

A skill is a set of instructions an agent follows when a task calls for it: how your team writes release notes, how to fill a form, how to review a pull request. DipTick uses the open Agent Skills format, so skills written for Claude and other agents work here too.

## What a skill is

A folder holding a `SKILL.md`: a name (a-z, 0-9 and -, the same as the folder's), what the skill is for and when to use it, then its instructions. It may hold other files its instructions name: `references/` to read, `assets/` to use, `scripts/` to run.

## Adding one

Settings > Skills > **Add a skill…**, or `diptick skills add <folder|zip>`. DipTick checks it against the format, copies it into its own `skills` folder beside your settings file, takes out any invisible characters (text a person cannot see is where hidden instructions hide), shows you all of it, and asks your yes. A skill in `~\.claude\skills` or `~\.agents\skills` comes in the same way: DipTick reads skills from its own folder and nowhere else, never from a project you open.

Your yes is kept as a fingerprint of every file of the skill. If any file changes afterwards, the skill is offered to no agent until **Approve…** shows it to you again and you say yes.

## Using one

A skill is used only in the chats you put it in: the rail's SKILLS (one click switches an approved skill on or off in the chat on screen), `/skill <name>`, or `diptick chat skills add <name>`.

Agents in that chat are told each skill's name and what it is for. When one needs a skill, it reads it in full with its `load_skill` tool, and any file the skill names; each read shows in the chat, folded under the answer that made it, and in the audit log. An agent whose model cannot call tools is given each skill whole instead.

## Scripts

A skill's scripts (`.ps1`, `.py` or `.js`) run only in DipTick's own sandbox (see [Running code](running-code)), and only where running code is allowed in the chat: an agent asks with `run_code`, naming the skill, the script and its arguments, and you are shown the script whole and asked, every time. You can run one yourself with `diptick run --skill <name> <script> [--arg <a>]...`.

For each run, DipTick copies the skill into the run's own folder and checks the copy against the fingerprint of your yes: a skill that changed runs nothing. The copy is read-only to the run, at the path `SKILL_DIR` names; the script has no network and none of your folders, as any run. Where running code is not allowed, an agent reads a script as text instead.

## From the command line

`diptick skills` lists them with their state; `skills show <name>`, `skills add <folder|zip> [--replace]`, `skills approve <name>`, `skills remove <name>`, and `skills check`, which exits 1 when any skill is not approved as it is. See [The command line](command-line).
