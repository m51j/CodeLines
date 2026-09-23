using System.Collections.ObjectModel;
using CodeLines.Core.Abstractions;
using CodeLines.Core.AiUsage.Agents;
using CodeLines.Core.Models;
using CodeLines.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CodeLines.App.ViewModels;

/// <summary>A choice in the daily log's project filter; a null id means every enabled project.</summary>
public sealed record DailyLogProjectOption(Guid? Id, string Label)
{
    public override string ToString() => Label;
}

public sealed partial class MainViewModel
{
    public const string DailyLogPageName = "Daily log";
    private readonly IDailyLogRepository _dailyLogRepository;
    private readonly SemaphoreSlim _dailyLogSaveGate = new(1, 1);
    private DailyLogFile _dailyLog = new();
    private IReadOnlyList<DailyLogTimelineRow> _dailyLogTimeline = [];
    private bool _refreshingDailyLog;

    public ObservableCollection<DailyLogTimelineRow> DailyLogRows { get; } = [];
    public ObservableCollection<DailyLogProjectOption> DailyLogProjectOptions { get; } = [];
    public ObservableCollection<string> DailyLogDates { get; } = [];
    public ObservableCollection<DailyCompareRow> DailyCompareRows { get; } = [];
    public ObservableCollection<DailyCompareLanguageRow> DailyCompareLanguageRows { get; } = [];
    [ObservableProperty] private DailyLogProjectOption? selectedDailyLogProject;
    [ObservableProperty] private string? compareDateA;
    [ObservableProperty] private string? compareDateB;
    [ObservableProperty] private string dailyCompareWarning = "";
    [ObservableProperty] private string dailyLogSummary = "";
    [ObservableProperty] private string dailyLogError = "";
    [ObservableProperty] private bool refreshAiAfterScan = true;

    public string DailyLogPath => _dailyLogRepository.LogPath;
    public bool CanExportDailyLog => _dailyLogTimeline.Count > 0;

    partial void OnSelectedDailyLogProjectChanged(DailyLogProjectOption? value) { if (!_refreshingDailyLog) RefreshDailyLogView(); }
    partial void OnCompareDateAChanged(string? value) { if (!_refreshingDailyLog) RefreshDailyCompare(); }
    partial void OnCompareDateBChanged(string? value) { if (!_refreshingDailyLog) RefreshDailyCompare(); }

    public Task ExportDailyLogAsync(string path) => DailyLogComparer.ExportCsvAsync(_dailyLogTimeline, path);

    private async Task LoadDailyLogAsync()
    {
        RefreshAiAfterScan = _settings.DailyLog.RefreshAiAfterScan;
        try { _dailyLog = await _dailyLogRepository.LoadAsync(); }
        catch (Exception ex) { DailyLogError = $"The daily log could not be read: {ex.Message}"; }
        Projects.CollectionChanged += (_, _) => RefreshDailyLogProjects();
        RefreshDailyLogProjects();
    }

    private void SaveDailyLogSettings() => _settings.DailyLog.RefreshAiAfterScan = RefreshAiAfterScan;

    /// <summary>Records today's line counts of the scanned projects. Returns false when the log could not be saved.</summary>
    private async Task<bool> RecordDailyLinesAsync(IEnumerable<ProjectMetrics> scanned)
    {
        DailyLogRecorder.RecordLines(_dailyLog, scanned, SelectedScope, SelectedTokenizer, DateTimeOffset.Now);
        var saved = await SaveDailyLogAsync();
        RefreshDailyLogView();
        return saved;
    }

    private async Task RecordDailyAiAsync(AgentUsagePayload combined)
    {
        DailyLogRecorder.RecordAi(_dailyLog, combined, Projects.ToDictionary(p => p.Model.Id, p => p.RootPath), DateTimeOffset.Now);
        await SaveDailyLogAsync();
        RefreshDailyLogView();
    }

    /// <summary>After a scan, refresh the AI usage too, so the day's record holds both.</summary>
    private void RefreshAiUsageAfterScan()
    {
        if (AiUsageEnabled && RefreshAiAfterScan && !IsAiUsageRunning) _ = RunAiUsageAsync(rescan: false, announce: false);
    }

    private async Task<bool> SaveDailyLogAsync()
    {
        await _dailyLogSaveGate.WaitAsync();
        try
        {
            await _dailyLogRepository.SaveAsync(_dailyLog);
            DailyLogError = "";
            return true;
        }
        catch (Exception ex)
        {
            DailyLogError = $"The daily log could not be saved: {ex.Message}";
            return false;
        }
        finally { _dailyLogSaveGate.Release(); }
    }

    private void RefreshDailyLogProjects()
    {
        var selected = SelectedDailyLogProject?.Id;
        _refreshingDailyLog = true;
        try
        {
            DailyLogProjectOptions.Clear();
            DailyLogProjectOptions.Add(new(null, "All enabled projects"));
            foreach (var project in Projects) DailyLogProjectOptions.Add(new(project.Model.Id, project.Name));
            SelectedDailyLogProject = DailyLogProjectOptions.FirstOrDefault(o => o.Id == selected) ?? DailyLogProjectOptions[0];
        }
        finally { _refreshingDailyLog = false; }
        RefreshDailyLogView();
    }

    private (IReadOnlyCollection<Guid> Projects, bool AllAgents) DailyLogSelection() => SelectedDailyLogProject?.Id is { } id
        ? ([id], false)
        : (Projects.Where(p => p.IsEnabled).Select(p => p.Model.Id).ToHashSet(), true);

    private void RefreshDailyLogView()
    {
        var (projects, allAgents) = DailyLogSelection();
        _dailyLogTimeline = DailyLogComparer.Timeline(_dailyLog, projects, allAgents);
        var (a, b) = (CompareDateA, CompareDateB);
        _refreshingDailyLog = true;
        try
        {
            DailyLogRows.Clear();
            DailyLogDates.Clear();
            foreach (var row in _dailyLogTimeline.Reverse())
            {
                DailyLogRows.Add(row);
                DailyLogDates.Add(row.Date);
            }
            // Default comparison: the two most recent days.
            CompareDateB = b is not null && DailyLogDates.Contains(b) ? b : DailyLogDates.FirstOrDefault();
            CompareDateA = a is not null && DailyLogDates.Contains(a) ? a : DailyLogDates.Skip(1).FirstOrDefault() ?? CompareDateB;
        }
        finally { _refreshingDailyLog = false; }
        var scanned = _dailyLogTimeline.Count(r => r.Lines is not null && r.CarriedProjects < r.Projects);
        DailyLogSummary = _dailyLogTimeline.Count == 0
            ? "No days recorded yet. Every scan and AI usage refresh adds today's numbers; a later scan on the same day replaces them."
            : $"{_dailyLogTimeline.Count:N0} days • {scanned:N0} with a scan • {_dailyLogTimeline[0].Date} to {_dailyLogTimeline[^1].Date}. The last scan of each day is kept.";
        OnPropertyChanged(nameof(CanExportDailyLog));
        RefreshDailyCompare();
    }

    private void RefreshDailyCompare()
    {
        DailyCompareRows.Clear();
        DailyCompareLanguageRows.Clear();
        DailyCompareWarning = "";
        if (CompareDateA is not { } a || CompareDateB is not { } b) return;
        var (projects, allAgents) = DailyLogSelection();
        var comparison = DailyLogComparer.Compare(_dailyLog, a, b, projects, allAgents);
        foreach (var row in comparison.Metrics) DailyCompareRows.Add(row);
        foreach (var row in comparison.Languages) DailyCompareLanguageRows.Add(row);
        DailyCompareWarning = comparison.Warning;
    }
}
