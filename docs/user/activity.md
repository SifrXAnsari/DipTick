# Activity and the audit log

Each time an agent asks to do something, the gate decides, and the decision goes to the audit log, `audit.jsonl` in your data folder: when, for whom, which capability, on what, the verdict and why. Never a prompt, an answer, a file's contents or a key; a web address is kept without its query.

## The Activity page

Activity, at the foot of the window's rail, shows the log, newest first.

- F2 Filter: by who asked, by what (the capability), or by the verdict.
- F3 Find: only the lines that hold some words.
- F4 Range: today, the last 7 days, or everything.
- F5 Refresh, and F8 Verify: whether the log is whole.
- F7 Export: the entries shown, to a file (JSON Lines, CSV or text), then whether the chain verified.

## How a change to the log shows

Each line carries a code, an HMAC, made from the line itself and the line before it, with a key only your Windows account can open. A line changed or taken out breaks the chain from there on. A note of how many lines the log has and how its last line ends, `audit.anchor`, catches lines cut off the end, and DipTick refuses to add to a log that does not match it.

DipTick also writes that note to the Windows Application event log: at most once a minute while it writes, and when it closes. So deleting the log, its key and its note together, and beginning again, still shows. `diptick audit verify` compares the log with every note the event log kept for it. A normal Windows account cannot clear that event log; an administrator's program can.

The Application log keeps a fixed size, 20 MB unless an administrator changed it, and its oldest events make way for new ones: weeks of them, usually. It remembers DipTick's notes that long, and a program running as you could write enough to push them out sooner. So `diptick audit verify` also says how far back the Application log reaches.

## A log of DipTick's own

`diptick audit protect`, run once from a terminal opened as administrator, gives DipTick a Windows event log of its own, named DipTick, for these notes. It is 1 GiB and never overwrites anything: when it is full, new notes are not kept, and every note already in it stays. Programs running as you can write to it but not clear it; only an administrator can, and DipTick's sandbox cannot even write to it. From then on the notes go there, and verify reads it as well as the Application log, which still holds the notes from before.

A program running as you could still fill it. Verify then says it is full, and since when; `diptick audit protect --archive`, as administrator, saves it whole to `ProgramData\DipTick\anchors`, where only administrators write, and clears it, and verify reads the archives too. `diptick audit protect --status` says what the log is now, for anyone.

## When you deleted it yourself

If you delete DipTick's data folder to start over, verify still reports the earlier log, because the event log remembers it. `diptick audit accept` says it was you: it asks first, and your yes becomes a line of the new log, chained like the others. From then on verify passes, and names the earlier log and when you said so. Windows does the same with its own Security log: clearing it writes an event saying it was cleared, and by whom.

Only what the event log saw can be accepted this way. A line changed inside the log still breaks the chain, and anything the event log sees after your yes is checked as before.

## From the command line

- `diptick audit`: the last 20 decisions; `diptick audit 50`, the last 50; `--all`, every one; `--since 2026-09-01`, from a date (also today or yesterday); `--who <name>`, one person's or one agent's; `--what model.call`, one capability; `--verdict Deny`, one verdict.
- `diptick audit verify`: whole, or where it breaks.
- `diptick audit accept`: the earlier log was yours to delete.
- `diptick audit export <file> [--format jsonl|csv|text]`, with the same `--since`, `--who`, `--what` and `--verdict`.
