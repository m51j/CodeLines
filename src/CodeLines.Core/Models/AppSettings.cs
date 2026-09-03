namespace CodeLines.Core.Models;

public sealed class AppSettings
{
    public List<ProjectDefinition> Projects { get; set; } = CreateDefaultProjects();
    public ScanOptions ScanOptions { get; set; } = new();
    public string Theme { get; set; } = "System";
    public ScanSnapshot? LastSnapshot { get; set; }

    public static List<ProjectDefinition> CreateDefaultProjects() =>
    [
    ];

    private static ProjectDefinition New(string name, string path) => new() { Name = name, RootPath = path };
}

