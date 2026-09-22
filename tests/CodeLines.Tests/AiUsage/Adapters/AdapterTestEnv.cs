using System.Text;
using CodeLines.Core.AiUsage.Agents;
using Microsoft.Data.Sqlite;

namespace CodeLines.Tests.AiUsage.Adapters;

/// <summary>A throwaway Home/AppData/LocalAppData/cache tree for adapter fixtures.</summary>
public sealed class AdapterTestEnv : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "codelines-adapters-" + Guid.NewGuid().ToString("N"));
    public AgentEnvironment Env { get; }

    public AdapterTestEnv()
    {
        Env = new(Path.Combine(Root, "home"), Path.Combine(Root, "appdata"), Path.Combine(Root, "localappdata"), Path.Combine(Root, "cache"));
        Directory.CreateDirectory(Root);
    }

    /// <summary>Writes <paramref name="content"/> to a path relative to <paramref name="baseDir"/>, creating folders.</summary>
    public static string Write(string baseDir, string relative, string content)
    {
        var path = Path.Combine(baseDir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    /// <summary>Creates a SQLite database at <paramref name="path"/> and runs <paramref name="sql"/> with optional blob parameters $p0, $p1, ...</summary>
    public static void Sqlite(string path, params (string Sql, object[] Args)[] statements)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        db.Open();
        foreach (var (sql, args) in statements)
        {
            using var command = db.CreateCommand();
            command.CommandText = sql;
            for (var i = 0; i < args.Length; i++) command.Parameters.AddWithValue($"$p{i}", args[i] ?? DBNull.Value);
            command.ExecuteNonQuery();
        }
    }

    public static (string, object[]) Sql(string sql, params object[] args) => (sql, args);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(Root, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>Hand-rolled protobuf encoder for fixtures.</summary>
public static class Pb
{
    public static byte[] Varint(ulong v)
    {
        var bytes = new List<byte>();
        do
        {
            var b = (byte)(v & 0x7f);
            v >>= 7;
            bytes.Add(v != 0 ? (byte)(b | 0x80) : b);
        } while (v != 0);
        return [.. bytes];
    }

    public static byte[] Int(int field, ulong value) => [.. Varint((ulong)field << 3), .. Varint(value)];
    public static byte[] Bytes(int field, byte[] value) => [.. Varint(((ulong)field << 3) | 2), .. Varint((ulong)value.Length), .. value];
    public static byte[] Str(int field, string value) => Bytes(field, Encoding.UTF8.GetBytes(value));
    public static byte[] Fixed64(int field) => [.. Varint(((ulong)field << 3) | 1), 1, 2, 3, 4, 5, 6, 7, 8];
    public static byte[] Fixed32(int field) => [.. Varint(((ulong)field << 3) | 5), 1, 2, 3, 4];
    public static byte[] Msg(params byte[][] parts) => [.. parts.SelectMany(p => p)];
}
