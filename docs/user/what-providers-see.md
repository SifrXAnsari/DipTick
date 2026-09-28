# What providers see

When an agent answers in a chat, DipTick sends its provider:

- the model's name;
- what the agent is told before the chat, exactly as **What they're told** shows it: the name you gave DipTick, in setup or Settings > You > Your name ("the person" when you gave none), who else is at the table with each one's provider and model, today's date, the tools and skills it may use, and your instructions (see [Agents at one table](agents));
- the chat so far: what you said and the answers that finished, the other agents' marked as theirs, then your new words (a paste kept in a file goes as its whole text);
- the files you attached, where its model takes them (see [Files, images and video](files));
- what its tools gave back, such as a file it read, a page it fetched, a search's results, a connector's answer or a run's output, each only as your permissions allow (see [Tools](tools));
- the key, where the provider needs one, to the host it was saved for and to no other; for Google Cloud, a token from your sign-in, only to Google.

Never sent: DipTick's own lines in the chat, your other settings, the audit log, or your other chats. A file on this PC goes only when you attach it, or when an agent reads it through a tool your permissions allow.

## Other calls

- Setup's key check and /model ask the provider for its list of models, with the key. That costs nothing.
- Quitting asks Ollama on this PC to let go of the models that answered (Settings > Models > Unload local models on quit). No other provider is told anything.
- `diptick ask --to team` sends the one question to each seat's provider, each through the gate.

## Other services you choose

- A search goes to your search service, Brave or your own SearXNG, with its words; a page DipTick fetches is asked of its own site. See [Research](research).
- A connector's tool call goes to that connector, its service or its program on this PC, with what the agent sends it. See [Connectors](connectors).
- An image or a video is asked of the maker you chose, with your words; a recording goes to your speech server. See [Files, images and video](files).

What a provider or a service does with what it is sent is up to its own terms. Ollama on this PC keeps it on this PC; a hosted provider receives it on its servers.

Each answer is one decision in the audit log, `model.call`, with the host it went to and never the words. See [Activity and the audit log](activity).
