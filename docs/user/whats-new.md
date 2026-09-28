# What's new

What each version of DipTick does, the newest first. Each part names the page that says more.

## 1.0.0

The first release. DipTick is a chat with models, one or several at a time, in one program that is a command line, a terminal screen and a window. It keeps your chats on this PC, asks you before an agent does anything you have not allowed, and writes every decision it makes to an audit log you can check. See [Start here](start-here).

### The terminal screen

- **To open it**: `diptick`, in a terminal or on a PC you reach over SSH. From the Start menu or Explorer, it opens in a console window of its own. See [Start here](start-here).
- **The same F-keys as the window**, on its bottom row, each doing the same thing, but for F2, which opens the window. See [Keyboard](keyboard).
- **Esc three times, quickly, quits it** and stops everything it runs, the local models included. A window opened from it is a program of its own, and stays open.
- **A paste of 5 lines or more** is kept in a text file, and the composer shows its name. See [Chats and tabs](chats).
- **Shift+Enter or Ctrl+J** starts a new line.

### The window

- **To open it**: `diptick window`, which gives the terminal back at once; or /window or F2 in the terminal screen, which goes on working. The window runs on its own: closing the terminal leaves it open. Both screens use the same chats, settings and audit log.
- **The F-key bar across the top**: F1 Help to F10 Quit, each a key and a button. See [Keyboard](keyboard).
- **Esc three times, quickly, quits it** and stops everything it runs, the local models included.
- **What you type and do not send stays with its chat**: switch chats, or quit, and it comes back.

### Tabs, windows and private chats

- **Each open chat is a tab**, and each answers, asks and streams on its own, so several chats answer at once. Ctrl+T opens a new tab; Ctrl+W closes one, and its chat is kept; Ctrl+Shift+T brings the last closed tab back. See [Chats and tabs](chats).
- **Ctrl+N opens a new window**, as in a web browser.
- **F2 Tabs** lists every tab in every window and what each is doing, with New tab, New private tab, New window and Reopen closed tab.
- **A question in a tab behind the others says so** on its tab ("needs your yes" for an approval, "waiting for you" for any other question) and at the right of the F-key bar, where one click goes to it.
- **Private chats**: Ctrl+Shift+N, /private, or `diptick window --private`. Nothing of one is kept, and before you send anything it says who sees what you send: each agent whose model runs somewhere else, and who runs it. See [Private chats](private-chats).
- **The chats on the left**: pinned ones first (/pin), then Today, Yesterday, This week and Older, with a box that finds one by its title. `diptick chat pin` does the same.
- **Where you left off**: the windows and tabs open when you quit come back the next time, each with its unsent draft. A private tab never does.

### Agents at one table

- **A chat seats up to four agents**, shown as chips at its top: each one's colour, name and model. They answer you together, and every answer is under its agent's name. **+ Add agent** seats another; the same agent can sit twice with two models. See [Agents at one table](agents).
- **They talk to one another**: in a debate they answer each other in turns; any of them can put a question to another, which shows under its answer and is answered in the other's next turn; a question to you hands the floor back.
- **@name asks one agent**, `@team` all of them, `@dip>tick` a chain, and the line over the box where you type says who will answer before you send.
- **Who answers** when you name nobody is the chat's own: all at once, one, a chain, a review, a debate, or only whom you name.
- **What they're told** shows exactly what each agent is sent before the chat, with this chat's own brief, which you can change. Settings > You takes your instructions for every chat.
- **What answers cost** shows under each answer, for the chat and for the day, where prices are set; Settings > Money can cap a chat, a day or a month, each off until you set it.
- **F4 Agents** and **F5 Retry**, in both screens; an answer's menu asks it again, or asks another agent.

### Every provider

- **DipTick knows every provider it could find**: runtimes on this PC, the makers of models, hosts, clouds and gateways. `diptick providers` lists them, grouped by kind, those DipTick can talk to first; Settings > Models > Add a model and setup's provider question use the same list, searchable. See [Providers](providers).
- **Each says what it needs** (a key, an address, a sign-in, or nothing), where its models come from, what they can do, and its page on what it keeps. One DipTick cannot talk to is listed with the reason.
- **Typing narrows the list, closest first**: a provider's name typed whole comes first, then names that start with what you typed. The line under the list says all of the one marked: what it is and what it needs.
- **A provider whose address is yours** asks only for its parts: a project and a location, a region, a resource's name, and shows the address as your answers build it. `diptick providers check <id>` lists its models first, which costs nothing.
- **Google Cloud**, with your Google Cloud sign-in: Gemini and the other models Google hosts, and Claude, in your own project. DipTick reads the sign-in from gcloud's file or the one GOOGLE_APPLICATION_CREDENTIALS names, renews its hour-long tokens, and sends them only to Google. A Google Compute Engine machine's own account can sign in too, where you turn it on; it is off.
- **Claude through the clouds**: on Amazon Bedrock, in Microsoft Foundry, on Claude Platform on AWS, and on Google Cloud, each through Anthropic's own SDK.
- **Google's own API** for Gemini, for Google Cloud's express mode (the Gemini Enterprise Agent Platform with an API key), and for gemma.cpp on this PC. A key goes in a header, never in an address.
- **Inferless** answers through KServe's Open Inference Protocol.

### Tools, and permissions you can use

- **Agents can call tools**: a clock, a calendar, an exact calculator and a network check, before they answer. Each call shows in the chat, folded under the answer that made it, and in the audit log. See [Tools](tools).
- **The Tools sheet**, from the rail or the clock beside the name: your clock with other places, the month with ISO weeks and the days between two dates, the network check, and the calculator.
- **Settings > Permissions** has each capability in words, with Allowed, Ask me first or Never; a box for each agent under it that can only narrow; and three presets that say what they change first. See [Permissions and approvals](permissions).
- **A chat's own rules**: tighten anything in one chat, or allow an ask in it. An approval offers **Allow for this chat** beside Allow once and Deny.

### Research, with sources you can check

- **Agents search the web and read pages**: Claude and Gemini 3 with their providers' own tools, every other agent with DipTick's, through Brave (your key) or a SearXNG you run. What a provider charges per search is not in the costs shown. See [Research](research).
- **Sources under every answer**, numbered: what DipTick fetched, what the provider read or cited, and search results nobody fetched. One click opens the page; a [n] with no such source reads **not fetched**.
- **DipTick's own fetch** goes only to public HTTPS addresses, asks about every redirect, and fetches without asking only an address you wrote, a search gave, or a fetched page linked to.
- **The Research chip**, /research and `chat research`: on or off for one chat.

### Files, images and video

- **Attach files to what you say**: images, PDFs and text files, with Attach (F7), a drop on the chat, or an image pasted. Each waits as a chip that says who at the table will not see it, and why. See [Files, images and video](files).
- **Every file stays with its chat**, under the line it came with: images drawn, with Save…, Open and Show in folder. An answer, or one of its code blocks, saves to a file from its menu.
- **Agents read and write files** with list_folder, read_file and write_file, only in folders your permissions open; each chat has its own folder in Documents\DipTick. DipTick opens every file in a way no link or junction can redirect.
- **Images and video from your words**: /image and /video, kept in the chat; agents can make them too. Images come from OpenAI, Google, Nscale, Replicate, fal, WaveSpeedAI, ComfyUI (your own workflows) or stable-diffusion.cpp; video from Google's Veo, Replicate, fal, WaveSpeedAI, ComfyUI or stable-diffusion.cpp. Each asks first unless you allow it: a hosted maker costs money on your key.
- **Speech to text** by whisper.cpp on this PC, with `diptick transcribe` and an agent's `transcribe`.
- **Claude reads images and PDFs** on every platform it runs on; Gemini reads them too.

### Connectors

- **Agents use your other services**: Atlassian (Jira, Confluence and more), ClickUp, Gmail and Outlook are listed in Settings > Connectors with what each needs, and any MCP server you add or import from a Claude Code `.mcp.json`. See [Connectors](connectors).
- **Sign in in your browser**; the tokens stay in Windows Credential Manager.
- **On only where you put them**: the rail's CONNECTORS switches one on or off in the chat on screen, and each tool is Allowed, Ask me first or Never.
- **A program on this PC runs in a sandbox of its own**, only after your yes to exactly what it runs: no network unless you give it the internet or your home and work networks, and none of your folders but those you name, each given to it alone and taken back when you stop naming it. **Sandbox…** in its menu, or `diptick connectors sandbox`. It ends when DipTick quits.

### Skills

- **Skills in the open Agent Skills format**: instructions agents follow when a task calls for them. Add one from a folder or a zip; DipTick shows it to you whole and uses it only after your yes. See [Skills](skills).
- **On only where you put them**: the rail's SKILLS switches one on or off in the chat on screen; agents there read it in full when they need it.
- **A skill that changes waits for your yes again.**
- **A skill's scripts run** in DipTick's sandbox, with the skill's files beside them, read-only and checked against your yes; you are asked every time.

### Running code

- **Agents can run code in DipTick's own sandbox**: PowerShell 7, Python or Node, with no network, none of your folders and none of your credentials, and limits on processes, memory and time. See [Running code](running-code).
- **It is off until you turn it on** (Settings > Permissions, running programs: Ask me first), and every run asks you first, showing the whole code.
- **Your own code** runs the same way, with `diptick run`.

### Help, and your data

- **F1 opens DipTick's own pages**, these, with a search. `diptick docs` lists, prints, searches and saves them. See [The command line](command-line).
- **About DipTick** gives the version, the licence, and what DipTick collects: nothing. See [About DipTick](about).
- **Settings > Data** says where each file DipTick keeps is, and "Export all my data" writes them all to one zip. See [Your data](your-data) and [Export and backup](export-and-backup).
- **The audit log is checked against the Windows event log**, so a log deleted or begun again shows; `diptick audit accept` says you deleted an earlier log yourself. See [Activity and the audit log](activity).
- **DipTick's own event log**: `diptick audit protect`, run once as administrator, keeps the audit log's notes where nothing overwrites them and only an administrator can clear them.
