using System.Diagnostics;
using System.Net.Sockets;
using ScopePilot.Domain;

namespace ScopePilot.Services;

public sealed class EnvironmentProbe
{
    public async Task<IReadOnlyList<EnvironmentCheck>> CheckAsync()
    {
        var codexPath = ResolveExecutable("codex.exe", FindCodexInstallations());
        var nodePath = ResolveExecutable("node.exe", [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe")]);
        var npxPath = ResolveExecutable("npx.cmd", [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "npx.cmd")]);
        var codex = await ProbeAsync("Codex CLI", codexPath, "--version");
        var node = await ProbeAsync("Node.js", nodePath, "--version");
        var npm = await ProbeAsync("npm / npx", npxPath, "--version");
        var burp = await ProbeTcpAsync("Burp MCP", "127.0.0.1", 9876, "BurpのMCPタブでサーバーを有効化してください");
        var burpProxy = await ProbeTcpAsync("Burp Proxy", "127.0.0.1", 8080, "BurpのProxy settingsでListenerを有効化してください");
        return [codex, node, npm, burp, burpProxy,
            new EnvironmentCheck("Playwright MCP", npm.Available, npm.Available
                ? $"npxを検出しました: {npxPath}"
                : "npxが見つかりません。Node.jsのnpm/npxを利用可能にしてください。")];
    }

    private static async Task<EnvironmentCheck> ProbeAsync(string name, string? command, string arguments)
    {
        if (string.IsNullOrWhiteSpace(command)) return new(name, false, "実行ファイルが見つかりません。");
        try
        {
            var isCommandScript = Path.GetExtension(command).Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
                                  Path.GetExtension(command).Equals(".bat", StringComparison.OrdinalIgnoreCase);
            var startInfo = new ProcessStartInfo
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            if (isCommandScript)
            {
                startInfo.FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
                startInfo.ArgumentList.Add("/d");
                startInfo.ArgumentList.Add("/c");
                startInfo.ArgumentList.Add(command);
                startInfo.ArgumentList.Add(arguments);
            }
            else
            {
                startInfo.FileName = command;
                startInfo.ArgumentList.Add(arguments);
            }

            using var process = Process.Start(startInfo);
            if (process is null) return new(name, false, "起動できませんでした。");
            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var detail = string.IsNullOrWhiteSpace(output) ? error.Trim() : output.Trim();
            var version = detail.Split('\n').FirstOrDefault()?.Trim() ?? string.Empty;
            return new(name, process.ExitCode == 0, $"{version}  ({command})");
        }
        catch (Exception ex)
        {
            return new(name, false, ex.Message);
        }
    }

    private static string? ResolveExecutable(string fileName, IEnumerable<string> knownPaths)
    {
        foreach (var path in knownPaths.Where(File.Exists)) return path;
        var pathValues = new[]
        {
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Process),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine)
        };
        foreach (var directory in pathValues.SelectMany(x => (x ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim(), fileName);
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* Broken PATH entries are ignored. */ }
        }
        return null;
    }

    private static IEnumerable<string> FindCodexInstallations()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
        if (!Directory.Exists(root)) return [];
        return Directory.GetFiles(root, "codex.exe", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc);
    }

    private static async Task<EnvironmentCheck> ProbeTcpAsync(string name, string host, int port, string failureHint)
    {
        try
        {
            using var client = new TcpClient();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await client.ConnectAsync(host, port, timeout.Token);
            return new(name, true, $"接続できました: http://{host}:{port}");
        }
        catch
        {
            return new(name, false, $"接続できません。{failureHint}: http://{host}:{port}");
        }
    }
}
