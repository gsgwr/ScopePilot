using Microsoft.Win32;
using ScopePilot.Services;
using ScopePilot.ViewModels;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ScopePilot;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();
    public MainWindow() : this(true) { }

    // UI regression checks can instantiate the window without loading or changing user data.
    internal MainWindow(bool initializeProject)
    {
        InitializeComponent();
        Title = $"ScopePilot {typeof(MainWindow).Assembly.GetName().Version?.ToString(3)}";
        DataContext = _viewModel;
        SourceInitialized += (_, _) => ThemeService.Apply(this);
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        Closed += (_, _) => SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        if (initializeProject) Loaded += async (_, _) => await _viewModel.InitializeAsync();
        Navigation.SelectedIndex = 1;
    }

    private void Navigation_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Workspace is not null && Navigation.SelectedItem is ListBoxItem { Tag: string tag } && int.TryParse(tag, out var index))
            Workspace.SelectedIndex = index;
    }

    private void Navigate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag }) NavigateTo(tag);
    }

    private void NavigateTo(string tag)
    {
        Navigation.SelectedItem = Navigation.Items.OfType<ListBoxItem>().FirstOrDefault(item => item.Tag?.ToString() == tag);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        if (e.Key == Key.S && _viewModel.SaveCommand.CanExecute(null))
        {
            _viewModel.SaveCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.F)
        {
            NavigateTo("3");
            Dispatcher.BeginInvoke(() => { CandidateSearchBox.Focus(); CandidateSearchBox.SelectAll(); });
            e.Handled = true;
        }
    }

    private async void CandidateSelection_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.IsRunning && sender is CheckBox { DataContext: ScopePilot.Domain.DiagnosticCandidate candidate })
            await _viewModel.SaveCandidateDecisionAsync(candidate);
    }

    private void ResetCandidateDecisions_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.ResetCandidateDecisionsCommand.CanExecute(null)) return;
        if (MessageBox.Show(this, "この案件の採用・除外の記録を解除し、観測済みの通信から再判定します。続行しますか？",
            "候補の判断をリセット", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
            _viewModel.ResetCandidateDecisionsCommand.Execute(null);
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e) =>
        Dispatcher.BeginInvoke(() => ThemeService.Apply(this));

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.IsRunning) return;
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

    private void RolesTextBox_TextChanged(object sender, TextChangedEventArgs e) =>
        Dispatcher.BeginInvoke(new Action(_viewModel.UpdateRoleOptions));

    private async void DeleteSelectedRun_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.IsRunning) return;
        if (_viewModel.SelectedRun is null)
        {
            MessageBox.Show(this, "実行履歴の一覧で削除する探索実行を選択してください。", "ScopePilot", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var answer = MessageBox.Show(this,
            $"{_viewModel.SelectedRun.StartedAtDisplay} の探索実行フォルダーを削除します。\n案件へ取り込み済みの通信は残ります。続行しますか？",
            "探索実行データの削除", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer == MessageBoxResult.Yes) await _viewModel.DeleteSelectedRunAsync();
    }

    private async void DeleteGeneratedArtifacts_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.IsRunning) return;
        var answer = MessageBox.Show(this,
            "現在の案件で生成したBurp出力とレポートを削除します。案件、観測通信、探索実行履歴は残ります。続行しますか？",
            "生成物の削除", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer == MessageBoxResult.Yes) await _viewModel.DeleteGeneratedArtifactsAsync();
    }
}
