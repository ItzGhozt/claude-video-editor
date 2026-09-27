using System.IO;

namespace ClaudeVideoEditor.Services;

public record Clip(string FullPath, string Relative, DateTime Modified, long Size)
{
    public string Name => Path.GetFileName(FullPath);
    public string Folder => Path.GetDirectoryName(Relative) ?? "";
    public bool HasFolder => Folder.Length > 0;
    public bool IsNew => DateTime.Now - Modified < TimeSpan.FromHours(1);
    public string Info => $"{FormatSize(Size)} · {Modified:g}";

    static string FormatSize(long b) => b switch
    {
        >= 1L << 30 => $"{b / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{b / (double)(1L << 20):0.0} MB",
        _ => $"{b / 1024.0:0} KB",
    };
}

public static class ClipScanner
{
    static readonly HashSet<string> Exts = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mov", ".m4v", ".mkv", ".avi", ".mts", ".webm", ".wmv" };

    public static List<Clip> Scan(string root)
    {
        var list = new List<Clip>();
        if (!Directory.Exists(root)) return list;
        var opts = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 4, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };
        foreach (var f in Directory.EnumerateFiles(root, "*", opts))
        {
            if (!Exts.Contains(Path.GetExtension(f))) continue;
            try
            {
                var info = new FileInfo(f);
                list.Add(new Clip(f, Path.GetRelativePath(root, f), info.LastWriteTime, info.Length));
            }
            catch (IOException) { }
        }
        return list.OrderByDescending(c => c.Modified).ToList();
    }
}
