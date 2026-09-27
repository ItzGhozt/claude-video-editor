using System.IO;
using System.Text.Json;

namespace ClaudeVideoEditor.Services;

public class ProjectInfo
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    /// Commands the user chose "Always allow" for, as Claude Code rule contents,
    /// e.g. { "Bash", "ffmpeg:*" }.
    public List<AllowRule> AlwaysAllow { get; set; } = new();
}

public record AllowRule(string ToolName, string RuleContent)
{
    public override string ToString() => $"{ToolName}({RuleContent})";
}

/// App preferences, saved as JSON in %APPDATA%\Claude Video Editor\prefs.json.
public class Prefs
{
    public bool OnboardingDone { get; set; }
    public string AuthMode { get; set; } = "account";   // "account" | "apiKey"
    public List<ProjectInfo> Projects { get; set; } = new();
    public string? SelectedPath { get; set; }

    static string FilePath => System.IO.Path.Combine(Toolchain.AppDataDir, "prefs.json");
    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static Prefs Current { get; private set; } = Load();

    static Prefs Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Prefs>(File.ReadAllText(FilePath)) ?? new Prefs();
        }
        catch (JsonException) { }
        catch (IOException) { }
        return new Prefs();
    }

    public void Save()
    {
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Opts));
        File.Move(tmp, FilePath, overwrite: true);
    }
}
