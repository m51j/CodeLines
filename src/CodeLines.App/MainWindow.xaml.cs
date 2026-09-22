using System.Windows;
using CodeLines.App.Services;
using CodeLines.App.ViewModels;
using CodeLines.Core.Services;
using Microsoft.Win32;

namespace CodeLines.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        var registry = new LanguageRegistry();
        var classifier = new FileClassifier(registry);
        var scanner = new SourceScanner(classifier, new GitIgnoreRuleProvider(), new LineMetricsAnalyzer(), new TokenCounter());
        _viewModel = new MainViewModel(new JsonProjectRepository(), scanner, new ExportService(), ApplyTheme);
        DataContext = _viewModel;
        Loaded += async (_, _) => await _viewModel.InitializeAsync();
    }

    private async void AddProject_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select a source project folder", Multiselect = false };
        if (dialog.ShowDialog(this) == true) await _viewModel.AddProjectAsync(dialog.FolderName);
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export CodeLines analysis",
            Filter = "CSV file (*.csv)|*.csv|JSON file (*.json)|*.json",
            DefaultExt = ".csv",
            FileName = $"codelines-{DateTime.Now:yyyyMMdd-HHmm}"
        };
        if (dialog.ShowDialog(this) != true) return;
        await _viewModel.ExportAsync(dialog.FileName);
        MessageBox.Show(this, "The analysis was exported successfully.", "CodeLines", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void ExportHistory_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Title = "Export Git history", Filter = "CSV file (*.csv)|*.csv|JSON file (*.json)|*.json", DefaultExt = ".csv", FileName = $"codelines-history-{DateTime.Now:yyyyMMdd-HHmm}" };
        if (dialog.ShowDialog(this) != true) return;
        try { await _viewModel.ExportHistoryAsync(dialog.FileName); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Git history", MessageBoxButton.OK, MessageBoxImage.Information); }
    }

    private static void ApplyTheme(string theme) => ThemeManager.Apply(theme);
}
