# The words

DipTick uses a few words the same way everywhere: on its screens, in these pages and in its commands.

- **Model**: what answers you. A model has a name, like llama3.2 or claude-opus-5.
- **Provider**: who serves the model: a runtime on this PC such as Ollama, a maker such as OpenAI or Anthropic, a host, a cloud or a gateway, or another server that speaks the OpenAI chat API, which DipTick calls openai-compatible. `diptick providers` lists them all.
- **Agent**: a model at work in a chat. When DipTick says an agent asked to do something, it means the model asked, through DipTick.
- **Main model**: the model that answers in the chat. Its settings are the main section: `main.provider`, `main.model`, `main.endpoint`.
- **Seat**: another model at the table, with its own provider, model, key and lane. `diptick ask --to team` asks every seat at once.
- **Lane**: what a seat may do, as capabilities.
- **Chat**: one conversation, kept so it can be opened again. The command line also calls it a session.
- **Draft**: what you have typed and not sent. It stays with its chat, and quitting keeps it.
- **Capability**: something an agent can be allowed to do, like `model.call` (answer) or `fs.read` (read a file). See [Permissions and approvals](permissions).
- **The gate**: the part of DipTick that decides each request against your permissions, asks you when they say to, and records the decision.
- **Audit log**: the record of those decisions. See [Activity and the audit log](activity).
- **Data folder**: where DipTick keeps your chats, the audit log and what the window and the terminal screen remember. See [Your data](your-data).
