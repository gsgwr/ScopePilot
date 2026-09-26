using Microsoft.Win32;
using ScopePilot.Services;
using ScopePilot.ViewModels;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace ScopePilot;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();
    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        SourceInitialized += (_, _) => ThemeService.Apply(this);
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        Closed += (_, _) => SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        Loaded += async (_, _) => await _viewModel.InitializeAsync();
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e) =>
        Dispatcher.BeginInvoke(() => ThemeService.Apply(this));

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "クローリング結果を取り込む",
            Filter = "対応ファイル (*.har;*.jsonl;*.txt)|*.har;*.jsonl;*.txt|HAR (*.har)|*.har|JSON Lines (*.jsonl)|*.jsonl|URL一覧 (*.txt)|*.txt"
        };
        if (dialog.ShowDialog(this) == true) await _viewModel.ImportAsync(dialog.FileName);
    }

    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_viewModel.ConsoleText))
            Clipboard.SetText(_viewModel.ConsoleText);
    }

    private void CandidateUrl_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string value } ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https")) return;

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"URLをブラウザで開けませんでした。\n{ex.Message}", "ScopePilot",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CandidateGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction == DataGridEditAction.Commit && e.Column.Header?.ToString() == "採用" &&
            e.Row.Item is ScopePilot.Domain.DiagnosticCandidate candidate)
            Dispatcher.BeginInvoke(new Action(() => _ = _viewModel.SaveCandidateDecisionAsync(candidate)));
    }

    private void GuidelineCheckBox_Click(object sender, RoutedEventArgs e) =>
        Dispatcher.BeginInvoke(new Action(() => _ = _viewModel.ApplyGuidelineSelectionAsync()));

    private void RunArtifact_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string fileName }) _viewModel.OpenSelectedRunArtifact(fileName);
    }
}
