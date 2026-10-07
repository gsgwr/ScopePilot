using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ScopePilot.Domain;

namespace ScopePilot.Services;

public sealed record ApiAiRunResult(int ExitCode, bool Cancelled, string ResultPath);

public sealed class ApiDocumentAiRunner
{
    private Process? _process;

    public static string? FindExecutable(DocumentAiProvider provider)
    {
        var name = provider == DocumentAiProvider.Codex ? "codex.exe" : "claude.exe";
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var known = new List<string> { Path.Combine(profile, ".local", "bin", name) };
        if (provider == DocumentAiProvider.Codex)
        {
            var root = Path.Combine(local, "OpenAI", "Codex", "bin");
            if (Directory.Exists(root)) known.InsertRange(0, Directory.GetFiles(root, name, SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc));
        }
        foreach (var target in new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
            known.AddRange((Environment.GetEnvironmentVariable("PATH", target) ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => Path.Combine(x.Trim().Trim('"'), name)));
        return known.FirstOrDefault(File.Exists);
    }

    internal static ProcessStartInfo CreateStartInfo(string executable, DocumentAiProvider provider, string runDirectory)
    {
        var info = new ProcessStartInfo(executable)
        {
            WorkingDirectory = runDirectory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };
        var args = provider == DocumentAiProvider.Codex
            ? new[] { "exec", "--json", "--color", "never", "--sandbox", "read-only", "--ignore-user-config", "--ignore-rules", "--ephemeral",
                "--config", "approval_policy=\"never\"", "--config", "features.shell_tool=false", "--config", "features.apply_patch_freeform=false",
                "--config", "web_search=\"disabled\"", "--skip-git-repo-check", "--cd", runDirectory,
                "--output-schema", Path.Combine(runDirectory, "result-schema.json"),
                "--output-last-message", Path.Combine(runDirectory, "api-document-result.json"), "-" }
            : new[] { "--safe-mode", "-p", "--output-format", "json", "--json-schema", ApiDocumentService.ResultSchema,
                "--settings", "{\"disableAllHooks\":true}", "--tools", "", "--strict-mcp-config", "--mcp-config", "{\"mcpServers\":{}}", "--no-session-persistence" };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        return info;
    }

    public async Task<ApiAiRunResult> RunAsync(string runDirectory, DocumentAiProvider provider,
        IProgress<string> progress, CancellationToken cancellationToken)
    {
        var executable = FindExecutable(provider) ?? throw new FileNotFoundException(
            $"{provider} CLIが見つかりません。{(provider == DocumentAiProvider.Claude ? "Claude Codeのネイティブ版" : "Codex CLI")}をインストールしてログインしてください。");
        var prompt = await File.ReadAllTextAsync(Path.Combine(runDirectory, "prompt.md"), cancellationToken);
        var resultPath = Path.Combine(runDirectory, "api-document-result.json");
        var info = CreateStartInfo(executable, provider, runDirectory);
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"{provider} CLIを起動できませんでした。");
        _process = process;
        progress.Report($"{provider}でAPIドキュメントからリクエストを生成しています (PID {process.Id})。");
        // Drain both streams before writing a potentially large document to stdin.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var registration = cancellationToken.Register(Stop);
        var cancelled = false;
        Exception? inputError = null;
        try
        {
            await process.StandardInput.WriteAsync(prompt.AsMemory(), cancellationToken);
            process.StandardInput.Close();
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (Exception ex) when (cancellationToken.IsCancellationRequested && ex is OperationCanceledException or IOException)
        {
            cancelled = true;
            Stop();
            await process.WaitForExitAsync();
        }
        catch (Exception ex)
        {
            inputError = ex;
            Stop();
            await process.WaitForExitAsync();
        }
        finally { _process = null; }
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        await File.WriteAllTextAsync(Path.Combine(runDirectory, "api-document-run.log"),
            $"{provider} exit={process.ExitCode}; cancelled={cancelled}\n{stderr}\n{stdout}", new UTF8Encoding(false));
        if (inputError is not null)
            throw new IOException($"{provider} CLIへの入力を完了できませんでした。API実行ログを確認してください。", inputError);
        if (!cancelled && process.ExitCode == 0 && provider == DocumentAiProvider.Claude)
            await File.WriteAllTextAsync(resultPath, ExtractClaudeResult(stdout), new UTF8Encoding(false));
        if (process.ExitCode != 0 && !cancelled)
            progress.Report($"{provider} CLI終了コード: {process.ExitCode}。実行履歴のAPI実行ログで詳細を確認してください。");
        return new(process.ExitCode, cancelled, resultPath);
    }

    internal static string ExtractClaudeResult(string stdout)
    {
        using var document = JsonDocument.Parse(stdout);
        var root = document.RootElement;
        if (root.TryGetProperty("is_error", out var error) && error.ValueKind == JsonValueKind.True)
            throw new InvalidDataException("Claude CLIが生成エラーを返しました。API実行ログを確認してください。");
        if (!root.TryGetProperty("structured_output", out var result) || result.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Claudeの構造化出力がありません。--json-schema対応版とログイン状態を確認してください。");
        return result.GetRawText();
    }

    public void Stop()
    {
        try { if (_process is { HasExited: false } process) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
