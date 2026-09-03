using System.Text.Json;
using System.Text.Json.Serialization;
using CodeLines.Core.Abstractions;
using CodeLines.Core.Models;

namespace CodeLines.Core.Services;

public sealed class JsonProjectRepository : IProjectRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public JsonProjectRepository(string? settingsPath = null)
    {
        SettingsPath = settingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodeLines", "settings.json");
    }

    public string SettingsPath { get; }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(SettingsPath)) return new AppSettings();
        try
        {
            await using var stream = File.OpenRead(SettingsPath);
            return await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions, cancellationToken)
                   ?? new AppSettings();
        }
        catch (JsonException)
        {
            var backup = SettingsPath + $".invalid-{DateTime.Now:yyyyMMddHHmmss}";
            File.Copy(SettingsPath, backup, false);
            return new AppSettings();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(SettingsPath)!;
        Directory.CreateDirectory(directory);
        var tempPath = SettingsPath + ".tmp";
        await using (var stream = File.Create(tempPath))
            await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken);
        File.Move(tempPath, SettingsPath, true);
    }
}
