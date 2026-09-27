using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Windows;

namespace ClaudeVideoEditor.Services;

public enum SetupStatus { Unknown, Ok, Missing, Working, Failed }

/// One row of the first-run checklist.
public class SetupItem : INotifyPropertyChanged
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Blurb { get; init; } = "";
    public bool Optional { get; init; }
    public bool IsKeyEntry { get; init; }

    SetupStatus _status;
    string _message = "", _log = "";
    public SetupStatus Status { get => _status; set { _status = value; Notify(); Notify(nameof(CanInstall)); Notify(nameof(IsOk)); Notify(nameof(IsWorking)); Notify(nameof(IsFailed)); } }
    public string Message { get => _message; set { _message = value; Notify(); } }
    public string Log { get => _log; set { _log = value; Notify(); Notify(nameof(HasLog)); } }
    public bool HasLog => Log.Length > 0;
    public bool IsOk => Status == SetupStatus.Ok;
    public bool IsWorking => Status == SetupStatus.Working;
    public bool IsFailed => Status == SetupStatus.Failed;
    public bool CanInstall => !IsKeyEntry && Status is SetupStatus.Missing or SetupStatus.Failed;
    public string OptionalLabel => Optional ? "recommended" : "";

    public event PropertyChangedEventHandler? PropertyChanged;
    void Notify([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

/// Everything video-use needs on Windows, each with a check and (where it can be
/// done without admin rights) a one-click installer from the official source.
public class SetupManager
{
    public static SetupManager Shared { get; } = new();

    public SetupItem Claude { get; } = new() { Id = "claude", Title = "Claude Code", Blurb = "The Claude agent that does the editing. Installed from claude.ai." };
    public SetupItem Git { get; } = new() { Id = "git", Title = "Git for Windows", Optional = true, Blurb = "Gives Claude a Bash shell, which video-use's instructions are written for. Installed with winget." };
    public SetupItem Ffmpeg { get; } = new() { Id = "ffmpeg", Title = "ffmpeg", Blurb = "Cuts, grades and renders the video. Installed with winget (Gyan.FFmpeg)." };
    public SetupItem Uv { get; } = new() { Id = "uv", Title = "uv (Python tools)", Blurb = "Sets up the Python libraries video-use uses. Installed from astral.sh." };
    public SetupItem VideoUse { get; } = new() { Id = "video-use", Title = "video-use editing skill", Blurb = "The open-source editing skill by Browser Use (github.com/browser-use/video-use). Installed to %USERPROFILE%\\Developer\\video-use." };
    public SetupItem ElevenLabs { get; } = new() { Id = "elevenlabs", Title = "ElevenLabs key (transcription)", Optional = true, IsKeyEntry = true, Blurb = "video-use transcribes speech with ElevenLabs Scribe, so it can cut on words. Free keys at elevenlabs.io." };

    public IReadOnlyList<SetupItem> Items => new[] { Claude, Git, Ffmpeg, Uv, VideoUse, ElevenLabs };
    public bool AllRequiredDone => Items.Where(i => !i.Optional).All(i => i.IsOk);

    public event Action? Changed;

    public async Task RefreshAsync()
    {
        var r = await Task.Run(() => new
        {
            Claude = Toolchain.Claude != null,
            Git = Toolchain.GitBash != null,
            Ffmpeg = Toolchain.Ffmpeg != null && Toolchain.Ffprobe != null,
            Uv = Toolchain.Uv != null,
            VideoUse = Toolchain.VideoUseRegistered && Toolchain.VideoUsePython != null,
            Eleven = ElevenLabsKey() != null,
        });
        void Set(SetupItem i, bool ok) { if (!i.IsWorking) i.Status = ok ? SetupStatus.Ok : SetupStatus.Missing; }
        Set(Claude, r.Claude); Set(Git, r.Git); Set(Ffmpeg, r.Ffmpeg); Set(Uv, r.Uv); Set(VideoUse, r.VideoUse); Set(ElevenLabs, r.Eleven);
        Changed?.Invoke();
    }

    public async Task InstallAsync(SetupItem item)
    {
        string? winget = Toolchain.Winget;
        string script = item.Id switch
        {
            "claude" => "irm https://claude.ai/install.ps1 | iex",
            "git" => winget == null ? "" : $"& '{winget}' install --id Git.Git -e --source winget --accept-source-agreements --accept-package-agreements --disable-interactivity",
            "ffmpeg" => winget == null ? "" : $"& '{winget}' install --id Gyan.FFmpeg -e --source winget --accept-source-agreements --accept-package-agreements --disable-interactivity",
            "uv" => "irm https://astral.sh/uv/install.ps1 | iex",
            "video-use" => VideoUseScript(),
            _ => "",
        };
        if (script.Length == 0)
        {
            item.Status = SetupStatus.Failed;
            item.Message = "winget (App Installer) isn't available. Install \"App Installer\" from the Microsoft Store, then try again.";
            return;
        }
        item.Status = SetupStatus.Working;
        item.Message = "";
        item.Log = "";
        var ui = Application.Current.Dispatcher;
        int code;
        try
        {
            code = await ProcessRunner.PowerShellAsync(script, line => ui.BeginInvoke(() =>
            {
                item.Log += line + "\n";
                if (item.Log.Length > 6000) item.Log = item.Log[^6000..];
            }));
        }
        catch (Exception e) { code = -1; item.Log += e.Message; }
        if (code == 0)
        {
            item.Status = SetupStatus.Unknown;
            await RefreshAsync();
            if (!item.IsOk) { item.Status = SetupStatus.Failed; item.Message = "Installed, but Windows hasn't picked it up yet. Click Check Again, or restart the app."; }
        }
        else
        {
            item.Status = SetupStatus.Failed;
            var last = item.Log.Trim().Split('\n').LastOrDefault()?.Trim();
            item.Message = string.IsNullOrEmpty(last) ? $"Install failed (code {code})." : last;
        }
        Changed?.Invoke();
    }

    /// Download video-use from its GitHub repo (a zip, so git isn't needed), link it
    /// into Claude Code's skills folder and install its Python dependencies.
    static string VideoUseScript()
    {
        static string Q(string s) => "'" + s.Replace("'", "''") + "'";
        var dir = Toolchain.VideoUseDir;
        var link = Toolchain.SkillLink;
        var uv = Toolchain.Uv;
        var s = "";
        if (uv == null)
            s += "Write-Output 'Installing uv first...'; irm https://astral.sh/uv/install.ps1 | iex; " +
                 "$env:Path = \"$env:USERPROFILE\\.local\\bin;$env:Path\"; ";
        s += $@"
if (-not (Test-Path (Join-Path {Q(dir)} 'SKILL.md'))) {{
  Write-Output 'Downloading video-use from github.com/browser-use/video-use...'
  $tmp = Join-Path $env:TEMP ('video-use-' + [guid]::NewGuid())
  New-Item -ItemType Directory -Path $tmp | Out-Null
  Invoke-WebRequest 'https://github.com/browser-use/video-use/archive/refs/heads/main.zip' -OutFile (Join-Path $tmp 'v.zip')
  Expand-Archive (Join-Path $tmp 'v.zip') -DestinationPath $tmp
  New-Item -ItemType Directory -Force -Path (Split-Path {Q(dir)}) | Out-Null
  Move-Item (Join-Path $tmp 'video-use-main') {Q(dir)}
  Remove-Item $tmp -Recurse -Force
}}
New-Item -ItemType Directory -Force -Path (Split-Path {Q(link)}) | Out-Null
# A junction needs no admin rights (a symlink would).
if (-not (Test-Path {Q(link)})) {{ New-Item -ItemType Junction -Path {Q(link)} -Target {Q(dir)} | Out-Null }}
Set-Location {Q(dir)}
Write-Output 'Installing Python libraries (this can take a few minutes)...'
& {(uv != null ? Q(uv) : "uv")} sync
if ($LASTEXITCODE -ne 0) {{ throw 'uv sync failed' }}
Write-Output 'Done.'
";
        return s;
    }

    // MARK: ElevenLabs key (video-use reads it from .env in its folder)

    public static string? ElevenLabsKey()
    {
        if (Environment.GetEnvironmentVariable("ELEVENLABS_API_KEY") is { Length: > 0 } k) return k;
        var path = Path.Combine(Toolchain.VideoUseDir, ".env");
        if (!File.Exists(path)) return null;
        foreach (var line in File.ReadAllLines(path))
            if (line.StartsWith("ELEVENLABS_API_KEY=") && line["ELEVENLABS_API_KEY=".Length..].Trim() is { Length: > 0 } v) return v;
        return null;
    }

    public async Task SaveElevenLabsKeyAsync(string raw)
    {
        var key = raw.Trim();
        if (key.Length == 0) return;
        var item = ElevenLabs;
        if (!Toolchain.VideoUseInstalled)
        {
            item.Status = SetupStatus.Failed;
            item.Message = "Install video-use first; the key is saved in its folder.";
            return;
        }
        item.Status = SetupStatus.Working;
        int code = 0;
        try
        {
            // Same quota-free check video-use's own installer uses.
            using var http = new HttpClient();
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.elevenlabs.io/v1/user");
            req.Headers.Add("xi-api-key", key);
            using var resp = await http.SendAsync(req);
            code = (int)resp.StatusCode;
        }
        catch (HttpRequestException) { }
        if (code == 401)
        {
            item.Status = SetupStatus.Failed;
            item.Message = "ElevenLabs rejected that key. Check it and paste it again.";
            return;
        }
        var path = Path.Combine(Toolchain.VideoUseDir, ".env");
        var lines = File.Exists(path) ? File.ReadAllLines(path).Where(l => !l.StartsWith("ELEVENLABS_API_KEY=") && l.Length > 0).ToList() : new List<string>();
        lines.Add("ELEVENLABS_API_KEY=" + key);
        try
        {
            File.WriteAllLines(path, lines);
            item.Status = SetupStatus.Ok;
            item.Message = code == 200 ? "Key verified and saved." : "Key saved (couldn't verify it right now).";
        }
        catch (IOException e)
        {
            item.Status = SetupStatus.Failed;
            item.Message = "Couldn't save the key: " + e.Message;
        }
        Changed?.Invoke();
    }
}
