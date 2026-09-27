using System.IO;
using System.Text.Json.Nodes;

namespace ClaudeVideoEditor.Services;

/// Turns rambling spoken notes into a clear editing prompt with a one-shot,
/// tool-less Claude call, so it can't touch any files.
public static class PromptBuilder
{
    public static string Instructions(string notes, ProjectInfo project, IEnumerable<Clip> clips)
    {
        var listing = string.Join("\n", clips.Take(120).Select(c => "- " + c.Relative));
        return $@"You help a video editor write instructions for another Claude session that edits their footage with ffmpeg. Below are their spoken, unedited thoughts (speech-to-text, so expect filler words, repeats and mis-heard words). Turn them into ONE clear, detailed prompt they can send to that editing session.

Project folder: {project.Name} ({project.Path})
Video files in the folder, newest first:
{(listing.Length == 0 ? "(none found)" : listing)}

Write the prompt in the editor's own voice (""I want…""), organised under short headings, and include only what applies:
- Goal: what the final video is and where it will be posted
- Source clips: which files to use. Match mis-heard names to the real filenames above; if unsure, say ""the clips of …""
- Structure: order, what to keep and what to cut, the opening hook and the ending
- Length and pacing
- Look and sound: grade, music, ambient audio, transitions
- Text: captions, subtitles, titles
- Format: aspect ratio and resolution (default to 9:16, 1080x1920, if they mention reels, TikTok or Instagram)
- Output: a filename and save location inside the project folder
- Check-ins: ask to see the cut list before rendering anything

Keep every specific detail they mention. Don't invent preferences they didn't express. If something important is missing, add a final ""Questions for me"" section with at most 3 short questions. Output ONLY the prompt text: no preamble, no code fences.

Their thoughts:
""""""
{notes}
""""""";
    }

    public static async Task<string> BuildAsync(string notes, ProjectInfo project, IEnumerable<Clip> clips)
    {
        if (Toolchain.Claude is not string claude)
            throw new InvalidOperationException("Claude Code isn't installed. Open Setup from the Help menu.");
        // With the default agent system prompt, a tool-less run still tries to call
        // tools and fails ("tool call could not be parsed"), so replace it outright.
        var args = new[]
        {
            "-p", "--output-format", "json", "--tools", "",
            "--system-prompt", "You are a writing assistant with no tools. You never call tools or take actions; you only reply with the requested text.",
        };
        var (code, output, err) = await ProcessRunner.RunAsync(claude, args, Instructions(notes, project, clips),
                                                               Path.GetTempPath(), AuthManager.ClaudeEnvironment(), TimeSpan.FromMinutes(3));
        JsonNode? j = null;
        try { j = JsonNode.Parse(output); } catch (System.Text.Json.JsonException) { }
        if (j?["result"]?.GetValue<string>() is string text && j["is_error"]?.GetValue<bool>() != true) return text.Trim();
        var msg = j?["result"]?.GetValue<string>() ?? err.Trim();
        throw new InvalidOperationException(msg.Length > 0 ? msg : $"Claude didn't return a prompt (code {code}).");
    }
}
