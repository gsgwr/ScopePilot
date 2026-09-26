using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace ScopePilot.Services;

public sealed record ExplorationRunResult(int ExitCode, bool Cancelled, bool HumanInterventionPending, string ResultPath);

public sealed class CodexExplorationRunner
{
    private Process? _process;

    public async Task<ExplorationRunResult> RunAsync(
        string runDirectory,
        IProgress<string> progress,
        Action<string> interventionDetected,
        CancellationToken cancellationToken)
    {
        var codex = FindCodex();
        if (codex is null) throw new FileNotFoundException("Codex CLIが見つかりません。環境チェックを実行してください。");

        var promptPath = Path.Combine(runDirectory, "prompt.md");
        var schemaPath = Path.Combine(runDirectory, "result-schema.json");
        var resultPath = Path.Combine(runDirectory, "codex-result.json");
        var interventionPath = Path.Combine(runDirectory, "human-intervention.json");
        var prompt = await File.ReadAllTextAsync(promptPath, cancellationToken);

        var startInfo = new ProcessStartInfo
        {
            FileName = codex,
            WorkingDirectory = runDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in new[]
        {
            "exec", "--json", "--color", "never", "--sandbox", "workspace-write",
            "--config", "approval_policy=\"never\"",
            "--config", "mcp_servers.playwright.default_tools_approval_mode=\"approve\"",
            "--config", "mcp_servers.playwright.disabled_tools=[\"browser_run_code_unsafe\",\"browser_file_upload\",\"browser_drop\",\"browser_drag\"]",
            "--config", "mcp_servers.burp.default_tools_approval_mode=\"approve\"",
            "--config", "mcp_servers.burp.enabled_tools=[\"get_proxy_http_history\",\"get_proxy_http_history_regex\"]",
            "--skip-git-repo-check", "--cd", runDirectory, "--output-schema", schemaPath,
            "--output-last-message", resultPath
        }) startInfo.ArgumentList.Add(argument);
        startInfo.ArgumentList.Add(prompt);

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        startInfo.Environment["CODEX_HOME"] = Path.Combine(profile, ".codex");
        startInfo.Environment["HOME"] = profile;
        startInfo.Environment["USERPROFILE"] = profile;

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Codex CLIを起動できませんでした。");
        _process = process;
        progress.Report($"Codex探索ジョブを開始しました (PID {process.Id})。");

        using var registration = cancellationToken.Register(() => TryStop(process));
        var stdout = ReadLinesAsync(process.StandardOutput, line => ReportEvent(line, progress), cancellationToken);
        var stderr = ReadLinesAsync(process.StandardError, line => progress.Report($"Codex: {line}"), cancellationToken);
        var monitor = MonitorInterventionAsync(process, interventionPath, progress, interventionDetected, cancellationToken);

        var cancelled = false;
        try { await process.WaitForExitAsync(cancellationToken); }
        catch (OperationCanceledException) { cancelled = true; TryStop(process); await process.WaitForExitAsync(); }
        finally { _process = null; }

        await Task.WhenAll(IgnoreCancellation(stdout), IgnoreCancellation(stderr), IgnoreCancellation(monitor));
        return new(process.ExitCode, cancelled, File.Exists(interventionPath), resultPath);
    }

    public void Stop()
    {
        if (_process is { HasExited: false } process) TryStop(process);
    }

    public static void Resume(string runDirectory)
    {
        var signal = Path.Combine(runDirectory, "resume.signal");
        File.WriteAllText(signal, DateTimeOffset.Now.ToString("O"));
    }

    private static async Task MonitorInterventionAsync(Process process, string path, IProgress<string> progress,
        Action<string> interventionDetected, CancellationToken cancellationToken)
    {
        var reported = false;
        while (!process.HasExited && !cancellationToken.IsCancellationRequested)
        {
            if (File.Exists(path) && !reported)
            {
                reported = true;
                string detail;
                try { detail = await File.ReadAllTextAsync(path, cancellationToken); }
                catch { detail = "手動操作が必要です。"; }
                interventionDetected(detail);
                progress.Report("ブラウザで手動操作が必要です。内容を確認し、完了後に［手動操作完了］を押してください。");
            }
            if (!File.Exists(path)) reported = false;
            await Task.Delay(1000, cancellationToken);
        }
    }

    private static async Task ReadLinesAsync(StreamReader reader, Action<string> handle, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null) break;
            if (!string.IsNullOrWhiteSpace(line)) handle(line);
        }
    }

    private static void ReportEvent(string line, IProgress<string> progress)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var type = root.TryGetProperty("type", out var typeValue) ? typeValue.GetString() : null;
            if (type == "thread.started" && root.TryGetProperty("thread_id", out var thread))
                progress.Report($"Codexタスク: {thread.GetString()}");
            else if (type == "item.completed" && root.TryGetProperty("item", out var item) &&
                     item.TryGetProperty("type", out var itemType) && itemType.GetString() == "agent_message" &&
                     item.TryGetProperty("text", out var text))
                progress.Report(text.GetString() ?? string.Empty);
            else if (type is "error" or "turn.failed") progress.Report($"Codexエラー: {line}");
        }
        catch (JsonException) { progress.Report(line); }
    }

    private static async Task IgnoreCancellation(Task task)
    {
        try { await task; } catch (OperationCanceledException) { }
    }

    private static void TryStop(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
    }

    private static string? FindCodex()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
        if (!Directory.Exists(root)) return null;
        return Directory.GetFiles(root, "codex.exe", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
    }
}
