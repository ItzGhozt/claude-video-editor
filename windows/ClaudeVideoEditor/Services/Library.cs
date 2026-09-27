using System.IO;
using System.Text;
using System.Text.Json;

namespace ClaudeVideoEditor.Services;

public class SavedPrompt
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "";
    public string Text { get; set; } = "";
    public string Folder { get; set; } = PromptLibrary.DefaultFolder;
    public DateTime Created { get; set; } = DateTime.Now;
    public string Preview => Text.Length > 140 ? Text[..140].Replace('\n', ' ') + "…" : Text.Replace('\n', ' ');
}

/// Favourite prompts, filed into folders, shared by every project.
public class PromptLibrary
{
    public static PromptLibrary Shared { get; } = new();
    public const string DefaultFolder = "Favorites";

    public List<SavedPrompt> Prompts { get; private set; } = new();
    public List<string> ExtraFolders { get; private set; } = new();
    public event Action? Changed;

    static string FilePath => Path.Combine(Toolchain.AppDataDir, "saved-prompts.json");
    record Saved(List<SavedPrompt> Prompts, List<string> Folders);

    PromptLibrary()
    {
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<Saved>(File.ReadAllText(FilePath)) is Saved s)
            {
                Prompts = s.Prompts ?? new();
                ExtraFolders = s.Folders ?? new();
            }
        }
        catch (JsonException) { }
        catch (IOException) { }
    }

    public List<string> Folders =>
        new[] { DefaultFolder }.Concat(ExtraFolders).Concat(Prompts.Select(p => p.Folder).OrderBy(f => f)).Distinct().ToList();

    public List<SavedPrompt> In(string folder) => Prompts.Where(p => p.Folder == folder).OrderByDescending(p => p.Created).ToList();
    public bool Contains(string text) => Prompts.Any(p => p.Text == text);

    public SavedPrompt Add(string title, string text, string folder)
    {
        var f = string.IsNullOrWhiteSpace(folder) ? DefaultFolder : folder.Trim();
        var p = new SavedPrompt { Title = CleanTitle(title, text), Text = text, Folder = f };
        Prompts.Add(p);
        Save();
        return p;
    }

    public void Update(SavedPrompt p) { if (Prompts.Contains(p)) Save(); }
    public void Delete(SavedPrompt p) { Prompts.Remove(p); Save(); }

    public void AddFolder(string name)
    {
        var n = name.Trim();
        if (n.Length == 0 || Folders.Contains(n)) return;
        ExtraFolders.Add(n);
        Save();
    }

    /// Deletes a folder; its prompts move to Favorites.
    public void DeleteFolder(string name)
    {
        if (name == DefaultFolder) return;
        ExtraFolders.Remove(name);
        foreach (var p in Prompts.Where(p => p.Folder == name)) p.Folder = DefaultFolder;
        Save();
    }

    /// A short title from the first words when the user doesn't give one.
    public static string CleanTitle(string title, string text)
    {
        if (!string.IsNullOrWhiteSpace(title)) return title.Trim();
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var t = string.Join(' ', words.Take(7));
        return words.Length > 7 ? t + "…" : t;
    }

    void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(new Saved(Prompts, ExtraFolders))); }
        catch (IOException) { }
        Changed?.Invoke();
    }
}

/// The user's likes and dislikes, kept as a small Markdown file that every Claude
/// session reads at start and may update when the user states a lasting preference.
public class StyleMemory
{
    public static StyleMemory Shared { get; } = new();

    public List<string> Likes { get; private set; } = new();
    public List<string> Dislikes { get; private set; } = new();
    public event Action? Changed;
    public bool IsEmpty => Likes.Count == 0 && Dislikes.Count == 0;

    public static string MemoryDir
    {
        get
        {
            var d = Path.Combine(Toolchain.AppDataDir, "memory");
            Directory.CreateDirectory(d);
            return d;
        }
    }
    public static string FilePath => Path.Combine(MemoryDir, "preferences.md");

    StyleMemory() => Reload();

    public void Reload()
    {
        if (!File.Exists(FilePath)) { Write(); return; }
        var likes = new List<string>();
        var dislikes = new List<string>();
        string section = "";
        foreach (var raw in File.ReadAllLines(FilePath))
        {
            var line = raw.Trim();
            if (line.StartsWith("## like", StringComparison.OrdinalIgnoreCase)) { section = "likes"; continue; }
            if (line.StartsWith("## dislike", StringComparison.OrdinalIgnoreCase)) { section = "dislikes"; continue; }
            if (!(line.StartsWith("- ") || line.StartsWith("* "))) continue;
            var item = line[2..].Trim();
            if (item.Length == 0) continue;
            if (section == "likes") likes.Add(item); else if (section == "dislikes") dislikes.Add(item);
        }
        var changed = !likes.SequenceEqual(Likes) || !dislikes.SequenceEqual(Dislikes);
        Likes = likes;
        Dislikes = dislikes;
        if (changed) Changed?.Invoke();
    }

    public void Add(string text, bool like)
    {
        var t = text.Trim();
        if (t.Length == 0) return;
        Reload();
        var (into, from) = like ? (Likes, Dislikes) : (Dislikes, Likes);
        if (!into.Contains(t)) into.Add(t);
        from.Remove(t);
        Write();
    }

    public void Remove(string text)
    {
        Reload();
        Likes.Remove(text);
        Dislikes.Remove(text);
        Write();
    }

    public void Clear()
    {
        Likes = new();
        Dislikes = new();
        Write();
    }

    /// Text for system prompts, or null when there's nothing yet.
    public string? Summary
    {
        get
        {
            if (IsEmpty) return null;
            var sb = new StringBuilder();
            if (Likes.Count > 0) sb.Append("Likes:\n").Append(string.Join("\n", Likes.Select(l => "- " + l))).Append('\n');
            if (Dislikes.Count > 0) sb.Append("Dislikes:\n").Append(string.Join("\n", Dislikes.Select(l => "- " + l))).Append('\n');
            return sb.ToString();
        }
    }

    void Write()
    {
        var text = "# My editing style\n\n" +
                   "Preferences the user has told Claude Video Editor. Claude reads this before every edit.\n" +
                   "Keep each item short. One per line, starting with \"- \".\n\n" +
                   "## Likes\n" + string.Join("\n", Likes.Select(l => "- " + l)) + "\n\n" +
                   "## Dislikes\n" + string.Join("\n", Dislikes.Select(l => "- " + l)) + "\n";
        try { File.WriteAllText(FilePath, text); } catch (IOException) { }
        Changed?.Invoke();
    }
}
