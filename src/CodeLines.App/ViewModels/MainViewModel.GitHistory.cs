using System.Collections.ObjectModel;
using System.Diagnostics;
using CodeLines.Core.Abstractions;
using CodeLines.Core.Models;
using CodeLines.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CodeLines.App.ViewModels;

public sealed partial class MainViewModel
{
    private readonly IGitHistoryAnalyzer _gitAnalyzer;
    private GitHistorySnapshot? _displayedHistory;
    public ObservableCollection<GitProjectHistory> HistoryResults { get; } = [];
    public IReadOnlyList<string> HistoryPeriods { get; } = ["7", "30", "365", "Custom"];
    public IReadOnlyList<GitHistoryMode> HistoryModes { get; } = Enum.GetValues<GitHistoryMode>();
    [ObservableProperty] private bool gitHistoryEnabled;
    [ObservableProperty] private string historyPeriod = "7";
    [ObservableProperty] private string customHistoryDays = "90";
    [ObservableProperty] private GitHistoryMode historyMode;
    [ObservableProperty] private GitProjectHistory? selectedHistory;
    [ObservableProperty] private string historySummary = "Refresh to analyze local Git history.";
    [ObservableProperty] private bool isHistoryRunning;
    [ObservableProperty] private bool historyCancellationRequested;
    [ObservableProperty] private bool historyProgressIndeterminate;
    [ObservableProperty] private double historyProjectProgress;
    [ObservableProperty] private string historyProgressText = "Ready to analyze";
    [ObservableProperty] private string historyWorkText = "";
    [ObservableProperty] private string historyCurrentPath = "";
    [ObservableProperty] private string historyTimingText = "";
    [ObservableProperty] private string historyTotalsText = "All projects • no results yet";

    public GitHistoryTotals HistoryTotals => GitHistoryTotals.FromProjects(HistoryResults, _displayedHistory?.Mode ?? HistoryMode);
    public bool CanExportHistory => GitHistoryEnabled && !IsBusy && _displayedHistory is not null;
    public bool IsProgressIndeterminate => IsHistoryRunning && HistoryProgressIndeterminate;
    private bool CanRefreshHistory() => GitHistoryEnabled && !IsBusy;
    private bool CanCancelHistory() => IsHistoryRunning && !HistoryCancellationRequested;
    private bool CanStartAnalysis() => !IsBusy;
    private bool CanCancelAnalysis() => IsBusy;

    partial void OnIsBusyChanged(bool value)
    {
        RefreshHistoryCommand.NotifyCanExecuteChanged();
        ScanAllCommand.NotifyCanExecuteChanged();
        ScanSelectedProjectCommand.NotifyCanExecuteChanged();
        CancelActiveAnalysisCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanExportHistory));
    }
    partial void OnIsHistoryRunningChanged(bool value)
    {
        CancelHistoryCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsProgressIndeterminate));
    }
    partial void OnHistoryCancellationRequestedChanged(bool value) => CancelHistoryCommand.NotifyCanExecuteChanged();
    partial void OnHistoryProgressIndeterminateChanged(bool value) => OnPropertyChanged(nameof(IsProgressIndeterminate));
    partial void OnGitHistoryEnabledChanged(bool value)
    {
        RefreshHistoryCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanExportHistory));
        if (!value)
        {
            if (CurrentPage == "Code changes") CurrentPage = "Settings";
            InvalidateHistory();
        }
    }
    partial void OnHistoryPeriodChanged(string value) => InvalidateHistory();
    partial void OnCustomHistoryDaysChanged(string value) => InvalidateHistory();
    partial void OnHistoryModeChanged(GitHistoryMode value) => InvalidateHistory();
    partial void OnSelectedScopeChanged(CountingScope value) => InvalidateHistory();
    partial void OnGlobalExclusionsChanged(string value) => InvalidateHistory();

    [RelayCommand(CanExecute = nameof(CanCancelHistory))]
    private void CancelHistory()
    {
        if (!IsHistoryRunning) return;
        HistoryCancellationRequested = true;
        RefreshHistoryCommand.Cancel();
        StatusText = HistoryProgressText = "Cancelling Git history…";
    }

    [RelayCommand(CanExecute = nameof(CanCancelAnalysis))]
    private void CancelActiveAnalysis()
    {
        if (IsHistoryRunning) CancelHistory();
        else { ScanAllCommand.Cancel(); ScanSelectedProjectCommand.Cancel(); }
    }

    private void InvalidateHistory()
    {
        CancelHistory();
        HistoryResults.Clear();
        SelectedHistory = null;
        _displayedHistory = null;
        HistorySummary = "Options changed. Refresh to analyze local Git history.";
        HistoryTotalsText = "All projects • no results for these options";
        OnPropertyChanged(nameof(CanExportHistory));
    }

    private void LoadHistorySettings()
    {
        GitHistoryEnabled = _settings.GitHistory.Enabled;
        HistoryPeriod = _settings.GitHistory.Days is 7 or 30 or 365 ? _settings.GitHistory.Days.ToString() : "Custom";
        CustomHistoryDays = _settings.GitHistory.Days.ToString();
        HistoryMode = _settings.GitHistory.Mode;
        if (GitHistoryEnabled && _settings.LastGitHistory is { } saved) ShowHistory(saved);
        foreach (var project in Projects) WatchHistoryProject(project);
        Projects.CollectionChanged += (_, e) =>
        {
            InvalidateHistory();
            if (e.OldItems is not null)
                foreach (ProjectItemViewModel project in e.OldItems) project.PropertyChanged -= HistoryProjectChanged;
            if (e.NewItems is not null)
                foreach (ProjectItemViewModel project in e.NewItems) WatchHistoryProject(project);
        };
    }

    private void WatchHistoryProject(ProjectItemViewModel project) => project.PropertyChanged += HistoryProjectChanged;
    private void HistoryProjectChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => InvalidateHistory();

    private void SaveHistorySettings()
    {
        _settings.GitHistory.Enabled = GitHistoryEnabled;
        _settings.GitHistory.Mode = HistoryMode;
        if (TryHistoryDays(out var days)) _settings.GitHistory.Days = days;
        _settings.LastGitHistory = _displayedHistory;
    }

    private bool TryHistoryDays(out int days) => int.TryParse(HistoryPeriod == "Custom" ? CustomHistoryDays : HistoryPeriod, out days)
        && days is >= 1 and <= 36500;

    [RelayCommand(IncludeCancelCommand = true, CanExecute = nameof(CanRefreshHistory))]
    private async Task RefreshHistoryAsync(CancellationToken cancellationToken)
    {
        if (!CanRefreshHistory()) return;
        if (!TryHistoryDays(out var days)) { StatusText = HistorySummary = "Enter a whole number of days between 1 and 36500."; return; }
        IsBusy = IsHistoryRunning = true;
        HistoryCancellationRequested = false;
        ProgressValue = HistoryProjectProgress = 0;
        HistoryCurrentPath = HistoryWorkText = "";
        var clock = Stopwatch.StartNew();
        var projectCount = 0;
        try
        {
            ApplyEditorValues();
            var end = DateTimeOffset.Now;
            var options = new GitHistoryOptions { Enabled = true, Days = days, Mode = HistoryMode };
            var scanOptions = new ScanOptions { CountingScope = SelectedScope, GlobalExcludePatterns = _settings.ScanOptions.GlobalExcludePatterns.ToList() };
            var projects = Projects.Where(p => p.IsEnabled).Select(p => new ProjectDefinition
            { Id = p.Model.Id, Name = p.Model.Name, RootPath = p.Model.RootPath, ExcludePatterns = p.Model.ExcludePatterns.ToList() }).ToList();
            projectCount = projects.Count;
            _displayedHistory = _settings.LastGitHistory = null;
            HistoryResults.Clear(); SelectedHistory = null;
            HistoryTotalsText = $"Partial totals • 0/{projectCount} projects processed";
            HistorySummary = "Analysis in progress. Totals include processed projects only; export is available when finished.";
            if (projectCount == 0)
            {
                StatusText = HistorySummary = HistoryProgressText = "No enabled projects. Enable or add a project first.";
                HistoryTotalsText = "All projects • no enabled projects";
                return;
            }
            for (var i = 0; i < projectCount; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await AnalyzeHistoryProject(projects[i], i, projectCount, scanOptions, options, end, clock, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                HistoryResults.Add(result);
                SelectedHistory ??= result;
                HistoryTotalsText = $"Partial totals • {HistoryResults.Count}/{projectCount} projects processed • {HistoryTotals.IncompleteProjects} incomplete/unavailable";
                ProgressValue = Math.Min(99, (i + 1) * 100d / projectCount);
            }
            HistoryProgressIndeterminate = true;
            StatusText = HistoryProgressText = "Saving history results…";
            HistoryCurrentPath = "";
            var snapshot = new GitHistorySnapshot { Start = end.AddDays(-days), End = end, Mode = options.Mode, Scope = scanOptions.CountingScope, Projects = HistoryResults.ToList() };
            _settings.LastGitHistory = snapshot;
            await _repository.SaveAsync(_settings, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            ShowHistory(snapshot);
            ProgressValue = HistoryProjectProgress = 100;
            StatusText = HistoryProgressText = $"Git history complete • {projectCount} projects • {snapshot.Totals.IncompleteProjects} incomplete/unavailable";
            HistoryWorkText = "No projects remaining.";
        }
        catch (OperationCanceledException)
        {
            _settings.LastGitHistory = _displayedHistory;
            StatusText = HistoryProgressText = "Git history cancelled";
            HistorySummary = $"Cancelled • {HistoryResults.Count}/{projectCount} projects processed. Visible totals are partial; refresh before exporting.";
            HistoryTotalsText = $"Partial totals • {HistoryResults.Count}/{projectCount} projects processed (cancelled)";
        }
        catch (Exception ex)
        {
            _settings.LastGitHistory = _displayedHistory;
            StatusText = HistoryProgressText = "Git history failed";
            HistorySummary = $"Git history failed: {ex.Message}. Visible totals are partial; refresh before exporting.";
            HistoryTotalsText = $"Partial totals • {HistoryResults.Count}/{projectCount} projects processed (failed)";
        }
        finally
        {
            clock.Stop();
            HistoryTimingText = $"Elapsed {Duration(clock.Elapsed)}";
            HistoryProgressIndeterminate = false;
            IsHistoryRunning = IsBusy = false;
        }
    }

    private async Task<GitProjectHistory> AnalyzeHistoryProject(ProjectDefinition project, int index, int count,
        ScanOptions scanOptions, GitHistoryOptions options, DateTimeOffset end, Stopwatch totalClock, CancellationToken token)
    {
        var reporter = new LatestHistoryProgress();
        var projectClock = Stopwatch.StartNew();
        ApplyHistoryProgress(reporter.Latest, project, index, count, totalClock.Elapsed, projectClock.Elapsed);
        using var stopUpdates = new CancellationTokenSource();
        var updates = UpdateProgressAsync();
        try
        {
            return await Task.Run(() => _gitAnalyzer.AnalyzeAsync(project, scanOptions, options, end, token, reporter), token);
        }
        finally
        {
            await stopUpdates.CancelAsync();
            await updates;
        }

        async Task UpdateProgressAsync()
        {
            // Coalesce file events, and update elapsed time even while Git is still producing output.
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
            try
            {
                while (await timer.WaitForNextTickAsync(stopUpdates.Token))
                    if (!token.IsCancellationRequested)
                        ApplyHistoryProgress(reporter.Latest, project, index, count, totalClock.Elapsed, projectClock.Elapsed);
            }
            catch (OperationCanceledException) when (stopUpdates.IsCancellationRequested) { }
        }
    }

    private void ApplyHistoryProgress(GitHistoryProgress p, ProjectDefinition project, int index, int count, TimeSpan elapsed, TimeSpan projectElapsed)
    {
        HistoryProgressIndeterminate = p.TotalComparisons is null;
        var fileFraction = p.TotalFiles is > 0 ? Math.Min(.99, (double)p.CompletedFiles / p.TotalFiles.Value) : 0;
        var fraction = p.TotalComparisons is > 0 ? Math.Clamp((p.CompletedComparisons + fileFraction) / p.TotalComparisons.Value, 0, 1) : 0;
        HistoryProjectProgress = fraction * 100;
        // This is project-weighted work progress, not an estimate of elapsed/remaining time.
        ProgressValue = Math.Min(99, (index + fraction) * 100 / count);
        HistoryProgressText = $"Project {index + 1}/{count} — {project.Name} • {p.Stage}";
        var unit = HistoryMode == GitHistoryMode.Activity ? "commits" : "comparisons";
        HistoryWorkText = p.TotalComparisons is { } total
            ? $"{p.CompletedComparisons:N0}/{total:N0} {unit} complete • {Math.Max(0, total - p.CompletedComparisons):N0} remaining"
            : "Discovering work; remaining comparisons not known yet";
        if (p.TotalFiles is { } files)
            HistoryWorkText += $" • Files in current comparison: {p.CompletedFiles:N0}/{files:N0} ({Math.Max(0, files - p.CompletedFiles):N0} remaining)";
        HistoryWorkText += $" • {count - index - 1} projects after this one";
        HistoryCurrentPath = p.Commit is null ? "" : $"Commit {p.Commit}" + (p.Path is null ? "" : $" • {p.Path}");
        HistoryTimingText = $"Elapsed {Duration(elapsed)} • Current project {Duration(projectElapsed)}";
        if (p.CompletedComparisons >= 2 && fraction is > 0 and < 1)
            HistoryTimingText += $" • Current project ETA ≈ {Duration(TimeSpan.FromSeconds(projectElapsed.TotalSeconds * (1 - fraction) / fraction))} (varies by commit size)";
        StatusText = $"{HistoryProgressText} • {HistoryWorkText} • {Duration(elapsed)} elapsed";
    }

    private static string Duration(TimeSpan time) => time.TotalHours >= 1
        ? $"{(int)time.TotalHours}h {time.Minutes:00}m {time.Seconds:00}s" : $"{(int)time.TotalMinutes}m {time.Seconds:00}s";

    private sealed class LatestHistoryProgress : IProgress<GitHistoryProgress>
    {
        private GitHistoryProgress _latest = new("Opening repository");
        public GitHistoryProgress Latest => Volatile.Read(ref _latest);
        public void Report(GitHistoryProgress value) => Interlocked.Exchange(ref _latest, value);
    }

    private void ShowHistory(GitHistorySnapshot snapshot)
    {
        _displayedHistory = snapshot;
        HistoryResults.Clear();
        foreach (var result in snapshot.Projects) HistoryResults.Add(result);
        SelectedHistory = HistoryResults.FirstOrDefault();
        HistorySummary = $"Snapshot {snapshot.End.LocalDateTime:g} • {snapshot.Start.LocalDateTime:g} — {snapshot.End.LocalDateTime:g} • {snapshot.Mode} • {snapshot.Scope}. Refresh to check current branch / HEAD.";
        HistoryTotalsText = $"All projects • {snapshot.Totals.Projects} processed • {snapshot.Totals.IncompleteProjects} incomplete/unavailable";
        OnPropertyChanged(nameof(HistoryTotals));
        OnPropertyChanged(nameof(CanExportHistory));
    }

    public Task ExportHistoryAsync(string path) => CanExportHistory && _displayedHistory is { } snapshot
        ? GitHistoryExportService.ExportAsync(snapshot, path)
        : throw new InvalidOperationException("Finish refreshing Git history before exporting.");
}
