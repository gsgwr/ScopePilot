using System.Text;
using System.Text.Json;
using ScopePilot.Domain;

namespace ScopePilot.Services;

public sealed record BurpScopeExportResult(string Directory, string RegexPath, string UrlPath, string JsonPath, int SelectedCount);

public sealed class BurpScopeExportService
{
    public async Task<BurpScopeExportResult> ExportAsync(EngagementProject project)
    {
        var selected = project.Candidates.Where(x => x.Selected).ToArray();
        if (selected.Length == 0) throw new InvalidOperationException("採用された診断対象候補がありません。候補一覧で採用してください。");

        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScopePilot", "exports", project.Id.ToString("N"));
        var directory = Path.Combine(root, DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss-fff"));
        Directory.CreateDirectory(directory);

        var regexes = selected.Select(BuildScopeRegex).Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal).ToArray();
        var urls = selected.Select(x => x.Representative.Url).Where(x => Uri.TryCreate(x, UriKind.Absolute, out _))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var regexPath = Path.Combine(directory, "burp-scope-regex.txt");
        var urlPath = Path.Combine(directory, "burp-scope-urls.txt");
        var jsonPath = Path.Combine(directory, "burp-scope.json");

        await File.WriteAllLinesAsync(regexPath, regexes, new UTF8Encoding(false));
        await File.WriteAllLinesAsync(urlPath, urls, new UTF8Encoding(false));
        var manifest = new
        {
            generatedAt = DateTimeOffset.Now,
            projectId = project.Id,
            projectName = project.Name,
            allowedOrigins = project.AllowedOrigins.Split(['\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            selectedCandidates = selected.Select(x => new { x.Pattern, x.Category, x.Representative.Url, scopeRegex = BuildScopeRegex(x) }).ToArray(),
            deferredCandidatePatterns = project.DeferredCandidatePatterns,
            limitations = new[]
            {
                "Burp Suite Community EditionではScope設定の自動変更を行いません。burp-scope-regex.txtをBurpのTarget > Scopeへ手動登録してください。",
                "Scopeは通信を許可・遮断する機能ではなく、Burpの対象表示・対象機能を絞る設定です。実際の診断範囲は案件の許可Originと照合してください。"
            }
        };
        await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        await File.WriteAllTextAsync(Path.Combine(directory, "README.txt"),
            "Burp Suite Community Editionへの登録手順\r\n\r\n1. BurpのTarget > Scopeを開きます。\r\n2. Include in scopeのURL欄で、burp-scope-regex.txtの各行をURL regexとして追加します。\r\n3. 対象Originが案件の許可範囲と一致することを確認します。\r\n4. 除外した候補はこのScope出力には含めていません。\r\n\r\n代表URLはburp-scope-urls.txt、証跡と選定理由はburp-scope.jsonを参照してください。\r\n", new UTF8Encoding(false));
        return new(directory, regexPath, urlPath, jsonPath, selected.Length);
    }

    private static string BuildScopeRegex(DiagnosticCandidate candidate)
    {
        var source = candidate.Pattern;
        var separator = source.IndexOf(' ');
        if (separator >= 0 && separator + 1 < source.Length) source = source[(separator + 1)..];
        if (!Uri.TryCreate(candidate.Representative.Url, UriKind.Absolute, out var representative)) return string.Empty;
        if (!source.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !source.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            source = representative.GetLeftPart(UriPartial.Authority) + (source.StartsWith('/') ? source : "/" + source);

        var pathStart = source.IndexOf('/', source.IndexOf("://", StringComparison.Ordinal) + 3);
        if (pathStart < 0) pathStart = source.Length;
        var authority = source[..pathStart];
        var pathAndQuery = source[pathStart..];
        var queryStart = pathAndQuery.IndexOf('?');
        var path = queryStart >= 0 ? pathAndQuery[..queryStart] : pathAndQuery;
        var regex = "^" + RegexEscape(authority) + RegexEscape(path);
        regex = ReplacePlaceholders(regex);
        return regex + "(?:\\?.*)?$";
    }

    private static string RegexEscape(string value) => System.Text.RegularExpressions.Regex.Escape(value);

    private static string ReplacePlaceholders(string value) => value
        .Replace("\\{guid\\}", "[^/?#]+", StringComparison.OrdinalIgnoreCase)
        .Replace("\\{date\\}", "[^/?#]+", StringComparison.OrdinalIgnoreCase)
        .Replace("\\{id\\}", "[^/?#]+", StringComparison.OrdinalIgnoreCase)
        .Replace("\\{value\\}", "[^/?#]+", StringComparison.OrdinalIgnoreCase);
}
