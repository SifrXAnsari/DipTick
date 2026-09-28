# DipTick

A chat with models, for Windows. You type, and one agent or several answer: in a terminal, in a
window, or from the command line. DipTick keeps your chats on your PC, asks you before an agent
does anything you have not allowed, and writes every decision it makes to an audit log you can
check.

---

## What it is

- **Agents at one table.** Up to four agents in one chat, each on a model from whichever provider
  you choose. They answer together, one after another, review each other or debate, and you say
  who answers with `@name`.
- **About a hundred providers.** Runtimes on your own PC (Ollama, LM Studio, llama.cpp and more),
  the model makers' own APIs, hosts, clouds (Google Cloud, Amazon Bedrock, Microsoft Foundry) and
  gateways. The list is part of DipTick and is never fetched.
- **Tools, research and files.** A clock, a calendar, an exact calculator and a network check;
  web search and page fetching, with every source numbered under the answer that used it; images,
  PDFs and text in, images and video out; speech to text on your own PC.
- **Connectors and skills.** MCP servers for your other services, and skills in the open Agent
  Skills format, each on only in the chats you put it in.
- **Code in a sandbox.** Agents can run short programs in a Windows AppContainer inside a Job
  object: no network, none of your folders, none of your keys. It is off until you turn it on, and
  every run asks you first.
- **A gate in front of all of it.** Everything an agent can do is Allowed, Ask me first or Never:
  for everyone, for one agent, or for one chat. A question starts on Deny. Every decision goes to
  an audit log whose lines are chained, so a change to it shows.

## What it is not

- **Not a service.** There is no account, no server of its own, no subscription, no telemetry, and
  no update that arrives on its own. DipTick collects nothing about you. It talks only to the
  providers and services you choose, with your own keys, and what you send them is between you and
  them.
- **Not supported.** No technical assistance of any kind is offered. The Help inside DipTick (F1,
  or `diptick docs`) is its documentation.

## Install

Run `DipTick-1.0.1-x64.msi`. It asks Windows for an administrator, then puts `diptick.exe` in
`C:\Program Files\DipTick`, that folder on PATH, and DipTick on the Start menu. Nothing else is
needed: diptick.exe carries everything it runs on. A newer MSI replaces an older version in place.

The MSI is not signed, so Windows may say it does not know the publisher.

Uninstalling (Settings > Apps) takes away what the installer put there. Your chats and settings
stay in your own folders until you delete them: `diptick data` says where they are.

## Getting started

```
diptick            the terminal screen; setup opens the first time
diptick window     the window, while the terminal stays free
diptick help       every command
```

In the terminal screen, `/window` or F2 opens the window beside it. Type to talk, `@name` asks one
agent, `/` lists the commands, and Esc three times quits.

## Building it

On Windows (x64) with the .NET 10 SDK: `.\build.ps1`. It writes `out\diptick.exe`, one
self-contained file. `.\build.ps1 -AcceptWixEula` also builds `out\DipTick-<version>-x64.msi`
with the WiX Toolset, which runs only once its licence is accepted.

## Licence

MIT. Copyright (c) 2026 Sifr X Ansari: see [LICENSE](LICENSE). You may use it, share it, take it
apart and change it, as long as the copyright notice and the licence stay with it. It comes with
no warranty of any kind.

`diptick.exe` also carries other people's work, each under its own licence: see
[THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).
