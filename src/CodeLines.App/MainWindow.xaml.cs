using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CodeLines.App.Services;
using CodeLines.App.ViewModels;
using CodeLines.Core.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace CodeLines.App;

public partial class MainWindow : Window
{
    private const string AiUsageHost = "ai-usage.codelines";
    private readonly MainViewModel _viewModel;
    private Task<bool>? _aiUsageBrowserReady;
    private int _shownAiUsageVersion;

    public MainWindow()
    {
        var repository = new JsonProjectRepository();
        // Theme the window before its first frame; the full settings load only finishes after it is shown.
        ApplyTheme(repository.ReadTheme() ?? "System");
        InitializeComponent();
        var registry = new LanguageRegistry();
        var classifier = new FileClassifier(registry);
        var scanner = new SourceScanner(classifier, new GitIgnoreRuleProvider(), new LineMetricsAnalyzer(), new TokenCounter());
        _viewModel = new MainViewModel(repository, scanner, new ExportService(), ApplyTheme);
        DataContext = _viewModel;
        Loaded += async (_, _) => await _viewModel.InitializeAsync();
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
    }

    private async void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.CurrentPage) or nameof(MainViewModel.AiUsageReportVersion))
            await ShowAiUsageReportAsync();
    }

    /// <summary>Loads the report the first time the tab is visible, and again after every refresh.</summary>
    private async Task ShowAiUsageReportAsync()
    {
        if (_viewModel.CurrentPage != MainViewModel.AiUsagePageName || !_viewModel.HasAiUsageReport) return;
        var version = _viewModel.AiUsageReportVersion;
        if (version == _shownAiUsageVersion || !await EnsureAiUsageBrowserAsync()) return;
        _shownAiUsageVersion = version;
        var core = AiUsageBrowser.CoreWebView2;
        // The pages are rewritten in place, so drop cached copies before reloading them.
        await core.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.DiskCache | CoreWebView2BrowsingDataKinds.CacheStorage);
        var current = Uri.TryCreate(core.Source, UriKind.Absolute, out var uri) && uri.Host == AiUsageHost ? uri.Fragment : "";
        core.Navigate($"https://{AiUsageHost}/dashboard.html{current}");
    }

    private Task<bool> EnsureAiUsageBrowserAsync() => _aiUsageBrowserReady ??= InitializeAiUsageBrowserAsync();

    private async Task<bool> InitializeAiUsageBrowserAsync()
    {
        try
        {
            CoreWebView2Environment.GetAvailableBrowserVersionString();
            var userData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodeLines", "WebView2");
            var environment = await CoreWebView2Environment.CreateAsync(null, userData);
            await AiUsageBrowser.EnsureCoreWebView2Async(environment);
            var core = AiUsageBrowser.CoreWebView2;
            Directory.CreateDirectory(_viewModel.AiUsageOutputDirectory);
            core.SetVirtualHostNameToFolderMapping(AiUsageHost, _viewModel.AiUsageOutputDirectory, CoreWebView2HostResourceAccessKind.Allow);
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            // The report is local; anything else opens in the default browser.
            core.NewWindowRequested += (_, e) => { e.Handled = true; OpenExternal(e.Uri); };
            core.NavigationStarting += (_, e) =>
            {
                if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var target) && target.Host != AiUsageHost && target.Scheme != "about")
                {
                    e.Cancel = true;
                    OpenExternal(e.Uri);
                }
            };
            return true;
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            _viewModel.AiUsageBrowserUnavailable = true;
            return false;
        }
    }

    private static void OpenExternal(string uri)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out var target) && target.Scheme is "http" or "https")
            Process.Start(new ProcessStartInfo(target.AbsoluteUri) { UseShellExecute = true });
    }

    private async void ExportAiUsage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export AI usage",
            Filter = "Single HTML file (*.html)|*.html|Combined data, JSON (*.json)|*.json",
            DefaultExt = ".html",
            FileName = $"ai-usage-{DateTime.Now:yyyyMMdd-HHmm}"
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            await _viewModel.ExportAiUsageAsync(dialog.FileName);
            MessageBox.Show(this, "The AI usage report was exported successfully.", "CodeLines", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "AI usage", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private async void AddProject_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select a source project folder", Multiselect = false };
        if (dialog.ShowDialog(this) == true) await _viewModel.AddProjectAsync(dialog.FolderName);
    }

    private async void ExportProjects_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Title = "Export project list", Filter = "CodeLines project list (*.json)|*.json", DefaultExt = ".json", FileName = $"codelines-projects-{DateTime.Now:yyyyMMdd}" };
        if (dialog.ShowDialog(this) != true) return;
        try { await _viewModel.ExportProjectsAsync(dialog.FileName); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Export projects", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private async void ImportProjects_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Import project list", Filter = "CodeLines project list or settings (*.json)|*.json", Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        try { await _viewModel.ImportProjectsAsync(dialog.FileName); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Import projects", MessageBoxButton.OK, MessageBoxImage.Warning); }
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

    private async void ExportDailyLog_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Title = "Export daily log", Filter = "CSV file (*.csv)|*.csv", DefaultExt = ".csv", FileName = $"codelines-daily-{DateTime.Now:yyyyMMdd}" };
        if (dialog.ShowDialog(this) != true) return;
        try { await _viewModel.ExportDailyLogAsync(dialog.FileName); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Daily log", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private static void ApplyTheme(string theme) => ThemeManager.Apply(theme);
}
