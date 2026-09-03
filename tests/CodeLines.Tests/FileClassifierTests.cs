using CodeLines.Core.Models;
using CodeLines.Core.Services;

namespace CodeLines.Tests;

public sealed class FileClassifierTests
{
    private readonly FileClassifier _classifier = new(new LanguageRegistry());

    [Theory]
    [InlineData(CountingScope.SourceOnly, "Program.cs", true, FileCategory.SourceCode)]
    [InlineData(CountingScope.SourceOnly, "README.md", false, FileCategory.DocumentationText)]
    [InlineData(CountingScope.SourceOnly, "appsettings.json", false, FileCategory.Configuration)]
    [InlineData(CountingScope.SourceAndConfiguration, "appsettings.json", true, FileCategory.Configuration)]
    [InlineData(CountingScope.SourceAndConfiguration, "README.md", false, FileCategory.DocumentationText)]
    [InlineData(CountingScope.AllText, "README.md", true, FileCategory.DocumentationText)]
    [InlineData(CountingScope.AllText, "notes.unknown", true, FileCategory.DocumentationText)]
    public void Classifies_files_for_each_scope(CountingScope scope, string path, bool included, FileCategory category)
    {
        var result = _classifier.Classify(path, scope);
        Assert.Equal(included, result is not null);
        if (result is not null) Assert.Equal(category, result.Category);
    }

    [Theory]
    [InlineData("screen.tsx", "TypeScript")]
    [InlineData("view.xaml", "XAML")]
    [InlineData("main.dart", "Dart")]
    [InlineData("query.sql", "SQL")]
    [InlineData("native.cpp", "C/C++")]
    [InlineData("build.kts", "Kotlin")]
    [InlineData("Dockerfile", "Build Script")]
    public void Detects_expected_languages(string path, string expected)
    {
        Assert.Equal(expected, _classifier.Classify(path, CountingScope.SourceOnly)?.Language.Name);
    }
}

