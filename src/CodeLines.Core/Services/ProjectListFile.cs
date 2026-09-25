using System.Text.Json;
using System.Text.Json.Nodes;
using CodeLines.Core.Models;

namespace CodeLines.Core.Services;

/// <summary>Reads and writes the project list as a portable JSON file, for moving it between machines.</summary>
public static class ProjectListFile
{
    public const string Format = "codelines-projects";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task ExportAsync(IEnumerable<ProjectDefinition> projects, string path, CancellationToken cancellationToken = default)
    {
        var file = new ProjectListDocument { Projects = projects.ToList() };
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, file, JsonOptions, cancellationToken);
    }

    /// <summary>Accepts an exported list, a bare array, or a whole settings.json (only its projects are read).</summary>
    public static async Task<List<ProjectDefinition>> ImportAsync(string path, CancellationToken cancellationToken = default)
    {
        JsonNode? root;
        try
        {
            await using var stream = File.OpenRead(path);
            root = await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken);
        }
        catch (JsonException ex) { throw new InvalidDataException("The file is not valid JSON.", ex); }

        var list = root switch
        {
            JsonArray array => array,
            JsonObject obj => obj["projects"] as JsonArray,
            _ => null
        } ?? throw new InvalidDataException("The file does not contain a project list.");

        var projects = list.Deserialize<List<ProjectDefinition?>>(JsonOptions) ?? [];
        return projects
            .Where(p => p is not null && !string.IsNullOrWhiteSpace(p.RootPath))
            .Select(p =>
            {
                if (string.IsNullOrWhiteSpace(p!.Name)) p.Name = Path.GetFileName(p.RootPath.TrimEnd('\\', '/'));
                p.ExcludePatterns ??= [];
                return p;
            })
            .ToList();
    }

    private sealed class ProjectListDocument
    {
        public string Format { get; set; } = ProjectListFile.Format;
        public int Version { get; set; } = 1;
        public DateTimeOffset ExportedAt { get; set; } = DateTimeOffset.Now;
        public List<ProjectDefinition> Projects { get; set; } = [];
    }
}
