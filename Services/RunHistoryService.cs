using System.Text.Json;

namespace ScopePilot.Services;

public sealed record ExplorationRunSummary(
    string RunId,
    string Directory,
    DateTimeOffset StartedAt,
    string Status,
    string StatusDisplay,
    int FastRequestCount,
    int AiObservedCount,
    int RequestPatternCount,
    int FormPatternCount,
    string Summary,
    string Limitations)
{
    public string StartedAtDisplay => StartedAt.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss");
    public string PatternCountDisplay => $"{RequestPatternCount:N0} / {FormPatternCount:N0}";
}

public sealed class RunHistoryService
{
    private readonly string _runsDirectory;

    public RunHistoryService(string? runsDirectory = null)
    {
        _runsDirectory = runsDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScopePilot", "runs");
    }

    public async Task<IReadOnlyList<ExplorationRunSummary>> ListAsync(Guid projectId)
    {
        var projectDirectory = Path.Combine(_runsDirectory, projectId.ToString("N"));
        if (!Directory.Exists(projectDirectory)) return [];
        var results = new List<ExplorationRunSummary>();
        foreach (var directory in new DirectoryInfo(projectDirectory).GetDirectories().OrderByDescending(x => x.LastWriteTimeUtc))
        {
            try { results.Add(await ReadAsync(directory)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                results.Add(new(directory.Name, directory.FullName, directory.CreationTime, "unreadable", "読取エラー",
                    0, 0, 0, 0, ex.Message, string.Empty));
            }
        }
        return results;
    }

    private static async Task<ExplorationRunSummary> ReadAsync(DirectoryInfo directory)
    {
        var engagement = await ReadJsonAsync(Path.Combine(directory.FullName, "engagement.json"));
        var fast = await ReadJsonAsync(Path.Combine(directory.FullName, "fast-crawl-summary.json"));
        var aiInput = await ReadJsonAsync(Path.Combine(directory.FullName, "ai-input.json"));
        var result = await ReadJsonAsync(Path.Combine(directory.FullName, "codex-result.json"));
        var intervention = File.Exists(Path.Combine(directory.FullName, "human-intervention.json"));
        var startedAt = ReadDate(engagement, "runStartedAt") ?? directory.CreationTime;
        var fastRequestCount = ReadInt(fast, "observedRequestCount");
        var requestPatterns = ReadNestedInt(aiInput, "summary", "requestPatternCount");
        var formPatterns = ReadNestedInt(aiInput, "summary", "formPatternCount");
        var aiObservedCount = await CountNonEmptyLinesAsync(Path.Combine(directory.FullName, "ai-observed-requests.jsonl"));
        var status = ReadString(result, "status");
        var summary = ReadString(result, "summary");
        var limitations = ReadStringArray(result, "limitations");
        if (string.IsNullOrWhiteSpace(status))
        {
            if (intervention) status = "waiting";
            else if (File.Exists(Path.Combine(directory.FullName, "codex-run.log"))) status = "failed";
            else if (fast is not null) status = ReadString(fast, "status") is "completed" ? "crawled" : "failed";
            else status = "prepared";
        }
        if (string.IsNullOrWhiteSpace(summary)) summary = status switch
        {
            "prepared" => "探索パッケージを生成済みです。",
            "crawled" => "高速クロールまで完了しています。",
            "waiting" => "手動操作の完了を待っています。",
            "failed" => "結果ファイルが生成されず終了しました。ログを確認してください。",
            _ => string.Empty
        };
        return new(directory.Name, directory.FullName, startedAt, status, StatusDisplay(status), fastRequestCount,
            aiObservedCount, requestPatterns, formPatterns, summary, limitations);
    }

    private static string StatusDisplay(string status) => status.ToLowerInvariant() switch
    {
        "completed" => "完了",
        "partial" => "部分結果",
        "failed" => "失敗",
        "waiting" => "手動操作待ち",
        "crawled" => "クロール完了",
        "prepared" => "準備済み",
        _ => status
    };

    private static async Task<JsonElement?> ReadJsonAsync(string path)
    {
        if (!File.Exists(path)) return null;
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        return document.RootElement.Clone();
    }

    private static string ReadString(JsonElement? root, string property) =>
        root is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(property, out var item) && item.ValueKind == JsonValueKind.String
            ? item.GetString() ?? string.Empty : string.Empty;

    private static int ReadInt(JsonElement? root, string property) =>
        root is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(property, out var item) && item.TryGetInt32(out var number)
            ? number : 0;

    private static int ReadNestedInt(JsonElement? root, string parent, string property) =>
        root is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(parent, out var nested) && nested.ValueKind == JsonValueKind.Object &&
        nested.TryGetProperty(property, out var item) && item.TryGetInt32(out var number) ? number : 0;

    private static DateTimeOffset? ReadDate(JsonElement? root, string property) =>
        root is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(property, out var item) && item.TryGetDateTimeOffset(out var date)
            ? date : null;

    private static string ReadStringArray(JsonElement? root, string property)
    {
        if (root is not { ValueKind: JsonValueKind.Object } value || !value.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array)
            return string.Empty;
        return string.Join(Environment.NewLine, array.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => "・" + x.GetString()));
    }

    private static async Task<int> CountNonEmptyLinesAsync(string path)
    {
        if (!File.Exists(path)) return 0;
        var count = 0;
        using var reader = File.OpenText(path);
        while (await reader.ReadLineAsync() is { } line) if (!string.IsNullOrWhiteSpace(line)) count++;
        return count;
    }
}
