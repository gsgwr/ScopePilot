using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ScopePilot.Services;
using ScopePilot.Domain;
using ScopePilot.ViewModels;
using Xunit;

namespace ScopePilot.Tests;

public sealed class WindowLayoutTests
{
    [Fact]
    public void Pages_RenderAtMinimumAndDefaultSizes_InBothThemes()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            MainWindow? window = null;
            var bindingErrors = new BindingErrors();
            try
            {
                PresentationTraceSources.DataBindingSource.Listeners.Add(bindingErrors);
                window = new MainWindow(false);
                var workspace = (TabControl)window.FindName("Workspace");
                var navigation = (ListBox)window.FindName("Navigation");
                var layoutRoot = (FrameworkElement)window.Content;
                var snapshotDirectory = Environment.GetEnvironmentVariable("SCOPEPILOT_UI_SNAPSHOTS");
                var model = (MainViewModel)window.DataContext;
                // Long detail text exercises overflow independently of empty-state rendering.
                var request = new ObservedRequest { Method = "GET", Url = "https://example.test/account/profile?section=notifications", StatusCode = 200, Role = "一般ユーザー", ContentType = "text/html", Source = "fast-crawl" };
                var candidate = new DiagnosticCandidate { Selected = true, Representative = request, Category = "認証・認可", SimilarCount = 24, Reason = "利用者ごとの表示内容が変わるため、静的ページとは断定できません。", DiagnosticCaution = "異なる認証ロールで参照可能な情報を確認してください。" + new string('。', 200), DecisionTrace = string.Join(Environment.NewLine, Enumerable.Repeat("GETリクエスト → 認証状態による応答の違い → 診断対象として採用", 20)), GuidelineBasis = "選択された参照基準" };
                var finding = new DiagnosticFinding { Title = "アクセス制御の確認", Description = candidate.Reason };
                model.SelectedFinding = new DiagnosticFinding { Title = "アクセス制御の確認", Description = candidate.Reason, Evidence = candidate.DecisionTrace, Limitations = "別のロールでの検証は未実施です。" };
                model.SelectedRun = new ExplorationRunSummary("preview", "C:\\example\\runs\\preview", DateTimeOffset.Now, "partial", "部分結果", 123, 22, 18, 4, "一部の通信を取得できませんでした。", candidate.DecisionTrace);
                foreach (var populated in new[] { false, true })
                {
                if (populated)
                {
                    model.Project.Requests.Add(request);
                    model.Project.Candidates.Add(candidate);
                    model.FilteredCandidates.Add(candidate);
                    model.Project.Findings.Add(finding);
                    model.RunHistory.Add(model.SelectedRun!);
                    model.Project.ApiRequests.Add(new ApiRequestDraft
                    {
                        Method = "POST", Url = "https://example.test/v1/pets", HeadersText = "Content-Type: application/json\nAuthorization: <TOKEN>",
                        Body = "{\"name\":\"sample\"}", Summary = "文書に記載された登録処理", SourceReference = "#/paths/~1pets/post",
                        UnresolvedValues = ["Authorization: <TOKEN>"], Notes = [new string('注', 200)]
                    });
                    window.DataContext = null;
                    window.DataContext = model;
                }
                foreach (var apiMode in new[] { false, true })
                {
                model.ProjectModeIndex = apiMode ? 1 : 0;
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                foreach (var dark in new[] { false, true })
                foreach (var size in new[] { new Size(960, 700), new Size(1360, 900) })
                {
                    ThemeService.Apply(window, dark);
                    window.Width = size.Width;
                    window.Height = size.Height;
                    foreach (var item in navigation.Items.OfType<ListBoxItem>().Where(x => x.Tag is string))
                    {
                        navigation.SelectedItem = item;
                        Assert.Equal(int.Parse((string)item.Tag), workspace.SelectedIndex);
                        layoutRoot.Measure(size);
                        layoutRoot.Arrange(new Rect(size));
                        layoutRoot.UpdateLayout();
                        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                        if (populated && workspace.SelectedIndex == 3) model.SelectedCandidate = candidate;
                        if (populated && workspace.SelectedIndex == 8) model.SelectedApiRequest = model.Project.ApiRequests.First();
                        Assert.True(workspace.ActualWidth >= 680, $"Workspace width: {workspace.ActualWidth}");
                        Assert.True(workspace.ActualHeight >= 400);
                        foreach (var grid in Descendants<DataGrid>(workspace))
                            Assert.True(grid.ActualHeight >= 90, $"Page {item.Tag}: table lost its usable height.");
                        if (!string.IsNullOrWhiteSpace(snapshotDirectory))
                        {
                            Directory.CreateDirectory(snapshotDirectory);
                            var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
                            bitmap.Render(layoutRoot);
                            var encoder = new PngBitmapEncoder();
                            encoder.Frames.Add(BitmapFrame.Create(bitmap));
                            using var stream = File.Create(Path.Combine(snapshotDirectory, $"{(apiMode ? "api" : "web")}-{(populated ? "filled" : "empty")}-{(dark ? "dark" : "light")}-{size.Width}-page{item.Tag}.png"));
                            encoder.Save(stream);
                        }
                    }
                }
                }
                }
                Assert.Empty(bindingErrors.Errors);
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                window?.Close();
                PresentationTraceSources.DataBindingSource.Listeners.Remove(bindingErrors);
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "UI layout check timed out.");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private sealed class BindingErrors : TraceListener
    {
        public List<string> Errors { get; } = [];
        public override void Write(string? message) { if (message?.Contains("Error:") == true) Errors.Add(message); }
        public override void WriteLine(string? message) => Write(message);
    }
}
