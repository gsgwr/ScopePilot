using System.Text.Json;
using ScopePilot.Domain;

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
    public string Role { get; init; } = "未認証";
    public int NewPatternCount { get; init; }
    public int ChangedPatternCount { get; init; }
    public int MissingPatternCount { get; init; }
    public string DiffCountDisplay => $"+{NewPatternCount:N0} / Δ{ChangedPatternCount:N0} / -{MissingPatternCount:N0}";
    public string DiffSummary { get; init; } = "比較対象となる以前の実行はありません。";
    public string DiffDetails { get; init; } = string.Empty;
}

public sealed record RunDiffEntry(string ChangeType, string Method, string Pattern, string Role, string Detail)
{
    public string Display => $"[{ChangeType}] {Method} {Pattern}{(string.IsNullOrWhiteSpace(Role) ? string.Empty : $"  ({Role})")}{(string.IsNullOrWhiteSpace(Detail) ? string.Empty : $"  {Detail}")}";
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
        for (var index = 0; index < results.Count - 1; index++)
            results[index] = await AddDiffAsync(results[index], results[index + 1]);
        return results;
    }

    private static async Task<ExplorationRunSummary> AddDiffAsync(ExplorationRunSummary current, ExplorationRunSummary previous)
    {
        var currentPatterns = await ReadPatternsAsync(current.Directory);
        var previousPatterns = await ReadPatternsAsync(previous.Directory);
        var changes = new List<RunDiffEntry>();
        foreach (var (key, pattern) in currentPatterns.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!previousPatterns.TryGetValue(key, out var old))
                changes.Add(new("新規", pattern.Method, pattern.Pattern, pattern.Role, PatternState(pattern)));
            else if (!string.Equals(pattern.Signature, old.Signature, StringComparison.OrdinalIgnoreCase))
                changes.Add(new("変更", pattern.Method, pattern.Pattern, pattern.Role,
                    $"{PatternState(old)} → {PatternState(pattern)}"));
        }
        foreach (var (key, pattern) in previousPatterns.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            if (!currentPatterns.ContainsKey(key))
                changes.Add(new("未観測", pattern.Method, pattern.Pattern, pattern.Role, PatternState(pattern)));

        var added = changes.Count(x => x.ChangeType == "新規");
        var changed = changes.Count(x => x.ChangeType == "変更");
        var missing = changes.Count(x => x.ChangeType == "未観測");
        var details = changes.Count == 0
            ? "差分はありません。"
            : string.Join(Environment.NewLine, changes.Take(100).Select(x => x.Display)) +
              (changes.Count > 100 ? $"{Environment.NewLine}ほか {changes.Count - 100:N0}件" : string.Empty);
        return current with
        {
            NewPatternCount = added,
            ChangedPatternCount = changed,
            MissingPatternCount = missing,
            DiffSummary = $"直前の実行（{previous.StartedAtDisplay}）との差分: 新規{added:N0}件、変更{changed:N0}件、今回未観測{missing:N0}件",
            DiffDetails = details
        };
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
            aiObservedCount, requestPatterns, formPatterns, summary, limitations)
        { Role = ReadString(engagement, "activeRole") is { Length: > 0 } role ? role : "未認証" };
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

    private static async Task<Dictionary<string, RunPattern>> ReadPatternsAsync(string directory)
    {
        var patterns = new Dictionary<string, RunPattern>(StringComparer.OrdinalIgnoreCase);
        foreach (var fileName in new[] { "fast-observed-requests.jsonl", "ai-observed-requests.jsonl", "observed-requests.jsonl" })
        {
            var path = Path.Combine(directory, fileName);
            if (!File.Exists(path)) continue;
            using var reader = File.OpenText(path);
            while (await reader.ReadLineAsync() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var request = JsonSerializer.Deserialize<ObservedRequest>(line, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (request is null || string.IsNullOrWhiteSpace(request.Url)) continue;
                    var method = string.IsNullOrWhiteSpace(request.Method) ? "GET" : request.Method.ToUpperInvariant();
                    var pattern = UrlPatternNormalizer.NormalizeForSelection(request.Url);
                    var role = request.Role?.Trim() ?? string.Empty;
                    var key = $"{method}\n{pattern}\n{role}";
                    var item = new RunPattern(method, pattern, role);
                    item.StatusCodes.Add(request.StatusCode?.ToString() ?? "不明");
                    item.ContentTypes.Add(NormalizeContentType(request.ContentType));
                    item.FormCounts.Add(request.FormCount);
                    if (!patterns.TryGetValue(key, out var existing)) patterns[key] = item;
                    else
                    {
                        existing.StatusCodes.UnionWith(item.StatusCodes);
                        existing.ContentTypes.UnionWith(item.ContentTypes);
                        existing.FormCounts.UnionWith(item.FormCounts);
                    }
                }
                catch (JsonException) { }
            }
        }
        return patterns;
    }

    private static string NormalizeContentType(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "不明";
        return value.Split(';', 2)[0].Trim().ToLowerInvariant();
    }

    private static string PatternState(RunPattern pattern) =>
        $"status={string.Join(',', pattern.StatusCodes.Order())}, type={string.Join(',', pattern.ContentTypes.Order())}, forms={string.Join(',', pattern.FormCounts.Order())}";

    private sealed class RunPattern(string method, string pattern, string role)
    {
        public string Method { get; } = method;
        public string Pattern { get; } = pattern;
        public string Role { get; } = role;
        public HashSet<string> StatusCodes { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> ContentTypes { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<int> FormCounts { get; } = [];
        public string Signature => $"{string.Join(',', StatusCodes.Order())}|{string.Join(',', ContentTypes.Order())}|{string.Join(',', FormCounts.Order())}";
    }
}
