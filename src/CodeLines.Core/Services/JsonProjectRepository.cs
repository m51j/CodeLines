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

    /// <summary>
    /// Reads only the saved theme, stopping as soon as it is found, so the window can be themed
    /// before the full settings file (which carries the last snapshot) finishes loading.
    /// </summary>
    public string? ReadTheme()
    {
        try
        {
            using var stream = File.OpenRead(SettingsPath);
            var buffer = new byte[16 * 1024];
            var length = 0;
            var state = new JsonReaderState();
            var themeIsNext = false;
            while (true)
            {
                var read = stream.Read(buffer, length, buffer.Length - length);
                length += read;
                var reader = new Utf8JsonReader(buffer.AsSpan(0, length), read == 0, state);
                while (reader.Read())
                {
                    if (themeIsNext) return reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                    themeIsNext = reader is { TokenType: JsonTokenType.PropertyName, CurrentDepth: 1 }
                                  && string.Equals(reader.GetString(), "theme", StringComparison.OrdinalIgnoreCase);
                }

                if (read == 0) return null;
                // Keep the incomplete token for the next pass, growing the buffer only when it fills it.
                var consumed = (int)reader.BytesConsumed;
                buffer.AsSpan(consumed, length - consumed).CopyTo(buffer);
                length -= consumed;
                if (length == buffer.Length) Array.Resize(ref buffer, buffer.Length * 2);
                state = reader.CurrentState;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
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
