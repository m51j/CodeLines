using CodeLines.Core.Abstractions;
using CodeLines.Core.Models;

namespace CodeLines.Core.Services;

public sealed class LanguageRegistry : ILanguageRegistry
{
    private static readonly IReadOnlySet<string> EmptyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<LanguageDefinition> Languages { get; }

    public LanguageRegistry()
    {
        Languages =
        [
            Source("C#", [".cs"], ["//"], [("/*", "*/")]),
            Source("XAML", [".xaml"], [], [("<!--", "-->")]),
            Source("Razor", [".razor", ".cshtml"], ["//"], [("@*", "*@"), ("<!--", "-->")]),
            Source("TypeScript", [".ts", ".tsx", ".mts", ".cts"], ["//"], [("/*", "*/")]),
            Source("JavaScript", [".js", ".jsx", ".mjs", ".cjs"], ["//"], [("/*", "*/")]),
            Source("HTML", [".html", ".htm"], [], [("<!--", "-->")]),
            Source("CSS", [".css", ".scss", ".sass", ".less"], ["//"], [("/*", "*/")]),
            Source("SQL", [".sql"], ["--"], [("/*", "*/")]),
            Source("Dart", [".dart"], ["//"], [("/*", "*/")]),
            Source("C/C++", [".c", ".cc", ".cpp", ".cxx", ".h", ".hh", ".hpp", ".hxx"], ["//"], [("/*", "*/")]),
            Source("Python", [".py", ".pyw"], ["#"], []),
            Source("Rust", [".rs"], ["//"], [("/*", "*/")]),
            Source("Java", [".java"], ["//"], [("/*", "*/")]),
            Source("Kotlin", [".kt", ".kts"], ["//"], [("/*", "*/")]),
            Source("Swift", [".swift"], ["//"], [("/*", "*/")]),
            Source("Objective-C", [".m", ".mm"], ["//"], [("/*", "*/")]),
            Source("PowerShell", [".ps1", ".psm1", ".psd1"], ["#"], [("<#", "#>")]),
            Source("Batch", [".bat", ".cmd"], ["REM ", "::"], []),
            Source("Shell", [".sh", ".bash", ".zsh", ".fish"], ["#"], []),
            Source("Go", [".go"], ["//"], [("/*", "*/")]),
            Source("PHP", [".php"], ["//", "#"], [("/*", "*/")]),
            Source("Ruby", [".rb"], ["#"], []),
            Source("F#", [".fs", ".fsx", ".fsi"], ["//"], [("(*", "*)")]),
            Source("Visual Basic", [".vb"], ["'"], []),
            Source("Lua", [".lua"], ["--"], [("--[[", "]]" )]),
            Source("R", [".r"], ["#"], []),
            Source("Vue", [".vue"], ["//"], [("/*", "*/"), ("<!--", "-->")]),
            Source("Svelte", [".svelte"], ["//"], [("/*", "*/"), ("<!--", "-->")]),
            SourceWithNames("Build Script", [".gradle"], ["Makefile", "Dockerfile", "Jenkinsfile"], ["#", "//"], [("/*", "*/")]),

            Config("JSON", [".json", ".jsonc"], ["//"], [("/*", "*/")]),
            Config("XML", [".xml", ".config", ".props", ".targets", ".csproj", ".fsproj", ".vbproj", ".vcxproj"], [], [("<!--", "-->")]),
            Config("YAML", [".yaml", ".yml"], ["#"], []),
            Config("TOML", [".toml"], ["#"], []),
            Config("INI", [".ini", ".editorconfig", ".properties"], ["#", ";"], []),
            Config("Solution", [".sln", ".slnx"], ["#"], []),

            Text("Markdown", [".md", ".markdown", ".mdx"]),
            Text("Text", [".txt", ".text", ".log"]),
            Text("Documentation", [".rst", ".adoc", ".asc"])
        ];
    }

    public LanguageDefinition? Find(string path)
    {
        var extension = Path.GetExtension(path);
        var fileName = Path.GetFileName(path);
        return Languages.FirstOrDefault(x => x.Extensions.Contains(extension) || x.FileNames.Contains(fileName));
    }

    private static LanguageDefinition Source(string name, string[] extensions, string[] lines,
        (string Start, string End)[] blocks) => Make(name, FileCategory.SourceCode, extensions, [], lines, blocks);

    private static LanguageDefinition SourceWithNames(string name, string[] extensions, string[] names,
        string[] lines, (string Start, string End)[] blocks) => Make(name, FileCategory.SourceCode, extensions, names, lines, blocks);

    private static LanguageDefinition Config(string name, string[] extensions, string[] lines,
        (string Start, string End)[] blocks) => Make(name, FileCategory.Configuration, extensions, [], lines, blocks);

    private static LanguageDefinition Text(string name, string[] extensions) =>
        Make(name, FileCategory.DocumentationText, extensions, [], [], []);

    private static LanguageDefinition Make(string name, FileCategory category, string[] extensions,
        string[] names, string[] lines, (string Start, string End)[] blocks) =>
        new(name, category,
            new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase),
            names.Length == 0 ? EmptyNames : new HashSet<string>(names, StringComparer.OrdinalIgnoreCase),
            lines, blocks.Select(x => new CommentPair(x.Start, x.End)).ToArray());
}

