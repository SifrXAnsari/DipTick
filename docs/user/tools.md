# Tools

DipTick has tools of its own, for you on the Tools sheet and for agents in a chat. Open the sheet from **Tools** at the foot of the rail, or with a click on the clock beside the name.

## For you

- **Clock** (F2): the time here, and the other places you keep (Add a place…, or Settings > Tools > Other time zones). Time zone ids are IANA's, like Asia/Tokyo, or Windows' own.
- **Calendar** (F4): the month, with its ISO week numbers (weeks start on Monday; week 1 holds the year's first Thursday), the week of any day you pick, and the days between two dates.
- **Network** (F7): whether this PC is online, and whether DipTick can reach each service it uses: the name looked up, the connection, TLS and the HTTP status, each timed. Type any host to check it instead. F5 checks again. One host at a time: a check, never a scan.
- **Calculator**: exact decimal arithmetic. `+ − × ÷ ^ ( )`, `%` after a number, `of` for times, and `abs`, `round`, `floor`, `ceil`, `min`, `max`, `sqrt`. 0.1 + 0.2 is 0.3 here. A power with a fractional exponent is the one thing worked in binary floating point, and the answer is marked ≈. Nothing typed is ever run as code.

## For agents

An agent whose model can call tools is offered these, and its turn can use them before it answers:

| Tool | What it does | Permission |
| --- | --- | --- |
| `time_now` | the date, weekday, time, time zone and offset, here or in another zone | none: it reads the clock |
| `calendar_days` | the days between two dates, a date plus days, a date's weekday and ISO week | none |
| `calc` | the calculator above | none |
| `net_check` | the network check, only of services DipTick already uses | `net.probe`, Ask me first by default |
| `web_search` | a search through the service you chose, results numbered | `web.search`, allowed by default: see [Research](research) |
| `web_fetch` | one web page, as readable text | `net.fetch`, answered by where the address came from: see [Research](research) |
| `list_folder`, `read_file`, `write_file` | a folder's files, a text file, a file written | `fs.read`, `fs.write`: see [Files, images and video](files) |
| `make_image`, `make_video` | an image, or a short video, made from a description | `media.make`, Ask me first by default: it costs money |
| `mcp__<connector>__<tool>` | a connector's tool, in the chats it is on in | `mcp.call:<connector>/<tool>`, Ask me first by default: see [Connectors](connectors) |
| `load_skill` | a skill on in the chat, in full, or one of its files | none: it reads only skills you approved and put in the chat; see [Skills](skills) |
| `run_code` | a short program run in DipTick's sandbox, with its output | `shell.run`, Never by default, and always asks: see [Running code](running-code) |

Every call shows in the chat, folded under the answer that made it, even when several agents answer at once: one click unfolds what it asked and what it got. Every call is also a line in the audit log (who, which tool, and a fingerprint of what it asked, never the words). What a tool gives back reaches the agent as information, never as instructions. A turn makes at most 20 calls; each has a minute (making an image has 3, making a video 7, since Veo takes up to 6), and at most 64 KiB of what it gives back reaches the agent.

Today's date is in every agent's instructions, so a date needs no call.

Whether an agent is offered tools: Settings > Models, its **Tools** row: auto (as its provider says; Ollama is asked about each model), on, or off. Each tool still goes through your permissions: see [Permissions and approvals](permissions).

## From the command line

`diptick tools` lists them. `diptick tools time [--zone <id>]`, `tools cal [<yyyy-mm>] [--between <date> <date>] [--add <date> <days>]`, `tools calc <arithmetic>`, `tools net [<host>]`. In a chat, /tools and /calc.
