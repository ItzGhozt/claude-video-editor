using System.IO;

namespace ClaudeVideoEditor.Services;

/// Finds the command-line tools the app depends on. Installers (winget, the Claude
/// Code and uv installers) update PATH in the registry, not in this running process,
/// so every lookup re-reads the user and machine PATH and checks known locations.
public static class Toolchain
{
    public static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    static readonly string LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    static readonly string ProgramFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

    /// Where the app installs video-use (the path its own install.md recommends).
    public static readonly string DefaultVideoUseDir = Path.Combine(Home, "Developer", "video-use");
    public static readonly string SkillLink = Path.Combine(Home, ".claude", "skills", "video-use");

    public static string AppDataDir
    {
        get
        {
            var d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude Video Editor");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    static IEnumerable<string> KnownDirs() => new[]
    {
        Path.Combine(Home, ".local", "bin"),                          // Claude Code + uv installers
        Path.Combine(LocalAppData, "Microsoft", "WinGet", "Links"),   // winget command aliases
        Path.Combine(LocalAppData, "Microsoft", "WindowsApps"),       // winget itself
        Path.Combine(Home, ".cargo", "bin"),
    };

    public static IEnumerable<string> PathDirs()
    {
        var parts = new List<string>();
        foreach (var target in new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
            parts.AddRange((Environment.GetEnvironmentVariable("Path", target) ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries));
        return KnownDirs().Concat(parts).Select(p => Environment.ExpandEnvironmentVariables(p.Trim()))
            .Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    public static string? Find(string exe)
    {
        foreach (var dir in PathDirs())
        {
            try
            {
                var p = Path.Combine(dir, exe);
                if (File.Exists(p)) return p;
            }
            catch (ArgumentException) { /* malformed PATH entry */ }
        }
        return null;
    }

    public static string? Claude => Find("claude.exe");
    public static string? Ffmpeg => Find("ffmpeg.exe") ?? FindWingetFfmpeg("ffmpeg.exe");
    public static string? Ffprobe => Find("ffprobe.exe") ?? FindWingetFfmpeg("ffprobe.exe");
    public static string? Uv => Find("uv.exe");
    public static string? Winget => Find("winget.exe");

    /// winget sometimes installs Gyan.FFmpeg without creating the Links alias.
    static string? FindWingetFfmpeg(string exe)
    {
        var pkgs = Path.Combine(LocalAppData, "Microsoft", "WinGet", "Packages");
        if (!Directory.Exists(pkgs)) return null;
        try
        {
            return Directory.EnumerateDirectories(pkgs, "Gyan.FFmpeg*")
                .SelectMany(d => Directory.EnumerateFiles(d, exe, SearchOption.AllDirectories))
                .FirstOrDefault();
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// Git Bash gives Claude Code its Bash tool; without it Claude uses PowerShell.
    public static string? GitBash
    {
        get
        {
            var candidates = new[]
            {
                Path.Combine(ProgramFiles, "Git", "bin", "bash.exe"),
                Path.Combine(LocalAppData, "Programs", "Git", "bin", "bash.exe"),
            };
            var hit = candidates.FirstOrDefault(File.Exists);
            if (hit != null) return hit;
            var git = Find("git.exe");  // ...\Git\cmd\git.exe -> ...\Git\bin\bash.exe
            if (git == null) return null;
            var bash = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(git)!, "..", "bin", "bash.exe"));
            return File.Exists(bash) ? bash : null;
        }
    }

    /// The video-use checkout Claude will load: wherever the skill link points,
    /// else the default install location.
    public static string VideoUseDir
    {
        get
        {
            try
            {
                var info = new DirectoryInfo(SkillLink);
                if (info.Exists && info.LinkTarget is string target)
                {
                    var full = Path.GetFullPath(target, Path.GetDirectoryName(SkillLink)!);
                    if (File.Exists(Path.Combine(full, "SKILL.md"))) return full;
                }
            }
            catch (IOException) { }
            return DefaultVideoUseDir;
        }
    }

    public static bool VideoUseInstalled => File.Exists(Path.Combine(VideoUseDir, "SKILL.md"));
    public static bool VideoUseRegistered => File.Exists(Path.Combine(SkillLink, "SKILL.md"));
    public static string? VideoUsePython
    {
        get
        {
            var p = Path.Combine(VideoUseDir, ".venv", "Scripts", "python.exe");
            return File.Exists(p) ? p : null;
        }
    }

    /// Environment for every child process: a fresh PATH (with video-use's venv
    /// first, so `python` has its dependencies) and the Git Bash location.
    public static Dictionary<string, string> BuildEnvironment()
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
            env[(string)e.Key] = (string?)e.Value ?? "";
        var dirs = new List<string>();
        if (VideoUsePython != null) dirs.Add(Path.Combine(VideoUseDir, ".venv", "Scripts"));
        foreach (var tool in new[] { Ffmpeg })
            if (tool != null) dirs.Add(Path.GetDirectoryName(tool)!);
        dirs.AddRange(PathDirs());
        env["Path"] = string.Join(';', dirs.Distinct(StringComparer.OrdinalIgnoreCase));
        if (GitBash is string bash) env["CLAUDE_CODE_GIT_BASH_PATH"] = bash;
        return env;
    }
}
