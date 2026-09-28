# Agents at one table

A chat can seat up to four agents: the model that answers you, and the agents set up in Settings > Models. They answer you together, they answer each other, and any of them can put a question to another. Every turn is kept under its agent's name, goes through that agent's own permissions, and is recorded in the audit log.

## The table

The chips at the top of the chat are its table: each agent's colour, its name in this chat, and its model.

- **One click on an agent**: another model for it in this chat only (Settings keep its own), what it is told, ask it the last question, take it from this chat, or its settings.
- **+ Add agent** seats another agent set up in Settings > Models. One already here can sit again with another model, under that model's name, so two models of one provider can answer side by side. **Set up another agent…** starts Settings > Models > Add a model. DipTick asks before seating one, since the chat is then sent somewhere new.
- **A new chat** starts with the model that answers you and the agents Settings > Chats > New chats seat names.
- **F4** opens the table from the keyboard: every agent, adding one, who answers, and what they're told.

A chat nobody has spoken in yet is kept from the moment its table or its mode changes.

## Who answers

Start what you say with a name to choose who answers it; the line over the box where you type says who will, before you send.

| You write | Who answers |
| --- | --- |
| `@tick why?` | tick only |
| `@dip @tick why?` | dip and tick, at the same time, neither seeing the other's answer |
| `@team why?` or `@all why?` | every agent here (`@both` too, with two) |
| `@dip>tick why?` | dip, then tick, who sees dip's answer |
| `\@dip` | nobody new: a plain @ |

A name nobody at the table has is refused before anything is sent. An `@name` in the middle of what you say, or in an agent's answer, addresses nobody.

With no name, the chat's mode decides, from the chip under the table:

- **All answer**: every agent at once, each blind to the others' answers in that round. The first thing a new chat does.
- **tick answers**: one agent.
- **You pick who answers**: nobody, until you name someone.
- **dip, then tick**: a chain; each sees the ones before it.
- **tick reviews dip**: dip does the work, tick checks it; with revise, dip then answers the review. A review that finds nothing ends the round.
- **Debate**: the agents answer each other in turns, each the last speaker, 2 turns an agent unless you say how many.

`/mode` shows and changes it; Settings > Chats > New chats' mode sets it for new chats. Settings > Chats > Turns a round (4, at most 8), Seconds a round and Tokens a round keep a round in bounds, and a round cut short says so.

## Talking to one another

- In a debate each agent sees the others' answers, marked as theirs, and answers the last one. Two passes in a row, an answer that repeats an earlier one, or `[hand back]` ends it; `/pass` lets them go on with no new words from you.
- Any agent can put a question to another. It shows under the answer that asks it ("asks tick: …"), every agent and you see it, and the other answers it in its next turn. A question put to you hands the floor back to you.
- An agent cannot give another instructions or approve anything: only you do. What an agent writes is shown, never obeyed as a command.

## Answers

- Each answer is under its agent's name and colour, with the tokens it read and wrote (~ where DipTick counted them itself, the provider not saying) and, where the agent's prices are set, what it cost.
- Answers given at the same time show one under another; **Compare** on one of them lays them side by side, and **One under another** puts them back.
- An answer's menu (right-click, or ⋯): **Ask it again** (F5 for the last answer), and **Ask another agent**, the same question to another at the table. The earlier answer stays.

## What they're told

The **What they're told** chip opens exactly what each agent is sent before the chat: who is here, how to read what it is sent, how to put a question to another agent, its part this turn, and your own instructions, marked as yours: Settings > You > Your instructions for every chat, the agent's own (Settings > Models), and this chat's brief, which you can change there. `/brief` shows the same, and `/brief` with words sets this chat's brief.

## What it costs

- The chat's cost shows at the top right, and today's in the status bar, where prices are set (Settings > Models, each agent's Price in and Price out, per million tokens). `/cost` gives each agent's part; `/cost today` every chat's today.
- Settings > Money can cap a chat, a day or a month; each cap is off until you set it. An answer that could take the spending past a cap is not asked for, and the chat says why. Spending never waits for a yes otherwise.

## The terminal screen

The line at the top lists the table and who answers. `/seats`, `/mode`, `/retry`, `/brief`, `/cost` and `/pass` do what the chips do, and F4 shows the table, F5 asks the last question again.

## From the command line

`diptick chat seats` lists a chat's table, and `add`, `remove` and `model` change it; `chat mode` shows or sets the mode; `chat say --to <name>|team|a,b`, `--mode <mode>` for one round and `--literal` to send an @ as it is; `chat retry [--seq <n>] [--to <name>]`; `chat brief [--seat <name>] [--set <words>]`; `chat cost [--today]`; `chat pass`. `chat say --events` writes each step of a round as it happens. See [The command line](command-line).
