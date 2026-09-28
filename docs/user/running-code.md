# Running code

Agents can write a short program, run it, read what it printed, and fix it: to work out a number, reshape a file, check an idea. The code runs in DipTick's own sandbox, never as you.

## Turning it on

Running code is off until you turn it on: the Standard permissions keep `shell.run` at **Never**. Settings > Permissions, the row for running programs (`shell.run`): Ask me first, or the Trusted preset. Then an agent that can call tools is offered `run_code` in your chats.

However it is set, running code always asks you first, every time, and shows you the whole code: a rule that allows it is read as asking, `--yes` does not answer it, and "Allow for this chat" allows only that one run.

## The sandbox

- The code runs as DipTick's AppContainer, a Windows sandbox with no capabilities: no network at all, none of your folders, none of your credentials, and no other program's windows or clipboard.
- It runs inside a Job object: at most 16 processes at once, 1 GiB of memory, 60 seconds of CPU; it stops at 60 seconds of time unless the agent asks for more, up to 10 minutes. When DipTick quits, anything still running ends.
- It writes only in its own folder: in a kept chat, `runs` in the chat's folder (Documents\DipTick\<chat>\runs), kept, so what it made is yours; in a private chat, a folder of DipTick's that is removed after the run. Its home and temporary files are in `.sandbox` inside that folder, and are not counted as what it made.
- Its environment is its own: nothing of DipTick's settings, keys or variables.
- In PowerShell, its folder is the drive `Run:\`: the sandbox cannot see the folders above it, so it starts there. A script ends as `pwsh -File` would end it: `exit n` ends the run with n; a script that does not parse, or that an error stops, ends with 1; one that reaches its end ends with 0, whatever the last program it ran returned.

## The languages

PowerShell 7 (`pwsh`), Python and Node, whichever this PC has, installed for all users (in Program Files). One installed only in your user folder cannot be read from the sandbox, and DipTick says so rather than opening your folder to it. Python from the Microsoft Store cannot run there either, nor the `python` that only opens the Store.

## In the chat

Each run is a line under the answer that made it, folded: one click shows the code, the exit code, the time, what it printed, its errors, and the files it made.

## A skill's scripts

With a skill on in the chat, `run_code` can run one of its scripts, or code of its own beside the skill's files: the skill is copied into the run's folder, checked against your yes, and left read-only there, at the path `SKILL_DIR` names. See [Skills](skills).

## From the command line

`diptick run <file>` runs your own code the same way, through the same question: `--lang pwsh|python|node` when the file's extension does not say, `--seconds <n>` for more time. `diptick run --skill <name> <script> [--arg <a>]...` runs an approved skill's script. See [The command line](command-line).
