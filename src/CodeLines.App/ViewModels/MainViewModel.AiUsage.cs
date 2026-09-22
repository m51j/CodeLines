using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CodeLines.Core.AiUsage;
using CodeLines.Core.AiUsage.Agents;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CodeLines.App.ViewModels;

public sealed partial class MainViewModel
{
    public const string AiUsagePageName = "AI usage";
    private readonly IAiUsageReportBuilder _aiUsage;
    private CancellationTokenSource? _aiUsageCancellation;
    private AiUsageResult? _aiUsageResult;
    private bool _aiUsageRefreshedThisSession;

    public ObservableCollection<AiUsageAgentOption> AiUsageAgents { get; } = [];
    [ObservableProperty] private bool aiUsageEnabled = true;
    [ObservableProperty] private bool aiUsageUseUtc;
    [ObservableProperty] private bool aiUsageDeduplicate = true;
    [ObservableProperty] private bool aiUsageAutoRefresh = true;
    [ObservableProperty] private string aiUsageExtraRoots = "";
    [ObservableProperty] private bool isAiUsageRunning;
    [ObservableProperty] private string aiUsageStatus = "Refresh to read local AI agent usage.";
    [ObservableProperty] private string aiUsageDetails = "";
    [ObservableProperty] private double aiUsageProgress;
    [ObservableProperty] private bool aiUsageProgressIndeterminate;
    [ObservableProperty] private bool hasAiUsageReport;
    /// <summary>Incremented whenever new pages are written, so the view reloads them.</summary>
    [ObservableProperty] private int aiUsageReportVersion;
    [ObservableProperty] private bool aiUsageBrowserUnavailable;
    [ObservableProperty] private string aiTokens30DaysText = "—";

    public string AiUsageOutputDirectory => _aiUsage.OutputDirectory;
    public string AiUsageDashboardFile => Path.Combine(_aiUsage.OutputDirectory, "dashboard.html");
    public bool CanExportAiUsage => _aiUsageResult is { HasData: true } && !IsAiUsageRunning;
    public string AiUsageEmptyText => AiUsageBrowserUnavailable
        ? "The Microsoft Edge WebView2 runtime is not installed, so the report cannot be shown here. Use “Open in browser”."
        : IsAiUsageRunning ? "Reading local AI agent data…" : "No AI agent usage has been found yet. Refresh to look for Claude Code, Codex, Copilot Chat and other agents.";
    private bool CanRefreshAiUsage() => AiUsageEnabled && !IsAiUsageRunning;

    partial void OnIsAiUsageRunningChanged(bool value)
    {
        RefreshAiUsageCommand.NotifyCanExecuteChanged();
        RescanAiUsageCommand.NotifyCanExecuteChanged();
        CancelAiUsageCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanExportAiUsage));
        OnPropertyChanged(nameof(AiUsageEmptyText));
    }
    partial void OnHasAiUsageReportChanged(bool value) => OpenAiUsageInBrowserCommand.NotifyCanExecuteChanged();
    partial void OnAiUsageBrowserUnavailableChanged(bool value) => OnPropertyChanged(nameof(AiUsageEmptyText));
    partial void OnAiUsageEnabledChanged(bool value)
    {
        RefreshAiUsageCommand.NotifyCanExecuteChanged();
        RescanAiUsageCommand.NotifyCanExecuteChanged();
        if (value) return;
        CancelAiUsage();
        if (CurrentPage == AiUsagePageName) CurrentPage = "Settings";
    }

    partial void OnCurrentPageChanged(string value)
    {
        if (value == AiUsagePageName && AiUsageEnabled && AiUsageAutoRefresh && !_aiUsageRefreshedThisSession && !IsAiUsageRunning)
            RefreshAiUsageCommand.Execute(null);
    }

    [RelayCommand(CanExecute = nameof(CanRefreshAiUsage))]
    private Task RefreshAiUsageAsync() => RunAiUsageAsync(rescan: false);

    [RelayCommand(CanExecute = nameof(CanRefreshAiUsage))]
    private Task RescanAiUsageAsync() => RunAiUsageAsync(rescan: true);

    [RelayCommand(CanExecute = nameof(IsAiUsageRunning))]
    private void CancelAiUsage()
    {
        if (_aiUsageCancellation is not { IsCancellationRequested: false } cancellation) return;
        cancellation.Cancel();
        AiUsageStatus = "Cancelling…";
    }

    [RelayCommand(CanExecute = nameof(HasAiUsageReport))]
    private void OpenAiUsageInBrowser()
    {
        if (File.Exists(AiUsageDashboardFile)) Process.Start(new ProcessStartInfo(AiUsageDashboardFile) { UseShellExecute = true });
    }

    public Task ExportAiUsageAsync(string path)
    {
        if (_aiUsageResult is not { Combined: { } combined } result)
            throw new InvalidOperationException("Refresh the AI usage report before exporting it.");
        return path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? File.WriteAllTextAsync(path, AiUsagePayloadWriter.ToJson(combined))
            : AiUsageReportBuilder.ExportSingleFileAsync(result, path);
    }

    private async Task RunAiUsageAsync(bool rescan)
    {
        if (IsAiUsageRunning) return;
        _aiUsageRefreshedThisSession = true;
        using var cancellation = new CancellationTokenSource();
        _aiUsageCancellation = cancellation;
        IsAiUsageRunning = true;
        AiUsageProgressIndeterminate = true;
        AiUsageStatus = rescan ? "Re-reading every AI agent file…" : "Reading local AI agent data…";
        var clock = Stopwatch.StartNew();
        var options = CreateAiUsageOptions(rescan);
        var progress = new Progress<AiUsageProgress>(p =>
        {
            if (cancellation.IsCancellationRequested) return;
            AiUsageProgressIndeterminate = p.Total == 0;
            AiUsageProgress = p.Total == 0 ? 0 : p.Done * 100d / p.Total;
            AiUsageStatus = p.Total == 0 ? p.Message + "…" : $"{p.Message} • {p.Done:N0}/{p.Total:N0} files";
        });
        try
        {
            var result = await Task.Run(() => _aiUsage.BuildAsync(options, progress, cancellation.Token), cancellation.Token);
            ApplyAiUsageResult(result);
            AiUsageStatus = result.HasData
                ? $"Updated {result.GeneratedAt.LocalDateTime:g} • {Duration(clock.Elapsed)}"
                : "No AI agent usage was found on this computer.";
            StatusText = "AI usage report updated";
        }
        catch (OperationCanceledException) { AiUsageStatus = "AI usage refresh cancelled"; }
        catch (Exception ex) { AiUsageStatus = $"AI usage refresh failed: {ex.Message}"; }
        finally
        {
            _aiUsageCancellation = null;
            IsAiUsageRunning = false;
            AiUsageProgressIndeterminate = false;
        }
    }

    private AiUsageOptions CreateAiUsageOptions(bool rescan) => new()
    {
        UseUtc = AiUsageUseUtc,
        Deduplicate = AiUsageDeduplicate,
        Rescan = rescan,
        DisabledAgents = AiUsageAgents.Where(a => !a.IsEnabled).Select(a => a.Id).ToList(),
        ExtraClaudeRoots = SplitLines(AiUsageExtraRoots)
    };

    private void ApplyAiUsageResult(AiUsageResult result)
    {
        _aiUsageResult = result;
        HasAiUsageReport = result.HasData;
        AiUsageReportVersion++;
        foreach (var option in AiUsageAgents)
            option.Status = result.Agents.FirstOrDefault(a => a.Id == option.Id)?.Summary ?? "";
        AiUsageDetails = string.Join("  •  ", result.Tabs.Select(t => $"{t.Label} {AiUsageTemplates.FormatTokens(t.Tokens)}"));
        var failed = result.Agents.Where(a => a.State == AiUsageAgentState.Failed).ToList();
        if (failed.Count > 0) AiUsageDetails += (AiUsageDetails.Length > 0 ? "  •  " : "") + string.Join("  •  ", failed.Select(a => $"{a.Label}: {a.Summary}"));
        UpdateProjectAiUsage();
        OnPropertyChanged(nameof(CanExportAiUsage));
    }

    /// <summary>Attributes agent sessions to the workspace projects by their working folder.</summary>
    private void UpdateProjectAiUsage()
    {
        if (_aiUsageResult?.Combined is not { } combined)
        {
            foreach (var project in Projects) project.SetAiUsage(AiProjectUsage.None, AiProjectUsage.None);
            AiTokens30DaysText = "—";
            return;
        }
        var roots = Projects.ToDictionary(p => p, p => p.RootPath);
        var since = DateTimeOffset.Now.AddDays(-30);
        var allTime = AiProjectUsageMatcher.Match(combined, roots);
        var recent = AiProjectUsageMatcher.Match(combined, roots, since);
        foreach (var project in Projects) project.SetAiUsage(allTime[project], recent[project]);
        var cutoff = since.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        AiTokens30DaysText = AiUsageTemplates.FormatTokens(AgentVector.RealTokens(
            combined.Totals(filterDays: day => string.CompareOrdinal(day, cutoff) > 0)));
    }

    private void LoadAiUsageSettings()
    {
        var saved = _settings.AiUsage;
        AiUsageEnabled = saved.Enabled;
        AiUsageUseUtc = saved.UseUtc;
        AiUsageDeduplicate = saved.Deduplicate;
        AiUsageAutoRefresh = saved.AutoRefreshOnOpen;
        AiUsageExtraRoots = string.Join(Environment.NewLine, saved.ExtraClaudeRoots);
        AiUsageAgents.Clear();
        foreach (var (id, label) in _aiUsage.KnownAgents)
            AiUsageAgents.Add(new(id, label, !saved.DisabledAgents.Contains(id, StringComparer.OrdinalIgnoreCase)));
        // The previous session's pages stay on disk, so they can be shown at once while a refresh runs.
        if (File.Exists(AiUsageDashboardFile))
        {
            HasAiUsageReport = true;
            AiUsageReportVersion++;
            AiUsageStatus = $"Last report {File.GetLastWriteTime(AiUsageDashboardFile):g}. Refresh for the latest numbers.";
        }
        Projects.CollectionChanged += (_, _) => UpdateProjectAiUsage();
    }

    private void SaveAiUsageSettings()
    {
        var saved = _settings.AiUsage;
        saved.Enabled = AiUsageEnabled;
        saved.UseUtc = AiUsageUseUtc;
        saved.Deduplicate = AiUsageDeduplicate;
        saved.AutoRefreshOnOpen = AiUsageAutoRefresh;
        saved.ExtraClaudeRoots = SplitLines(AiUsageExtraRoots);
        saved.DisabledAgents = AiUsageAgents.Where(a => !a.IsEnabled).Select(a => a.Id).ToList();
    }

    private static List<string> SplitLines(string text) =>
        text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}
