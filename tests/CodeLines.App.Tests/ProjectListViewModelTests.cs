using System.IO;
using CodeLines.App.ViewModels;
using CodeLines.Core.Abstractions;
using CodeLines.Core.Models;
using CodeLines.Core.Services;

namespace CodeLines.App.Tests;

public sealed class ProjectListViewModelTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "codelines-projects-vm-" + Guid.NewGuid().ToString("N"))).FullName;

    [Fact]
    public async Task Exported_projects_import_into_another_machine_without_duplicates()
    {
        var alpha = Directory.CreateDirectory(Path.Combine(_root, "alpha")).FullName;
        var source = new MemoryRepository(new ProjectDefinition { Name = "Alpha", RootPath = alpha },
            new ProjectDefinition { Name = "Elsewhere", RootPath = Path.Combine(_root, "missing") });
        var from = Create(source);
        await from.InitializeAsync();
        var file = Path.Combine(_root, "projects.json");
        await from.ExportProjectsAsync(file);

        var target = new MemoryRepository(new ProjectDefinition { Name = "Mine", RootPath = alpha + Path.DirectorySeparatorChar });
        var to = Create(target);
        await to.InitializeAsync();
        await to.ImportProjectsAsync(file);

        Assert.Equal(["Mine", "Elsewhere"], to.Projects.Select(p => p.Name));
        Assert.Equal(["Mine", "Elsewhere"], target.Settings.Projects.Select(p => p.Name));
        Assert.Equal("Imported 1 projects • 1 already in the list • 1 folders not found on this machine", to.StatusText);

        await to.ImportProjectsAsync(file);
        Assert.Equal(2, to.Projects.Count);
    }

    private static MainViewModel Create(MemoryRepository repository) => new(repository,
        new SourceScanner(new FileClassifier(new LanguageRegistry()), new GitIgnoreRuleProvider(), new LineMetricsAnalyzer(), new TokenCounter()),
        new ExportService(), _ => { }, dailyLog: new MemoryDailyLog());

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private sealed class MemoryRepository(params ProjectDefinition[] projects) : IProjectRepository
    {
        public string SettingsPath => "memory";
        public AppSettings Settings { get; set; } = new() { Projects = [.. projects], AiUsage = { AutoRefreshOnOpen = false } };
        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Settings);
        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) { Settings = settings; return Task.CompletedTask; }
    }
}
