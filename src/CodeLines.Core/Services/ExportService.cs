using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeLines.Core.Abstractions;
using CodeLines.Core.Models;

namespace CodeLines.Core.Services;

public sealed class ExportService : IExportService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task ExportJsonAsync(ScanSnapshot snapshot, string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, snapshot, JsonOptions, cancellationToken);
    }

    public async Task ExportCsvAsync(ScanSnapshot snapshot, string path, CancellationToken cancellationToken = default)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Project,Path,Extension,Language,Category,Bytes,Tokens,PhysicalLines,BlankLines,CodeLines,CommentLines,MixedLines,ContentLines");
        foreach (var file in snapshot.Projects.SelectMany(x => x.Files))
        {
            cancellationToken.ThrowIfCancellationRequested();
            builder.AppendJoin(',', Csv(file.ProjectName), Csv(file.RelativePath), Csv(file.Extension), Csv(file.Language),
                file.Category, file.Bytes, file.Tokens, file.Lines.PhysicalLines, file.Lines.BlankLines,
                file.Lines.CodeLines, file.Lines.CommentLines, file.Lines.MixedLines, file.Lines.ContentLines);
            builder.AppendLine();
        }
        await File.WriteAllTextAsync(path, builder.ToString(), new UTF8Encoding(true), cancellationToken);
    }

    private static string Csv(object? value)
    {
        var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        return '"' + text.Replace("\"", "\"\"") + '"';
    }
}
