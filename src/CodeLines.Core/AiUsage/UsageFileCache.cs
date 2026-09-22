using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeLines.Core.AiUsage;

public sealed record UsageCacheStats(int Files, int Parsed, int Reused);

/// <summary>
/// Incremental per-source cache (ccstats' cache.mjs / filecache.mjs): transcripts are append-only,
/// so a file whose size and modification time are unchanged is reused instead of re-parsed.
/// </summary>
public sealed class UsageFileCache<T>(string cacheFile, string version, JsonConverter<T> recordConverter)
{
    private readonly JsonSerializerOptions _json = new() { Converters = { recordConverter } };

    public string CacheFile { get; } = cacheFile;

    public async Task<(List<T> Records, UsageCacheStats Stats)> LoadAsync(IReadOnlyList<string> files,
        Func<string, CancellationToken, List<T>> parse, bool rescan, IProgress<(int Done, int Total)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var cache = rescan ? null : await ReadAsync(cancellationToken);
        var entries = new CacheEntry?[files.Count];
        var parsed = 0;
        var reused = 0;
        var done = 0;
        // Changed files are parsed in parallel; results are placed back by index so record order stays deterministic.
        await Parallel.ForEachAsync(Enumerable.Range(0, files.Count),
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1), CancellationToken = cancellationToken },
            (i, ct) =>
            {
                var file = files[i];
                var info = new FileInfo(file);
                if (info.Exists)
                {
                    var mtime = info.LastWriteTimeUtc.Ticks;
                    if (cache?.Files.TryGetValue(file, out var hit) == true && hit.Size == info.Length && hit.Mtime == mtime)
                    {
                        entries[i] = hit;
                        Interlocked.Increment(ref reused);
                    }
                    else
                    {
                        entries[i] = new CacheEntry { Size = info.Length, Mtime = mtime, Records = parse(file, ct) };
                        Interlocked.Increment(ref parsed);
                    }
                }
                var count = Interlocked.Increment(ref done);
                if (count % 25 == 0 || count == files.Count) progress?.Report((count, files.Count));
                return ValueTask.CompletedTask;
            });

        var next = new CacheDocument { Version = version };
        var records = new List<T>();
        for (var i = 0; i < files.Count; i++)
        {
            if (entries[i] is not { } entry) continue;
            next.Files[files[i]] = entry;
            records.AddRange(entry.Records);
        }
        await WriteAsync(next, cancellationToken);
        return (records, new UsageCacheStats(files.Count, parsed, reused));
    }

    private async Task<CacheDocument?> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(CacheFile)) return null;
            await using var stream = File.OpenRead(CacheFile);
            var document = await JsonSerializer.DeserializeAsync<CacheDocument>(stream, _json, cancellationToken);
            return document?.Version == version ? document : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException)
        {
            return null;
        }
    }

    private async Task WriteAsync(CacheDocument document, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CacheFile)!);
        var temp = CacheFile + ".tmp";
        await using (var stream = File.Create(temp))
            await JsonSerializer.SerializeAsync(stream, document, _json, cancellationToken);
        File.Move(temp, CacheFile, true);
    }

    private sealed class CacheDocument
    {
        [JsonPropertyName("version")] public string Version { get; set; } = "";
        [JsonPropertyName("files")] public Dictionary<string, CacheEntry> Files { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class CacheEntry
    {
        [JsonPropertyName("size")] public long Size { get; set; }
        [JsonPropertyName("mtime")] public long Mtime { get; set; }
        [JsonPropertyName("records")] public List<T> Records { get; set; } = [];
    }
}

/// <summary>Recursive file listing that ignores unreadable folders, like ccstats' walk().</summary>
public static class UsageFiles
{
    public static List<string> Walk(string root, Func<string, bool> nameFilter)
    {
        var result = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            try
            {
                foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos())
                {
                    if (entry is DirectoryInfo sub)
                    {
                        if (!sub.Attributes.HasFlag(FileAttributes.ReparsePoint)) pending.Push(sub.FullName);
                    }
                    else if (nameFilter(entry.Name)) result.Add(entry.FullName);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return result;
    }

    /// <summary>JavaScript's default <c>Array.prototype.sort()</c> order (UTF-16 code units).</summary>
    public static List<string> Sorted(this IEnumerable<string> paths)
    {
        var list = paths.ToList();
        list.Sort(StringComparer.Ordinal);
        return list;
    }
}
