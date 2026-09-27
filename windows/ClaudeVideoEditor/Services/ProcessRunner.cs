using System.Diagnostics;
using System.IO;
using System.Text;

namespace ClaudeVideoEditor.Services;

public static class ProcessRunner
{
    public static ProcessStartInfo StartInfo(string exe, IEnumerable<string> args, string? workDir = null,
                                             IDictionary<string, string>? env = null)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false),
            WorkingDirectory = workDir ?? Path.GetTempPath(),
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment.Clear();
        foreach (var (k, v) in env ?? Toolchain.BuildEnvironment()) psi.Environment[k] = v;
        return psi;
    }

    /// Runs to completion and returns (exit code, stdout, stderr).
    public static async Task<(int Code, string Out, string Err)> RunAsync(
        string exe, IEnumerable<string> args, string? stdin = null, string? workDir = null,
        IDictionary<string, string>? env = null, TimeSpan? timeout = null)
    {
        using var p = new Process { StartInfo = StartInfo(exe, args, workDir, env) };
        p.Start();
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        if (stdin != null) await p.StandardInput.WriteAsync(stdin);
        p.StandardInput.Close();
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromMinutes(10));
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } return (-1, await outTask, "Timed out."); }
        return (p.ExitCode, await outTask, await errTask);
    }

    /// Runs a PowerShell script, streaming each output line to `onLine`.
    public static async Task<int> PowerShellAsync(string script, Action<string> onLine)
    {
        var args = new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command",
                           "$ProgressPreference='SilentlyContinue'; $ErrorActionPreference='Stop'; " + script };
        using var p = new Process { StartInfo = StartInfo("powershell.exe", args), EnableRaisingEvents = true };
        p.StartInfo.RedirectStandardInput = false;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) onLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) onLine(e.Data); };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        await p.WaitForExitAsync();
        return p.ExitCode;
    }
}
