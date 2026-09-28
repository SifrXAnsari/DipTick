# Private chats

A private chat keeps nothing. Nothing of it goes to `sessions.db`, it is never in the list on the left, its tab and its draft are never remembered for next time, and it is never in an export. When its tab closes it is gone for good: Ctrl+Shift+T does not bring it back.

## Starting one

Ctrl+Shift+N, New private tab under the ▾ beside the tabs or in F2's list of tabs, or /private in any chat. The tab says "Private" in words. From the command line, `diptick window --private` opens the window on a private tab, and `diptick chat say --private -- …` has a private chat that lasts as long as that one command.

## What it promises

Before you send anything, the chat says who can see what you send:

- With every agent at the chat's table on this PC: "Private: nothing of this chat is kept, and nothing leaves this PC."
- With any of them somewhere else: "Private: nothing of this chat is kept on this PC", then, for each agent whose model runs elsewhere, who runs it and which model it is, as seeing what you send to it. Whoever runs a model sees what you send it and what it answers, and what they keep is theirs to say: see [What providers see](what-providers-see).

What you copy from a private chat goes to the clipboard with the format Windows keeps out of its clipboard history and off your other devices.

## What an agent did stays checkable

Whether an agent was allowed to do something is recorded as always, without any words from the chat, in a log of its own: one file a day in `audit-private` in your data folder, each day its own keyed chain. A day's file is deleted whole once that day began 30 days ago, or however many days Settings > Chats > Private decisions kept for says (15 to 30), so no line is kept longer; DipTick deletes it the next time it opens a chat after that. The main audit log never holds a private chat's lines. `diptick audit --private` shows them, and `diptick audit verify --private` checks each day. Unlike the main log, they leave no note in the Windows event log, since they are deleted on purpose.

## What it cannot promise

- **What you keep yourself**: what you save, copy or paste somewhere else.
- **What Windows writes from memory**: the page file can hold memory any program used, when Windows moves it out of RAM; hibernating, or hybrid sleep, writes a copy of memory to disk; and a crash dump, when one is set up or made in Task Manager, holds a program's memory (Microsoft's pages on the page file, on hibernation, and on collecting user-mode dumps).
- **What your terminal keeps**: in the terminal screen, what was on screen is your terminal's too. Windows Terminal 1.21 and later, when its startup is set to open windows from a previous session, remember what was on the screen and show it again when it starts.
- **What the provider keeps**, for a model that is not on this PC.
