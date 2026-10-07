using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ScopePilot.Domain;
using ScopePilot.Services;
using ScopePilot.ViewModels;
using Xunit;

namespace ScopePilot.Tests;

public sealed class ApiDocumentTests
{
    [Fact]
    public void GeneratedRequests_KeepBodySourceAndPlaceholders_WithoutObservedResponses()
    {
        var project = Project();
        var result = ApiDocumentService.ParseResult(ResultJson(), project, "run");
        var request = Assert.Single(result.Requests);
        Assert.Equal("https://api.example.test/v1/pets/42?expand=owner", request.Url);
        Assert.Equal("POST", request.Method);
        Assert.Contains("日本語", request.Body);
        Assert.Contains("paths", request.SourceReference);
        Assert.Contains("TOKEN", Assert.Single(request.UnresolvedValues));
        Assert.False(request.Selected);
        Assert.Empty(project.Requests);
        Assert.Empty(project.Findings);
        Assert.Equal("partial", result.Status);
    }

    [Theory]
    [InlineData("https://outside.example.test/pets")]
    [InlineData("//outside.example.test/pets")]
    [InlineData("/pets#fragment")]
    [InlineData("/pets\r\nInjected: x")]
    public void ModelOutput_RejectsNonRelativeOrMalformedPaths(string path) =>
        Assert.Throws<InvalidDataException>(() => ApiDocumentService.ParseResult(ResultJson(path), Project(), "run"));

    [Fact]
    public void ModelOutput_RejectsInjectedHeaderValues_AndExcessRequests()
    {
        Assert.Throws<InvalidDataException>(() => ApiDocumentService.ParseResult(ResultJson(headerValue: "x\r\nInjected: value"), Project(), "run"));
        var project = Project();
        project.MaxRequests = 1;
        Assert.Throws<InvalidDataException>(() => ApiDocumentService.ParseResult(ResultJson(count: 2), project, "run"));
    }

    [Fact]
    public async Task Export_UsesReviewedEditsAndUtf8Length_AndEnforcesCurrentScope()
    {
        using var temp = new TempDirectory();
        var project = Project();
        var request = Assert.Single(ApiDocumentService.ParseResult(ResultJson(), project, "run").Requests);
        project.ApiRequests.Add(request);
        var exporter = new ApiRequestExportService(temp.Path);
        await Assert.ThrowsAsync<InvalidOperationException>(() => exporter.ExportAsync(project));
        request.Selected = true;
        request.Body = "{\"name\":\"修正済み\"}";
        request.HeadersText += "\nContent-Length: 9999\nHost: wrong.example.test";
        var exported = await exporter.ExportAsync(project);
        var raw = await File.ReadAllTextAsync(System.IO.Path.Combine(exported.Directory, "requests", "0001.http"));
        Assert.StartsWith("POST /v1/pets/42?expand=owner HTTP/1.1\r\nHost: api.example.test\r\n", raw);
        Assert.Contains($"Content-Length: {Encoding.UTF8.GetByteCount(request.Body)}\r\n\r\n{request.Body}", raw);
        Assert.DoesNotContain("wrong.example.test", raw);
        Assert.True(File.Exists(System.IO.Path.Combine(exported.Directory, "api-request-checklist.tsv")));
        project.AllowedOrigins = "https://different.example.test";
        await Assert.ThrowsAsync<InvalidDataException>(() => exporter.ExportAsync(project));
        Assert.Throws<InvalidDataException>(() => ApiRequestFormatter.EnsureAllowed("http://api.example.test/v1", "https://api.example.test"));
        Assert.Throws<InvalidDataException>(() => ApiRequestFormatter.EnsureAllowed("https://api.example.test:8443/v1", "https://api.example.test"));
    }

    [Fact]
    public void OldProjects_DefaultToWeb_AndApiSettingsAndDraftsRoundTrip()
    {
        var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
        Assert.Equal(InputMode.WebExploration, JsonSerializer.Deserialize<EngagementProject>("{\"Name\":\"old\"}", options)!.Mode);
        var project = Project();
        project.ApiDocument.Provider = DocumentAiProvider.Claude;
        project.ApiRequests.Add(Assert.Single(ApiDocumentService.ParseResult(ResultJson(), project, "run").Requests));
        project.ApiRequests[0].Selected = true;
        var restored = JsonSerializer.Deserialize<EngagementProject>(JsonSerializer.Serialize(project, options), options)!;
        Assert.Equal(InputMode.ApiDocument, restored.Mode);
        Assert.Equal(DocumentAiProvider.Claude, restored.ApiDocument.Provider);
        Assert.Equal(project.ApiDocument.Content, restored.ApiDocument.Content);
        Assert.Equal(project.ApiRequests[0].RawRequest, restored.ApiRequests[0].RawRequest);
        Assert.True(restored.ApiRequests[0].Selected);
    }

    [Fact]
    public async Task ApiPackageAndHistory_KeepGeneratedCountSeparateFromObservationCounts()
    {
        using var temp = new TempDirectory();
        var project = Project();
        var run = await new ApiDocumentService(temp.Path).BuildAsync(project);
        Assert.True(File.Exists(System.IO.Path.Combine(run, "api-document.txt")));
        Assert.False(File.Exists(System.IO.Path.Combine(run, "fast-observed-requests.jsonl")));
        await File.WriteAllTextAsync(System.IO.Path.Combine(run, "api-document-summary.json"),
            "{\"status\":\"partial\",\"summary\":\"generated\",\"requestCount\":7,\"limitations\":[\"external ref\"]}");
        var history = Assert.Single(await new RunHistoryService(temp.Path).ListAsync(project.Id));
        Assert.Equal(InputMode.ApiDocument, history.Mode);
        Assert.Equal(0, history.FastRequestCount);
        Assert.Equal(0, history.AiObservedCount);
        Assert.Equal(7, history.RequestPatternCount);
        Assert.Contains("external ref", history.Limitations);
        using var schema = JsonDocument.Parse(ApiDocumentService.ResultSchema);
        Assert.Equal("object", schema.RootElement.GetProperty("type").GetString());
    }

    [Fact]
    public void ApiMode_DisablesMcpSetup_AndBothProvidersUseStdinWithStructuredOutput()
    {
        var model = new MainViewModel();
        model.ProjectModeIndex = 1;
        Assert.True(model.IsApiMode);
        Assert.False(model.SetupMcpCommand.CanExecute(null));
        Assert.Equal("8", model.ReviewPageTag);
        var codex = ApiDocumentAiRunner.CreateStartInfo("codex.exe", DocumentAiProvider.Codex, "run");
        Assert.True(codex.RedirectStandardInput);
        Assert.Contains("--ignore-user-config", codex.ArgumentList);
        Assert.Contains("read-only", codex.ArgumentList);
        Assert.Contains("--output-schema", codex.ArgumentList);
        var claude = ApiDocumentAiRunner.CreateStartInfo("claude.exe", DocumentAiProvider.Claude, "run");
        Assert.True(claude.RedirectStandardInput);
        Assert.Contains("--json-schema", claude.ArgumentList);
        Assert.Contains("--strict-mcp-config", claude.ArgumentList);
        Assert.Contains("--safe-mode", claude.ArgumentList);
        Assert.DoesNotContain("--bare", claude.ArgumentList);
        Assert.Equal("", claude.ArgumentList[claude.ArgumentList.ToList().IndexOf("--tools") + 1]);
        var wrapped = JsonSerializer.Serialize(new { is_error = false, structured_output = JsonDocument.Parse(ResultJson()).RootElement });
        Assert.Single(ApiDocumentService.ParseResult(ApiDocumentAiRunner.ExtractClaudeResult(wrapped), Project(), "run").Requests);
        Assert.Throws<InvalidDataException>(() => ApiDocumentAiRunner.ExtractClaudeResult("{\"is_error\":true}"));
    }

    [Fact]
    public async Task WebHistory_ComparesPreviousWebRun_WhenApiGenerationIsBetweenRuns()
    {
        using var temp = new TempDirectory();
        var project = Project();
        var root = System.IO.Path.Combine(temp.Path, project.Id.ToString("N"));
        var oldWeb = System.IO.Path.Combine(root, "old-web");
        var newWeb = System.IO.Path.Combine(root, "new-web");
        Directory.CreateDirectory(oldWeb);
        Directory.CreateDirectory(newWeb);
        await File.WriteAllTextAsync(System.IO.Path.Combine(oldWeb, "observed-requests.jsonl"), JsonSerializer.Serialize(new { method = "GET", url = "https://api.example.test/pets", statusCode = 200 }));
        await File.WriteAllTextAsync(System.IO.Path.Combine(newWeb, "observed-requests.jsonl"), JsonSerializer.Serialize(new { method = "GET", url = "https://api.example.test/pets", statusCode = 500 }));
        var apiRun = await new ApiDocumentService(temp.Path).BuildAsync(project);
        Directory.SetLastWriteTimeUtc(oldWeb, DateTime.UtcNow.AddMinutes(-3));
        Directory.SetLastWriteTimeUtc(apiRun, DateTime.UtcNow.AddMinutes(-2));
        Directory.SetLastWriteTimeUtc(newWeb, DateTime.UtcNow.AddMinutes(-1));
        var history = await new RunHistoryService(temp.Path).ListAsync(project.Id);
        Assert.Equal("new-web", history[0].RunId);
        Assert.Equal(1, history[0].ChangedPatternCount);
        Assert.Equal(InputMode.ApiDocument, history[1].Mode);
        Assert.Equal(0, history[1].ChangedPatternCount);
    }

    private static EngagementProject Project() => new()
    {
        Mode = InputMode.ApiDocument, AllowedOrigins = "https://api.example.test",
        ApiDocument = new() { FileName = "openapi.yaml", Content = "openapi: 3.1.0\npaths: {}", BaseUrl = "https://api.example.test/v1" }
    };

    private static string ResultJson(string path = "/pets/42?expand=owner", string headerValue = "application/json", int count = 1) => JsonSerializer.Serialize(new
    {
        status = "partial", summary = "Requests generated", limitations = new[] { "Token is unresolved" },
        requests = Enumerable.Range(0, count).Select(_ => new
        {
            method = "POST", path, headers = new[] { new { name = "Content-Type", value = headerValue } }, body = "{\"name\":\"日本語\"}",
            operationId = "updatePet", summary = "Update pet", sourceReference = "#/paths/~1pets~1{id}/post",
            unresolvedValues = new[] { "Authorization: <TOKEN>" }, notes = new[] { "Document example" }
        })
    });

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ScopePilot.Tests", Guid.NewGuid().ToString("N"));
        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
    }
}
