namespace ClaudeVideoEditor.Services;

/// Which Claude model to use. The CLI's aliases (opus, sonnet, haiku) always mean
/// the latest model of that family.
public record ClaudeModel(string Alias, string Name, string Blurb)
{
    public static readonly ClaudeModel[] All =
    {
        new("", "Default", "Whatever your Claude plan or settings use by default"),
        new("opus", "Opus", "Most capable: best for complex edits, slower"),
        new("sonnet", "Sonnet", "Fast and smart: great for most edits"),
        new("haiku", "Haiku", "Fastest and lightest: quick questions and simple cuts"),
    };

    public static ClaudeModel Current => All.FirstOrDefault(m => m.Alias == Prefs.Current.Model) ?? All[0];

    public IEnumerable<string> Arguments => Alias.Length == 0 ? Array.Empty<string>() : new[] { "--model", Alias };

    public string Label => $"{Name}: {Blurb}";
    public override string ToString() => Name;
}
