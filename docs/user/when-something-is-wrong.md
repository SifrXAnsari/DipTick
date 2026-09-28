# When something is wrong

## First: diptick doctor

`diptick doctor`, or /doctor in a chat, checks everything it can without changing anything or spending anything: the settings file, the password, the audit log's chain, your permissions, the main model's setup and key, and each seat. It says what it found, and what to do.

## Often

- **No model is set up yet**: run setup: Settings > You > Run setup again, or `diptick setup`.
- **The provider refused**: DipTick shows the provider's own words. A 401 means the key is wrong or missing: Settings > Models > Key, or `diptick key set main`. A 404 usually means the provider has no model by that name: /model lists theirs.
- **Ollama does not answer**: is it running? `ollama list` in a terminal says. DipTick looks for it at http://localhost:11434 unless Settings > Models > Address says otherwise.
- **The audit log does not verify**: `diptick audit verify` says where it breaks. DipTick adds nothing to a log that does not match its note; see [Activity and the audit log](activity). A log you deleted yourself also shows here, because the Windows event log remembers it; `diptick audit accept` says it was you.
- **A setting seems ignored**: `diptick config list` shows each value and where it came from, and `diptick config check` names every problem in the file. A value that does not fit is set aside and its default used.
- **The window does not open**: a failure before it opens shows in a message box. `diptick window --wait`, in a terminal, keeps the window tied to that terminal until it closes, so what stops it is printed there. The terminal screen, `diptick`, has the same chats and settings meanwhile.
- **A pasted block went to the wrong place**: in the terminal screen, a paste goes to the composer only while it has the keyboard; see [Chats](chats).

## When you ask someone for help

`diptick doctor --output json` and `diptick audit verify` give what someone helping you needs. Neither holds a key or a chat.
