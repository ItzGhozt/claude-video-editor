using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Windows;

namespace ClaudeVideoEditor.Services;

public enum ChatKind { User, Assistant, Tool, ToolResult, Notice, Error, Approval }
public enum ApprovalState { Pending, Allowed, AlwaysAllowed, Denied, Expired }

public class ChatItem : INotifyPropertyChanged
{
    public ChatKind Kind { get; set; }
    public string Text { get; set; } = "";
    public string? Detail { get; set; }

    // Approval cards
    public string? RequestId { get; set; }
    [JsonIgnore] public JsonNode? Input { get; set; }
    public AllowRule? SuggestedRule { get; set; }
    ApprovalState _approval;
    public ApprovalState Approval { get => _approval; set { _approval = value; Notify(); Notify(nameof(IsPending)); Notify(nameof(ApprovalSummary)); } }
    public bool IsPending => Kind == ChatKind.Approval && Approval == ApprovalState.Pending;
    public string ApprovalSummary => Approval switch
    {
        ApprovalState.Allowed => "Allowed once",
        ApprovalState.AlwaysAllowed => $"Always allowed in this project: {SuggestedRule?.RuleContent}",
        ApprovalState.Denied => "Denied",
        ApprovalState.Expired => "No longer needed",
        _ => "",
    };
    public string AlwaysLabel => SuggestedRule == null ? "" : "Always allow " + SuggestedRule.RuleContent.Replace(":*", "").Replace(" *", "");

    public event PropertyChangedEventHandler? PropertyChanged;
    void Notify([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

/// One long-lived `claude -p --input-format stream-json …` process per project.
/// Messages go in on stdin as JSON lines; events come back on stdout. Actions that
/// need approval arrive as `control_request`/`can_use_tool` and are answered with a
/// `control_response` when the user clicks Allow / Always allow / Deny.
/// If the process dies (Stop, crash, relaunch) the next message restarts it with
/// --resume, so the conversation carries on.
public class ClaudeSession : INotifyPropertyChanged
{
    public ProjectInfo Project { get; }
    public ObservableCollection<ChatItem> Items { get; } = new();

    string _live = "", _draft = "";
    bool _busy;
    int _turns;
    public string LiveText { get => _live; private set { _live = value; Notify(); Notify(nameof(HasLiveText)); } }
    public bool HasLiveText => LiveText.Length > 0;
    public bool IsBusy { get => _busy; private set { _busy = value; Notify(); } }
    public string Draft { get => _draft; set { _draft = value; Notify(); } }
    /// Bumped after every reply; the clip list watches it to pick up new renders.
    public int TurnsFinished { get => _turns; private set { _turns = value; Notify(); } }

    string? _sessionId;
    Process? _proc;
    ClaudeModel? _runningModel;
    string _activeModel = "";
    /// The model Claude reports for the running session, e.g. "claude-sonnet-5".
    public string ActiveModel { get => _activeModel; private set { _activeModel = value; Notify(); } }
    string _stderrTail = "";

    public ClaudeSession(ProjectInfo project)
    {
        Project = project;
        Load();
    }

    static string AppendedPrompt()
    {
        var py = Toolchain.VideoUsePython is string p ? $"Run video-use helpers with `{p}`, which has their dependencies. " : "";
        return "You are running inside \"Claude Video Editor\", a Windows app that wraps this session. The user sees " +
               "your replies as chat bubbles and has a clip browser showing the video files in this folder. " +
               "Save rendered outputs inside this folder (for example under an `edits` or `finished` subfolder) so they " +
               "show up in the browser. Refer to files by their path relative to this folder. Keep replies short and " +
               "plain-spoken; the user is an editor, not a programmer. For editing work use the video-use skill if it is " +
               "available. " + py + "You can only work inside this folder; if something outside it is needed, ask the " +
               "user to copy it in. Commands other than simple file operations need the user's approval, which they give " +
               "in the app; prefer a few well-chosen commands over many small ones.\n\n" + StylePrompt();
    }

    /// The user's likes/dislikes, and how to keep them up to date.
    internal static string StylePrompt()
    {
        StyleMemory.Shared.Reload();
        var current = StyleMemory.Shared.Summary is string s
            ? "The user's saved style preferences (apply them unless they say otherwise):\n" + s
            : "The user hasn't saved any style preferences yet.";
        return current + "\nTheir preferences file is " + StyleMemory.FilePath + ". When the user states a lasting preference " +
               "about how they like their videos (for example \"I hate fast zooms\", \"always use warm grades\", \"keep reels " +
               "under 30 seconds\"), update that file with the Edit tool: add one short bullet under \"## Likes\" or " +
               "\"## Dislikes\", don't duplicate, and remove any entry it contradicts. Then tell them in a few words that you " +
               "saved it. Don't record one-off instructions for a single edit, and never write anything else in that folder.";
    }

    // MARK: Sending

    public void Send(string raw)
    {
        var text = raw.Trim();
        if (text.Length == 0 || IsBusy) return;
        Items.Add(new ChatItem { Kind = ChatKind.User, Text = text });
        Draft = "";
        IsBusy = true;
        // Picked a different model since this session started: restart on the new
        // one. --resume keeps the conversation.
        if (_proc is { HasExited: false } running && _runningModel != ClaudeModel.Current)
        {
            _proc = null;
            try { running.Kill(true); } catch { }
            Items.Insert(Items.Count - 1, new ChatItem { Kind = ChatKind.Notice, Text = "Now using " + ClaudeModel.Current.Name });
        }
        if (_proc is not { HasExited: false })
        {
            try { Start(); }
            catch (Exception e)
            {
                Items.Add(new ChatItem { Kind = ChatKind.Error, Text = "Couldn't start Claude: " + e.Message });
                IsBusy = false;
                return;
            }
        }
        Write(new JsonObject
        {
            ["type"] = "user",
            ["message"] = new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
            },
        });
        Save();
    }

    public void Stop()
    {
        if (_proc is not { HasExited: false } p) return;
        try { p.Kill(true); } catch { }
        if (IsBusy)
        {
            CommitLiveText();
            ExpireApprovals();
            Items.Add(new ChatItem { Kind = ChatKind.Notice, Text = "Stopped." });
            IsBusy = false;
        }
        Save();
    }

    public void NewChat()
    {
        Stop();
        _proc = null;
        _sessionId = null;
        Items.Clear();
        LiveText = "";
        Save();
    }

    // MARK: Approvals

    public void Respond(ChatItem item, ApprovalState decision)
    {
        if (!item.IsPending || item.RequestId == null) return;
        JsonObject response;
        if (decision == ApprovalState.Denied)
        {
            response = new JsonObject { ["behavior"] = "deny", ["message"] = "The user clicked Deny in the app." };
        }
        else
        {
            response = new JsonObject { ["behavior"] = "allow", ["updatedInput"] = item.Input?.DeepClone() ?? new JsonObject() };
            if (decision == ApprovalState.AlwaysAllowed && item.SuggestedRule is AllowRule rule)
            {
                // Takes effect now for this session, and via the settings file next time.
                response["updatedPermissions"] = new JsonArray(new JsonObject
                {
                    ["type"] = "addRules",
                    ["rules"] = new JsonArray(new JsonObject { ["toolName"] = rule.ToolName, ["ruleContent"] = rule.RuleContent }),
                    ["behavior"] = "allow",
                    ["destination"] = "session",
                });
                if (!Project.AlwaysAllow.Contains(rule)) { Project.AlwaysAllow.Add(rule); Prefs.Current.Save(); }
            }
        }
        Write(new JsonObject
        {
            ["type"] = "control_response",
            ["response"] = new JsonObject { ["subtype"] = "success", ["request_id"] = item.RequestId, ["response"] = response },
        });
        item.Approval = decision;
        Save();
    }

    void ExpireApprovals()
    {
        foreach (var i in Items.Where(i => i.IsPending)) i.Approval = ApprovalState.Expired;
    }

    /// "Always allow" offers a rule for the command's program, e.g. `ffmpeg:*`,
    /// never for the exact one-off command line.
    internal static AllowRule? SuggestRule(string tool, JsonNode? input)
    {
        if (tool is not ("Bash" or "PowerShell")) return null;
        var cmd = input?["command"]?.GetValue<string>()?.Trim() ?? "";
        if (cmd.Length == 0) return null;
        var first = cmd.Split(new[] { ' ', '\t', '\n' }, 2)[0].Trim('"', '\'', '&');
        var name = Path.GetFileNameWithoutExtension(first);
        if (name.Length == 0 || name.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '_' or '.'))) return null;
        // Blanket-allowing a shell or interpreter wrapper would allow everything.
        var wrappers = new[] { "cmd", "powershell", "pwsh", "bash", "sh", "sudo", "env", "cd", "rm", "del", "Remove-Item" };
        if (wrappers.Contains(name, StringComparer.OrdinalIgnoreCase)) return null;
        return new AllowRule(tool, name + ":*");
    }

    // MARK: Process

    void Start()
    {
        if (Toolchain.Claude is not string claude)
            throw new InvalidOperationException("Claude Code isn't installed. Open Setup from the Help menu.");
        var args = new List<string>
        {
            "--settings", SessionSettings.WriteFor(Project),
            "--permission-mode", "acceptEdits",
            "--permission-prompts", "host", "--permission-prompt-tool", "stdio",
            "-p", "--input-format", "stream-json", "--output-format", "stream-json",
            "--verbose", "--include-partial-messages",
            "--append-system-prompt", AppendedPrompt(),
        };
        args.AddRange(ClaudeModel.Current.Arguments);
        if (_sessionId != null) { args.Add("--resume"); args.Add(_sessionId); }

        var p = new Process
        {
            StartInfo = ProcessRunner.StartInfo(claude, args, Project.Path, AuthManager.ClaudeEnvironment()),
            EnableRaisingEvents = true,
        };
        var ui = Application.Current.Dispatcher;
        p.OutputDataReceived += (_, e) => { if (e.Data is string line) ui.BeginInvoke(() => Handle(line)); };
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not string line) return;
            ui.BeginInvoke(() =>
            {
                _stderrTail += line + "\n";
                if (_stderrTail.Length > 2000) _stderrTail = _stderrTail[^2000..];
            });
        };
        p.Exited += (_, _) => ui.BeginInvoke(() => Exited(p));
        _stderrTail = "";
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        _proc = p;
        _runningModel = ClaudeModel.Current;
    }

    void Write(JsonNode msg)
    {
        if (_proc is not { HasExited: false } p) return;
        p.StandardInput.Write(msg.ToJsonString() + "\n");
        p.StandardInput.Flush();
    }

    void Exited(Process p)
    {
        if (!ReferenceEquals(p, _proc)) return;
        _proc = null;
        ExpireApprovals();
        if (!IsBusy) return;
        CommitLiveText();
        var why = string.Join("\n", _stderrTail.Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(6));
        Items.Add(new ChatItem { Kind = ChatKind.Error, Text = why.Length > 0 ? why : $"Claude exited unexpectedly (code {p.ExitCode})." });
        IsBusy = false;
        Save();
    }

    // MARK: Parsing stream-json

    /// Test hook: feed one stream-json line as if Claude had printed it.
    internal void HandleLine(string line) => Handle(line);

    void Handle(string line)
    {
        JsonNode? ev;
        try { ev = JsonNode.Parse(line); } catch (JsonException) { return; }
        if (ev is null) return;
        switch (ev["type"]?.GetValue<string>())
        {
            case "system":
                if (ev["subtype"]?.GetValue<string>() == "init")
                {
                    if (ev["session_id"]?.GetValue<string>() is string sid) _sessionId = sid;
                    if (ev["model"]?.GetValue<string>() is string model) ActiveModel = model;
                }
                break;
            case "stream_event":
                var e = ev["event"];
                if (e?["type"]?.GetValue<string>() == "content_block_delta" &&
                    e["delta"]?["type"]?.GetValue<string>() == "text_delta" &&
                    e["delta"]?["text"]?.GetValue<string>() is string t)
                    LiveText += t;
                break;
            case "assistant":
                foreach (var block in ev["message"]?["content"]?.AsArray() ?? new JsonArray())
                {
                    switch (block?["type"]?.GetValue<string>())
                    {
                        case "text":
                            var txt = (block["text"]?.GetValue<string>() ?? "").Trim();
                            LiveText = "";
                            if (txt.Length > 0) Items.Add(new ChatItem { Kind = ChatKind.Assistant, Text = txt });
                            break;
                        case "tool_use":
                            CommitLiveText();
                            Items.Add(ToolItem(block));
                            break;
                    }
                }
                break;
            case "user":
                foreach (var block in ev["message"]?["content"]?.AsArray() ?? new JsonArray())
                {
                    if (block?["type"]?.GetValue<string>() != "tool_result") continue;
                    var isErr = block["is_error"]?.GetValue<bool>() ?? false;
                    var content = Flatten(block["content"]);
                    Items.Add(new ChatItem { Kind = ChatKind.ToolResult, Text = isErr ? "Error" : "Output", Detail = content.Length > 4000 ? content[..4000] : content });
                }
                break;
            case "control_request":
                var req = ev["request"];
                if (req?["subtype"]?.GetValue<string>() == "can_use_tool")
                {
                    CommitLiveText();
                    var tool = req["tool_name"]?.GetValue<string>() ?? "Tool";
                    var input = req["input"];
                    var desc = req["description"]?.GetValue<string>() ?? input?["description"]?.GetValue<string>();
                    Items.Add(new ChatItem
                    {
                        Kind = ChatKind.Approval,
                        RequestId = ev["request_id"]?.GetValue<string>(),
                        Input = input?.DeepClone(),
                        Text = desc ?? $"Claude wants to use {tool}",
                        Detail = input?["command"]?.GetValue<string>() ?? input?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                        SuggestedRule = SuggestRule(tool, input),
                    });
                }
                break;
            case "result":
                CommitLiveText();
                ExpireApprovals();
                if (ev["session_id"]?.GetValue<string>() is string rsid) _sessionId = rsid;
                if (ev["is_error"]?.GetValue<bool>() == true)
                {
                    var err = ev["result"]?.GetValue<string>() ?? "error";
                    // Errors like "Not logged in" also arrive as an assistant message first.
                    if (Items.LastOrDefault() is { Kind: ChatKind.Assistant } last && last.Text == err.Trim()) Items.Remove(last);
                    Items.Add(new ChatItem { Kind = ChatKind.Error, Text = err });
                }
                IsBusy = false;
                StyleMemory.Shared.Reload();   // Claude may have added a preference
                TurnsFinished++;
                Save();
                break;
        }
    }

    void CommitLiveText()
    {
        var t = LiveText.Trim();
        if (t.Length > 0) Items.Add(new ChatItem { Kind = ChatKind.Assistant, Text = t });
        LiveText = "";
    }

    static ChatItem ToolItem(JsonNode block)
    {
        var name = block["name"]?.GetValue<string>() ?? "Tool";
        var input = block["input"];
        string File(string key) => System.IO.Path.GetFileName(input?[key]?.GetValue<string>() ?? "") is { Length: > 0 } f ? f : "a file";
        var summary = name switch
        {
            "Bash" or "PowerShell" => input?["description"]?.GetValue<string>() ?? input?["command"]?.GetValue<string>() ?? "Running a command",
            "Read" => "Looking at " + File("file_path"),
            "Write" or "Edit" => "Writing " + File("file_path"),
            "Skill" => "Using skill " + (input?["skill"]?.GetValue<string>() ?? ""),
            _ => name,
        };
        var detail = input?["command"]?.GetValue<string>() ?? input?.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        return new ChatItem { Kind = ChatKind.Tool, Text = summary, Detail = detail };
    }

    static string Flatten(JsonNode? n) => n switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonArray a => string.Join("\n", a.Select(b => b?["text"]?.GetValue<string>() ?? $"[{b?["type"]?.GetValue<string>() ?? "content"}]")),
        _ => "",
    };

    // MARK: Persistence (one chat per project)

    record Saved(string? SessionId, List<ChatItem> Items);

    string SavePath
    {
        get
        {
            var dir = System.IO.Path.Combine(Toolchain.AppDataDir, "chats");
            Directory.CreateDirectory(dir);
            var safe = string.Concat(Project.Path.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            return System.IO.Path.Combine(dir, safe + ".json");
        }
    }

    void Save()
    {
        try { File.WriteAllText(SavePath, JsonSerializer.Serialize(new Saved(_sessionId, Items.ToList()))); }
        catch (IOException) { }
    }

    void Load()
    {
        try
        {
            if (!File.Exists(SavePath)) return;
            var s = JsonSerializer.Deserialize<Saved>(File.ReadAllText(SavePath));
            if (s == null) return;
            _sessionId = s.SessionId;
            foreach (var i in s.Items)
            {
                if (i.IsPending) i.Approval = ApprovalState.Expired;   // the process that asked is gone
                Items.Add(i);
            }
        }
        catch (JsonException) { }
        catch (IOException) { }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    void Notify([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
