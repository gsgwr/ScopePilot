using System.Diagnostics;
using System.Text;

namespace ScopePilot.Services;

public sealed record FastCrawlResult(int ExitCode, bool Cancelled, bool ProxyUnavailable);

public sealed class FastCrawlerRunner
{
    private Process? _process;

    public async Task<FastCrawlResult> RunAsync(string runDirectory, IProgress<string> progress, CancellationToken cancellationToken)
    {
        var node = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe");
        var script = Path.Combine(AppContext.BaseDirectory, "tools", "fast-crawl.js");
        if (!File.Exists(node)) throw new FileNotFoundException("Node.jsが見つかりません。", node);
        if (!File.Exists(script)) throw new FileNotFoundException("高速クローラが見つかりません。", script);

        var startInfo = new ProcessStartInfo
        {
            FileName = node,
            WorkingDirectory = runDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(script);
        startInfo.ArgumentList.Add(runDirectory);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("高速クローラを起動できませんでした。");
        _process = process;
        progress.Report($"高速GETクローラを開始しました (PID {process.Id})。");
        using var registration = cancellationToken.Register(() => TryStop(process));
        var output = ReadLinesAsync(process.StandardOutput, progress, cancellationToken);
        var error = ReadLinesAsync(process.StandardError, new Progress<string>(line => progress.Report($"Crawler: {line}")), cancellationToken);
        var cancelled = false;
        try { await process.WaitForExitAsync(cancellationToken); }
        catch (OperationCanceledException) { cancelled = true; TryStop(process); await process.WaitForExitAsync(); }
        finally { _process = null; }
        await Task.WhenAll(IgnoreCancellation(output), IgnoreCancellation(error));
        var proxyUnavailable = await ReadProxyUnavailableAsync(runDirectory);
        return new(process.ExitCode, cancelled, proxyUnavailable);
    }

    public void Stop() { if (_process is { HasExited: false } process) TryStop(process); }

    private static async Task ReadLinesAsync(StreamReader reader, IProgress<string> progress, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(token);
            if (line is null) break;
            if (!string.IsNullOrWhiteSpace(line)) progress.Report(line);
        }
    }

    private static async Task IgnoreCancellation(Task task) { try { await task; } catch (OperationCanceledException) { } }
    private static void TryStop(Process process) { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { } }

    private static async Task<bool> ReadProxyUnavailableAsync(string runDirectory)
    {
        var summaryPath = Path.Combine(runDirectory, "fast-crawl-summary.json");
        if (!File.Exists(summaryPath)) return false;
        try
        {
            await using var stream = File.OpenRead(summaryPath);
            using var document = await System.Text.Json.JsonDocument.ParseAsync(stream);
            return document.RootElement.TryGetProperty("failureCode", out var code) &&
                   string.Equals(code.GetString(), "proxy-unavailable", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}
