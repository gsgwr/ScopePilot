using System.Net;
using System.Text;
using System.Text.Json;
using ScopePilot.Domain;

namespace ScopePilot.Services;

public sealed record FindingReportResult(string Directory, string JsonPath, string HtmlPath, int FindingCount);

public sealed class FindingReportService
{
    public async Task<FindingReportResult> ExportAsync(EngagementProject project)
    {
        if (project.Findings.Count == 0) throw new InvalidOperationException("出力できる診断所見がありません。Codex探索結果を取り込んでください。");
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScopePilot", "reports", project.Id.ToString("N"));
        var directory = Path.Combine(root, DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss-fff"));
        Directory.CreateDirectory(directory);
        var jsonPath = Path.Combine(directory, "diagnostic-findings.json");
        var htmlPath = Path.Combine(directory, "diagnostic-findings.html");
        var payload = new
        {
            generatedAt = DateTimeOffset.Now,
            projectId = project.Id,
            projectName = project.Name,
            startUrl = project.StartUrl,
            disclaimer = "本レポートは探索時点の観測に基づく一次整理です。脆弱性の確定には診断担当者による再現確認が必要です。",
            findings = project.Findings
        };
        await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        await File.WriteAllTextAsync(htmlPath, BuildHtml(project), new UTF8Encoding(false));
        return new(directory, jsonPath, htmlPath, project.Findings.Count);
    }

    private static string BuildHtml(EngagementProject project)
    {
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=\"ja\"><head><meta charset=\"utf-8\"><title>ScopePilot 診断所見</title>");
        sb.Append("<style>body{font-family:Segoe UI,Meiryo,sans-serif;color:#172033;margin:32px}h1{border-bottom:2px solid #165DFF;padding-bottom:8px}.finding{border:1px solid #DDE2E8;border-radius:6px;padding:16px;margin:16px 0;break-inside:avoid}.meta{color:#5E6C84}.label{font-weight:600;margin-top:10px}pre{white-space:pre-wrap;background:#F4F6F8;padding:10px;border-radius:4px}</style></head><body>");
        sb.Append($"<h1>ScopePilot 診断所見</h1><p>案件: {Encode(project.Name)}<br>開始URL: {Encode(project.StartUrl)}<br>生成日時: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}</p>");
        sb.Append("<p><strong>注意:</strong> 本レポートは探索時点の観測に基づく一次整理です。脆弱性の確定には診断担当者による再現確認が必要です。</p>");
        foreach (var finding in project.Findings)
        {
            sb.Append("<section class=\"finding\">");
            sb.Append($"<h2>{Encode(finding.Title)}</h2><p class=\"meta\">深刻度: {Encode(finding.Severity)} / 確信度: {Encode(finding.Confidence)} / 分類: {Encode(finding.Category)} / 状態: {Encode(finding.Status)}</p>");
            AppendBlock(sb, "説明", finding.Description);
            AppendBlock(sb, "観測根拠", finding.Evidence);
            AppendBlock(sb, "対象URL", finding.AffectedUrls);
            AppendBlock(sb, "参照基準", finding.GuidelineBasis);
            AppendBlock(sb, "制約・未確認事項", finding.Limitations);
            sb.Append("</section>");
        }
        return sb.Append("</body></html>").ToString();
    }

    private static void AppendBlock(StringBuilder sb, string label, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        sb.Append($"<div class=\"label\">{Encode(label)}</div><pre>{Encode(value)}</pre>");
    }

    private static string Encode(string value) => WebUtility.HtmlEncode(value ?? string.Empty);
}
