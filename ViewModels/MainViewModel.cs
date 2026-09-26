using System.Collections.ObjectModel;
using ScopePilot.Domain;
using ScopePilot.Infrastructure;
using ScopePilot.Services;

namespace ScopePilot.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly ProjectStore _store = new();
    private readonly RequestImporter _importer = new();
    private readonly CandidateSelector _selector = new();
    private readonly EnvironmentProbe _environment = new();
    private readonly ExplorationPackageBuilder _packageBuilder = new();
    private readonly McpSetupService _mcpSetup = new();
    private readonly CodexExplorationRunner _runner = new();
    private readonly FastCrawlerRunner _fastCrawler = new();
    private readonly AiInputBuilder _aiInputBuilder = new();
    private readonly BurpScopeExportService _burpScopeExport = new();
    private readonly CodexFindingImporter _findingImporter = new();
    private readonly FindingReportService _findingReport = new();
    private EngagementProject _project = new();
    private CancellationTokenSource? _runCancellation;
    private string? _currentRunDirectory;
    private bool _isRunning;
    private string _interventionText = string.Empty;
    private DiagnosticCandidate? _selectedCandidate;
    private DiagnosticFinding? _selectedFinding;

    public MainViewModel()
    {
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        NewProjectCommand = new AsyncRelayCommand(NewProjectAsync, () => !IsRunning);
        CheckEnvironmentCommand = new AsyncRelayCommand(CheckEnvironmentAsync);
        SetupMcpCommand = new AsyncRelayCommand(SetupMcpAsync);
        BuildExplorationPackageCommand = new AsyncRelayCommand(BuildExplorationPackageAsync);
        StartExplorationCommand = new AsyncRelayCommand(StartExplorationAsync, () => !IsRunning);
        StopExplorationCommand = new RelayCommand(StopExploration, () => IsRunning);
        ResumeExplorationCommand = new RelayCommand(ResumeExploration, () => IsRunning && Project.Status == ProjectStatus.WaitingForHuman);
        GenerateCandidatesCommand = new RelayCommand(GenerateCandidates);
        ExportBurpScopeCommand = new AsyncRelayCommand(ExportBurpScopeAsync, () => !IsRunning);
        ExportFindingsCommand = new AsyncRelayCommand(ExportFindingsAsync, () => !IsRunning);
    }

    public EngagementProject Project { get => _project; private set { if (SetProperty(ref _project, value)) RefreshSummary(); } }
    public ObservableCollection<string> Logs { get; } = [];
    public AsyncRelayCommand SaveCommand { get; }
    public AsyncRelayCommand NewProjectCommand { get; }
    public AsyncRelayCommand CheckEnvironmentCommand { get; }
    public AsyncRelayCommand SetupMcpCommand { get; }
    public AsyncRelayCommand BuildExplorationPackageCommand { get; }
    public AsyncRelayCommand StartExplorationCommand { get; }
    public RelayCommand StopExplorationCommand { get; }
    public RelayCommand ResumeExplorationCommand { get; }
    public RelayCommand GenerateCandidatesCommand { get; }
    public AsyncRelayCommand ExportBurpScopeCommand { get; }
    public AsyncRelayCommand ExportFindingsCommand { get; }
    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (!SetProperty(ref _isRunning, value)) return;
            StartExplorationCommand.RaiseCanExecuteChanged();
            NewProjectCommand.RaiseCanExecuteChanged();
            StopExplorationCommand.RaiseCanExecuteChanged();
            ResumeExplorationCommand.RaiseCanExecuteChanged();
            ExportBurpScopeCommand.RaiseCanExecuteChanged();
            ExportFindingsCommand.RaiseCanExecuteChanged();
        }
    }
    public string InterventionText { get => _interventionText; private set => SetProperty(ref _interventionText, value); }
    public DiagnosticCandidate? SelectedCandidate { get => _selectedCandidate; set => SetProperty(ref _selectedCandidate, value); }
    public DiagnosticFinding? SelectedFinding { get => _selectedFinding; set => SetProperty(ref _selectedFinding, value); }
    public string StatusText => Project.Status switch { ProjectStatus.Draft => "設定中", ProjectStatus.Ready => "準備完了", ProjectStatus.Crawling => "探索中", ProjectStatus.WaitingForHuman => "手動操作待ち", ProjectStatus.Organizing => "整理中", ProjectStatus.ReviewReady => "確認可能", ProjectStatus.Partial => "部分結果", ProjectStatus.Paused => "一時停止", _ => "失敗" };
    public string SummaryText => $"{Project.Name}  |  観測 {Project.Requests.Count:N0}件  |  候補 {Project.Candidates.Count(x => x.Selected):N0}件";
    public string RequestSummary => $"観測した通信: {Project.Requests.Count:N0}件";
    public string CandidateSummary => $"代表化した候補: {Project.Candidates.Count:N0}件（採用 {Project.Candidates.Count(x => x.Selected):N0}件／除外キャッシュ {Project.DeferredCandidatePatterns.Count:N0}件）";
    public string FindingSummary => $"診断所見: {Project.Findings.Count:N0}件";
    public string ConsoleText => string.Join(Environment.NewLine, Logs);

    public async Task InitializeAsync()
    {
        try
        {
            Project = await _store.LoadLatestAsync() ?? new EngagementProject();
            Project.Guidelines ??= new GuidelineSelectionOptions();
            Project.Findings ??= [];
            Log(Project.CreatedAt == Project.UpdatedAt ? "新規案件を開始しました。" : "直近の案件を復元しました。");
            if (Project.Requests.Count > 0)
            {
                GenerateCandidates();
                await _store.SaveAsync(Project);
                Log("最新のURLパターン規則で診断対象候補を再生成しました。");
            }
        }
        catch (Exception ex) { Log($"案件の復元に失敗しました: {ex.Message}"); }
    }

    public async Task<int> ImportAsync(string path)
    {
        try
        {
            Log($"取込開始: {path}");
            var imported = await _importer.ImportAsync(path);
            var known = Project.Requests.Select(x => $"{x.Method} {x.Url}").ToHashSet(StringComparer.Ordinal);
            var added = 0;
            foreach (var request in imported)
            {
                if (known.Add($"{request.Method} {request.Url}")) { Project.Requests.Add(request); added++; }
                else if (request.PageInspected)
                {
                    var existing = Project.Requests.First(x => x.Method == request.Method && x.Url == request.Url);
                    existing.PageInspected = true;
                    existing.FormCount = request.FormCount;
                    existing.FormFieldNames = request.FormFieldNames;
                    if (!string.IsNullOrWhiteSpace(request.PageTitle)) existing.PageTitle = request.PageTitle;
                }
            }
            Log($"{imported.Count:N0}件を読み取り、{added:N0}件を追加しました。");
            GenerateCandidates();
            await SaveAsync();
            return imported.Count;
        }
        catch (Exception ex) { Log($"取込失敗: {ex.Message}"); return 0; }
    }

    private async Task SaveAsync()
    {
        try
        {
            SyncCandidateDecisionCache();
            var valid = IsConfigurationValid();
            if (!valid) Project.Status = ProjectStatus.Draft;
            else if (Project.Status is ProjectStatus.Draft or ProjectStatus.Failed) Project.Status = ProjectStatus.Ready;
            await _store.SaveAsync(Project); Log($"案件を保存しました: {_store.DataDirectory}"); RefreshSummary();
        }
        catch (Exception ex) { Log($"保存失敗: {ex.Message}"); }
    }

    public Task SaveCandidateDecisionsAsync() => SaveAsync();

    public async Task ApplyGuidelineSelectionAsync()
    {
        Project.Guidelines ??= new GuidelineSelectionOptions();
        GenerateCandidates();
        await SaveAsync();
        Log("参照ガイドラインの変更を候補選定と案件設定へ反映しました。");
    }

    private async Task NewProjectAsync()
    {
        try
        {
            SyncCandidateDecisionCache();
            await _store.SaveAsync(Project);
            Project = new EngagementProject();
            _currentRunDirectory = null;
            InterventionText = string.Empty;
            SelectedCandidate = null;
            SelectedFinding = null;
            Logs.Clear();
            RaisePropertyChanged(nameof(ConsoleText));
            await _store.SaveAsync(Project);
            Log("新しい案件を開始しました。観測通信、診断候補、実行ログを初期化しました。");
            RefreshSummary();
        }
        catch (Exception ex) { Log($"新しい案件を開始できませんでした: {ex.Message}"); }
    }

    private async Task CheckEnvironmentAsync()
    {
        Log("環境チェックを開始します。");
        foreach (var item in await _environment.CheckAsync()) Log($"[{(item.Available ? "OK" : "要設定")}] {item.Name}: {item.Detail}");
    }

    private async Task SetupMcpAsync()
    {
        Log("CodexへのMCP登録を開始します。");
        var result = await _mcpSetup.ConfigureAsync();
        foreach (var message in result.Messages) Log(message);
        Log(result.Success ? "MCPの登録が完了しました。Codexを再起動すると設定が反映されます。" : "MCP登録に失敗しました。上記ログを確認してください。");
    }

    private async Task BuildExplorationPackageAsync()
    {
        var path = await BuildPackageAsync();
        if (path is null) return;
        Log($"探索パッケージを生成しました: {path}");
        Log("パッケージは［AI探索を開始］から実行できます。");
    }

    private async Task StartExplorationAsync()
    {
        Log("MCP設定を確認しています。");
        var setup = await _mcpSetup.ConfigureAsync();
        foreach (var message in setup.Messages) Log(message);
        if (!setup.Success)
        {
            Project.Status = ProjectStatus.Failed;
            RefreshSummary();
            Log("MCP設定を準備できないため探索を開始しませんでした。");
            return;
        }

        var path = await BuildPackageAsync();
        if (path is null) return;

        _currentRunDirectory = path;
        InterventionText = string.Empty;
        _runCancellation = new CancellationTokenSource();
        if (Project.MaxMinutes > 0) _runCancellation.CancelAfter(TimeSpan.FromMinutes(Project.MaxMinutes));
        IsRunning = true;
        Project.Status = ProjectStatus.Crawling;
        RefreshSummary();
        await _store.SaveAsync(Project);

        try
        {
            var progress = new Progress<string>(Log);
            var crawl = await _fastCrawler.RunAsync(path, progress, _runCancellation.Token);
            if (crawl.Cancelled)
            {
                Project.Status = ProjectStatus.Paused;
                Log("高速クローラを停止しました。");
                return;
            }
            if (crawl.ProxyUnavailable)
            {
                Project.Status = ProjectStatus.Failed;
                Log("Burp Proxy (127.0.0.1:8080) に接続できないため探索を開始できませんでした。Burp Suiteを起動し、Proxy > Proxy settings の Listener が有効であることを確認してから再実行してください。");
                RefreshSummary();
                return;
            }
            if (crawl.ExitCode != 0)
                Log($"高速クローラが終了コード {crawl.ExitCode} で停止しました。Codex探索へフォールバックします。");
            else
            {
                Log("高速GETクロールが完了しました。アプリ側でAI対象を分類します。");
                var aiInput = await _aiInputBuilder.BuildAsync(path);
                Log($"AI事前分類: 通信{aiInput.TotalRequests:N0}件中、静的アセット・静的画面候補{aiInput.StaticExcluded:N0}件と除外キャッシュ{aiInput.DeferredExcluded:N0}件を対象外とし、採用済み・新規のリクエスト{aiInput.RequestPatternCount:N0}パターン・フォーム{aiInput.FormPatternCount:N0}パターンに集約しました。");
                if (aiInput.TruncatedPatternCount > 0)
                    Log($"AI入力の上限により{aiInput.TruncatedPatternCount:N0}パターンを省略しました。全件の証跡は実行フォルダーに保持しています。");
                if (!aiInput.RequiresAi)
                {
                    Log("AIで確認すべき動的パターンがないため、Codex探索を起動せず完了します。");
                    var fastObservations = Path.Combine(path, "fast-observed-requests.jsonl");
                    if (File.Exists(fastObservations) && new FileInfo(fastObservations).Length > 0)
                        await ImportAsync(fastObservations);
                    else
                        Project.Status = ProjectStatus.Ready;
                    return;
                }
                Log("動的な代表パターンだけをCodexへ渡して機能確認を開始します。");
            }

            var result = await _runner.RunAsync(path, progress, OnInterventionDetected, _runCancellation.Token);
            if (!result.Cancelled && result.ExitCode == 0 && await IsTransportFailureAsync(result.ResultPath))
            {
                Log("Playwright MCPの切断を検出しました。新しいMCPプロセスで探索を1回だけ自動再開します。");
                await File.AppendAllTextAsync(Path.Combine(path, "prompt.md"),
                    Environment.NewLine + "前回のPlaywright MCP接続が切断されたため、新しいMCPセッションでの再開です。ai-input.jsonと既存のexploration-summary.jsonを読み、未確認の代表パターンから続行し、最終結果ファイルを置き換えてください。" + Environment.NewLine,
                    _runCancellation.Token);
                result = await _runner.RunAsync(path, progress, OnInterventionDetected, _runCancellation.Token);
            }
            if (result.Cancelled)
            {
                Project.Status = ProjectStatus.Paused;
                Log("探索を停止しました。");
            }
            else if (result.HumanInterventionPending)
            {
                Project.Status = ProjectStatus.WaitingForHuman;
                Log("手動操作が完了しないまま探索ジョブが終了しました。");
            }
            else
            {
                if (result.ExitCode != 0)
                    Log($"探索ジョブは終了コード {result.ExitCode} で終了しました。保存済みの観測結果を確認します。");
                var finalStatus = await LogFinalResultAsync(result.ResultPath);
                var observationFiles = new[]
                {
                    Path.Combine(path, "fast-observed-requests.jsonl"),
                    Path.Combine(path, "ai-observed-requests.jsonl"),
                    Path.Combine(path, "observed-requests.jsonl")
                }.Where(file => File.Exists(file) && new FileInfo(file).Length > 0).Distinct().ToArray();
                var importedEvidenceCount = 0;
                if (observationFiles.Length > 0)
                {
                    Log(string.Equals(finalStatus, "failed", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(finalStatus, "partial", StringComparison.OrdinalIgnoreCase)
                        || result.ExitCode != 0
                        ? "探索中に得られた部分的な観測リクエストを取り込みます。"
                        : "探索が完了しました。観測リクエストを取り込みます。");
                    foreach (var observationFile in observationFiles)
                        importedEvidenceCount += await ImportAsync(observationFile);
                }
                var partialResult = result.ExitCode != 0 ||
                    string.Equals(finalStatus, "failed", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(finalStatus, "partial", StringComparison.OrdinalIgnoreCase);
                if (partialResult && (importedEvidenceCount > 0 ||
                    string.Equals(finalStatus, "partial", StringComparison.OrdinalIgnoreCase)))
                {
                    Project.Status = ProjectStatus.Partial;
                    Log("取得済みの結果を部分結果として確認できます。Burp履歴の未取得など、探索結果の制約を確認してください。");
                }
                else if (partialResult)
                {
                    Project.Status = ProjectStatus.Failed;
                    Log("Codexが探索を技術エラーとして終了しました。探索結果の説明を確認してください。");
                }
                else if (observationFiles.Length == 0)
                {
                    Project.Status = ProjectStatus.Ready;
                    Log("探索は完了しましたが、観測リクエストファイルは生成されませんでした。");
                }
            }
        }
        catch (Exception ex)
        {
            Project.Status = ProjectStatus.Failed;
            Log($"探索の起動または実行に失敗しました: {ex.Message}");
        }
        finally
        {
            IsRunning = false;
            _runCancellation?.Dispose();
            _runCancellation = null;
            ResumeExplorationCommand.RaiseCanExecuteChanged();
            RefreshSummary();
            await _store.SaveAsync(Project);
        }
    }

    private async Task<string?> BuildPackageAsync()
    {
        if (!Uri.TryCreate(Project.StartUrl, UriKind.Absolute, out var start) || start.Scheme is not ("http" or "https"))
        { Log("開始URLをhttpまたはhttpsの完全なURLで入力してください。"); return null; }
        if (string.IsNullOrWhiteSpace(Project.AllowedOrigins))
        { Log("診断を許可されたOriginを1件以上入力してください。"); return null; }
        await SaveAsync();
        return await _packageBuilder.BuildAsync(Project);
    }

    private void StopExploration()
    {
        Log("探索の停止を要求しました。");
        _runCancellation?.Cancel();
        _fastCrawler.Stop();
        _runner.Stop();
    }

    private void ResumeExploration()
    {
        if (_currentRunDirectory is null) return;
        CodexExplorationRunner.Resume(_currentRunDirectory);
        Project.Status = ProjectStatus.Crawling;
        InterventionText = string.Empty;
        ResumeExplorationCommand.RaiseCanExecuteChanged();
        RefreshSummary();
        Log("手動操作の完了をCodexへ通知しました。探索を再開します。");
    }

    private void OnInterventionDetected(string detail)
    {
        Project.Status = ProjectStatus.WaitingForHuman;
        InterventionText = FormatIntervention(detail);
        ResumeExplorationCommand.RaiseCanExecuteChanged();
        RefreshSummary();
    }

    private async Task<string?> LogFinalResultAsync(string resultPath)
    {
        if (!File.Exists(resultPath)) return null;
        try
        {
            using var result = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(resultPath));
            if (result.RootElement.TryGetProperty("summary", out var summary)) Log($"探索結果: {summary.GetString()}");
            var imported = await _findingImporter.ImportAsync(resultPath, _currentRunDirectory ?? string.Empty);
            if (imported.Count > 0)
            {
                foreach (var finding in imported)
                {
                    var fingerprint = CodexFindingImporter.Fingerprint(finding);
                    var existing = Project.Findings.FirstOrDefault(item => CodexFindingImporter.Fingerprint(item) == fingerprint);
                    if (existing is not null) Project.Findings.Remove(existing);
                    Project.Findings.Add(finding);
                }
                Log($"Codex所見を{imported.Count:N0}件取り込みました。深刻度・根拠・制約を確認してください。");
                RefreshSummary();
            }
            else if (result.RootElement.TryGetProperty("findings", out _))
                Log("Codex結果に取り込み可能な診断所見はありませんでした。");
            return result.RootElement.TryGetProperty("status", out var status) ? status.GetString() : null;
        }
        catch { Log($"Codex結果ファイル: {resultPath}"); return null; }
    }

    private static async Task<bool> IsTransportFailureAsync(string resultPath)
    {
        if (!File.Exists(resultPath)) return false;
        try
        {
            using var result = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(resultPath));
            var root = result.RootElement;
            var status = root.TryGetProperty("status", out var statusValue) ? statusValue.GetString() : null;
            var summary = root.TryGetProperty("summary", out var summaryValue) ? summaryValue.GetString() : null;
            return string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase) &&
                   summary?.Contains("Transport", StringComparison.OrdinalIgnoreCase) == true;
        }
        catch { return false; }
    }

    private static string FormatIntervention(string detail)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(detail);
            var root = document.RootElement;
            var reason = root.TryGetProperty("reason", out var reasonValue) ? reasonValue.GetString() : null;
            var instruction = root.TryGetProperty("instruction", out var instructionValue) ? instructionValue.GetString() : null;
            var page = root.TryGetProperty("page", out var pageValue) ? pageValue.GetString() : null;
            return $"手動操作が必要です\n理由: {reason}\nページ: {page}\n操作: {instruction}";
        }
        catch { return detail; }
    }

    private bool IsConfigurationValid() =>
        Uri.TryCreate(Project.StartUrl, UriKind.Absolute, out var start) && start.Scheme is "http" or "https" &&
        !string.IsNullOrWhiteSpace(Project.AllowedOrigins);

    private void GenerateCandidates()
    {
        Project.Status = ProjectStatus.Organizing;
        SyncCandidateDecisionCache();
        var previousDecisions = Project.Candidates
            .GroupBy(x => NormalizeCandidatePattern(x.Pattern), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last().Selected, StringComparer.OrdinalIgnoreCase);
        var adopted = Project.AdoptedCandidatePatterns.Select(NormalizeCandidatePattern)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var deferred = Project.DeferredCandidatePatterns.Select(NormalizeCandidatePattern)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = _selector.Select(Project.Requests, Project.Guidelines ?? new GuidelineSelectionOptions());
        Project.Candidates.Clear();
        foreach (var candidate in candidates)
        {
            var key = NormalizeCandidatePattern(candidate.Pattern);
            if (previousDecisions.TryGetValue(key, out var previousSelected)) candidate.Selected = previousSelected;
            else if (adopted.Contains(key)) candidate.Selected = true;
            else if (deferred.Contains(key)) candidate.Selected = false;
            candidate.Decision = candidate.Selected ? "候補" : "除外キャッシュ";
            Project.Candidates.Add(candidate);
        }
        SyncCandidateDecisionCache();
        SelectedCandidate = Project.Candidates.FirstOrDefault();
        Project.Status = candidates.Count > 0 ? ProjectStatus.ReviewReady : ProjectStatus.Ready;
        Log($"{Project.Requests.Count:N0}件から{candidates.Count:N0}個の代表パターンを生成しました。"); RefreshSummary();
    }

    private async Task ExportBurpScopeAsync()
    {
        try
        {
            SyncCandidateDecisionCache();
            var result = await _burpScopeExport.ExportAsync(Project);
            await _store.SaveAsync(Project);
            Log($"Burp Scope出力を生成しました: 採用{result.SelectedCount:N0}件 / {result.Directory}");
            Log("Community EditionではBurpのTarget > Scopeへburp-scope-regex.txtを手動登録してください。");
        }
        catch (Exception ex) { Log($"Burp Scope出力に失敗しました: {ex.Message}"); }
    }

    private async Task ExportFindingsAsync()
    {
        try
        {
            var result = await _findingReport.ExportAsync(Project);
            Log($"診断所見レポートを出力しました: {result.FindingCount:N0}件 / {result.Directory}");
            Log($"JSON: {result.JsonPath}");
            Log($"HTML: {result.HtmlPath}");
        }
        catch (Exception ex) { Log($"診断所見レポートの出力に失敗しました: {ex.Message}"); }
    }

    private void SyncCandidateDecisionCache()
    {
        var adopted = Project.AdoptedCandidatePatterns.Select(NormalizeCandidatePattern)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var deferred = Project.DeferredCandidatePatterns.Select(NormalizeCandidatePattern)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in Project.Candidates)
        {
            var key = NormalizeCandidatePattern(candidate.Pattern);
            if (candidate.Selected) { adopted.Add(key); deferred.Remove(key); }
            else { deferred.Add(key); adopted.Remove(key); }
        }
        Project.AdoptedCandidatePatterns.Clear();
        foreach (var item in adopted.Order(StringComparer.OrdinalIgnoreCase)) Project.AdoptedCandidatePatterns.Add(item);
        Project.DeferredCandidatePatterns.Clear();
        foreach (var item in deferred.Order(StringComparer.OrdinalIgnoreCase)) Project.DeferredCandidatePatterns.Add(item);
    }

    private static string NormalizeCandidatePattern(string value)
    {
        var separator = value.IndexOf(' ');
        if (separator <= 0 || separator == value.Length - 1) return value.Trim();
        var method = value[..separator].ToUpperInvariant();
        var url = value[(separator + 1)..].Trim();
        return Uri.TryCreate(url, UriKind.Absolute, out _)
            ? $"{method} {UrlPatternNormalizer.NormalizeForSelection(url)}"
            : value.Trim();
    }

    private void Log(string message) { Logs.Insert(0, $"{DateTime.Now:HH:mm:ss}  {message}"); while (Logs.Count > 500) Logs.RemoveAt(Logs.Count - 1); RaisePropertyChanged(nameof(ConsoleText)); }
    private void RefreshSummary() { RaisePropertyChanged(nameof(StatusText)); RaisePropertyChanged(nameof(SummaryText)); RaisePropertyChanged(nameof(RequestSummary)); RaisePropertyChanged(nameof(CandidateSummary)); RaisePropertyChanged(nameof(FindingSummary)); }
}
