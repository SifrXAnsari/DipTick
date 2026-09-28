# Export and backup

Everything DipTick keeps is yours, and it gives all of it back.

## All your data, in one zip

`diptick data export <file.zip>` writes one zip file. In the window: Settings > Data > Export all my data. Inside:

- `README.txt`: what is inside, in plain words.
- `manifest.json`: DipTick's version, when the zip was made, and each file's size and SHA-256, so a changed file shows.
- `settings/diptick.master.json`: your settings. No key is ever in it, and the password's hash is replaced by "set" or "not set".
- `chats/<id>.md` and `chats/<id>.jsonl`: every kept chat, to read, and the same for programs.
- `audit/audit.jsonl` and `audit/verify.txt`: the audit log, and whether it was whole when the zip was made. Its key stays behind: Windows ties it to your account.
- `ui.json`: what the window and the terminal screen remember.

The zip holds your chats as plain text: keep it where you would keep them. `--chats none` leaves them out, `--chats` with ids separated by commas takes only those, and `--no-audit` leaves the log out. Opened on another PC, every chat reads as it is.

## One chat

/export and a file name, in the chat; or `diptick chat export --session <id> <file>`, with `--format jsonl` for programs.

## The audit log

`diptick audit export <file>`, or F7 on the Activity page: the entries you choose, then whether the chain verified. See [Activity and the audit log](activity).

## These pages

`diptick docs export <folder>`, or F7 in Help, writes every page as a Markdown file.
