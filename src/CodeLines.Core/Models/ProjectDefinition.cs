namespace CodeLines.Core.Models;

public sealed class ProjectDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string RootPath { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public List<string> ExcludePatterns { get; set; } = [];
}

