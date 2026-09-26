using System.Diagnostics;

namespace ScopePilot.Services;

public sealed record McpSetupResult(bool Success, IReadOnlyList<string> Messages);

public sealed class McpSetupService
{
    public async Task<McpSetupResult> ConfigureAsync()
    {
        var messages = new List<string>();
        var codex = FindLatest(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin"), "codex.exe");
        var node = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe");
        var java = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "BurpSuite", "jre", "bin", "java.exe");
        var proxy = Path.Combine(AppContext.BaseDirectory, "tools", "mcp-proxy-all.jar");
        var scopePilotRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScopePilot");
        var playwrightOutput = Path.Combine(scopePilotRoot, "playwright");
        var playwrightCli = Path.Combine(AppContext.BaseDirectory, "tools", "playwright-runtime", "node_modules", "@playwright", "mcp", "cli.js");

        if (codex is null) messages.Add("Codex CLIが見つかりません。");
        if (!File.Exists(node)) messages.Add($"Node.jsが見つかりません: {node}");
        if (!File.Exists(java)) messages.Add($"BurpのJavaが見つかりません: {java}");
        if (!File.Exists(proxy)) messages.Add($"Burp MCPプロキシが見つかりません: {proxy}");
        if (!File.Exists(playwrightCli)) messages.Add($"Playwright MCP本体が見つかりません: {playwrightCli}");
        if (messages.Count > 0) return new(false, messages);

        Directory.CreateDirectory(playwrightOutput);

        var playwright = await EnsureServerAsync(codex!, "playwright",
            [node, playwrightCli, "--browser", "msedge", "--proxy-server", "http://127.0.0.1:8080",
                "--ignore-https-errors", "--timeout-action", "15000", "--timeout-navigation", "90000",
                "--timeout-settle", "1000", "--output-dir", playwrightOutput],
            null, ["cli.js", "--timeout-action", "15000", "--timeout-navigation", "90000"]);
        messages.Add(playwright);

        var burp = await EnsureServerAsync(codex!, "burp",
            [java, "-jar", proxy, "--sse-url", "http://127.0.0.1:9876"], null, ["--sse-url"]);
        messages.Add(burp);

        var success = messages.All(x => x.StartsWith("[OK]", StringComparison.Ordinal));
        return new(success, messages);
    }

    private static async Task<string> EnsureServerAsync(string codex, string name, IReadOnlyList<string> command,
        IReadOnlyDictionary<string, string>? environment, IReadOnlyList<string> requiredMarkers)
    {
        var existing = await RunCodexAsync(codex, ["mcp", "get", name]);
        if (existing.ExitCode == 0 && requiredMarkers.All(marker => existing.Output.Contains(marker, StringComparison.OrdinalIgnoreCase)) &&
            (environment is null || environment.Keys.All(key => existing.Output.Contains(key, StringComparison.OrdinalIgnoreCase))))
            return $"[OK] {name}: 必要な設定で登録済みです。";

        if (existing.ExitCode == 0)
        {
            var removed = await RunCodexAsync(codex, ["mcp", "remove", name]);
            if (removed.ExitCode != 0) return $"[失敗] {name}: 既存設定を更新できません。{FirstUsefulLine(removed.Error, removed.Output)}";
        }

        var arguments = new List<string> { "mcp", "add", name };
        if (environment is not null)
        {
            foreach (var item in environment)
            {
                arguments.Add("--env");
                arguments.Add($"{item.Key}={item.Value}");
            }
        }
        arguments.Add("--");
        arguments.AddRange(command);
        var added = await RunCodexAsync(codex, arguments);
        if (added.ExitCode == 0) return $"[OK] {name}: Codexへ登録しました。";
        return $"[失敗] {name}: {FirstUsefulLine(added.Error, added.Output)}";
    }

    private static async Task<ProcessResult> RunCodexAsync(string codex, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = codex,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        startInfo.Environment["CODEX_HOME"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        startInfo.Environment["HOME"] = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        startInfo.Environment["USERPROFILE"] = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null) return new(-1, string.Empty, "Codex CLIを起動できませんでした。");
            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return new(process.ExitCode, output, error);
        }
        catch (Exception ex) { return new(-1, string.Empty, ex.Message); }
    }

    private static string? FindLatest(string root, string fileName)
    {
        if (!Directory.Exists(root)) return null;
        return Directory.GetFiles(root, fileName, SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
    }

    private static string FirstUsefulLine(params string[] values) => values
        .SelectMany(x => x.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        .FirstOrDefault(x => !x.StartsWith("WARNING", StringComparison.OrdinalIgnoreCase)) ?? "不明なエラー";

    private sealed record ProcessResult(int ExitCode, string Output, string Error);
}
