using System.Text.Json;
using System.Text.Json.Serialization;
using CodeLines.Core.Abstractions;
using CodeLines.Core.Models;

namespace CodeLines.Core.Services;

public sealed class JsonDailyLogRepository : IDailyLogRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public JsonDailyLogRepository(string? logPath = null)
    {
        LogPath = logPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodeLines", "daily-log.json");
    }

    public string LogPath { get; }

    public async Task<DailyLogFile> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(LogPath)) return new DailyLogFile();
        try
        {
            await using var stream = File.OpenRead(LogPath);
            return await JsonSerializer.DeserializeAsync<DailyLogFile>(stream, JsonOptions, cancellationToken)
                   ?? new DailyLogFile();
        }
        catch (JsonException)
        {
            var backup = LogPath + $".invalid-{DateTime.Now:yyyyMMddHHmmss}";
            File.Copy(LogPath, backup, false);
            return new DailyLogFile();
        }
    }

    public async Task SaveAsync(DailyLogFile log, CancellationToken cancellationToken = default)
    {
        // Serialize before the first await, so the caller's thread can keep changing the log afterwards.
        var bytes = JsonSerializer.SerializeToUtf8Bytes(log, JsonOptions);
        Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
        var tempPath = LogPath + ".tmp";
        await File.WriteAllBytesAsync(tempPath, bytes, cancellationToken);
        File.Move(tempPath, LogPath, true);
    }
}
