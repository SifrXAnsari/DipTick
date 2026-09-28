# Chats and tabs

Every chat is kept in one file on this PC, `sessions.db` in your data folder, so it can be opened again after DipTick closes. Settings > Chats > Keep sessions turns that off; then a chat lasts only while it is open. A private chat is never kept at all: see [Private chats](private-chats).

## Tabs and windows

Each open chat is a tab across the top of the chat, as in a web browser. Each one answers, asks and streams on its own. When a question waits in a tab behind the others, its tab says "needs your yes" for an approval, or "waiting for you" for any other question; the right end of the bar across the top counts it, one click there goes to it, and the window's taskbar button flashes until you come back.

- **A new chat**: Ctrl+T, the + beside the tabs, the button at the top of the rail, or /new. It opens in a new tab.
- **A private chat**: Ctrl+Shift+N, the ▾ beside the tabs, or /private.
- **A new window**: Ctrl+N. Two chats side by side are two windows, which Windows snaps.
- **Every tab**: F2 lists every tab in every window and what each is doing, with New tab, New private tab, New window, Reopen closed tab and Move this tab to a new window.
- **Close a tab**: Ctrl+W, Ctrl+F4, its ✕, or a middle click. Its chat stays kept, and Ctrl+Shift+T brings the last closed tab back with its draft. A question still open in it is left, as its ✕ would leave it: an approval is denied. Closing the last tab of the last window closes the window.
- **Move between tabs**: Ctrl+Tab and Ctrl+Shift+Tab, or Ctrl+PgDn and Ctrl+PgUp; Ctrl+1 to Ctrl+8 for that tab, Ctrl+9 for the last one. Ctrl+Shift+PgUp and Ctrl+Shift+PgDn move the tab you are on.
- **Where you left off**: the windows and tabs open when you quit come back the next time, each with its draft and where it was scrolled. A private tab never does.

The terminal screen shows one chat at a time: /new, /private and /open change it. /window or F2 opens the window beside it, and the terminal screen goes on working.

## The chats on the left

The rail lists the kept chats: pinned ones first, then Today, Yesterday, This week and Older, and the box above them finds one by its title. One click opens a chat in the tab you are on, Ctrl+click opens it in a new tab, and a chat already open goes to its tab, since two tabs never hold one chat. Right-click a chat for Open in new tab, Open in new window, Rename, Pin, Export, Copy chat, Close and Delete. A click on CHATS or MODELS folds that section away, and it stays folded.

## In a chat

- **Who is in it**: its table of agents, at its top. See [Agents at one table](agents).
- **Name it**: /rename and a title. Until you name it, a chat is called by its first words.
- **Pin it**: /pin keeps it at the top of the list; /pin off lets it go back among the rest.
- **Find in it**: F3, then some words. Only the lines that hold them show, until F3 again.
- **Copy**: /copy for the last answer, /copy 2 for the one before it, /copy all for the whole chat.
- **Export**: /export and a file name writes the chat to a text file.
- **Close**: /close takes it out of the list. It is still kept: `diptick chat list --all` shows it.
- **Delete**: /delete, after your yes, for good.

## Drafts

What you type and do not send stays with its chat: switch chats and it comes back; quit and it is there next time.

## Pasting

In the window, a paste goes into the box where you type, as it is. In the terminal screen, pasted lines wait in the composer for Enter; they are never sent line by line. A paste of 5 lines or more is kept in a text file in your data folder, under `pastes`, and the composer shows its name, like `[Pasted text #1: 42 lines → paste-20260927-101503-1.txt]`. Enter sends its whole text; the chat on screen keeps the short line. The file goes when the terminal screen closes, unless a kept draft still names it. Settings > Chats > Paste to a file from changes the 5, and 0 keeps every paste in the composer.

## From the command line

`diptick chat list`, `chat show`, `chat new`, `chat say`, `chat title`, `chat pin`, `chat export`, `chat close` and `chat delete` do the same, pinned chats first in the list, and `--private` on any of them is a private chat, which export and pin refuse. `diptick window --private` opens the window on a private tab. See [The command line](command-line), and [Export and backup](export-and-backup) for taking them all with you.
