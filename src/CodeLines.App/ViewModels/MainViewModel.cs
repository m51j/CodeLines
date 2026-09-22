using System.Collections.ObjectModel;
using System.IO;
using CodeLines.Core.Abstractions;
using CodeLines.Core.AiUsage;
using CodeLines.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CodeLines.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly IProjectRepository _repository;
    private readonly ISourceScanner _scanner;
    private readonly IExportService _exportService;
    private readonly Action<string> _applyTheme;
    private AppSettings _settings = new();

    public MainViewModel(IProjectRepository repository, ISourceScanner scanner, IExportService exportService,
        Action<string> applyTheme, IGitHistoryAnalyzer? gitAnalyzer = null, IAiUsageReportBuilder? aiUsage = null)
    {
        _repository = repository;
        _scanner = scanner;
        _exportService = exportService;
        _applyTheme = applyTheme;
        HistoryResults.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HistoryTotals));
        _gitAnalyzer = gitAnalyzer ?? new CodeLines.Core.Services.GitHistoryAnalyzer(
            new CodeLines.Core.Services.FileClassifier(new CodeLines.Core.Services.LanguageRegistry()));
        _aiUsage = aiUsage ?? new AiUsageReportBuilder();
    }

    public ObservableCollection<ProjectItemViewModel> Projects { get; } = [];
    public ObservableCollection<ProjectMetrics> Results { get; } = [];
    public ObservableCollection<LanguageMetrics> LanguageRows { get; } = [];
    public ObservableCollection<FileMetrics> FileRows { get; } = [];
    public IReadOnlyList<CountingScope> ScopeOptions { get; } = Enum.GetValues<CountingScope>();
    public IReadOnlyList<string> TokenizerOptions { get; } = ["o200k_base", "cl100k_base"];
    public IReadOnlyList<string> ThemeOptions { get; } = ["System", "Light", "Dark"];

    [ObservableProperty] private string currentPage = "Dashboard";
    [ObservableProperty] private CountingScope selectedScope = CountingScope.SourceOnly;
    [ObservableProperty] private string selectedTokenizer = "o200k_base";
    [ObservableProperty] private string selectedTheme = "System";
    [ObservableProperty] private long maxFileSizeMb = 5;
    [ObservableProperty] private string globalExclusions = string.Empty;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string statusText = "Ready to scan";
    [ObservableProperty] private double progressValue;
    [ObservableProperty] private ProjectItemViewModel? selectedProject;
    [ObservableProperty] private ProjectMetrics? selectedResult;

    public long TotalProjects => Results.Count;
    public long TotalFiles => Results.Sum(x => x.FileCount);
    public long TotalLines => Results.Sum(x => x.PhysicalLines);
    public long TotalCodeLines => Results.Sum(x => x.CodeLines);
    public long TotalTokens => Results.Sum(x => x.TotalTokens);
    public string LastScanText => _settings.LastSnapshot is null ? "Not scanned yet" : $"Last scan {_settings.LastSnapshot.ScannedAt.LocalDateTime:g}";
    public string SettingsPath => _repository.SettingsPath;

    public async Task InitializeAsync()
    {
        _settings = await _repository.LoadAsync();
        Projects.Clear();
        foreach (var project in _settings.Projects) Projects.Add(new(project));
        SelectedProject = Projects.FirstOrDefault();
        SelectedScope = _settings.ScanOptions.CountingScope;
        SelectedTokenizer = _settings.ScanOptions.TokenizerEncoding;
        MaxFileSizeMb = Math.Max(1, _settings.ScanOptions.MaxFileSizeBytes / 1024 / 1024);
        GlobalExclusions = string.Join(Environment.NewLine, _settings.ScanOptions.GlobalExcludePatterns);
        SelectedTheme = _settings.Theme;
        _applyTheme(SelectedTheme);
        if (_settings.LastSnapshot is not null) ApplySnapshot(_settings.LastSnapshot);
        LoadHistorySettings();
        LoadAiUsageSettings();
        OnPropertyChanged(nameof(SettingsPath));
        OnPropertyChanged(nameof(LastScanText));
    }

    [RelayCommand]
    private void Navigate(string? page)
    {
        if (!string.IsNullOrWhiteSpace(page)) CurrentPage = page;
    }

    [RelayCommand(IncludeCancelCommand = true, CanExecute = nameof(CanStartAnalysis))]
    private async Task ScanAllAsync(CancellationToken cancellationToken)
    {
        if (IsBusy) return;
        IsBusy = true;
        ProgressValue = 0;
        StatusText = "Discovering project files…";
        try
        {
            ApplyEditorValues();
            var enabled = Projects.Where(x => x.IsEnabled).ToList();
            var scanResults = new List<ProjectMetrics>();
            for (var index = 0; index < enabled.Count; index++)
            {
                var projectIndex = index;
                var row = enabled[index];
                row.Apply();
                var reporter = new Progress<ScanProgress>(p =>
                {
                    var local = p.DiscoveredFiles == 0 ? 0 : (double)p.ProcessedFiles / p.DiscoveredFiles;
                    ProgressValue = ((projectIndex + local) / Math.Max(1, enabled.Count)) * 100;
                    StatusText = $"{p.ProjectName}  •  {p.ProcessedFiles:N0}/{p.DiscoveredFiles:N0} files";
                });
                scanResults.Add(await _scanner.ScanProjectAsync(row.Model, _settings.ScanOptions, reporter, cancellationToken));
            }

            var snapshot = new ScanSnapshot
            {
                ScannedAt = DateTimeOffset.Now,
                CountingScope = SelectedScope,
                TokenizerEncoding = SelectedTokenizer,
                Projects = scanResults
            };
            _settings.LastSnapshot = snapshot;
            ApplySnapshot(snapshot);
            await _repository.SaveAsync(_settings, cancellationToken);
            ProgressValue = 100;
            StatusText = $"Completed • {snapshot.FileCount:N0} files analyzed";
        }
        catch (OperationCanceledException) { StatusText = "Scan cancelled"; }
        catch (Exception ex) { StatusText = $"Scan failed: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        ApplyEditorValues();
        await _repository.SaveAsync(_settings);
        _applyTheme(SelectedTheme);
        StatusText = "Settings saved";
    }

    [RelayCommand(IncludeCancelCommand = true, CanExecute = nameof(CanStartAnalysis))]
    private async Task ScanSelectedProjectAsync(CancellationToken cancellationToken)
    {
        if (IsBusy || SelectedProject is null) return;
        IsBusy = true;
        ProgressValue = 0;
        try
        {
            ApplyEditorValues();
            SelectedProject.Apply();
            var reporter = new Progress<ScanProgress>(p =>
            {
                ProgressValue = p.DiscoveredFiles == 0 ? 0 : (double)p.ProcessedFiles / p.DiscoveredFiles * 100;
                StatusText = $"{p.ProjectName}  •  {p.ProcessedFiles:N0}/{p.DiscoveredFiles:N0} files";
            });
            var result = await _scanner.ScanProjectAsync(SelectedProject.Model, _settings.ScanOptions, reporter, cancellationToken);
            var sameScanContract = _settings.LastSnapshot is not null &&
                                   _settings.LastSnapshot.CountingScope == SelectedScope &&
                                   string.Equals(_settings.LastSnapshot.TokenizerEncoding, SelectedTokenizer, StringComparison.OrdinalIgnoreCase);
            var priorResults = sameScanContract ? Results.Where(x => x.ProjectId != result.ProjectId) : [];
            var merged = priorResults.Append(result)
                .OrderBy(x => _settings.Projects.FindIndex(p => p.Id == x.ProjectId)).ToList();
            var snapshot = new ScanSnapshot
            {
                ScannedAt = DateTimeOffset.Now,
                CountingScope = SelectedScope,
                TokenizerEncoding = SelectedTokenizer,
                Projects = merged
            };
            _settings.LastSnapshot = snapshot;
            ApplySnapshot(snapshot);
            SelectedResult = Results.FirstOrDefault(x => x.ProjectId == result.ProjectId);
            await _repository.SaveAsync(_settings, cancellationToken);
            ProgressValue = 100;
            StatusText = $"Completed {result.ProjectName} • {result.FileCount:N0} files";
        }
        catch (OperationCanceledException) { StatusText = "Scan cancelled"; }
        catch (Exception ex) { StatusText = $"Scan failed: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task RemoveProjectAsync(ProjectItemViewModel? project)
    {
        if (project is null || IsBusy) return;
        Projects.Remove(project);
        _settings.Projects.Remove(project.Model);
        await _repository.SaveAsync(_settings);
    }

    public async Task AddProjectAsync(string path)
    {
        if (Projects.Any(x => string.Equals(Path.GetFullPath(x.RootPath), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)))
        {
            StatusText = "That project is already in the list";
            return;
        }
        var model = new ProjectDefinition { Name = new DirectoryInfo(path).Name, RootPath = path };
        _settings.Projects.Add(model);
        var row = new ProjectItemViewModel(model);
        Projects.Add(row);
        SelectedProject = row;
        await _repository.SaveAsync(_settings);
        StatusText = $"Added {model.Name}";
    }

    public Task ExportAsync(string path) => path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
        ? _exportService.ExportJsonAsync(_settings.LastSnapshot ?? new ScanSnapshot(), path)
        : _exportService.ExportCsvAsync(_settings.LastSnapshot ?? new ScanSnapshot(), path);

    partial void OnSelectedResultChanged(ProjectMetrics? value)
    {
        LanguageRows.Clear();
        FileRows.Clear();
        if (value is null) return;
        foreach (var row in value.Languages) LanguageRows.Add(row);
        foreach (var row in value.Files) FileRows.Add(row);
    }

    private void ApplyEditorValues()
    {
        foreach (var project in Projects) project.Apply();
        _settings.Projects = Projects.Select(x => x.Model).ToList();
        _settings.ScanOptions.CountingScope = SelectedScope;
        _settings.ScanOptions.TokenizerEncoding = SelectedTokenizer;
        _settings.ScanOptions.MaxFileSizeBytes = Math.Max(1, MaxFileSizeMb) * 1024 * 1024;
        _settings.ScanOptions.GlobalExcludePatterns = GlobalExclusions
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        _settings.Theme = SelectedTheme;
        SaveHistorySettings();
        SaveAiUsageSettings();
    }

    private void ApplySnapshot(ScanSnapshot snapshot)
    {
        Results.Clear();
        foreach (var result in snapshot.Projects) Results.Add(result);
        SelectedResult = Results.FirstOrDefault();
        OnPropertyChanged(nameof(TotalProjects));
        OnPropertyChanged(nameof(TotalFiles));
        OnPropertyChanged(nameof(TotalLines));
        OnPropertyChanged(nameof(TotalCodeLines));
        OnPropertyChanged(nameof(TotalTokens));
        OnPropertyChanged(nameof(LastScanText));
    }
}
