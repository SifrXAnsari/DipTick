using DipTick.Models;

namespace DipTick.Controllers;

/// <summary><c>diptick help [verb]</c> and <c>--help</c> anywhere. To stdout, exit 0.</summary>
internal static class HelpText
{
    private static readonly Dictionary<string, string> Verbs = new(StringComparer.Ordinal)
    {
        ["version"] = """
            usage: diptick version

            Prints "diptick <version>". With --json, as an object.
            """,
        ["init"] = """
            usage: diptick init [--force]

            Writes a starter diptick.master.json at --config's path, else %APPDATA%\DipTick\. Refuses if one is there;
            --force overwrites it, after a yes (--yes answers it). --dry-run says what it would do.
            """,
        ["config"] = $"""
            usage: diptick config [list|show]           every setting, and the layer that set it
                   diptick config get <key>             one value
                   diptick config set <key> <value>     write one value (lists: comma-separated)
                   diptick config unset <key>           remove one value so its default applies
                   diptick config check                 list problems; exit 1 if any
                   diptick config password              set the password (typed, or piped on stdin)

            Layers, strongest first: flag, {RunFlags.Prefix}* variable, the file, the default.
            permissions.* changes widen or narrow what agents may do, so they need a yes.
            'set' never writes user.passwordHash; 'config password' does.
            """,
        ["login"] = """
            usage: diptick login

            Asks for the password and checks it. Piped on stdin, reads one line.
            """,
        ["whoami"] = """
            usage: diptick whoami

            The user name, whether a password is set, and which config file is in use.
            """,
        ["policy"] = """
            usage: diptick policy [show]
                   diptick policy check <capability> [target]    the decision, recorded nowhere
                   diptick policy try <capability> [target]      through the gate: ask, record, answer
                   diptick policy set <rule> allow|ask|deny [--ai <id>] [--session <id>]
                   diptick policy unset <rule> --session <id>    a chat's own rule taken away
                   diptick policy chat [--session <id|last>]     a chat's own rules
                   diptick policy folders [add|remove <folder>]
                   diptick policy hosts [add|remove <host>]      "*" is any public site
                   diptick policy preset locked|standard|trusted

            Capabilities: model.call fs.read fs.write fs.delete net.fetch net.probe web.search
            media.make mcp.call db.read db.write shell.run; a group like fs.*; *; or one narrowed to a
            target, like mcp.call:clickup/*. Anything no rule names is denied; deny beats ask beats
            allow. set moves a rule in the settings' lists; with --ai it narrows one agent (its lane
            and its ask list); with --session it is that chat's own rule, which tightens anything and
            turns an ask into an allow in that chat only, and never loosens a deny. Letting agents do
            more asks for a yes; every change is a line in the audit log. check and try exit 1 when
            denied, 3 when a yes was needed and nobody could be asked.
            """,
        ["audit"] = """
            usage: diptick audit [<count>|--all] [--since <date>] [--who <name>] [--what <capability>] [--verdict <verdict>] [--private]
                                             the decisions made; the last 20 unless asked
                   diptick audit verify [--private]
                                             check the chain, and every anchor the Windows
                                             event log kept for it; exit 1 where it breaks
                   diptick audit accept      you deleted the earlier log the event log saw:
                                             kept as a line of the log; verify names it after
                   diptick audit protect [--status|--archive]
                                             as administrator: a Windows event log of DipTick's
                                             own for the anchors, never overwritten, cleared only
                                             by an administrator; --status says what it is;
                                             --archive saves it to ProgramData and clears it
                   diptick audit export <file> [--format jsonl|csv|text] [the same filters]
                                             the entries, then whether the chain verified

            --since takes today, yesterday, a date like 2026-09-20, or an ISO date and time. --who
            is a person's name, or an agent's (agent:<seat>). --what is a capability, like
            model.call; --verdict is Allow, Deny, Ask or DryRun. accept and export honour --dry-run
            and ask first. --private reads the private chats' decisions instead: one log a day in
            audit-private, each its own chain, deleted whole after audit.privateDays days (15 to 30).
            """,
        ["docs"] = """
            usage: diptick docs                    the pages
                   diptick docs <page>             one page, by its id or its title
                   diptick docs search <words…>    the lines that hold every word
                   diptick docs export <folder>    every page as a Markdown file

            DipTick's official pages: the same the window's Help and the terminal screen's Help
            show, inside diptick.exe, offline. Each says which version of DipTick it describes.
            """,
        ["data"] = """
            usage: diptick data                    where DipTick keeps each thing
                   diptick data export <file.zip> [--chats all|none|<id>,...] [--no-audit]

            export writes one zip: README.txt; manifest.json, with each file's size and SHA-256;
            your settings, with no key and the password's hash as "set" or "not set"; every kept
            chat as Markdown and as JSON Lines; the audit log and whether it verified; ui.json.
            It holds your chats as plain text. It honours --dry-run and asks before it overwrites.
            """,
        ["doctor"] = """
            usage: diptick doctor

            Checks the config, the audit chain, the password, the database and the models' settings.
            Exit 1 if any check fails; warnings do not fail it.
            """,
        ["completion"] = """
            usage: diptick completion pwsh

            Prints a PowerShell tab-completion script. To use it now:
              diptick completion pwsh | Out-String | Invoke-Expression
            """,
        ["seats"] = """
            usage: diptick seats [list]
                   diptick seats check
                   diptick seats add <id> --provider <p> --model <m> [--key-variable <VAR>] [--endpoint <url>] [--name <n>]
                   diptick seats remove <id>

            Up to four agents sit at the table, each with its own provider, model, key and lane.
            Adding or removing one needs your yes: it changes where the conversation goes. A seat's
            lane (<id>.lane) only narrows the permissions; it never widens them.
            """,
        ["chat"] = """
            usage: diptick chat say [--session <id|last|new>] [--to <name>|team|a,b] [--mode <mode>]
                                    [--research on|off] [--attach <file>]... [--literal] [--events] [--] <words...> | -
                   diptick chat pass [--session <id|last>] [--mode <mode>] [--events]
                   diptick chat research [--session <id|last>] [on|off]
                   diptick chat sources [--session <id|last>] [--seq <n>]
                   diptick chat files [--session <id|last>] [--seq <n>] [--file <id>] [--bytes]
                   diptick chat save [--session <id|last>] (--seq <n> [--block <k>] | --file <id>) [--] <file>
                   diptick chat connectors [--session <id|last>] [add|remove <name>]
                   diptick chat skills [--session <id|last>] [add|remove <name>]
                   diptick chat retry [--session <id|last>] [--seq <n>] [--to <name>] [--events]
                   diptick chat seats [--session <id|last>] [list] | add <agent> [--model <m>] [--as <name>] | remove <name> | model <name> <model>
                   diptick chat mode [--session <id|last>] [<mode>]
                   diptick chat brief [--session <id|last|new>] [--seat <name>] [--set <words>]
                   diptick chat cost [--session <id|last>] [--today]
                   diptick chat list [--all]
                   diptick chat show [--session <id|last>] [--seq <n> [--markdown]]
                   diptick chat new [--title <title>]
                   diptick chat title [--session <id|last>] [--] <title>
                   diptick chat pin [--session <id|last>] [--off]
                   diptick chat export [--session <id|last>] [--format text|jsonl] [--] <file>
                   diptick chat close [--session <id|last>]
                   diptick chat delete <id> --confirm=<id>

            say is one round of a kept chat: your words, then the answers of the agents at its table
            (at most four), each kept as it arrives. @name at the start asks one, @a @b those,
            @team everyone, @a>b a chain; \@ is a plain @; --to does the same, and --literal turns it
            off. With no address the chat's mode decides: all, solo <name>, pick, relay <a> <b>,
            review <builder> <reviewer> [revise], or stack <a> <b> [turns] (a debate); --mode sets
            it for one round, and 'chat mode' for the chat. An agent may put a question to another
            (its answer's ask act), which that agent answers on its next turn; an agent may call
            the tools it is offered (diptick tools), each through your permissions. pass lets them go
            on with no new words; retry asks the question behind an answer again, of the same agent
            or --to another. seats changes a chat's table (adding one asks first); brief shows
            exactly what each agent is told, and sets the chat's own brief; cost shows what the
            chat, or today, cost where prices are set. research switches the chat's searching and
            fetching on or off (off is the chat's own rules denying web.search and net.fetch), and
            say --research does it before the round; sources lists what the chat's answers were
            given to read, numbered as the answers cite them. say --attach sends a file with your words,
            as often as you give it: an image (PNG, JPEG, GIF, WebP), a PDF or a text file, at most 20 MB
            each and 20 MB together; each agent is sent what its model takes, and told of the rest.
            Files alone are a message too. files lists the chat's files, attached or made, with their
            numbers; --bytes (with --output json) adds each image's bytes in base64. save writes line
            n's words, its k-th code block (--block), or a file of the chat (--file) where you say,
            asking before it replaces one. connectors says which connectors are on in the chat, and
            switches one on or off there: agents see only the tools of those on; skills does the same
            for approved skills, which agents are told of and read when they need them. With no
            --session say starts a new chat and names it on stderr; --session last goes on with the
            newest. --output json prints one object at the end; --events prints JSON Lines as it
            happens, which is what the window draws. Words after a lone -- are always words, never
            options.

            Sessions are kept in sessions.db in DipTick's data folder (DIPTICK_DATA_DIR, else
            LocalAppData\DipTick) while chat.save is true; with it false they last as long as the
            process. 'diptick window --session <id|last|new>' opens one in the window. show prints
            every line with its author's mark; show --seq <n> prints that one line's text alone,
            an answer as it reads (the window's Copy), or as written with --markdown. export asks
            before overwriting a file. close takes a session off the list and keeps it. delete is
            for keeps and takes the id twice; --yes does not delete. pin keeps a session at the top
            of the list; --off lets it go back. A dry run changes nothing.

            --private, on any of these, is a private chat: kept in this process's memory only,
            apart from every other chat, whatever chat.save says, so 'chat say --private' lasts as
            long as that command. Its decisions go to the day's private audit log; it is never in an
            export, and export and pin refuse it.
            """,
        ["acts"] = """
            usage: diptick acts check [-|<file>] [--phase draft|review|synth|vote|revise|work] [--proposal <file>] [--unfinished]

            What DipTick would count in one agent's diptick-acts block, with no network.
            """,
        ["ask"] = """
            usage: diptick ask [--to <seat>|team|a,b] <question...>
                   diptick ask [--to ...] -          read the question from stdin

            With --to, the seats answer at once and blind, each through its own gate, recorded as
            agent:<seat>; the answers are shown attributed. The exit is 1 if any seat failed.

            Sends one question to main.provider / main.model and streams the answer to stdout. The call
            goes through the harness as model.call and is recorded. --json prints {"answer": ...} at the
            end instead; --timeout overrides main.timeoutSeconds for this run.
            """,
        ["window"] = """
            usage: diptick window [--session <id|last|new>] [--private] [--wait]   (also: diptick ui)

            The window. At a terminal it opens as a program of its own and the command returns at
            once, so the terminal stays free; closing the terminal leaves the window open. --wait
            keeps the command until the window closes, and prints there anything that stops it
            opening. It asks for the password first when one is set. With no model set up, setup
            opens inside it the first time; after that it opens from Settings, You, Run setup
            again. The F-key bar across the top shows what F1 to F10 do on the screen you are on;
            each is a button too. Type / for the commands, Ctrl+K for the command list, Ctrl+O for
            the model picker, Shift+Tab for the mode. F10, Ctrl+Q, /exit, or exit typed alone
            leaves, asking first while something runs; Esc three times in quick succession leaves
            at once and stops everything it runs, the local models included. It opens no console
            window. --private opens it on a private tab; --session opens a kept chat. The terminal
            screen opens it with /window or F2. Every flag applies to it; --no-interactive with
            window is a usage error.
            """,
        ["ui"] = """
            usage: diptick ui [--session <id|last|new>] [--private] [--wait]

            Another name for diptick window: see diptick help window.
            """,
        ["tui"] = """
            usage: diptick tui [--session <id|last|new>]   (or diptick alone, at a keyboard)

            The terminal screen: the window's screens drawn inside the terminal, and what diptick
            opens alone. It works on a machine with no desktop or in a session over SSH, and needs
            a terminal on both ends. The F-key bar on the bottom row shows what F1 to F10 do on the
            screen you are on, the window's own keys but for F2, which opens the window (/window
            too) while the terminal screen goes on working; a click runs one too. Enter sends;
            Shift+Enter (where the terminal reports it) or Ctrl+J starts a new line, and a pasted
            block stays whole until you send it. A paste of 5 lines or more (chat.pasteLines) is
            kept in a text file in DipTick's data folder, under pastes, and the composer shows the
            file's name; Enter sends its whole text. F10, Ctrl+Q, /exit, or exit typed alone
            leaves, asking first while something runs; Esc three times in quick succession leaves
            at once and stops everything it runs, the local models included, and Esc held down
            counts once. A window it opened is a program of its own and stays open. Started from
            the Start menu or a shortcut, with no terminal, it opens a console of its own.
            --session opens a kept chat; the default is a new one.
            """,
        ["tools"] = """
            usage: diptick tools [list]
                   diptick tools time [--zone <id>]
                   diptick tools cal [<yyyy-mm>] [--between <date> <date>] [--add <date> <days>]
                   diptick tools calc <arithmetic>
                   diptick tools net [<host>|<address>]

            DipTick's own tools, the same ones the Tools sheet shows and agents may call. time: the
            date and time here in your Windows format, or in another zone (a Windows id like
            "Eastern Standard Time" or an IANA id like America/New_York), with the zone's name and its
            offset from UTC. cal: a month with its ISO week numbers (today marked *), the days
            between two dates, or a date plus days; dates are yyyy-mm-dd, today, yesterday or
            tomorrow. calc: exact decimal arithmetic, + - * / ^ ( ), % after a number, 'of' for
            times, abs round floor ceil min max sqrt; parsed, never run as code; exit 1 when it
            comes to nothing. net: is this PC online, then every service DipTick uses, one at a
            time (the name looked up, the connection, TLS, the HTTP status, each timed), or the one
            host you name; exit 1 when one cannot be reached. Here you ask, so no gate; an agent's
            net_check reaches only DipTick's own services, through net.probe.
            """,
        ["research"] = """
            usage: diptick research [status]
                   diptick research search [--] <words...>
                   diptick research fetch [--] <https-address>

            status: the search service agents use (research.search: none, brave or searxng) and what
            it still needs. search: the service's results, numbered, with their passages; Brave
            needs your key (diptick key set research), a SearXNG its address and json in its
            search.formats. fetch: one page, as an agent's fetch gets it: HTTPS only, public
            addresses only (never this PC or a private network, checked again where it connects),
            each redirect said, at most 5 of them, 5 MiB and 30 seconds; its readable text printed.
            Here you ask, so no gate; an agent's search and fetch go through web.search and
            net.fetch. --output json prints the whole result. A dry run sends nothing.
            """,
        ["connectors"] = """
            usage: diptick connectors [list]
                   diptick connectors add <name> --url <address> [--header Name=value]... [--client-id <id>] [--scopes <s>]
                                          [--callback-port <n>] [--sse] [--allow-private-network]
                   diptick connectors add <name> --command <program> [--arg <a>]... [--env NAME=value]...
                                          [--network none|internet|local|both]
                   diptick connectors add <name> --from atlassian|clickup|gmail|outlook [--tenant <id>] [--client-id <id>]
                   diptick connectors import <file>
                   diptick connectors enable|disable|test|tools|signin|signout|remove <name>
                   diptick connectors secret <name> <header>|client-secret
                   diptick connectors sandbox <name> [--network none|internet|local|both] [--read <folder>]...
                                          [--write <folder>]... [--no-folders] [--on|--off]

            Connectors are MCP servers: services, or programs on this PC, whose tools agents may call.
            They are kept in connectors.json beside your settings file, in Claude Code's mcpServers
            shape, so import takes a .mcp.json as it is; no secret is ever in the file (a header names a
            variable, ${MY_TOKEN}, or its value is saved in Credential Manager with secret). A remote one
            is reached over https only (http only to this PC), never through a proxy, never following a
            redirect, and never to your private network unless --allow-private-network. A local one runs
            in a sandbox of its own: no network unless --network gives it the internet or your home and
            work networks, and none of your folders but those sandbox names (read, or read and write);
            --off runs it as you instead. It is off until enable shows you the exact program, every
            argument, each variable's name and its sandbox, and you say yes; any change asks again.
            DipTick ends it, and whatever it started, when it quits; remove takes back what it was given.
            signin signs in in your browser (the answer comes back to 127.0.0.1, and the tokens go to
            Credential Manager); with no --client-id the service registers DipTick itself, where it can.
            test connects and lists the tools; tools says what the rules say of each. Each call an agent
            makes is mcp.call:<name>/<tool>, asked first by default; 'diptick chat connectors add <name>'
            puts a connector in a chat. --output json prints the whole result.
            """,
        ["skills"] = """
            usage: diptick skills [list]
                   diptick skills show <name>
                   diptick skills add <folder|zip> [--replace]
                   diptick skills approve <name>
                   diptick skills remove <name>
                   diptick skills check

            Skills in the open Agent Skills format: a folder with a SKILL.md (a name, what it is for, then
            its instructions), and files it names. DipTick keeps its own copies in skills, beside your
            settings file, and reads skills from nowhere else. add checks one against the format, copies
            it (a zip never writes outside its folder), takes invisible characters out of its SKILL.md,
            shows you all of it, and asks your yes; the yes keeps its hash in skills.lock.json. A skill
            whose files change afterwards is offered to nobody until approve shows it again and you say
            yes. check exits 1 when any is not approved as it is. A skill's scripts run only in DipTick's
            sandbox, through run_code or 'diptick run --skill <name> <script>', with a copy of the skill
            checked against your yes, read-only, and each run asked. 'diptick chat skills add <name>' puts
            a skill in a chat: agents there are told its name and what it is for, and read it in full with
            load_skill when they need it; an agent that cannot call tools is given it whole.
            """,
        ["run"] = """
            usage: diptick run [--lang pwsh|python|node] [--seconds <n>] [--] <file>|-
                   diptick run --skill <name> [--seconds <n>] <script> [--arg <a>]...

            Runs code in DipTick's own sandbox, the way an agent's run_code does: as DipTick's
            AppContainer, with no network, none of your folders and none of your credentials, inside a
            Job object that allows at most 16 processes, 1 GiB of memory and 60 seconds of CPU, with no
            clipboard and no other windows. It writes only in its own folder, named when it ends. It
            stops at 60 seconds unless --seconds says (at most 600). The language is the file's own
            (.ps1, .py, .js) unless --lang says; the interpreter must be installed for all users, where
            the sandbox can read it. --skill runs one of an approved skill's scripts, by its path in the
            skill (scripts/extract.py), with each --arg; the skill's files are copied beside it, checked
            against your yes, read-only, at SKILL_DIR. It goes through shell.run, which always asks and
            shows the whole code, even where a rule allows it, and which the Standard permissions keep
            at Never: switch it to Ask me first to run code at all. A PowerShell script ends as pwsh -File
            would end it. Output goes to stdout, errors to stderr; the exit is 0 when the code's own exit
            was 0, else 1. A dry run runs nothing.
            """,
        ["image"] = """
            usage: diptick image [--provider openai|google|nscale|replicate|fal|wavespeed|comfyui|sdcpp] [--size WxH]
                                 [--session <id|last> [--private]] [--out <file>] [--] <words...>

            One image made from your words, by the provider media.images names (auto: OpenAI when
            its key is saved, else Google), with the model media.imageModel names; Nscale, Replicate,
            fal and WaveSpeedAI take the model you name there, and the model's own fields from
            media.imageInput (a JSON object); ComfyUI runs your workflow (media.comfyImageWorkflow),
            filling its {{prompt}}, {{width}}, {{height}} and {{seed}}; stable-diffusion.cpp's
            sd-server uses the model it loaded. A server of yours is at media.imageAddress. It costs
            money on your key, so it goes through media.make like an agent's, asks first unless
            allowed, and is recorded. --size is OpenAI's: 1024x1024, 1536x1024, 1024x1536, or any size
            in multiples of 16, an aspect from 1:3 to 3:1, no edge over 3840; others take it as asked.
            It is written to --out, else to images in your files folder; with --session it is kept in
            that chat as yours, under a line that says so. A file already there is replaced only
            after a yes; without one the new image takes the next free name, since it cost money. A
            dry run sends nothing.
            """,
        ["video"] = """
            usage: diptick video [--seconds 4|6|8] [--resolution 720p|1080p|4k] [--portrait] [--session <id|last> [--private]] [--out <file>] [--] <words...>

            A short video, made by the provider media.video names: Google's Veo unless set
            (media.videoModel; veo-3.1-generate-preview unless set) through the Gemini API, with the
            key DipTick uses for Google. 1080p and 4k are 8 seconds only; Veo Lite has no 4k. It takes
            from 11 seconds to 6 minutes; Ctrl+C stops waiting. Google keeps it for two days, and
            every video carries a SynthID watermark. Replicate, fal and WaveSpeedAI take the model
            media.videoModel names, the words, and the model's own fields from media.videoInput;
            sd-server makes it at the resolution asked for the seconds asked (16 frames a second);
            ComfyUI runs media.comfyVideoWorkflow. It costs money: media.make, asked first unless
            allowed, and recorded. Written as image writes its file, as the type it came in (MP4,
            WebM); with --session, saved to that chat's folder and kept in the chat (a private chat
            keeps it in memory only). DipTick does not play video: Open uses Windows' own player.
            """,
        ["transcribe"] = """
            usage: diptick transcribe [--out <file>] [--] <recording>

            The words said in a recording (WAV, FLAC, MP3 or Ogg), by the speech server media.speech
            names: whispercpp, whisper.cpp's whisper-server, at http://127.0.0.1:8080 unless
            media.speechAddress says. It goes through model.call, as a question to a model does, and
            is recorded. The words go to stdout, or to --out. A dry run sends nothing.
            """,
        ["setup"] = """
            usage: diptick setup [--full]

            One question at a time: your name, your provider, its key, a check that the key works (it
            lists the provider's models and costs nothing), and the model. --full also sets a
            password and whether each answer waits for your yes. Nothing is saved until you say
            so; b goes back, q or exit stops. Piped answers need --yes to save: a pipe never gives
            a yes. The window runs the same setup: by itself the first time it opens with no model,
            and after that from Settings, You, Run setup again.
            """,
        ["settings"] = """
            usage: diptick settings [<name>]

            Every setting by section, with its value; one setting in full: what it does, its
            default, what it takes, whether it needs a yes, and the command that changes it. It
            changes nothing: 'diptick config set' does, or /settings in the chat.
            """,
        ["key"] = """
            usage: diptick key [status]       where each model's key comes from; never the key
                   diptick key set <where>    save a key: typed hidden, or one line on stdin
                   diptick key clear <where>  remove a saved key (needs a yes)

            <where> is main, a seat, research (the Brave Search API's key), a provider (openai,
            anthropic, openrouter) or a host. Keys are
            kept in Windows Credential Manager, one per host, for this Windows account on this PC,
            and are only ever sent to that host. A key is never an argument. A key variable
            (main.keyVariable) wins over a saved key.
            """,
        ["models"] = """
            usage: diptick models [--for main|<seat>]
                   diptick models unload [--for main|<seat>] [--model <id>]

            The models the provider offers, from its own list, newest first. It costs nothing; it
            is still a model.call through the harness, because it carries the key. unload asks
            Ollama to let go of the model now, so it stops holding memory (a request with
            keep_alive 0, as 'ollama stop' does); --model names another model the same Ollama
            serves. A remote provider holds its own models and is not asked; another runtime on
            this PC is left running, and unload says so. Quitting DipTick unloads the models that
            answered while it ran, unless quit.unloadModels is false.
            """,
        ["providers"] = """
            usage: diptick providers [list] [--kind local|lab|host|cloud|gateway] [--works]
                                     [--can vision|tools|images|video|search|pdf] [--find <words>]
                   diptick providers show <id>
                   diptick providers check <id> [--address <url> | --<part> <value> ...]
                                               [--key-variable <VAR>] [--workspace <wrkspc_...>]

            Every provider DipTick knows: runtimes on this PC, model makers, hosts, clouds and
            gateways, grouped by kind, those that work now first. The list is compiled into
            DipTick and never fetched; each entry says the day its pages were read and which.
            show says all about one: how DipTick talks to it, its address or what you give for
            one, how its key goes, where its models come from, what they can do, and its page on
            what it keeps. check lists its models with your key (the key saved for its host, or
            --key-variable), which costs nothing; it is a model.call through the harness. A
            provider whose address is yours takes its parts (for Google Cloud, --project and
            --location), or --address. One DipTick cannot talk to is listed with the reason, and
            check refuses it.
            """,
    };

    /// <summary>The command line's page in the docs (<see cref="Docs"/>): this very help, so the two cannot differ.</summary>
    internal static string Page()
    {
        using var overview = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        _ = Write(null, overview, TextWriter.Null);
        var text = new System.Text.StringBuilder("# The command line\n\nEverything DipTick's screens do is one of these commands first, and each prints its own help: `diptick help <verb>`, or `--help` anywhere on the line.\n\n```\n");
        text.Append(overview.ToString().TrimEnd()).Append("\n```\n");
        foreach (var (verb, help) in Verbs)
        {
            text.Append("\n## diptick ").Append(verb).Append("\n\n```\n").Append(help.TrimEnd()).Append("\n```\n");
        }

        return text.ToString();
    }

    public static int Write(string? verb, TextWriter output, TextWriter error)
    {
        if (verb is not null && verb != "help")
        {
            if (!Verbs.TryGetValue(verb, out var text))
            {
                error.WriteLine($"diptick: no help for '{verb}'. Try 'diptick help'.");
                return Exit.Usage;
            }

            output.WriteLine(text);
            return Exit.Ok;
        }

        output.WriteLine($"""
            usage: diptick [flags] <verb> [arguments]
                   diptick                 alone at a keyboard: opens the terminal screen

            Verbs:
              tui           the terminal screen (also: diptick alone)
              window        the window, while the terminal stays free (also: ui)
              setup         your name, your provider, its key, its model; --full adds a password
              settings      every setting: its value, what it does, its default
              key           save, check or remove a model's key (Windows Credential Manager)
              models        the models your provider offers; unload a local one
              providers     every provider DipTick knows, and what each needs
              tools         DipTick's own tools: the clock, the calendar, the calculator, the network check
              research      the search service agents use; search it, or fetch a page, yourself
              connectors    services and programs agents may use (MCP): add, sign in, test, switch on
              skills        instructions agents follow when they need them (Agent Skills): add, approve
              run           code run in DipTick's sandbox: no network, none of your files; asks first
              image         an image made from your words (it costs money on your key)
              video         a short video made from your words (it costs money on your key)
              transcribe    the words said in a recording, by your speech server
              init          write a starter master config
              config        list, get, set, unset, check; set the password
              login         check the password
              whoami        who, and which config
              policy        what agents may do; check or try one request
              audit         the decisions made; verify the chain; export them
              doctor        check everything that can be checked
              docs          DipTick's own pages: list, read, search, export
              data          where DipTick keeps things; all of it in one zip
              completion    tab completion for PowerShell
              ask           one question to the model, or --to <seat>|team|a,b for several at once
              seats         who sits at the table: list, check, add, remove
              chat          talk in a kept session (say), and list, show, name, export, close them
              acts          check what DipTick would count in an agent's answer
              version       print the version
              help [verb]   this, or one verb's usage

            Flags (anywhere on the line; each has an off form):
              -n, --dry-run           change nothing, say what would change     --no-dry-run
              -y, --yes               answer yes to your own confirmations      --no-yes
              -i, --interactive       may ask questions (default: at a keyboard) --no-interactive, --no-input
                  --output <fmt>      text, json or none; --json for short      --no-json
                  --verbosity <lvl>   quiet, normal, verbose, debug; -q, --verbose, -d   --no-quiet ...
                  --color <when>      auto, always, never                       --no-color
                  --config <path>     this master config, or none               --no-config
                  --timeout <s>       seconds per database or network call      --no-timeout
              -h, --help              usage;  --version  the version

            Each flag also reads {RunFlags.Prefix}<NAME> (e.g. {RunFlags.Prefix}DRY_RUN) and the master config's
            "cli" section, in that order after the flag. NO_COLOR is honoured.

            --yes never answers an agent's request to write or delete; a person always does.
            The master config is {MasterConfig.FileName}: --config, else ${MasterConfig.PathVariable},
            else %APPDATA%\DipTick\, else beside diptick.exe. Never the current folder: a config
            there could have been written by anything, an agent included.

            Exit codes: 0 done, 1 failed or refused, 2 usage, 3 nothing done without a yes.
            """);
        return Exit.Ok;
    }
}
