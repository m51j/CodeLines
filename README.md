# CodeLines

CodeLines is a local Windows desktop application that measures source code, configuration, and documentation across multiple projects. It reports physical lines, code, comments, blank lines, content lines, file sizes, and LLM token counts by project, language, category, and file.

The application targets **.NET 10 / WPF**, uses the built-in Fluent theme, and follows MVVM with `CommunityToolkit.Mvvm`. No source text is uploaded and no API key is required.

## Run the application

Requirements:

- Windows 10 or later
- .NET 10 SDK

```powershell
dotnet restore CodeLines.slnx
dotnet run --project src/CodeLines.App/CodeLines.App.csproj
```

Build and test a release configuration:

```powershell
dotnet build CodeLines.slnx -c Release
dotnet test CodeLines.slnx -c Release
```

## Counting scopes

The scope selector is available in the top toolbar and in Settings.

| Scope | Included by default |
| --- | --- |
| `SourceOnly` | Programming languages, markup templates, scripts, and SQL |
| `SourceAndConfiguration` | Source plus JSON, XML, YAML, TOML, project files, and related configuration |
| `AllText` | Every known document format plus unknown files that pass binary detection |

Scope applies to both line and token totals. Documentation is kept in a separate `DocumentationText` category and uses content lines instead of code/comment classification.

## Line metrics

- **PhysicalLines**: every physical line, including blank lines.
- **BlankLines**: whitespace-only lines.
- **CodeLines**: nonblank lines containing code. A line containing code and a comment is included here.
- **CommentLines**: comment-only lines.
- **MixedLines**: code lines that also contain a comment.
- **ContentLines**: nonblank lines in documentation and general text files.

## Token counting

Token counts are calculated offline with `Microsoft.ML.Tokenizers`. Select either:

- `o200k_base` (default)
- `cl100k_base`

The chosen tokenizer is recorded with the saved scan snapshot and affects every included file in the selected scope.

## Exclusions and `.gitignore`

CodeLines reads `.gitignore` files at the project root and in nested directories. It supports ordered rules, directory patterns, `*`, `**`, `?`, anchored patterns, and `!` negation without calling the Git executable.

Default exclusions protect totals from common generated content, including `.git`, `.vs`, `bin`, `obj`, `node_modules`, `dist`, `build`, `target`, `.dart_tool`, `coverage`, `publish`, `Releases`, `TestResults`, Playwright results, and `artifacts`.

Global patterns are editable in Settings. Project-specific patterns are editable on the Projects page and are evaluated after repository rules.

## Projects and local data

The seven requested project paths are seeded on first launch. Use **Add project** to select another folder, or edit, disable, and remove rows on the Projects page.

Settings and the last scan are saved atomically at:

```text
%LOCALAPPDATA%\CodeLines\settings.json
```

CSV and JSON exports are available from the Analysis page.

## Add or change a language

Language definitions live in `src/CodeLines.Core/Services/LanguageRegistry.cs`. Add a source, configuration, or documentation definition with:

- display name;
- extensions and optional exact file names;
- line-comment markers;
- block-comment start/end pairs.

The classifier and scanner consume the registry through `ILanguageRegistry`, so adding a definition does not require UI changes.

## Architecture

- `CodeLines.Core`: models, language classification, line analysis, ignore rules, tokenization, scanning, persistence, and export.
- `CodeLines.App`: WPF Fluent interface and MVVM view models.
- `CodeLines.Tests`: scope, language, line, ignore, tokenizer, binary-file, and scanner tests.

Scanning is asynchronous and uses bounded parallelism. It supports cancellation, skips reparse-point directories to prevent loops, detects binary files, enforces a configurable maximum file size, and records unreadable paths as warnings without stopping the remaining scan.
