# Permissions and approvals

An agent can do nothing DipTick has not allowed. Each thing it can ask to do is a capability:

- `model.call`: answer you.
- `fs.read`, `fs.write`, `fs.delete`: read, write and delete files, only inside your folders.
- `net.fetch`: fetch a page from the web.
- `net.probe`: check the network, reaching a service DipTick already uses.
- `web.search`: search the web, through the search service you chose.
- `media.make`: make an image or a video, which costs money.
- `mcp.call`: use a connector's tool; each call is narrowed to the connector and the tool, `mcp.call:clickup/create_task` (see [Connectors](connectors)).
- `db.read`, `db.write`: read and write a database of yours. No tool in DipTick asks for them (see [Your data](your-data)).
- `shell.run`: run a program, in DipTick's sandbox; it always asks, whatever the rules say (see [Running code](running-code)).

A group names several at once, like `fs.*`, and `*` names every one. A rule can be narrowed to one target after a colon: `mcp.call:clickup/*` is every ClickUp tool, `mcp.call:clickup/create_task` one of them.

## The three lists

Settings > Permissions shows a row for each capability, in words, with **Allowed**, **Ask me first** and **Never**; each goes by the strictest list that names it. Never beats ask me first, which beats allowed, and anything no list names is never. Two more lists narrow the file and web capabilities: **Folders**, the only folders a file capability may touch, and **Web hosts**, the only hosts `net.fetch` may reach; `*` there is any public site, never this PC, your network or a link-local address. The folder DipTick keeps agents' files in (Documents\DipTick, or `files.folder`) is always one of the folders; writing there still asks, as `fs.write` does. See [Files, images and video](files).

Under each capability, a box for each agent can only narrow what everyone may do: as everyone, ask, or never. **Try a request** says what the rules would decide, without doing anything.

**Where an address came from counts.** A fetch whose address first appeared in what you wrote, in a search result, or in a page already fetched in the chat does not ask where the lists say Ask me first; any other address asks. A chat's own ask, or an agent's own, still asks every time, and never is never. See [Research](research).

## Presets

Three buttons above the rows set the three lists at once, and say what each changes before anything is written:

- **Locked**: everything asks or is never done.
- **Standard**, the settings DipTick starts with: answering and searching allowed; files, the web, the network check, media and connectors ask; deleting files, writing the database and running programs never.
- **Trusted**: reading, answering, searching and the network check allowed; writing, the web, programs, media and connectors ask.

Letting agents do more always asks for your yes first.

## One chat's own rules

A chat can have rules of its own. The permissions chip in the chat's header names the preset and says when the chat has rules of its own; **This chat's permissions…** in its menu changes them, as do /allow, /ask and /deny in the chat. A chat can tighten anything, and turn an ask into an allow for itself; nothing a chat says loosens a never.

## Approvals

When a rule says ask, DipTick asks you in the chat: which agent, what it wants to do, and where. **Allow once**, **Allow for this chat** (the chat keeps that rule for exactly what was asked), or **Deny**. The mark starts on Deny, so Enter without looking says no; Esc denies too, and in the window, so does leaving the question unanswered. An answer that calls tools asks once for itself, not again for each step after its tools.

**Ask before each answer** (in the permissions chip, or Shift+Tab) puts `model.call` on the ask list, so every answer waits for your yes: useful with a provider that charges. **Dry run** changes nothing and says what would have happened.

Your own yes to your own questions can be given ahead (/yes on, or `--yes` on the command line). It never answers an agent's request to write, delete, fetch, search or spend: a person always does.

## From the command line

- `diptick policy`: the lists, and each capability's rule.
- `diptick policy check <capability> [target]`: what would be decided, recorded nowhere.
- `diptick policy try <capability> [target]`: through the gate, asked and recorded like a real request.
- `diptick policy set <rule> allow|ask|deny [--ai <id>] [--session <id>]`: one rule, for everyone, for one agent, or for one chat; `policy unset <rule> --session <id>` takes a chat's rule away; `policy chat` lists a chat's rules.
- `diptick policy folders [add|remove <folder>]`, `policy hosts [add|remove <host>]`, `policy preset locked|standard|trusted`.

Every decision, and every change to these rules, goes to the audit log: see [Activity and the audit log](activity).
