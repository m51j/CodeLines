using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeLines.Core.Models;

namespace CodeLines.Core.Services;

public static class GitHistoryExportService
{
    public static async Task ExportAsync(GitHistorySnapshot snapshot, string path, CancellationToken token = default)
    {
        if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            await using var stream = File.Create(path);
            await JsonSerializer.SerializeAsync(stream, snapshot, new JsonSerializerOptions(JsonSerializerDefaults.Web)
            { WriteIndented = true, Converters = { new JsonStringEnumConverter() } }, token);
            return;
        }
        var builder = new StringBuilder("Project,Branch,Head,Start,End,Mode,Scope,Status,Commits,ChangedFiles,BinaryFiles,Path,Added,Deleted,ModifiedEstimate,Net,Binary,RecordType\r\n");
        foreach (var project in snapshot.Projects)
        {
            // Keep project status in exports even if it has no file rows.
            var rows = project.Files.Count == 0 ? new GitFileChange?[] { null } : project.Files.Cast<GitFileChange?>();
            foreach (var file in rows)
            {
                token.ThrowIfCancellationRequested();
                object?[] values = [project.ProjectName, project.Branch, project.Head, snapshot.Start.ToString("O"), snapshot.End.ToString("O"),
                    snapshot.Mode, snapshot.Scope, project.Status, project.Commits, project.ChangedFiles, project.BinaryFiles,
                    file?.Path, file?.Added, file?.Deleted, file?.ModifiedEstimate, file?.Net, file?.Binary, file is null ? "ProjectStatus" : "File"];
                builder.AppendLine(string.Join(',', values.Select(Csv)));
            }
        }
        var totals = snapshot.Totals;
        object?[] totalValues = ["All projects", "", "", snapshot.Start.ToString("O"), snapshot.End.ToString("O"), snapshot.Mode, snapshot.Scope,
            $"{totals.Projects} projects; {totals.IncompleteProjects} incomplete/unavailable", totals.Commits, totals.ChangedFiles, totals.BinaryFiles,
            "", totals.Added, totals.Deleted, totals.ModifiedEstimate, totals.Net, "", "Totals"];
        builder.AppendLine(string.Join(',', totalValues.Select(Csv)));
        await File.WriteAllTextAsync(path, builder.ToString(), new UTF8Encoding(true), token);
    }

    private static string Csv(object? value)
    {
        var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        if (value is string && text.Length > 0 && "=+-@\t\r".Contains(text[0])) text = "'" + text;
        return '"' + text.Replace("\"", "\"\"") + '"';
    }
}
