using Microsoft.Data.Sqlite;

namespace CodeLines.Core.AiUsage.Agents;

/// <summary>
/// Read-only access to the SQLite stores other agents keep (ccstats' agents/sqlite.mjs). The live database is opened
/// read-only; if the owning app holds a lock, a private copy (with its -wal and -shm) is read instead.
/// </summary>
public static class SqliteSource
{
    public static T WithDb<T>(string file, string tempDirectory, Func<SqliteConnection, T> read)
    {
        SqliteConnection? db = null;
        try
        {
            db = Open(file);
            using (var probe = db.CreateCommand())
            {
                probe.CommandText = "select count(*) from sqlite_master";
                probe.ExecuteScalar();
            }
        }
        catch (SqliteException)
        {
            db?.Dispose();
            Directory.CreateDirectory(tempDirectory);
            var copy = Path.Combine(tempDirectory, Path.GetFileName(Path.GetDirectoryName(file)) + "-" + Path.GetFileName(file));
            File.Copy(file, copy, true);
            foreach (var ext in new[] { "-wal", "-shm" })
            {
                if (File.Exists(file + ext)) File.Copy(file + ext, copy + ext, true);
                else File.Delete(copy + ext);
            }
            db = Open(copy);
        }
        using (db) return read(db);
    }

    public static bool HasTable(SqliteConnection db, string name)
    {
        using var command = db.CreateCommand();
        command.CommandText = "select 1 from sqlite_master where type='table' and name=$name";
        command.Parameters.AddWithValue("$name", name);
        return command.ExecuteScalar() is not null;
    }

    /// <summary>Every row as column → value (long, double, string, byte[] or null), like node:sqlite's all().</summary>
    public static List<Dictionary<string, object?>> Query(SqliteConnection db, string sql)
    {
        using var command = db.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<Dictionary<string, object?>>();
        while (reader.Read())
        {
            var row = new Dictionary<string, object?>(reader.FieldCount, StringComparer.Ordinal);
            for (var i = 0; i < reader.FieldCount; i++) row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>ccstats' <c>num()</c> for a column value: finite numbers only, anything else is 0.</summary>
    public static double Num(this Dictionary<string, object?> row, string column) => row.GetValueOrDefault(column) switch
    {
        long l => l,
        double d when double.IsFinite(d) => d,
        int i => i,
        _ => 0
    };

    public static string Text(this Dictionary<string, object?> row, string column) =>
        row.GetValueOrDefault(column) switch { string s => s, null => "", var other => Convert.ToString(other, System.Globalization.CultureInfo.InvariantCulture) ?? "" };

    public static bool IsNull(this Dictionary<string, object?> row, string column) => row.GetValueOrDefault(column) is null;

    private static SqliteConnection Open(string file)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = file,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }
}
