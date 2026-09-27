# Claude Video Editor

A Mac app for editing video by chatting with Claude. Pick a folder of footage, say what you
want ("make a 30-second 9:16 highlight reel, upbeat, cut on the action"), and Claude cuts,
grades, subtitles and renders it. You can watch every clip and render right in the app.

> **The editing itself is done by [video-use](https://github.com/browser-use/video-use)**, an
> open-source Claude Code skill by **[Browser Use](https://browser-use.com)** (originally
> written by Gregor Žunič, [@gregpr07](https://github.com/gregpr07)). This app is a desktop
> front end for it. See [Credits](#credits).

![Welcome screen](docs/welcome.png)

## Features

- **Sign in with Claude**: use your Claude Pro, Max, Team or Enterprise account (the same
  login as Claude Code), or an Anthropic API key stored in your Mac's keychain.
- **Guided setup**: a checklist that installs everything video-use needs (Claude Code,
  ffmpeg, uv, video-use itself) from their official sources, with one click where possible.
- **Chat to edit**: one conversation per project, kept across restarts. Each command
  Claude runs appears as a collapsible step.
- **Quick actions**: ready-made requests (highlight reel, cut dead air, export 9:16,
  subtitles, color grade) that you can tweak before sending.
- **Prompt Creator**: press the mic, talk through what you want, and Claude turns your
  thoughts into a clear, detailed editing brief using your real clip names. Speech-to-text
  runs on your Mac.
- **Clip browser and preview**: every video in the project, newest first, with renders
  tagged NEW. Drag a clip into the chat to refer to it.
- **Sandboxed projects**: each project's Claude session can only read and write that
  folder. See [Security](#security).

![Setup checklist](docs/setup.png)

## Install

**Requirements:** macOS 14 (Sonoma) or later, and a Claude subscription or Anthropic API key.
An [ElevenLabs](https://elevenlabs.io) key (free tier available) is recommended: video-use
uses it to transcribe speech so it can cut on words.

### Download (Apple Silicon)

1. Download `Claude-Video-Editor-x.y.z.zip` from [Releases](../../releases) and unzip it.
2. Drag **Claude Video Editor** into your Applications folder.
3. The app isn't notarized by Apple, so the first time, **right-click it › Open › Open**.
   (Or run `xattr -dr com.apple.quarantine "/Applications/Claude Video Editor.app"`.)

### Build from source (Apple Silicon or Intel)

```sh
xcode-select --install          # once, if you don't have the Command Line Tools
git clone https://github.com/ItzGhozt/claude-video-editor.git
cd claude-video-editor
./build.sh                      # -> dist/Claude Video Editor.app
```

Lines mentioning `xcrun ... PlatformPath` or XCTest during the build are harmless when
full Xcode isn't installed.

## Getting started

On first launch the app walks you through four steps:

1. **Welcome**: what the app does.
2. **Sign in**: *Sign in with Claude* opens your browser. If the page shows a code, paste it
   back into the app. Or expand *Use an Anthropic API key instead*.
3. **Set up**: click **Install** on anything that isn't ticked. Homebrew (needed for
   ffmpeg) asks for your Mac password, so its installer opens in Terminal.
4. **Start guide**: how to add a project, use quick actions and the Prompt Creator.

Then click **Add Folder…**, choose a folder of footage, and start chatting. The guide is
always available under **Help › Start Guide**. Account and setup live in
**Claude Video Editor › Settings**.

## Security

Each project runs its own Claude Code session (`claude -p`, streaming JSON) with a
generated settings file ([`Sandbox.swift`](Sources/ClaudeVideoEditor/Sandbox.swift)):

- **Permission checks stay on.** Sessions use `acceptEdits` mode. Edits inside the project
  folder are auto-approved, and anything not explicitly allowed is refused (headless mode
  has no one to ask).
- **OS sandbox.** Every shell command runs under Claude Code's macOS sandbox: writes are
  allowed only in the project folder and a scratch folder. Documents, Desktop, Downloads,
  Pictures, Music, Library, `~/.ssh`, `~/.aws`, `~/.config` and past Claude transcripts are
  unreadable, and so is the rest of any of those folders when your project lives inside
  one. Commands can't opt out of the sandbox (`allowUnsandboxedCommands: false`).
- **Network allowlist.** Sandboxed commands can only reach ElevenLabs (transcription),
  PyPI, GitHub and npm (for helpers video-use installs on demand).
- Skills (video-use) are read-only to the session.

## Where things are stored

| What | Where |
| --- | --- |
| Chats | `~/Library/Application Support/Claude Video Editor/chats/` |
| Per-project sandbox settings | `~/Library/Application Support/Claude Video Editor/sandbox/` |
| Anthropic API key (if used) | macOS keychain, service "Claude Video Editor" |
| video-use | `~/Developer/video-use`, linked at `~/.claude/skills/video-use` |
| ElevenLabs key | `~/Developer/video-use/.env` (where video-use reads it) |
| Claude account login | Managed by Claude Code (shared with the `claude` CLI) |

## Development

SwiftUI plus Swift Package Manager; no Xcode project is needed.

- `swift build` compiles; `./build.sh` makes the `.app`; `VERSION=1.0.0 scripts/release.sh`
  zips it for a release (`--publish` uploads it with `gh`).
- Dev switches (environment variables): `CE_SNAPSHOT=out.png` saves images of the app's
  windows after `CE_SNAPSHOT_DELAY` seconds, `CE_ONBOARD_STEP=n` opens onboarding at step
  *n*, `CE_OPEN_PROMPT=1` opens the Prompt Creator, and `CE_TEST_SEND="…"` sends one message
  to the selected project on launch.

## Credits

- **[video-use](https://github.com/browser-use/video-use)** by **Browser Use** does all of
  the editing: transcription, cutting, color grading, subtitles, animation overlays and
  self-review of renders. It was originally written by Gregor Žunič
  ([@gregpr07](https://github.com/gregpr07)) and is MIT-licensed, © 2026 Browser Use. This
  app downloads it from its official repository at setup time and doesn't include its code.
- **[Claude Code](https://code.claude.com)** by Anthropic runs each editing session.
- **[ffmpeg](https://ffmpeg.org)** renders the video, **[ElevenLabs Scribe](https://elevenlabs.io)**
  transcribes it (via video-use), and **[uv](https://github.com/astral-sh/uv)** by Astral
  sets up the Python environment.

See [CREDITS.md](CREDITS.md) for license texts. Claude Video Editor is an independent
project and isn't affiliated with or endorsed by Anthropic or Browser Use.

## License

[MIT](LICENSE)
