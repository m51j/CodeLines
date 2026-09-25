using CodeLines.Core.Models;
using CodeLines.Core.Services;

namespace CodeLines.Tests;

public sealed class LiveProjectsSmokeTests
{
    [Fact]
    [Trait("Category", "Live")]
    public async Task Scans_configured_projects_when_explicitly_enabled()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("CODELINES_RUN_LIVE_TESTS"), "1", StringComparison.Ordinal))
            return;

        var registry = new LanguageRegistry();
        var scanner = new SourceScanner(new FileClassifier(registry), new GitIgnoreRuleProvider(),
            new LineMetricsAnalyzer(), new TokenCounter());
        var options = new ScanOptions
        {
            CountingScope = CountingScope.SourceOnly,
            TokenizerEncoding = "o200k_base",
            MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount)
        };

        // The projects configured in the app on this machine.
        var settings = await new JsonProjectRepository().LoadAsync();
        foreach (var project in settings.Projects)
        {
            Assert.True(Directory.Exists(project.RootPath), $"Missing live project: {project.RootPath}");
            var result = await scanner.ScanProjectAsync(project, options);
            Assert.True(result.FileCount > 0, $"No source files found for {project.Name}");
            Assert.True(result.TotalTokens > 0, $"No tokens counted for {project.Name}");
            Assert.DoesNotContain(result.Files, file => HasGeneratedSegment(file.RelativePath));
        }
    }

    private static bool HasGeneratedSegment(string path)
    {
        var segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var excluded = new[] { ".git", ".vs", "bin", "obj", "node_modules", "dist", "build", "target", ".dart_tool" };
        return segments.Any(segment => excluded.Contains(segment, StringComparer.OrdinalIgnoreCase));
    }

}
