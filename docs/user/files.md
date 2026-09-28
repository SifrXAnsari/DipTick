# Files, images and video

Give an agent a picture, a PDF or a file of code and ask about it; save what it writes; have an image or a short video made from your words.

## Attaching a file

- **Attach** beside Send, or F7: pick one file or several. A file dropped anywhere on the chat is attached too, and so is an image pasted into where you type (a screenshot, say). In the terminal screen, F7 or `/attach <file>`.
- DipTick takes images (PNG, JPEG, GIF, WebP), PDFs, and text files (code, Markdown, CSV, JSON, plain text, as UTF-8): at most 20 MB a file, and 20 MB for all the files of one message, the smallest any provider takes.
- Each file waits over where you type, as a chip with its size. A chip in the warning colour names who at the table will not be sent it; the pointer on it says why. ✕ takes it off. `/detach` takes the last one off, `/detach all` every one.
- Send sends the files with your words. Files alone are a message too.
- A pasted image is held in memory until it is sent, never written to a file on the way.

## Who sees what

Each agent is sent what its model takes, and is told the name of anything it is not sent, so its answer can say so:

- Claude reads images and PDFs, on every platform: Anthropic, Claude Platform on AWS, Bedrock, Foundry and Google Cloud. An image may be about 7.5 MB, or 3.75 MB on Bedrock and Google Cloud (10 MB and 5 MB once encoded for sending).
- Models through the OpenAI shape (OpenAI, Ollama, the hosts and gateways) are sent images when their model sees; a PDF does not go through that shape.
- Gemini reads images (PNG, JPEG, WebP; no GIF) and PDFs.
- Text files go to every model, as text under their names.

Whether a model sees images comes from Ollama itself for a model on this PC, and from what DipTick knows of each provider for the rest.

## Where files are kept

What you attach, and every image made, is kept with the chat in sessions.db: its name, type, size, a SHA-256 fingerprint, and the file itself. A private chat keeps its files in memory only, like everything else of it.

Agents write files only through their tools (`write_file` below), and only into folders your permissions open. Each chat has a folder of its own, **Documents\DipTick\<chat>** (Settings > Tools > Where agents' files go, `files.folder`), which the file tools may always reach; writing there still asks first. Images made in a kept chat get a copy there too, so they open in Windows' own viewer. `files.folder` may not be a drive's root or your own profile folder.

Every file an agent reads or writes is opened by DipTick in a way no link can redirect: a junction, a symbolic link or a second name (a hard link) swapped into a folder is found on the file itself and refused, and a new file that lands anywhere else is removed before a byte is written.

## Saving

- **Save…** under any file in the chat: an image, a video, a PDF, a text file.
- **Save…** in an answer's menu (⋯, or a right-click): its words, as written, in Markdown. **Save code…** writes one of its code blocks.
- `/save <n> <file>` writes line n's words; `/save <n> --block <k> <file>` its k-th code block; `/save --file <id> <file>` a file of the chat. `/files` lists the chat's files with their numbers.

A file already there is replaced only after your yes.

## Making images

`/image <words>` makes one image from your words and keeps it in the chat, under a line that says so. An agent can make one too, with its `make_image` tool, when you ask it to.

- Settings > Tools > Images from (`media.images`): auto (OpenAI when you have saved its key, else Google), openai, google, nscale, replicate, fal, wavespeed, comfyui, sdcpp, or none.
- Image model (`media.imageModel`): gpt-image-2.5-flare unless you name another (gpt-image-2.5-sunburst takes more care); on Google, gemini-3.1-flash-image. Nscale, Replicate (`owner/name`), fal (like `fal-ai/flux/schnell`) and WaveSpeedAI (like `wavespeed-ai/flux-dev`) make images with the model you name there.
- OpenAI's sizes: 1024x1024, 1536x1024, 1024x1536, or any size in multiples of 16 with an aspect from 1:3 to 3:1 and no edge over 3840. The others take the size as you ask it.
- Every image Google makes carries a SynthID watermark.

It costs money on your own key, so it goes through `media.make`, which asks first unless you allow it, and every image is recorded.

### Other image makers

- **Replicate, fal and WaveSpeedAI** run thousands of models, each with fields of its own (an aspect ratio, a number of steps). DipTick sends your words, the size where you asked one, and the fields you set in Image model's own fields (`media.imageInput`), a JSON object such as `{"aspect_ratio": "16:9"}`; the model's page on the provider lists what it takes. Each waits on the provider's own queue, then fetches the image from where the provider put it. Your key goes only to the provider's own address, never to where the image is fetched from.
- **ComfyUI** runs your own workflow. In ComfyUI, File > Export (API) saves it as a JSON file; put `{{prompt}}` in the text of its prompt node, and `{{width}}`, `{{height}}` or `{{seed}}` in the fields you want DipTick to fill, then name the file in ComfyUI image workflow (`media.comfyImageWorkflow`). DipTick takes the first image its own save nodes report; a custom node that reports its files another way is not read.
- **stable-diffusion.cpp**'s sd-server makes images with the model it loaded, through its OpenAI-shaped address.
- A server of yours at another address or port is set in Image server's address (`media.imageAddress`): https, or http to this PC.

## Making video

`/video <words>` has Google's Veo make a short video with sound, and keeps it in the chat; an agent can too, with `make_video`.

- 4, 6 or 8 seconds; 720p, 1080p or 4k (1080p and 4k at 8 seconds only; Veo Lite has no 4k); wide, or upright.
- It takes from 11 seconds to 6 minutes. Esc stops waiting; Google may still finish it, and keeps it two days.
- The MP4 is saved in the chat's folder and shows as a card with Open (Windows' own player: DipTick does not play video) and Show in folder. A private chat keeps it in memory: Save… writes it where you say.
- Every video carries a SynthID watermark. It uses the key DipTick has for Google; Video from (`media.video`) and Video model (`media.videoModel`) in Settings > Tools.

OpenAI's video API is gone: it shut down on September 24, 2026.

Video from (`media.video`) can also name replicate, fal or wavespeed (with the model you name, your words and the fields in Video model's own fields, `media.videoInput`; the length and shape asked go only to Veo), comfyui (your video workflow, `media.comfyVideoWorkflow`, with `{{seconds}}` filled too), or sdcpp (sd-server, at the resolution asked, 16 frames a second for the seconds asked, as WebM). A video keeps the type it came in, MP4 or WebM, and its name says which.

## Speech to text

`diptick transcribe <recording>` gives the words said in a WAV, FLAC, MP3 or Ogg file; an agent can, with its `transcribe` tool, for a recording in a folder you opened to agents. It uses whisper.cpp's whisper-server, running on this PC: Speech to text (`media.speech`) whispercpp, at `http://127.0.0.1:8080` unless Speech server's address (`media.speechAddress`) says. Nothing leaves this PC unless you point that address elsewhere. Each is a `model.call`, as a question to a model is, and recorded.

## For agents

| Tool | What it does | Permission |
| --- | --- | --- |
| `list_folder` | a folder's files and folders, at most 1,000 | `fs.read` |
| `read_file` | a text file, from a line on; a long one in parts | `fs.read` |
| `write_file` | a text file, at most 4 MiB; a bare name goes to the chat's folder | `fs.write` |
| `make_image` | one image from a description | `media.make`, Ask me first |
| `make_video` | one short video from a description | `media.make`, Ask me first |
| `transcribe` | the words said in a recording, by your speech server | `fs.read`, then `model.call` |

## From the command line

`diptick chat say --attach <file> … -- <words>` (as many `--attach` as you like), `diptick chat files`, `diptick chat save --seq <n> <file>`, `diptick image [--provider <id>] [--size WxH] [--out <file>] -- <words>`, `diptick video [--seconds 4|6|8] [--resolution 720p|1080p|4k] [--portrait] [--out <file>] -- <words>`, `diptick transcribe [--out <file>] <recording>`. See [The command line](command-line).
