using CodeLines.Core.Models;
using CodeLines.Core.Services;

namespace CodeLines.Tests;

public sealed class PersistenceAndExportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "CodeLinesPersistenceTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Settings_and_last_snapshot_round_trip()
    {
        Directory.CreateDirectory(_root);
        var repository = new JsonProjectRepository(Path.Combine(_root, "settings.json"));
        var settings = CreateSettings();

        await repository.SaveAsync(settings);
        var loaded = await repository.LoadAsync();

        Assert.Equal(CountingScope.AllText, loaded.ScanOptions.CountingScope);
        Assert.Equal("cl100k_base", loaded.ScanOptions.TokenizerEncoding);
        Assert.Single(loaded.Projects);
        Assert.NotNull(loaded.LastSnapshot);
        Assert.Equal(7, loaded.LastSnapshot.TotalTokens);
        Assert.Equal(2, loaded.LastSnapshot.PhysicalLines);
    }

    [Fact]
    public async Task Exports_detailed_csv_and_json()
    {
        Directory.CreateDirectory(_root);
        var snapshot = CreateSettings().LastSnapshot!;
        var exporter = new ExportService();
        var csv = Path.Combine(_root, "report.csv");
        var json = Path.Combine(_root, "report.json");

        await exporter.ExportCsvAsync(snapshot, csv);
        await exporter.ExportJsonAsync(snapshot, json);

        Assert.Contains("Project,Path,Extension,Language", await File.ReadAllTextAsync(csv));
        Assert.Contains("Program.cs", await File.ReadAllTextAsync(csv));
        Assert.Contains("\"tokenizerEncoding\": \"cl100k_base\"", await File.ReadAllTextAsync(json));
    }

    [Fact]
    public void New_settings_start_with_no_projects()
    {
        Assert.Empty(new AppSettings().Projects);
    }

    [Fact]
    public async Task Project_list_round_trips_and_imports_from_a_settings_file()
    {
        Directory.CreateDirectory(_root);
        var project = new ProjectDefinition { Name = "Example", RootPath = @"C:\Example", IsEnabled = false, ExcludePatterns = ["*.g.cs"] };
        var exported = Path.Combine(_root, "projects.json");

        await ProjectListFile.ExportAsync([project], exported);
        var loaded = Assert.Single(await ProjectListFile.ImportAsync(exported));
        Assert.Contains("\"format\": \"codelines-projects\"", await File.ReadAllTextAsync(exported));
        Assert.Equal((project.Id, "Example", @"C:\Example", false), (loaded.Id, loaded.Name, loaded.RootPath, loaded.IsEnabled));
        Assert.Equal(["*.g.cs"], loaded.ExcludePatterns);

        var repository = new JsonProjectRepository(Path.Combine(_root, "settings.json"));
        await repository.SaveAsync(CreateSettings());
        Assert.Equal("Example", Assert.Single(await ProjectListFile.ImportAsync(repository.SettingsPath)).Name);
    }

    [Fact]
    public async Task Import_rejects_files_without_a_project_list_and_names_unnamed_projects()
    {
        Directory.CreateDirectory(_root);
        var bad = Path.Combine(_root, "bad.json");
        await File.WriteAllTextAsync(bad, "{ \"theme\": \"Dark\" }");
        await Assert.ThrowsAsync<InvalidDataException>(() => ProjectListFile.ImportAsync(bad));
        await File.WriteAllTextAsync(bad, "not json");
        await Assert.ThrowsAsync<InvalidDataException>(() => ProjectListFile.ImportAsync(bad));

        var bare = Path.Combine(_root, "bare.json");
        await File.WriteAllTextAsync(bare, """[{ "rootPath": "D:\\Code\\alpha\\" }, { "name": "no path" }]""");
        Assert.Equal("alpha", Assert.Single(await ProjectListFile.ImportAsync(bare)).Name);
    }

    private static AppSettings CreateSettings()
    {
        var project = new ProjectDefinition { Name = "Example", RootPath = @"C:\Example" };
        var file = new FileMetrics("Example", "Program.cs", ".cs", "C#", FileCategory.SourceCode,
            24, 7, new LineMetrics(2, 0, 1, 1, 0, 0));
        var metrics = ProjectMetrics.Create(project, [file], [], TimeSpan.FromMilliseconds(5));
        return new AppSettings
        {
            Projects = [project],
            ScanOptions = new ScanOptions { CountingScope = CountingScope.AllText, TokenizerEncoding = "cl100k_base" },
            LastSnapshot = new ScanSnapshot
            {
                CountingScope = CountingScope.AllText,
                TokenizerEncoding = "cl100k_base",
                Projects = [metrics]
            }
        };
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
