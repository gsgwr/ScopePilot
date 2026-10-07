using System.Text.Json.Serialization;
using ScopePilot.Infrastructure;
using ScopePilot.Services;

namespace ScopePilot.Domain;

public enum InputMode { WebExploration, ApiDocument }
public enum DocumentAiProvider { Codex, Claude }

public sealed class ApiDocumentSettings
{
    public DocumentAiProvider Provider { get; set; } = DocumentAiProvider.Codex;
    public string FileName { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
}

// Drafts describe documented requests. They never represent an observed response.
public sealed class ApiRequestDraft : ObservableObject
{
    private bool _selected;
    private string _method = "GET", _url = string.Empty, _headersText = string.Empty, _body = string.Empty;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public bool Selected { get => _selected; set => SetProperty(ref _selected, value); }
    public string Method { get => _method; set { if (SetProperty(ref _method, value)) RaisePropertyChanged(nameof(RawRequest)); } }
    public string Url { get => _url; set { if (SetProperty(ref _url, value)) RaisePropertyChanged(nameof(RawRequest)); } }
    public string HeadersText { get => _headersText; set { if (SetProperty(ref _headersText, value)) RaisePropertyChanged(nameof(RawRequest)); } }
    public string Body { get => _body; set { if (SetProperty(ref _body, value)) RaisePropertyChanged(nameof(RawRequest)); } }
    public string OperationId { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string SourceReference { get; set; } = string.Empty;
    public string Role { get; set; } = "未認証";
    public string[] UnresolvedValues { get; set; } = [];
    public string[] Notes { get; set; } = [];
    public string SourceRunDirectory { get; set; } = string.Empty;
    [JsonIgnore] public string ReviewNotes => string.Join(Environment.NewLine,
        new[] { $"出典: {SourceReference}" }.Concat(UnresolvedValues.Select(x => "要補完: " + x)).Concat(Notes));
    [JsonIgnore] public string RawRequest
    {
        get
        {
            try { return ApiRequestFormatter.Format(this); }
            catch (InvalidDataException ex) { return "リクエストを確認してください: " + ex.Message; }
        }
    }
}
