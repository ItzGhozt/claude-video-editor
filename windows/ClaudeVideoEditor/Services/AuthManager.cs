using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows.Threading;

namespace ClaudeVideoEditor.Services;

public enum AuthState { Checking, NoClaude, SignedOut, SigningIn, SignedIn, ApiKey }

/// Two ways to pay for Claude:
///  - Claude account (Pro/Max/Team/Enterprise): the same browser sign-in as Claude
///    Code, driven through `claude auth login`. Credentials stay with Claude Code.
///  - Anthropic API key: kept in Windows Credential Manager and handed to each
///    Claude process as ANTHROPIC_API_KEY.
public partial class AuthManager : INotifyPropertyChanged
{
    public static AuthManager Shared { get; } = new();
    public const string ApiKeyName = "anthropic-api-key";

    AuthState _state = AuthState.Checking;
    public AuthState State { get => _state; private set { _state = value; Notify(); Notify(nameof(IsReady)); } }
    string _title = "", _detail = "", _loginUrl = "", _error = "";
    public string Title { get => _title; private set { _title = value; Notify(); } }
    public string Detail { get => _detail; private set { _detail = value; Notify(); } }
    public string LoginUrl { get => _loginUrl; private set { _loginUrl = value; Notify(); } }
    public string Error { get => _error; set { _error = value; Notify(); } }
    public bool IsReady => State is AuthState.SignedIn or AuthState.ApiKey;

    Process? _login;
    DispatcherTimer? _poll;

    /// Environment for every Claude process the app starts.
    public static Dictionary<string, string> ClaudeEnvironment()
    {
        var env = Toolchain.BuildEnvironment();
        if (Prefs.Current.AuthMode == "apiKey" && SecretStore.Get(ApiKeyName) is string key)
            env["ANTHROPIC_API_KEY"] = key;
        else
            env.Remove("ANTHROPIC_API_KEY");   // a stray key would silently bill an API account
        // Sessions get their own temp folder, which the permission rules allow.
        env["TEMP"] = env["TMP"] = SessionSettings.ScratchDir;
        return env;
    }

    public async Task RefreshAsync()
    {
        if (State == AuthState.SigningIn) return;
        if (Prefs.Current.AuthMode == "apiKey" && SecretStore.Get(ApiKeyName) is string key)
        {
            Title = "Anthropic API key"; Detail = Mask(key); State = AuthState.ApiKey;
            return;
        }
        if (Toolchain.Claude is not string claude) { State = AuthState.NoClaude; return; }
        await ApplyStatusAsync(claude);
    }

    async Task<bool> ApplyStatusAsync(string claude)
    {
        var (_, output, _) = await ProcessRunner.RunAsync(claude, new[] { "auth", "status", "--json" },
                                                          timeout: TimeSpan.FromSeconds(30));
        JsonNode? j = null;
        try { j = JsonNode.Parse(output); } catch (System.Text.Json.JsonException) { }
        if (j?["loggedIn"]?.GetValue<bool>() != true)
        {
            if (State != AuthState.SigningIn) State = AuthState.SignedOut;
            return false;
        }
        Title = j["email"]?.GetValue<string>() ?? "Signed in";
        var parts = new List<string>();
        if (j["subscriptionType"]?.GetValue<string>() is string plan && plan.Length > 0)
            parts.Add("Claude " + char.ToUpper(plan[0]) + plan[1..]);
        if (j["orgName"]?.GetValue<string>() is string org && org.Length > 0) parts.Add(org);
        Detail = parts.Count > 0 ? string.Join(" · ", parts) : "Claude account";
        State = AuthState.SignedIn;
        return true;
    }

    // MARK: Claude account sign-in

    [GeneratedRegex(@"https://\S+")]
    private static partial Regex UrlRegex();

    public void SignInWithClaude()
    {
        if (Toolchain.Claude is not string claude) { State = AuthState.NoClaude; return; }
        CancelSignIn();
        Error = "";
        Prefs.Current.AuthMode = "account"; Prefs.Current.Save();

        var p = new Process
        {
            StartInfo = ProcessRunner.StartInfo(claude, new[] { "auth", "login", "--claudeai" }),
            EnableRaisingEvents = true,
        };
        var transcript = new System.Text.StringBuilder();
        var ui = Dispatcher.CurrentDispatcher;
        void OnData(string? line)
        {
            if (line == null) return;
            transcript.AppendLine(line);
            // "If the browser didn't open, visit: https://..."
            var m = UrlRegex().Match(line);
            if (m.Success) ui.BeginInvoke(() => LoginUrl = m.Value);
        }
        p.OutputDataReceived += (_, e) => OnData(e.Data);
        p.ErrorDataReceived += (_, e) => OnData(e.Data);
        p.Exited += (_, _) => ui.BeginInvoke(async () =>
        {
            if (!ReferenceEquals(p, _login)) return;
            _login = null;
            var last = transcript.ToString().Trim().Split('\n').LastOrDefault()?.Trim() ?? "";
            await FinishSignInAsync(p.ExitCode == 0 ? null : last);
        });
        try { p.Start(); }
        catch (Exception e) { Error = "Couldn't start sign-in: " + e.Message; return; }
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        _login = p;
        LoginUrl = "";
        State = AuthState.SigningIn;

        // The CLI may finish by itself once the browser redirects; poll so the UI
        // moves on as soon as Claude Code reports a login either way.
        _poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _poll.Tick += async (_, _) =>
        {
            var (_, o, _) = await ProcessRunner.RunAsync(claude, new[] { "auth", "status", "--json" }, timeout: TimeSpan.FromSeconds(20));
            if (o.Contains("\"loggedIn\": true") || o.Contains("\"loggedIn\":true")) await FinishSignInAsync(null);
        };
        _poll.Start();
    }

    /// For when the browser shows a code to paste back into the app.
    public void SubmitCode(string code)
    {
        code = code.Trim();
        if (code.Length == 0 || _login is not { HasExited: false } p) return;
        p.StandardInput.WriteLine(code);
        p.StandardInput.Flush();
    }

    public void CancelSignIn()
    {
        _poll?.Stop(); _poll = null;
        if (_login is { HasExited: false } p) { _login = null; try { p.Kill(true); } catch { } }
        _login = null;
        if (State == AuthState.SigningIn) State = AuthState.SignedOut;
    }

    async Task FinishSignInAsync(string? failure)
    {
        if (State != AuthState.SigningIn) return;
        _poll?.Stop(); _poll = null;
        if (_login is { HasExited: false } p) { _login = null; try { p.Kill(true); } catch { } }
        State = AuthState.Checking;
        if (Toolchain.Claude is not string claude) { State = AuthState.NoClaude; return; }
        if (!await ApplyStatusAsync(claude) && !string.IsNullOrEmpty(failure)) Error = failure;
    }

    // MARK: API key

    public async Task<bool> UseApiKeyAsync(string raw)
    {
        var key = raw.Trim();
        if (!key.StartsWith("sk-ant-"))
        {
            Error = "That doesn't look like an Anthropic API key (they start with sk-ant-).";
            return false;
        }
        Error = "";
        try
        {
            using var http = new HttpClient();
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/v1/models?limit=1");
            req.Headers.Add("x-api-key", key);
            req.Headers.Add("anthropic-version", "2023-06-01");
            using var resp = await http.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
            {
                Error = (int)resp.StatusCode == 401 ? "Anthropic rejected that key." : $"Anthropic returned an error ({(int)resp.StatusCode}).";
                return false;
            }
        }
        catch (HttpRequestException e) { Error = "Couldn't reach Anthropic: " + e.Message; return false; }
        SecretStore.Set(ApiKeyName, key);
        Prefs.Current.AuthMode = "apiKey"; Prefs.Current.Save();
        Title = "Anthropic API key"; Detail = Mask(key); State = AuthState.ApiKey;
        return true;
    }

    /// API key: forgets it. Claude account: `claude auth logout`, which also signs
    /// Claude Code out in the terminal (they share the login).
    public async Task SignOutAsync()
    {
        if (State == AuthState.ApiKey)
        {
            SecretStore.Delete(ApiKeyName);
            Prefs.Current.AuthMode = "account"; Prefs.Current.Save();
        }
        else if (Toolchain.Claude is string claude)
        {
            await ProcessRunner.RunAsync(claude, new[] { "auth", "logout" }, timeout: TimeSpan.FromSeconds(30));
        }
        State = AuthState.Checking;
        await RefreshAsync();
    }

    static string Mask(string key) => key.Length > 14 ? key[..10] + "…" + key[^4..] : "API key";

    public event PropertyChangedEventHandler? PropertyChanged;
    void Notify([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
