using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace SimilarVideoFinder.Core;

public sealed class CacheStore : IDisposable
{
    // Change this when metadata extraction, sampling or fingerprint algorithms change.
    public const int AlgorithmVersion = 2;
    private readonly SqliteConnection connection;
    private readonly object gate = new();
    private int cacheHits;
    public int CacheHits => Volatile.Read(ref cacheHits);
    public bool IsPersistent { get; }
    private readonly Dictionary<string, string> savedPayloads = new(StringComparer.OrdinalIgnoreCase);
    public CacheStore(string database, bool saveHistory = false)
    {
        IsPersistent = saveHistory;
        if (saveHistory) Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(database))!);
        connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = saveHistory ? database : ":memory:" }.ToString());
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            CREATE TABLE IF NOT EXISTS files(path TEXT PRIMARY KEY COLLATE NOCASE, size INTEGER NOT NULL,
                ticks INTEGER NOT NULL, version INTEGER NOT NULL, payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS scans(id INTEGER PRIMARY KEY AUTOINCREMENT, started TEXT NOT NULL,
                finished TEXT, state TEXT NOT NULL, options TEXT NOT NULL, report TEXT);
            CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS reviews(pair TEXT PRIMARY KEY, decision TEXT NOT NULL);
            """;
        cmd.ExecuteNonQuery();
    }
    public FileRecord? Load(string path, long size, long ticks)
    {
        lock (gate)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT payload FROM files WHERE path=$p AND size=$s AND ticks=$t AND version=$v";
            cmd.Parameters.AddWithValue("$p", path); cmd.Parameters.AddWithValue("$s", size);
            cmd.Parameters.AddWithValue("$t", ticks); cmd.Parameters.AddWithValue("$v", AlgorithmVersion);
            var value = cmd.ExecuteScalar() as string;
            if (value == null) return null;
            try { var file = JsonSerializer.Deserialize<FileRecord>(value); savedPayloads[path] = value; if (file != null) Interlocked.Increment(ref cacheHits); return file; } catch (JsonException) { return null; }
        }
    }
    public void Save(FileRecord file) => SaveMany([file]);
    public void SaveMany(IEnumerable<FileRecord> files)
    {
        lock (gate)
        {
            var changed = files.Select(f => (File: f, Json: JsonSerializer.Serialize(f)))
                .Where(item => !savedPayloads.TryGetValue(item.File.Path, out var existing) || existing != item.Json).ToArray();
            if (changed.Length == 0) return;
            using var transaction = connection.BeginTransaction();
            using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = "INSERT OR REPLACE INTO files VALUES($p,$s,$t,$v,$j)";
            var p = cmd.Parameters.Add("$p", SqliteType.Text);
            var s = cmd.Parameters.Add("$s", SqliteType.Integer);
            var t = cmd.Parameters.Add("$t", SqliteType.Integer);
            var v = cmd.Parameters.Add("$v", SqliteType.Integer);
            var j = cmd.Parameters.Add("$j", SqliteType.Text);
            foreach (var item in changed)
            {
                var file = item.File;
                p.Value = file.Path; s.Value = file.Size; t.Value = file.ModifiedTicks;
                v.Value = AlgorithmVersion; j.Value = item.Json; cmd.ExecuteNonQuery();
            }
            transaction.Commit();
            foreach (var item in changed) savedPayloads[item.File.Path] = item.Json;
        }
    }
    public long BeginScan(ScanOptions options)
    {
        lock (gate)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "INSERT INTO scans(started,state,options) VALUES($t,'Running',$o); SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$o", JsonSerializer.Serialize(options));
            return (long)cmd.ExecuteScalar()!;
        }
    }
    public void FinishScan(ScanReport report)
    {
        lock (gate)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE scans SET finished=$t,state=$s,report=$r WHERE id=$id";
            cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$s", report.Cancelled ? "Cancelled" : report.Status.ToString());
            cmd.Parameters.AddWithValue("$r", JsonSerializer.Serialize(report));
            cmd.Parameters.AddWithValue("$id", report.ScanId); cmd.ExecuteNonQuery();
            using var trim = connection.CreateCommand();
            trim.CommandText = "DELETE FROM scans WHERE id NOT IN (SELECT id FROM scans ORDER BY id DESC LIMIT 30)";
            trim.ExecuteNonQuery();
        }
    }
    public ScanReport? LatestReport()
    {
        lock (gate)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT report FROM scans WHERE report IS NOT NULL ORDER BY id DESC LIMIT 1";
            var json = cmd.ExecuteScalar() as string;
            try { return json == null ? null : JsonSerializer.Deserialize<ScanReport>(json); }
            catch (JsonException) { return null; }
        }
    }
    public List<(long Id, string Label)> History()
    {
        lock (gate)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT id,started,state FROM scans ORDER BY id DESC LIMIT 30";
            using var reader = cmd.ExecuteReader();
            var history = new List<(long, string)>();
            while (reader.Read()) history.Add((reader.GetInt64(0), $"Scan {reader.GetInt64(0)} � {DateTime.Parse(reader.GetString(1)).ToLocalTime():g} � {reader.GetString(2)}"));
            return history;
        }
    }
    public ScanReport? Report(long id)
    {
        lock (gate)
        {
            using var cmd = connection.CreateCommand(); cmd.CommandText = "SELECT report FROM scans WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", id);
            try { return cmd.ExecuteScalar() is string json ? JsonSerializer.Deserialize<ScanReport>(json) : null; }
            catch (JsonException) { return null; }
        }
    }
    public void SaveSettings(ScanOptions options)
    {
        lock (gate)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "INSERT OR REPLACE INTO settings VALUES('options',$v)";
            cmd.Parameters.AddWithValue("$v", JsonSerializer.Serialize(options)); cmd.ExecuteNonQuery();
        }
    }
    public ScanOptions? LoadSettings()
    {
        lock (gate)
        {
            using var cmd = connection.CreateCommand(); cmd.CommandText = "SELECT value FROM settings WHERE key='options'";
            try { return cmd.ExecuteScalar() is string json ? JsonSerializer.Deserialize<ScanOptions>(json) : null; }
            catch (JsonException) { return null; }
        }
    }
    public void Remove(string path)
    {
        lock (gate)
        {
            using var cmd = connection.CreateCommand(); cmd.CommandText = "DELETE FROM files WHERE path=$p";
            cmd.Parameters.AddWithValue("$p", path); cmd.ExecuteNonQuery();
            savedPayloads.Remove(path);
        }
    }
    public void Dispose() => connection.Dispose();
    public void SaveReview(FileRecord a, FileRecord b, string decision)
    {
        lock (gate)
        {
            using var cmd = connection.CreateCommand(); cmd.CommandText = "INSERT OR REPLACE INTO reviews VALUES($p,$d)";
            cmd.Parameters.AddWithValue("$p", ReviewAnalysis.PairKey(a, b)); cmd.Parameters.AddWithValue("$d", decision); cmd.ExecuteNonQuery();
        }
    }
    public string? Review(FileRecord a, FileRecord b)
    {
        lock (gate)
        {
            using var cmd = connection.CreateCommand(); cmd.CommandText = "SELECT decision FROM reviews WHERE pair=$p";
            cmd.Parameters.AddWithValue("$p", ReviewAnalysis.PairKey(a, b)); return cmd.ExecuteScalar() as string;
        }
    }
    public (long Files, long Scans, long Reviews, long Bytes) Statistics()
    {
        lock (gate)
        {
            long Query(string sql) { using var cmd = connection.CreateCommand(); cmd.CommandText = sql; return Convert.ToInt64(cmd.ExecuteScalar()); }
            return (Query("SELECT COUNT(*) FROM files"), Query("SELECT COUNT(*) FROM scans"), Query("SELECT COUNT(*) FROM reviews"), Query("PRAGMA page_count") * Query("PRAGMA page_size"));
        }
    }
    public void ClearHistory()
    {
        lock (gate)
        {
            using var cmd = connection.CreateCommand(); cmd.CommandText = "DELETE FROM files; DELETE FROM scans; DELETE FROM reviews; DELETE FROM settings; VACUUM;";
            cmd.ExecuteNonQuery(); savedPayloads.Clear();
        }
    }
    public int RemoveStaleFiles()
    {
        // Release the database reader before checking potentially slow network paths.
        List<FileRecord> files = [];
        lock (gate)
        {
            using var cmd = connection.CreateCommand(); cmd.CommandText = "SELECT payload FROM files"; using var reader = cmd.ExecuteReader();
            while (reader.Read()) { try { if (JsonSerializer.Deserialize<FileRecord>(reader.GetString(0)) is { } f) files.Add(f); } catch (JsonException) { } }
        }
        int removed = 0;
        foreach (var file in files) if (!file.IsCurrent()) { Remove(file.Path); removed++; }
        return removed;
    }
}
