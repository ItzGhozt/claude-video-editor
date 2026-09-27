using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeVideoEditor.Services;

/// Builds the Claude Code settings that keep a session inside its project folder.
///
/// Claude Code's OS sandbox isn't available on native Windows, so this relies on
/// permission rules, with every other decision going to the user:
///  - Sessions run in `acceptEdits` mode with `--permission-prompts host`: edits and
///    simple file commands (mkdir, cp, mv…) inside the project are approved
///    automatically; anything else (running ffmpeg, Python…) is sent to the app,
///    which shows an Allow / Deny card.
///  - `blockReadsOutsideWorkingDirectories` makes the file tools refuse paths
///    outside the project, and deny rules fence off private folders.
public static class SessionSettings
{
    /// Folders that commonly hold private files (resolved per user, including
    /// OneDrive-redirected Documents/Desktop).
    static IEnumerable<string> PrivateDirs()
    {
        var home = Toolchain.Home;
        var dirs = new List<string>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Path.Combine(home, "Downloads"),
            Path.Combine(home, ".ssh"),
            Path.Combine(home, ".aws"),
            Path.Combine(home, ".config"),
            Path.Combine(home, ".claude", "projects"),
        };
        try { dirs.AddRange(Directory.EnumerateDirectories(home, "OneDrive*")); } catch (IOException) { }
        return dirs.Where(d => !string.IsNullOrEmpty(d)).Select(Norm).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// C:\Users\alice\Videos -> /c/Users/alice/Videos (the form permission rules use).
    public static string Posix(string winPath)
    {
        var full = Path.GetFullPath(winPath).TrimEnd('\\');
        if (full.Length >= 2 && full[1] == ':')
            return "/" + char.ToLowerInvariant(full[0]) + full[2..].Replace('\\', '/');
        return full.Replace('\\', '/');
    }

    static string Rule(string tool, string winPath) => $"{tool}(/{Posix(winPath)}/**)";
    static string Norm(string p) => Path.GetFullPath(p).TrimEnd('\\');

    public static bool IsInside(string path, string dir)
    {
        path = Norm(path); dir = Norm(dir);
        return path.Equals(dir, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(dir + "\\", StringComparison.OrdinalIgnoreCase);
    }

    /// Everything under `root` except the allowed paths and the folders leading to them.
    static IEnumerable<string> SiblingsAround(IReadOnlyList<string> allowed, string root)
    {
        var outList = new List<string>();
        void Walk(string dir)
        {
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(dir).ToList(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
            foreach (var full in entries)
            {
                if (allowed.Any(a => IsInside(a, full)))
                {
                    if (!allowed.Any(a => Norm(a).Equals(Norm(full), StringComparison.OrdinalIgnoreCase))) Walk(full);
                }
                else outList.Add(full);
            }
        }
        Walk(root);
        return outList;
    }

    public static string ScratchDir
    {
        get
        {
            var d = Norm(Path.Combine(Path.GetTempPath(), "claude-video-editor"));
            Directory.CreateDirectory(d);
            return d;
        }
    }

    public static string WriteFor(ProjectInfo project)
    {
        var projectDir = Norm(project.Path);
        var skills = Path.Combine(Toolchain.Home, ".claude", "skills");
        var readOnly = new List<string> { skills };
        if (Toolchain.VideoUseInstalled) readOnly.Add(Norm(Toolchain.VideoUseDir));   // junction target
        // Temp lives under AppData\Local (denied below), but renders and preview
        // frames go there. Sessions get their own temp folder (TEMP/TMP point at
        // it) that is a read/write scratch area, like on macOS.
        var scratch = ScratchDir;
        // The style-memory folder, so Claude can record likes/dislikes the user
        // states. It's inside AppData (denied below), so the deny rules cover
        // everything around it instead.
        var memory = Norm(StyleMemory.MemoryDir);
        var allowed = new List<string> { projectDir, scratch, memory };
        allowed.AddRange(readOnly);

        var allow = new JsonArray
        {
            Rule("Read", projectDir), Rule("Edit", projectDir),
            Rule("Read", scratch), Rule("Edit", scratch),
            Rule("Read", memory), Rule("Edit", memory),
            "Skill", "Agent", "Glob", "Grep", "TodoWrite",
        };
        foreach (var r in readOnly) allow.Add(Rule("Read", r));
        foreach (var r in project.AlwaysAllow) allow.Add(r.ToString());

        var deny = new JsonArray();
        foreach (var p in PrivateDirs())
        {
            var inside = allowed.Where(a => IsInside(a, p)).ToList();
            var targets = inside.Count == 0 ? new[] { p } : SiblingsAround(inside, p);
            foreach (var t in targets) { deny.Add(Rule("Read", t)); deny.Add(Rule("Edit", t)); }
        }
        foreach (var r in readOnly) deny.Add(Rule("Edit", r));
        deny.Add(Rule("Edit", Environment.GetFolderPath(Environment.SpecialFolder.Windows)));
        deny.Add(Rule("Edit", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)));

        var settings = new JsonObject
        {
            ["permissions"] = new JsonObject
            {
                ["blockReadsOutsideWorkingDirectories"] = true,
                ["additionalDirectories"] = new JsonArray(readOnly.Prepend(memory).Prepend(scratch).Select(r => (JsonNode)r).ToArray()),
                ["allow"] = allow,
                ["deny"] = deny,
            },
        };

        var dir = Path.Combine(Toolchain.AppDataDir, "settings");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, SafeName(projectDir) + ".json");
        File.WriteAllText(file, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return file;
    }

    static string SafeName(string path) =>
        string.Concat(path.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ' ' ? '_' : c));
}
