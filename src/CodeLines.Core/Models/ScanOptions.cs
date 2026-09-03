namespace CodeLines.Core.Models;

public sealed class ScanOptions
{
    public CountingScope CountingScope { get; set; } = CountingScope.SourceOnly;
    public string TokenizerEncoding { get; set; } = "o200k_base";
    public long MaxFileSizeBytes { get; set; } = 5 * 1024 * 1024;
    public int MaxDegreeOfParallelism { get; set; } = Math.Max(2, Environment.ProcessorCount);
    public List<string> GlobalExcludePatterns { get; set; } = DefaultExclusions.ToList();

    public static readonly string[] DefaultExclusions =
    [
        ".git/", ".vs/", ".idea/", "bin/", "obj/", "node_modules/", "dist/", "dist-ssr/",
        "build/", "target/", ".dart_tool/", "coverage/", "publish/", "Releases/", "TestResults/",
        "playwright-report/", "test-results/", "artifacts/", "packages/", ".next/", "out/"
    ];
}

