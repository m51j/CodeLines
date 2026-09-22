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

## Git history (optional)

In **Settings**, turn on **Enable Git history statistics** and save settings. Open **Code changes**, choose **7**, **30**, **365**, or **Custom** days (1–36500), select a mode, and click **Refresh**. Git must be installed and available on PATH. Disabling the feature cancels analysis and hides its page.

- **Activity** sums committed changes on the current branch's first-parent history. Merge commits are compared with their first parent, so merged changes are counted once. The commit count includes all commits in this history window, even when their files are excluded.
- **NetChange** compares the captured HEAD with the first commit before the period on that same history path. If the repository began within the period, the baseline is an empty tree.
- Periods use commit timestamps and a captured analysis end time; the interface displays local dates. Uncommitted changes are excluded. No fetch, checkout, GitHub login, or network request occurs.
- Added and deleted are Git line counts. **Modified (est.)** pairs additions and deletions within adjacent replacement blocks; it overlaps those counts and must not be added to them. Net is added minus deleted. Whitespace and comment changes count as physical line changes.
- The current counting scope, global/project exclusions, and current on-disk `.gitignore` rules filter historical paths, including deleted files. A project inside a repository only counts its own folder. Renames within scope preserve Git's rename statistics; moves across the scope boundary count as a file entering or leaving scope. Binary changes appear as files with zero line counts.
- Saved results show their timestamp, branch, and HEAD. Refresh to check changes made outside CodeLines; options and project edits clear displayed results. CSV/JSON exports on this page contain history results separately from source-analysis exports.
- Missing Git, unreadable repositories, and shallow clones have explicit project statuses. Shallow boundary commits are excluded from activity counts; a net comparison requiring unavailable history is not reported as a complete result.

The tests include temporary Git repositories, view-model cancellation and settings checks, and WPF rendering in both themes. Render previews are written under `artifacts/history-ui`.

### History progress and totals

Refresh now reports the current project, discovery/comparison stage, commit, file, completed/remaining comparisons and files, and projects still waiting. Progress events are coalesced to four UI updates per second; elapsed time continues updating while a Git command is running. The project bar is indeterminate while the amount of work is unknown. Overall percentage weights projects equally, not by runtime. An approximate **current-project** ETA appears after at least two completed comparisons; it can vary with commit size.

Totals accumulate as projects finish and include added, deleted, estimated modified, net lines, files, binary files, and activity commits. They are explicitly partial during work or after cancellation, and show how many repositories were incomplete/unavailable. Totals sum each configured project separately; overlapping project folders are not deduplicated. Refresh and source-scan actions are disabled during analysis. Cancel works from the history page or the shared toolbar, and export stays disabled until the run finishes. With no enabled projects, the page explains how to start instead of reporting success.

JSON history exports include a `totals` object. CSV adds a `RecordType` column (`File`, `ProjectStatus`, or `Totals`) and one aggregate row; filter to `File` before summing file rows to avoid counting the aggregate again.
