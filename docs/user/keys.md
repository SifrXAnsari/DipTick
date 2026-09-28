# Keys

A key is what a hosted provider asks for before it answers: OpenAI, Anthropic and most of the others [Providers](providers) lists. A runtime on this PC, such as Ollama, needs none, and Google Cloud uses your Google Cloud sign-in instead.

## Where a key is kept

Setup, Settings > Models > Key, and `diptick key set main` save a key in Windows Credential Manager, under your Windows account, for the one host it belongs to. It is never written to the settings file, the audit log, a chat or any output, and it is sent only to the host it was saved for: DipTick follows no redirect with it.

For scripts, a key can come from an environment variable instead: `main.keyVariable` names the variable, its name only, never its value. A key in that variable wins over a saved one.

## Commands

- `diptick key`: where each model's key comes from; never the key itself.
- `diptick key set <where>`: save one, typed without showing, or one line from standard input.
- `diptick key clear <where>`: remove a saved one, after your yes.

`<where>` is main, a seat, research (your Brave Search API key, for agents' searches: see [Research](research)), a provider (openai, anthropic, openrouter) or a host. Google's Gemini API and Google Cloud's express mode (the Gemini Enterprise Agent Platform with an API key) take their keys in a header, never in an address.

## If a provider says no

A provider that refuses a key (401) says so in the chat, in its own words. Save the key again: Settings > Models > Key, or `diptick key set main`. See [When something is wrong](when-something-is-wrong).
