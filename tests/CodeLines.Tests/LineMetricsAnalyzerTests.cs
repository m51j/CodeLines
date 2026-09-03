using CodeLines.Core.Models;
using CodeLines.Core.Services;

namespace CodeLines.Tests;

public sealed class LineMetricsAnalyzerTests
{
    private readonly LanguageRegistry _registry = new();
    private readonly LineMetricsAnalyzer _analyzer = new();

    [Fact]
    public void Counts_code_blank_comment_and_mixed_lines()
    {
        const string source = "// header\n\nvar x = 1; // mixed\n/* block\ncontinued */\nreturn x;";
        var language = _registry.Find("sample.cs")!;

        var result = _analyzer.Analyze(source, language, FileCategory.SourceCode);

        Assert.Equal(6, result.PhysicalLines);
        Assert.Equal(1, result.BlankLines);
        Assert.Equal(2, result.CodeLines);
        Assert.Equal(3, result.CommentLines);
        Assert.Equal(1, result.MixedLines);
    }

    [Fact]
    public void Does_not_treat_comment_marker_inside_string_as_comment()
    {
        const string source = "var url = \"https://example.com\";\nvar text = \"/* literal */\";";
        var result = _analyzer.Analyze(source, _registry.Find("sample.ts")!, FileCategory.SourceCode);
        Assert.Equal(2, result.CodeLines);
        Assert.Equal(0, result.CommentLines);
        Assert.Equal(0, result.MixedLines);
    }

    [Fact]
    public void Text_files_use_content_lines_instead_of_code_lines()
    {
        var result = _analyzer.Analyze("Title\n\nParagraph", _registry.Find("README.md")!, FileCategory.DocumentationText);
        Assert.Equal(3, result.PhysicalLines);
        Assert.Equal(1, result.BlankLines);
        Assert.Equal(2, result.ContentLines);
        Assert.Equal(0, result.CodeLines);
    }

    [Fact]
    public void Final_newline_does_not_create_a_phantom_physical_line()
    {
        var result = _analyzer.Analyze("first\nsecond\n", _registry.Find("README.md")!, FileCategory.DocumentationText);
        Assert.Equal(2, result.PhysicalLines);
        Assert.Equal(2, result.ContentLines);
    }
}
