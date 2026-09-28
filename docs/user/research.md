# Research

Agents can search the web and read pages, so an answer about something newer than a model's training rests on what it read, and says so. Every page an answer used is listed under it, numbered, and one click opens it.

## Two ways, by provider

- **The provider's own tools.** Claude (from Anthropic, on Claude Platform on AWS, and in Microsoft Foundry) searches with Anthropic's web search and reads pages with its web fetch; on Google Cloud it searches only. Gemini 3 and later search with Google, and on the Gemini API read the pages you name. The provider runs these itself, so DipTick shows what they did and keeps what they found, but cannot stop to ask first: they are used only where your permissions would not ask either (see below). Anthropic charges $10 per 1,000 searches; its fetch costs only the tokens of the page.
- **DipTick's own tools**, for every other agent that can call tools, local models included: `web_search`, through the search service you choose, and `web_fetch`, which DipTick does itself. Each call goes through your permissions.

The two are never offered to one agent together.

## The search service

Settings > Tools > Search service, or `diptick config set research.search brave|searxng`:

- **Brave**, with your own key from the Brave Search API: $5 per 1,000 searches, with $5 of credit each month. Save the key in Settings > Tools > Brave's key, or with `diptick key set research`. DipTick uses Brave's endpoint made for agents, which gives passages from each page with each result.
- **A SearXNG you run**, at the address in research.searxngUrl. Its JSON output must be on: add `json` under `search: formats:` in its settings.yml, or it answers 403.

DipTick has no search and no key of its own. Changing the search service asks first: a search's words leave your PC.

## Fetching a page

DipTick's `web_fetch` takes one page at a time, when an agent asks for one in a chat you are in:

- HTTPS only, GET only, and never an address with a user name or password in it.
- Never this PC, a private network or a link-local address, by name or by number; what a name leads to is checked again where it connects.
- Every redirect is a fetch of its own, asked about like the first, at most 5.
- At most 5 MiB and 30 seconds a page. Pages become their main text (the article, without menus and advertising); plain text, Markdown, CSV, JSON and XML stay as they are; a PDF goes to a model that reads PDFs as it is. Anything else is refused.
- An address that first appeared in what you wrote, in a search result, or in a page already fetched in this chat is fetched without asking, where the settings say Ask me first. Any other address asks you: a model cannot send your data out in an address it made up.

`diptick research fetch <address>` fetches a page the same way for you; `diptick research search <words>` searches.

## Sources

Every page an answer used gets a number in its chat, its title, its address and when it was read, and is listed under the answer:

- **fetched by DipTick**: DipTick read the page itself, and keeps its text with the chat;
- **cited by the provider**, with the words cited, or **read by the provider**;
- **a search result, not fetched**: the search gave its passages; nobody read the whole page.

Agents are told to cite a source as [n] and to say what they know from memory. A [n] with no source n in the chat is shown as **[n: not fetched]**: a source that does not exist reads as one. The answer itself is kept as it was written.

`/sources` lists the chat's sources, `/sources 12` one answer's; `diptick chat sources`. A private chat keeps them in memory only, like everything else of it.

## Research on or off

The **Research** chip in the chat's header says Research on, Research off, or Set up research. Off is this chat's own rules denying `web.search` and `net.fetch`, so no agent here searches or fetches until you switch it on again; `/research off`, `diptick chat research off`, or `chat say --research off`.

## Permissions

- `web.search` is allowed by default: you chose the service.
- `net.fetch` is Ask me first, answered by where the address came from, as above; a chat's own ask, or an agent's own, still asks every time; Never is never.
- A provider's own search is used only when `web.search` is allowed outright for the agent in the chat; its own fetch only when any public site is open (hosts `*`) and a fetch like the ones above would not ask.

Every search and fetch, by DipTick or by a provider, is a line in the chat and in the audit log. See [Permissions and approvals](permissions).
