# Start here

DipTick is a chat with a model: you type, it answers. It keeps your chats on this PC, asks you before an agent does anything you have not allowed, and writes every decision it makes to an audit log you can check.

## Three ways in

- **The terminal screen**: run `diptick` in a terminal, or on a PC you reach over SSH. Started from the Start menu or Explorer, it opens in a console window of its own. Its F-key bar is the bottom row, and shows what F1 to F10 do on the screen you are on. `diptick tui` opens it too.
- **The window**: `diptick window` opens it and gives the terminal back at once, free for other commands. The window runs on its own: closing the terminal leaves it open. In the terminal screen, /window or F2 opens it too, and the terminal screen goes on working. Your chats down the left, the chat in the middle, and the F-key bar across the top. `diptick ui` is another name for `diptick window`, and `--private` opens it on a private tab.
- **The command line**: everything the two screens do is a `diptick` command first. `diptick help` lists them, and [The command line](command-line) has every one.

The terminal screen and the window use the same chats, the same settings and the same audit log.

## The first time

With no model set up, setup opens by itself, once. It asks your name, which agents are told (see [What providers see](what-providers-see)); who serves the model, from every provider DipTick knows (see [Providers](providers)); what that provider needs, such as a key or an address; and which model. Nothing is saved until you say so, and Esc leaves at any step. After that, setup opens from Settings > You > Run setup again, or `diptick setup`.

## Talking

Type, and press Enter. Shift+Enter or Ctrl+J starts a new line. / shows the commands. Esc stops an answer; Esc three times, quickly, quits the screen you are on and stops everything it runs. A window opened from the terminal screen is a program of its own, and stays open.

## Where next

- [The words](the-words): what DipTick means by model, agent, provider and seat.
- [Chats](chats): keeping, finding, exporting and deleting them.
- [Keyboard](keyboard): every key on every screen.
- [Your data](your-data): what DipTick keeps, and where.
- [When something is wrong](when-something-is-wrong): where to look first.
- [What's new](whats-new): what this version does, in one list.
- [About DipTick](about): its version, its licence, and what it collects: nothing.
