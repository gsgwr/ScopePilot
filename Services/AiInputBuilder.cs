using System.Text.Json;
using ScopePilot.Domain;

namespace ScopePilot.Services;

public sealed record AiInputBuildResult(
    bool RequiresAi,
    int TotalRequests,
    int StaticExcluded,
    int DeferredExcluded,
    int RequestPatternCount,
    int FormPatternCount,
    int TruncatedPatternCount,
    string Path);

public sealed class AiInputBuilder
{
    private const int MaxRequestPatterns = 200;
    private const int MaxFormPatterns = 100;
    private readonly RequestImporter _importer = new();

    public async Task<AiInputBuildResult> BuildAsync(string runDirectory)
    {
        var requestFile = Path.Combine(runDirectory, "fast-observed-requests.jsonl");
        var requests = File.Exists(requestFile)
            ? await _importer.ImportAsync(requestFile)
            : Array.Empty<ObservedRequest>();
        var (adoptedPatterns, deferredPatterns, guidelines, startUrl) = await ReadRunSettingsAsync(Path.Combine(runDirectory, "engagement.json"));

        var selectedRequests = new List<(ObservedRequest Request, SelectionAssessment Assessment, bool PreviouslyAdopted)>();
        var staticExcluded = 0;
        var deferredExcluded = 0;
        foreach (var request in requests)
        {
            var pattern = RequestPattern(request);
            var previouslyAdopted = adoptedPatterns.Contains(pattern);
            if (!previouslyAdopted && deferredPatterns.Contains(pattern))
            {
                deferredExcluded++;
                continue;
            }
            var assessment = GuidelineSelectionPolicy.Evaluate(request, guidelines);
            var isStartUrl = IsSameRequestUrl(request.Url, startUrl);
            if (!assessment.Selected && !previouslyAdopted && !isStartUrl) staticExcluded++;
            else selectedRequests.Add((request, assessment with
            {
                Selected = true,
                Reason = previouslyAdopted
                    ? "前回、診断対象として採用されたパターンです。 " + assessment.Reason
                    : isStartUrl
                        ? "案件の開始URLのため、静的画面候補でも初回のAI確認を省略しません。 " + assessment.Reason
                        : assessment.Reason,
                Signals = previouslyAdopted
                    ? assessment.Signals.Append("前回採用済み").ToArray()
                    : isStartUrl
                        ? assessment.Signals.Append("案件の開始URL").ToArray()
                        : assessment.Signals
            }, previouslyAdopted));
        }

        var requestGroups = selectedRequests
            .GroupBy(x => RequestPattern(x.Request), StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var first = group.First();
                return new
                {
                    pattern = group.Key,
                    method = first.Request.Method.ToUpperInvariant(),
                    representativeUrl = first.Request.Url,
                    occurrences = group.Count(),
                    statusCodes = group.Select(x => x.Request.StatusCode).Where(x => x.HasValue).Distinct().Order().ToArray(),
                    contentTypes = group.Select(x => BaseContentType(x.Request.ContentType)).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(5).ToArray(),
                    category = first.Assessment.Category,
                    reasons = group.SelectMany(x => x.Assessment.Signals).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                    guidelineBasis = group.SelectMany(x => x.Assessment.References).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                    selectionSource = group.Any(x => x.PreviouslyAdopted) ? "previously-adopted" : "new-discovery"
                };
            })
            .OrderByDescending(x => x.method != "GET")
            .ThenByDescending(x => CategoryPriority(x.category))
            .ThenByDescending(x => x.occurrences)
            .ThenBy(x => x.pattern, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var discoveredForms = await ReadFormsAsync(Path.Combine(runDirectory, "crawl-pages.jsonl"));
        var selectedForms = new List<FormPattern>();
        foreach (var form in discoveredForms)
        {
            var targetPattern = $"{form.Method} {NormalizeUrl(form.Action)}";
            var pagePattern = $"GET {NormalizeUrl(form.Page)}";
            var hasAdoptedContext = adoptedPatterns.Contains(targetPattern) || adoptedPatterns.Contains(pagePattern);
            if (!hasAdoptedContext && (deferredPatterns.Contains(targetPattern) || deferredPatterns.Contains(pagePattern)))
            {
                deferredExcluded++;
                continue;
            }
            selectedForms.Add(form);
        }
        var formGroups = selectedForms
            .GroupBy(x => x.Pattern, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var first = group.First();
                return new
                {
                    pattern = group.Key,
                    method = first.Method,
                    action = first.Action,
                    fields = first.Fields,
                    occurrences = group.Count(),
                    representativePage = first.Page,
                    reason = first.Method == "GET" ? "入力フォーム" : "状態変更の可能性があるフォーム"
                };
            })
            .OrderByDescending(x => x.method != "GET")
            .ThenByDescending(x => x.occurrences)
            .ThenBy(x => x.pattern, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var keptRequests = requestGroups.Take(MaxRequestPatterns).ToArray();
        var keptForms = formGroups.Take(MaxFormPatterns).ToArray();
        var truncated = Math.Max(0, requestGroups.Length - keptRequests.Length) + Math.Max(0, formGroups.Length - keptForms.Length);
        var output = new
        {
            generatedAt = DateTimeOffset.Now,
            policy = new
            {
                description = "前回採用済みと新規発見パターンを収録し、除外キャッシュを省いています。観測可能な選定規則による一次分類であり、除外は安全性の証明ではありません。",
                rawFilesAreEvidenceOnly = true,
                maxRequestPatterns = MaxRequestPatterns,
                maxFormPatterns = MaxFormPatterns,
                adoptedPatternCount = adoptedPatterns.Count,
                deferredPatternCount = deferredPatterns.Count
            },
            summary = new
            {
                totalObservedRequests = requests.Count,
                excludedStaticRequests = staticExcluded,
                excludedCachedRequestsAndForms = deferredExcluded,
                dynamicRequestOccurrences = selectedRequests.Count,
                requestPatternCount = requestGroups.Length,
                formPatternCount = formGroups.Length,
                truncatedPatternCount = truncated
            },
            requestPatterns = keptRequests,
            formPatterns = keptForms
        };

        var outputPath = Path.Combine(runDirectory, "ai-input.json");
        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true }));
        return new AiInputBuildResult(
            keptRequests.Length + keptForms.Length > 0,
            requests.Count,
            staticExcluded,
            deferredExcluded,
            requestGroups.Length,
            formGroups.Length,
            truncated,
            outputPath);
    }

    private static int CategoryPriority(string category) => category switch
    {
        "認証・セッション" => 9,
        "管理・権限" => 8,
        "ファイル処理" => 7,
        "AI機能" => 7,
        "運用・クラウド機能" => 6,
        "外部連携" => 6,
        "API" => 5,
        "検索・入力" or "入力パラメーター" => 4,
        _ => 1
    };

    private static async Task<IReadOnlyList<FormPattern>> ReadFormsAsync(string path)
    {
        if (!File.Exists(path)) return Array.Empty<FormPattern>();
        var forms = new List<FormPattern>();
        foreach (var line in await File.ReadAllLinesAsync(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var page = root.TryGetProperty("url", out var pageValue) ? pageValue.GetString() ?? string.Empty : string.Empty;
                if (!root.TryGetProperty("forms", out var formArray) || formArray.ValueKind != JsonValueKind.Array) continue;
                foreach (var form in formArray.EnumerateArray())
                {
                    var method = form.TryGetProperty("method", out var methodValue) ? methodValue.GetString()?.ToUpperInvariant() ?? "GET" : "GET";
                    var action = form.TryGetProperty("action", out var actionValue) ? actionValue.GetString() ?? page : page;
                    var fields = form.TryGetProperty("fields", out var fieldsValue) && fieldsValue.ValueKind == JsonValueKind.Array
                        ? fieldsValue.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray()
                        : Array.Empty<string>();
                    if (fields.Length == 0) continue;
                    var pattern = $"{method} {NormalizeUrl(action)} fields={string.Join(',', fields)}";
                    forms.Add(new FormPattern(pattern, method, action, fields, page));
                }
            }
            catch (JsonException) { }
        }
        return forms;
    }

    private static string RequestPattern(ObservedRequest request) => $"{request.Method.ToUpperInvariant()} {NormalizeUrl(request.Url)}";

    private static string NormalizeUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return value;
        return UrlPatternNormalizer.NormalizeForSelection(value);
    }

    private static async Task<(HashSet<string> Adopted, HashSet<string> Deferred, GuidelineSelectionOptions Guidelines, string StartUrl)> ReadRunSettingsAsync(string path)
    {
        if (!File.Exists(path)) return (new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase), new GuidelineSelectionOptions(), string.Empty);
        try
        {
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            var root = document.RootElement;
            var guidelines = root.TryGetProperty("guidelines", out var guidelineElement)
                ? JsonSerializer.Deserialize<GuidelineSelectionOptions>(guidelineElement.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new GuidelineSelectionOptions()
                : new GuidelineSelectionOptions();
            var startUrlElement = root.EnumerateObject()
                .FirstOrDefault(property => property.Name.Equals("startUrl", StringComparison.OrdinalIgnoreCase)).Value;
            var startUrl = startUrlElement.ValueKind == JsonValueKind.String
                ? startUrlElement.GetString() ?? string.Empty
                : string.Empty;
            return (ReadPatterns(root, "adoptedCandidatePatterns"), ReadPatterns(root, "deferredCandidatePatterns"), guidelines, startUrl);
        }
        catch (JsonException) { return (new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase), new GuidelineSelectionOptions(), string.Empty); }
    }

    private static bool IsSameRequestUrl(string requestUrl, string startUrl)
    {
        if (!Uri.TryCreate(requestUrl, UriKind.Absolute, out var request) || !Uri.TryCreate(startUrl, UriKind.Absolute, out var start)) return false;
        request = new UriBuilder(request) { Fragment = string.Empty }.Uri;
        start = new UriBuilder(start) { Fragment = string.Empty }.Uri;
        return Uri.Compare(request, start, UriComponents.HttpRequestUrl, UriFormat.UriEscaped, StringComparison.OrdinalIgnoreCase) == 0;
    }

    private static HashSet<string> ReadPatterns(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var values) || values.ValueKind != JsonValueKind.Array)
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return values.EnumerateArray()
            .Where(value => value.ValueKind == JsonValueKind.String)
            .Select(value => value.GetString())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => NormalizePattern(value!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string NormalizePattern(string value)
    {
        var separator = value.IndexOf(' ');
        if (separator <= 0 || separator == value.Length - 1) return value.Trim();
        var method = value[..separator].ToUpperInvariant();
        var url = value[(separator + 1)..].Trim();
        return Uri.TryCreate(url, UriKind.Absolute, out _) ? $"{method} {NormalizeUrl(url)}" : value.Trim();
    }

    private static string BaseContentType(string value) => value.Split(';', 2)[0].Trim();

    private sealed record FormPattern(string Pattern, string Method, string Action, string[] Fields, string Page);
}
