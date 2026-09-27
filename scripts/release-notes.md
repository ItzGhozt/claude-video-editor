Claude Video Editor edits video by chatting with Claude. It's built on
[video-use](https://github.com/browser-use/video-use) by Browser Use.

**New in this release: Windows.** A native Windows 10/11 app with everything the Mac app
has. Claude Code's sandbox isn't available on Windows, so Claude asks you in the app
(Allow / Always allow / Deny) before running commands. The Windows build passed automated
tests and screenshot checks on GitHub's Windows machines but **hasn't been tried on a real
PC yet**. Please [report anything that breaks](https://github.com/ItzGhozt/claude-video-editor/issues).

### Downloads

| File | For |
| --- | --- |
| `Claude-Video-Editor-macOS-*.zip` | macOS 14+, Apple Silicon and Intel |
| `Claude-Video-Editor-Windows-win-x64-*.zip` | Windows 10 (2004+) / 11, Intel or AMD |
| `Claude-Video-Editor-Windows-win-arm64-*.zip` | Windows 11 on ARM (e.g. Snapdragon) |

**macOS:** unzip, drag to Applications, then right-click › Open the first time (the app isn't
notarized). **Windows:** unzip and run `Claude Video Editor.exe`. If SmartScreen appears,
click More info › Run anyway (the app isn't code-signed).

### New: Saved Prompts and your style

- **Saved Prompts:** star any message you've sent to save it into a folder, and reuse it from
  the **Saved** menu.
- **Claude learns what you like:** tell it lasting preferences ("I hate fast zooms") or use
  👍 / 👎 on its replies. Every future edit and Prompt Creator prompt follows your
  **My Style** list, which you can edit anytime.

### Also in this release

- Long messages now scroll in the message box (macOS)

- The macOS app is now a universal build (Apple Silicon and Intel)
- Automated CI for both apps, and releases built by CI
- License holder: Isabel Yeow
