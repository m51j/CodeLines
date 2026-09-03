using CodeLines.Core.Models;
using CodeLines.Core.Services;

namespace CodeLines.Tests;

public sealed class SourceScannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "CodeLinesScannerTests", Guid.NewGuid().ToString("N"));
    private readonly SourceScanner _scanner;

    public SourceScannerTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        Directory.CreateDirectory(Path.Combine(_root, "bin"));
        File.WriteAllText(Path.Combine(_root, ".gitignore"), "ignored.cs\n");
        File.WriteAllText(Path.Combine(_root, "Program.cs"), "// comment\nvar answer = 42;\n");
        File.WriteAllText(Path.Combine(_root, "settings.json"), "{\"enabled\":true}");
        File.WriteAllText(Path.Combine(_root, "README.md"), "# Sample\n\nDocumentation");
        File.WriteAllText(Path.Combine(_root, "ignored.cs"), "ignored");
        File.WriteAllText(Path.Combine(_root, "bin", "generated.cs"), "generated");
        File.WriteAllBytes(Path.Combine(_root, "binary.unknown"), [0, 1, 2, 3, 4]);

        var registry = new LanguageRegistry();
        _scanner = new(new FileClassifier(registry), new GitIgnoreRuleProvider(), new LineMetricsAnalyzer(), new TokenCounter());
    }

    [Theory]
    [InlineData(CountingScope.SourceOnly, 1)]
    [InlineData(CountingScope.SourceAndConfiguration, 2)]
    [InlineData(CountingScope.AllText, 4)]
    public async Task Scope_changes_included_file_count(CountingScope scope, int expected)
    {
        var project = new ProjectDefinition { Name = "Fixture", RootPath = _root };
        var options = new ScanOptions { CountingScope = scope, MaxDegreeOfParallelism = 2 };
        var result = await _scanner.ScanProjectAsync(project, options);
        Assert.Equal(expected, result.FileCount);
        Assert.DoesNotContain(result.Files, x => x.RelativePath.Contains("ignored", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Files, x => x.RelativePath.Contains("generated", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Files, x => x.RelativePath.Contains("binary", StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
