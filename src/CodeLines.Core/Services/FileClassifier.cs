using CodeLines.Core.Abstractions;
using CodeLines.Core.Models;

namespace CodeLines.Core.Services;

public sealed class FileClassifier(ILanguageRegistry registry) : IFileClassifier
{
    private static readonly LanguageDefinition PlainText = new(
        "Plain Text", FileCategory.DocumentationText,
        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        new HashSet<string>(StringComparer.OrdinalIgnoreCase), [], []);

    public FileClassification? Classify(string path, CountingScope scope)
    {
        var language = registry.Find(path);
        if (language is null)
            return scope == CountingScope.AllText ? new(PlainText, FileCategory.DocumentationText) : null;

        var included = scope switch
        {
            CountingScope.SourceOnly => language.Category == FileCategory.SourceCode,
            CountingScope.SourceAndConfiguration => language.Category != FileCategory.DocumentationText,
            CountingScope.AllText => true,
            _ => false
        };
        return included ? new(language, language.Category) : null;
    }
}

