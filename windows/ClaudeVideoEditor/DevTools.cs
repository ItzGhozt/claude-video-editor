using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClaudeVideoEditor.Services;
using ClaudeVideoEditor.Views;

namespace ClaudeVideoEditor;

/// Checks and screenshots for CI (GitHub's Windows runners), where nobody can click.
public static class DevTools
{
    /// True while taking screenshots: suppresses the modal sign-in prompt.
    public static bool SnapshotMode { get; private set; }
    public static void BeginSnapshot() => SnapshotMode = true;

    // MARK: --self-test

    public static async Task<int> SelfTestAsync(string reportPath)
    {
        var log = new StringBuilder();
        int failures = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (!ok) failures++;
            log.AppendLine($"{(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "  -- " + detail : "")}");
        }

        try
        {
            // 1. Permission rules for a project that lives inside a private folder (Videos).
            var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            var project = Path.Combine(videos, "cve-selftest-project");
            var sibling = Path.Combine(videos, "cve-selftest-other");
            Directory.CreateDirectory(project);
            Directory.CreateDirectory(sibling);
            var info = new ProjectInfo { Name = "selftest", Path = project, AlwaysAllow = { new AllowRule("Bash", "ffmpeg:*") } };
            var settings = JsonNode.Parse(File.ReadAllText(SessionSettings.WriteFor(info)))!;
            var allow = settings["permissions"]!["allow"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
            var deny = settings["permissions"]!["deny"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
            var posixProject = SessionSettings.Posix(project);
            Check("Posix path form", posixProject.StartsWith("/c/") || posixProject.StartsWith("/d/"), posixProject);
            Check("project readable", allow.Contains($"Read(/{posixProject}/**)"));
            Check("project editable", allow.Contains($"Edit(/{posixProject}/**)"));
            Check("always-allow rule carried over", allow.Contains("Bash(ffmpeg:*)"));
            Check("Videos not denied wholesale (it contains the project)", !deny.Contains($"Read(/{SessionSettings.Posix(videos)}/**)"));
            Check("sibling of project denied", deny.Contains($"Read(/{SessionSettings.Posix(sibling)}/**)"));
            Check("project itself not denied", !deny.Any(d => d.Contains(posixProject)));
            var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            Check("Documents denied", deny.Contains($"Read(/{SessionSettings.Posix(docs)}/**)"));
            Check("reads outside working dirs blocked", settings["permissions"]!["blockReadsOutsideWorkingDirectories"]!.GetValue<bool>());

            var memDir = StyleMemory.MemoryDir;
            Check("style memory editable by Claude", allow.Contains($"Edit(/{SessionSettings.Posix(memDir)}/**)"));
            Check("style memory not denied", !deny.Any(d => d.Contains(SessionSettings.Posix(memDir))));
            var chats = Path.Combine(Toolchain.AppDataDir, "chats");
            Directory.CreateDirectory(chats);
            var settings2 = JsonNode.Parse(File.ReadAllText(SessionSettings.WriteFor(info)))!;
            var deny2 = settings2["permissions"]!["deny"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
            Check("chat history next to it stays denied", deny2.Contains($"Read(/{SessionSettings.Posix(chats)}/**)"));

            // 1b. Style memory and saved prompts round-trips.
            var mem = StyleMemory.Shared;
            var memBackup = File.Exists(StyleMemory.FilePath) ? File.ReadAllText(StyleMemory.FilePath) : null;
            mem.Clear();
            mem.Add("warm, slightly desaturated grades", true);
            mem.Add("fast zoom transitions", false);
            mem.Add("fast zoom transitions", false);
            var md = File.ReadAllText(StyleMemory.FilePath);
            Check("likes/dislikes written as Markdown", md.Contains("## Likes\n- warm, slightly desaturated grades") && md.Contains("## Dislikes\n- fast zoom transitions"));
            Check("no duplicate entries", mem.Dislikes.Count == 1);
            File.WriteAllText(StyleMemory.FilePath, md.Replace("## Dislikes\n", "## Dislikes\n- captions that cover faces\n"));
            mem.Reload();
            Check("edits Claude makes to the file are picked up", mem.Dislikes.Contains("captions that cover faces"));
            mem.Add("fast zoom transitions", true);
            Check("switching like/dislike moves the entry", mem.Likes.Contains("fast zoom transitions") && !mem.Dislikes.Contains("fast zoom transitions"));
            var sp = ClaudeSession.StylePrompt();
            Check("preferences go into the session prompt", sp.Contains("captions that cover faces") && sp.Contains(StyleMemory.FilePath));
            if (memBackup != null) File.WriteAllText(StyleMemory.FilePath, memBackup); else mem.Clear();
            mem.Reload();

            var lib = PromptLibrary.Shared;
            var before = lib.Prompts.Count;
            lib.AddFolder("CI Test Folder");
            var sv = lib.Add("", "Make a 30 second upbeat 9:16 reel from the best moments please", "CI Test Folder");
            Check("prompt saved into its folder", lib.In("CI Test Folder").Contains(sv) && lib.Contains(sv.Text));
            Check("title made from first words", sv.Title == "Make a 30 second upbeat 9:16 reel…", sv.Title);
            lib.DeleteFolder("CI Test Folder");
            Check("deleting a folder keeps its prompts (moved to Favorites)", sv.Folder == PromptLibrary.DefaultFolder && lib.Prompts.Contains(sv));
            lib.Delete(sv);
            Check("library back to how it was", lib.Prompts.Count == before);

            // 1c. Model choice.
            var savedModel = Prefs.Current.Model;
            Prefs.Current.Model = "sonnet";
            Check("model choice becomes --model", string.Join(" ", ClaudeModel.Current.Arguments) == "--model sonnet");
            Prefs.Current.Model = "";
            Check("default model adds no flag", !ClaudeModel.Current.Arguments.Any());
            Prefs.Current.Model = savedModel;
            var ms = new ClaudeSession(new ProjectInfo { Name = "model", Path = project });
            ms.HandleLine("""{"type":"system","subtype":"init","session_id":"m1","model":"claude-sonnet-5"}""");
            Check("reported model captured", ms.ActiveModel == "claude-sonnet-5");
            ms.NewChat();

            // 2. "Always allow" suggestions.
            AllowRule? S(string cmd) => ClaudeSession.SuggestRule("Bash", new JsonObject { ["command"] = cmd });
            Check("suggest ffmpeg", S("ffmpeg -y -i a.mp4 b.mp4")?.RuleContent == "ffmpeg:*");
            Check("suggest from full exe path", S("\"C:\\tools\\ffmpeg.exe\" -i a b")?.RuleContent == "ffmpeg:*");
            Check("never suggest rm", S("rm -rf x") == null);
            Check("never suggest a shell wrapper", S("powershell -c whatever") == null && S("bash -c x") == null);

            // 3. Stream parsing, including an approval request.
            var s = new ClaudeSession(new ProjectInfo { Name = "parse", Path = project });
            s.NewChat();
            s.HandleLine("""{"type":"system","subtype":"init","session_id":"abc"}""");
            s.HandleLine("""{"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"text_delta","text":"Hel"}}}""");
            Check("partial text streams", s.LiveText == "Hel");
            s.HandleLine("""{"type":"assistant","message":{"content":[{"type":"text","text":"Hello"},{"type":"tool_use","name":"Bash","input":{"command":"ffmpeg -version","description":"Check ffmpeg"}}]}}""");
            s.HandleLine("""{"type":"control_request","request_id":"r1","request":{"subtype":"can_use_tool","tool_name":"Bash","input":{"command":"ffmpeg -version"},"description":"Check ffmpeg"}}""");
            s.HandleLine("""{"type":"user","message":{"content":[{"type":"tool_result","content":"ffmpeg version 7","is_error":false}]}}""");
            s.HandleLine("""{"type":"result","subtype":"success","is_error":false,"session_id":"abc","result":"done"}""");
            var kinds = string.Join(",", s.Items.Select(i => i.Kind));
            Check("items parsed in order", kinds == "Assistant,Tool,Approval,ToolResult", kinds);
            var approval = s.Items.FirstOrDefault(i => i.Kind == ChatKind.Approval);
            Check("approval suggests ffmpeg rule", approval?.SuggestedRule?.RuleContent == "ffmpeg:*");
            Check("approval expires when the turn ends", approval?.Approval == ApprovalState.Expired);
            s.NewChat();
            s.HandleLine("""{"type":"assistant","message":{"content":[{"type":"text","text":"Not logged in · Please run /login"}]}}""");
            s.HandleLine("""{"type":"result","subtype":"success","is_error":true,"result":"Not logged in · Please run /login"}""");
            Check("error shown once, not twice", string.Join(",", s.Items.Select(i => i.Kind)) == "Error", string.Join(",", s.Items.Select(i => i.Kind)));
            s.NewChat();

            // 4. Real Claude Code on this machine (installed by CI), not signed in.
            if (Toolchain.Claude is string claude)
            {
                var (code, ver, _) = await ProcessRunner.RunAsync(claude, new[] { "--version" }, timeout: TimeSpan.FromMinutes(1));
                Check("claude --version", code == 0 && ver.Contains("Claude Code"), ver.Trim());
                var (_, status, _) = await ProcessRunner.RunAsync(claude, new[] { "auth", "status", "--json" }, timeout: TimeSpan.FromMinutes(1));
                Check("claude auth status parses", status.Contains("loggedIn"), status.Trim().Replace("\n", " "));

                // Starts a real session with our settings and flags; with no login it
                // should fail cleanly with an error in the chat, not crash.
                var live = new ClaudeSession(new ProjectInfo { Name = "live", Path = project });
                live.NewChat();
                live.Send("Say hi.");
                var deadline = DateTime.Now.AddMinutes(2);
                while (live.IsBusy && DateTime.Now < deadline) await Task.Delay(250);
                var summary = string.Join(" | ", live.Items.Select(i => $"{i.Kind}: {i.Text}"));
                Check("real session starts and finishes a turn", !live.IsBusy, summary);
                Check("settings accepted (no settings error)", !summary.Contains("settings", StringComparison.OrdinalIgnoreCase) || summary.Contains("login", StringComparison.OrdinalIgnoreCase), summary);
                log.AppendLine("      session transcript: " + summary);
                live.NewChat();
            }
            else log.AppendLine("SKIP  Claude Code not installed");

            log.AppendLine($"INFO  git bash: {Toolchain.GitBash ?? "none"}; ffmpeg: {Toolchain.Ffmpeg ?? "none"}; winget: {Toolchain.Winget ?? "none"}");
            Directory.Delete(project, true);
            Directory.Delete(sibling, true);
        }
        catch (Exception e)
        {
            failures++;
            log.AppendLine("FAIL  exception: " + e);
        }

        log.AppendLine(failures == 0 ? "ALL PASSED" : $"{failures} FAILED");
        File.WriteAllText(reportPath, log.ToString());
        return failures == 0 ? 0 : 1;
    }

    // MARK: --snapshot

    public static async Task SnapshotAsync(MainWindow mw, string dir)
    {
        SnapshotMode = true;
        Directory.CreateDirectory(dir);
        async Task Shot(Window w, string name)
        {
            await Task.Delay(1500);   // let async refreshes and layout settle
            w.UpdateLayout();
            if (w.Content is not FrameworkElement root) return;
            var dpi = VisualTreeHelper.GetDpi(root);
            var bmp = new RenderTargetBitmap((int)(root.ActualWidth * dpi.DpiScaleX), (int)(root.ActualHeight * dpi.DpiScaleY),
                                             dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            // Paint the theme background first; RenderTargetBitmap is transparent otherwise.
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(w.Background ?? Brushes.White, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
                dc.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            }
            bmp.Render(dv);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            await using var f = File.Create(Path.Combine(dir, name + ".png"));
            enc.Save(f);
        }

        for (int i = 0; i < 4; i++)
        {
            mw.ShowOnboarding(i);
            await Shot(mw, $"onboarding-{i}");
        }

        // Editor with a demo project and a sample conversation.
        var demo = Path.Combine(Path.GetTempPath(), "Demo Project");
        Directory.CreateDirectory(Path.Combine(demo, "finished"));
        foreach (var n in new[] { "beach_day_01.mp4", "beach_day_02.mp4", "sunset_timelapse.mov", @"finished\beach_reel_916.mp4" })
            File.WriteAllBytes(Path.Combine(demo, n), new byte[16]);
        var prefs = Prefs.Current;
        prefs.Projects.RemoveAll(p => p.Path == demo);
        prefs.Projects.Insert(0, new ProjectInfo { Name = "Demo Project", Path = demo });
        prefs.SelectedPath = demo;
        var session = EditorView.SessionFor(prefs.Projects[0]);
        session.NewChat();
        session.Items.Add(new ChatItem { Kind = ChatKind.User, Text = "Make a 30-second 9:16 reel from the beach clips, upbeat, and end on the sunset." });
        session.Items.Add(new ChatItem { Kind = ChatKind.Tool, Text = "Transcribing and listing the clips", Detail = "python helpers/transcribe_batch.py ." });
        session.Items.Add(new ChatItem { Kind = ChatKind.Assistant, Text = "Here's my plan: open on the wave splash from beach_day_02 (0:04), three quick beats from beach_day_01, then the sunset timelapse for the last 6 seconds. Want me to render it?" });
        session.Items.Add(new ChatItem
        {
            Kind = ChatKind.Approval, RequestId = "demo", Text = "Render the 9:16 reel",
            Detail = "ffmpeg -y -i beach_day_02.mp4 -i beach_day_01.mp4 -i sunset_timelapse.mov -filter_complex \"…\" finished/beach_reel_916.mp4",
            SuggestedRule = new AllowRule("Bash", "ffmpeg:*"),
        });
        mw.ShowEditor();
        await Shot(mw, "editor");

        // Library, with sample prompts and preferences.
        PromptLibrary.Shared.AddFolder("Reels");
        PromptLibrary.Shared.Add("Upbeat 30s reel", "Make a 30-second 9:16 reel from the best moments, upbeat, cut on the beat, end on the widest shot.", "Reels");
        PromptLibrary.Shared.Add("", "Trim the dead air and keep everything in order. Show me the cut list before rendering.", "Reels");
        StyleMemory.Shared.Add("Warm, cozy color grades", true);
        StyleMemory.Shared.Add("Cutting on the beat", true);
        StyleMemory.Shared.Add("Fast zoom transitions", false);
        var lw = LibraryWindow.Open();
        lw.FolderList.SelectedItem = "Reels";
        lw.PromptList.SelectedIndex = 0;
        await Shot(lw, "library-prompts");
        lw.Tabs.SelectedIndex = 1;
        await Shot(lw, "library-style");
        lw.Close();

        var pc = PromptCreatorWindow.Open();
        await Shot(pc, "prompt-creator");
        pc.Close();

        SimpleWindows.Credits();
        await Task.Delay(300);
        foreach (Window w in Application.Current.Windows)
            if (w.Title == "Credits") { await Shot(w, "credits"); w.Close(); break; }

        session.NewChat();
        prefs.Projects.RemoveAll(p => p.Path == demo);
    }
}
