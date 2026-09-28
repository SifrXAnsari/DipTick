# Providers

A provider serves the model that answers you. DipTick knows every one it could find, from runtimes on this PC to the clouds, and each says what it needs before you pick it.

## The list

`diptick providers` lists them, and so do Settings > Models > Add a model and setup's question about who serves your model. They are grouped by kind:

- **On this PC**: runtimes that serve models here, such as Ollama, LM Studio and llama.cpp. Nothing leaves this PC.
- **Model makers**: those who make models and serve their own, such as OpenAI, Anthropic and Google.
- **Hosts**: those who serve other makers' models.
- **Clouds**: platforms you reach through your own account there, such as Google Cloud, Amazon Bedrock and Microsoft Foundry.
- **Gateways**: one key in front of many providers.

In each group, the ones DipTick can talk to come first. One it cannot talk to is listed too, with the reason, so the list is whole. Some do not chat at all but make things through APIs of their own, and say so: Replicate, fal, WaveSpeedAI, ComfyUI and stable-diffusion.cpp make images and video, and whisper.cpp turns speech into text (see [Files, images and video](files)). Inferless answers through KServe's Open Inference Protocol: one prompt, one text back, no tools; it also reaches other servers that speak it.

The list is part of DipTick. It is never fetched over the network: it changes when DipTick does, and is written from each provider's own pages. Each entry says the day those pages were read, and which ones: `diptick providers show <id>`.

## Picking one

Settings > Models > Add a model shows the list, and typing narrows it. Picking one asks only for what that provider needs, then lists its models (it costs nothing), then asks where the model goes: to answer you, or as a new seat at the table. Setup asks the same way.

From the command line:

- `diptick providers show <id>` says all the list holds about one: how DipTick talks to it, its address or what you give for one, how its key is sent, where its models come from, what they can do, and its page on what it keeps.
- `diptick providers check <id>` lists its models with your key. It costs nothing, and it is still a `model.call` through the gate, recorded in the audit log.
- `diptick config set main.provider <id>` picks it for the model that answers you.

## What a provider needs

- **A key**, for most hosted providers. It is saved in Windows Credential Manager for its host only, as [Keys](keys) says, and sent the way that provider takes it.
- **An address**, for a provider that lives in your own account: a cloud, a deployment, a runtime on another port. DipTick asks only for its parts, such as a Google Cloud project and location, an AWS region or an Azure resource's name, and builds the address from them.
- **A workspace**, for Claude Platform on AWS: your workspace ID, which starts wrkspc_.
- **A Google Cloud sign-in**, for Google Cloud. Run `gcloud auth application-default login` once, or set GOOGLE_APPLICATION_CREDENTIALS to a service account's key file. DipTick reads that file itself. It asks Google for an access token when it needs one and renews it as it runs out, after an hour. Sign in again and DipTick uses the new sign-in at once, with no restart. The token is sent only to Google's own hosts; an address anywhere else is refused before any token is made.
- **A Google Compute Engine machine's own account**, where DipTick runs on one of Google's machines with no sign-in file. It is off: DipTick never asks the network for a machine's account unasked. Settings > Models > **Google Cloud machine's own account** (`providers.googleMachineAccount`) turns it on; even then DipTick asks only when the machine's firmware says it is a Google Compute Engine machine, and only the metadata server's own address, 169.254.169.254, taking a token only from an answer that says it is Google's. Agents' fetches still can never reach that address.
- **Nothing**, for a runtime on this PC, unless you locked it with a key of its own.

## Where the models come from

Most providers list their models themselves, and the list you pick from is theirs. Some have no list: DipTick then shows the models their maker publishes, or asks you to type the name (a deployment's name, for a cloud that serves your own deployments).

## What they keep

Each entry names the provider's own page on what it keeps of what you send, with a line from it where there is one. Read it before you send anything you would not want kept. What each provider sees at all is on [What providers see](what-providers-see).
