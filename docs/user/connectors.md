# Connectors

Connectors let agents use your other services: find a Jira issue, create a ClickUp task, read a Confluence page, draft an email. A connector is an MCP server (the Model Context Protocol): a service's own address, or a program on this PC that speaks MCP.

## Adding one

Settings > Connectors lists the ones DipTick knows, each with how it signs in and what it needs, and **Add**:

| Connector | Address | Sign-in | What it needs |
| --- | --- | --- | --- |
| Atlassian: Jira, Confluence, Jira Service Management, Bitbucket and more | `https://mcp.atlassian.com/v2/mcp` | in your browser, or an API token | its use draws on your organisation's Rovo credits |
| ClickUp | `https://mcp.clickup.com/mcp` | in your browser | in public beta; 50 calls a day on Free and 300 on Unlimited and up, and ClickUp's paid add-on changes those numbers |
| Gmail | `https://gmailmcp.googleapis.com/mcp/v1` | your own Google Cloud client (its ID and secret) | Google's Developer Preview, and the Gmail API and Gmail MCP API on in your project; it reads, searches, labels and drafts, never sends |
| Outlook | Microsoft's Work IQ Mail, at your tenant's address | your own Microsoft Entra app (its client ID) | in preview; a Microsoft 365 Copilot licence |

These were read on 2026-09-27; each row in Settings names its pages.

**Add your own…** takes a service's address (https only; plain http only to this PC), or a program on this PC. **Import a list…** takes another app's list, such as Claude Code's `.mcp.json`: each connector is shown and imported only with your yes.

## Signing in

**Connect** opens the service's sign-in page in your browser, only when it is an https address, and only when you asked. The answer comes back to this PC's own address, `127.0.0.1`, on a port opened for that sign-in. DipTick checks that the answer belongs to the sign-in it started and to the service it expected, then keeps the tokens in Windows Credential Manager, never in a file. Where you gave your own app's client ID, that is what signs in; otherwise the service registers DipTick itself, where it can. A service that registers apps only through a web page cannot: DipTick has no website, so register DipTick with it yourself and give its client ID.

**Sign out** removes the tokens. A header's value, such as an API token, is saved with **Save a header's value…**, in Credential Manager too.

## A program on this PC

A local connector runs in a sandbox of its own: a Windows AppContainer made for it alone, inside a Job object. It is off until you are shown exactly what runs (the program as found, every argument, each variable's name, and its sandbox) and say yes; if any of it changes, it asks again. It gets only a few of Windows' own variables and its own, none of DipTick's. DipTick starts it itself, with no window and nothing of DipTick's open to it, and ends it, and whatever it started, when DipTick quits.

What the sandbox gives it, and nothing more:

- **A network, by kind.** None, unless you give it one: the internet (not your own networks), your home and work networks (not the internet), or both. Windows gives a sandbox its network by kind, not by address, and never lets it reach programs on this PC itself. A program that fetches its package first, as npx and uvx do, needs the internet.
- **Folders you name.** Each one is given to this connector's sandbox alone, to read, or to read and write, by an entry on that folder's own access list; DipTick keeps a record of each, and takes it back when you stop naming the folder or remove the connector. A folder must be on a drive that keeps access lists (NTFS or ReFS), must not be a link, and can never be DipTick's own settings or data, or hold them.
- **A home of its own**, in DipTick's data (`connectors\<name>`): its profile, its settings and its temporary files.

**Sandbox…** in its menu changes these, as does `diptick connectors sandbox <name>`. Some programs cannot run in a sandbox: one installed only in your user folder, which the sandbox cannot read, or one that talks to a service on this PC (Docker, say). **Run it as you** takes it out of the sandbox, to run with your files, your network and your rights, as programs you start yourself do; that too is shown before your yes. What its sandbox named is kept, so **Give it a sandbox** later puts it back as it was, and what it was given of your folders is taken back at once.

A batch file (.cmd or .bat, such as npx) runs through cmd.exe, which reads `& | < > ^ % " !` as its own; an argument holding one is not passed.

## Using one in a chat

A connector's tools are offered only in the chats you put it in: the rail's CONNECTORS (one click switches it on or off in the chat on screen), `/connector on <name>`, or `diptick chat connectors add <name>`. An agent sees each tool as `mcp__<connector>__<tool>`, with the connector's own description of it, marked as the connector's words.

Every call is `mcp.call`, narrowed to the connector and the tool: `mcp.call:clickup/create_task`, or `mcp.call:clickup/*` for all of ClickUp. By default each call asks you first, and shows the agent, the connector, the tool and what it is sending. **Tools…** in a connector's menu sets one tool to Allowed, Ask me first or Never. What a tool gives back reaches the agent as information, never as instructions, and every call is a line in the chat and in the audit log (a fingerprint of what it sent, never the words).

A connector never gets to use your models, ask you questions, or see your folders through DipTick: DipTick offers it none of those.

## Where it is kept

`connectors.json`, beside your settings file (`%APPDATA%\DipTick`), in Claude Code's `mcpServers` shape. No secret is ever in it: a header may name a variable, `"Authorization": "Bearer ${MY_TOKEN}"`, and anything secret is in Credential Manager (`DipTick:connector:<name>:…`). No agent can write to the file.

## From the command line

`diptick connectors` lists them and their state; `connectors add`, `import`, `enable`, `disable`, `test`, `tools`, `signin`, `signout`, `secret`, `sandbox`, `remove`; `diptick chat connectors [add|remove <name>]`. See [The command line](command-line).
