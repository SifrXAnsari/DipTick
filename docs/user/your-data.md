# Your data

Everything DipTick keeps is on this PC, in one folder: your data folder. On most PCs that is `%LOCALAPPDATA%\DipTick`; the `DIPTICK_DATA_DIR` variable moves it. Settings > Data shows each file and opens its folder, and `diptick data` prints the same list. Nothing leaves this PC unless you send it, in a chat, to a provider or a service you chose (see [What providers see](what-providers-see)), or export it yourself.

## What each file is

- **sessions.db**: your chats. One file, a SQLite database, that only DipTick reads. Every line of every kept chat is in it, so a chat can be opened again after DipTick closes. With Settings > Chats > Keep sessions off, nothing is written to it. This is the database DipTick keeps for you: you never need to open it yourself, and `diptick data export` gives you everything in it as plain files.
- **audit.jsonl**: the audit log, one line for each decision the gate made: what was asked, for whom, and the answer. Never a prompt, an answer, a file's contents or a key. See [Activity and the audit log](activity).
- **audit.key** and **audit.anchor**: the log's key, which only your Windows account can open, and a note of how long the log is and how it ends, so a change to it shows. Keep them with the log.
- **ui.json**: what the window and the terminal screen remember between runs: that setup has opened once, the drafts you left unsent, and the windows and tabs open when you quit.
- **pastes**: long pastes from the terminal screen, kept while a composer or a kept draft still names them.
- **diptick.master.json**: your settings. It is not in the data folder: Settings > Data shows where it is. No key is ever in it; keys are in Windows Credential Manager (see [Keys](keys)).

The Windows Application event log also holds a note of the audit log's length, written by DipTick, so deleting the log still shows; once `diptick audit protect` has made DipTick a log of its own, the notes go there instead (see [Activity and the audit log](activity)). That note holds no content: a count, a code and which log it was.

## The program's own libraries

diptick.exe is one file that carries the libraries it draws and stores with. The first time a version starts, it unpacks them into a folder of its own, `%TEMP%\.net\diptick`, or under the folder the `DOTNET_BUNDLE_EXTRACT_BASE_DIR` variable names, and loads them from there every time after. Nothing of yours is in it. No agent may read or write there, and no connector is given it, as with your data folder. Each version unpacks into a folder of its own inside it (about 20 MB); one no version you still use needs can be deleted while DipTick is closed.

## The Database settings are something else

`diptick settings` and `diptick config` list a Database section: `db.kind`, `db.connection` and `db.secretVariable`, a connection to a database of your own. It is not where DipTick keeps anything; your chats are in sessions.db. DipTick checks these settings when it reads the file and connects to nothing with them, so Settings in the window and the terminal screen leaves the section out.

## Taking it all with you

`diptick data export <file.zip>`, or Settings > Data > Export all my data, writes all of the above into one zip file, your chats as plain text. See [Export and backup](export-and-backup).
