using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using StallWatch.Core.Findings;

namespace StallWatch.Core.Store;

/// <summary>A period during which StallWatch was watching, so "no findings" can be told apart from "not running".</summary>
public sealed record WatchSession(long Id, DateTime Start, DateTime End, string Command);

/// <summary>
/// Keeps findings and watch sessions in SQLite. The store must never become a flush storm itself: findings are
/// queued and written in one transaction every few seconds, with WAL and synchronous=NORMAL (no flush per commit).
/// Thread-safe: <see cref="Add"/> may be called from any thread.
/// </summary>
public sealed class FindingStore : IDisposable
{
    public static string DefaultDirectory =>
        Environment.GetEnvironmentVariable("STALLWATCH_DATA") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StallWatch");

    public static string DefaultPath => Path.Combine(DefaultDirectory, "stallwatch.db");

    readonly SqliteConnection _db;
    readonly ConcurrentQueue<Finding> _queue = new();
    readonly System.Threading.Timer? _timer;
    readonly Lock _writeLock = new();
    long _sessionId;
    bool _disposed;

    /// <param name="flushInterval">How often queued findings are written; null = only on <see cref="Flush"/>/Dispose.</param>
    public FindingStore(string path, TimeSpan? flushInterval = null)
    {
        if (Path.GetDirectoryName(Path.GetFullPath(path)) is { } dir) Directory.CreateDirectory(dir);
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        _db.Open();
        Exec("""
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            CREATE TABLE IF NOT EXISTS findings(
                id INTEGER PRIMARY KEY,
                time TEXT NOT NULL,
                kind TEXT NOT NULL,
                process TEXT,
                summary TEXT NOT NULL,
                evidence TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS findings_time ON findings(time);
            CREATE TABLE IF NOT EXISTS sessions(
                id INTEGER PRIMARY KEY,
                start TEXT NOT NULL,
                end TEXT NOT NULL,
                command TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS hidden(
                process TEXT PRIMARY KEY COLLATE NOCASE);
            """);
        if (flushInterval is { } every) _timer = new System.Threading.Timer(_ => Flush(), null, every, every);
    }

    /// <summary>Marks the start of a watch; its end time is extended on every flush and on Dispose.</summary>
    public void BeginSession(string command, DateTime start)
    {
        lock (_writeLock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT INTO sessions(start, end, command) VALUES($s, $s, $c) RETURNING id";
            cmd.Parameters.AddWithValue("$s", Iso(start));
            cmd.Parameters.AddWithValue("$c", command);
            _sessionId = (long)cmd.ExecuteScalar()!;
        }
    }

    public void Add(Finding f) => _queue.Enqueue(f);

    /// <summary>Writes queued findings and extends the current session to <paramref name="now"/>.</summary>
    public void Flush(DateTime? now = null)
    {
        lock (_writeLock)
        {
            if (_disposed) return;
            using var tx = _db.BeginTransaction();
            using var cmd = _db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO findings(time, kind, process, summary, evidence) VALUES($t, $k, $p, $s, $e)";
            var t = cmd.Parameters.Add("$t", SqliteType.Text);
            var k = cmd.Parameters.Add("$k", SqliteType.Text);
            var p = cmd.Parameters.Add("$p", SqliteType.Text);
            var s = cmd.Parameters.Add("$s", SqliteType.Text);
            var e = cmd.Parameters.Add("$e", SqliteType.Text);
            while (_queue.TryDequeue(out var f))
            {
                t.Value = Iso(f.Time);
                k.Value = f.Kind.ToString();
                p.Value = (object?)f.Process ?? DBNull.Value;
                s.Value = f.Summary;
                e.Value = JsonSerializer.Serialize(f.Evidence);
                cmd.ExecuteNonQuery();
            }
            if (_sessionId != 0)
            {
                using var end = _db.CreateCommand();
                end.Transaction = tx;
                end.CommandText = "UPDATE sessions SET end = $e WHERE id = $id";
                end.Parameters.AddWithValue("$e", Iso(now ?? DateTime.Now));
                end.Parameters.AddWithValue("$id", _sessionId);
                end.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public List<Finding> Findings(DateTime from, DateTime to)
    {
        lock (_writeLock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT time, kind, process, summary, evidence FROM findings WHERE time >= $f AND time < $t ORDER BY time";
            cmd.Parameters.AddWithValue("$f", Iso(from));
            cmd.Parameters.AddWithValue("$t", Iso(to));
            using var r = cmd.ExecuteReader();
            var list = new List<Finding>();
            while (r.Read())
            {
                if (!Enum.TryParse<FindingKind>(r.GetString(1), out var kind)) continue; // kind from a newer version
                list.Add(new Finding(Parse(r.GetString(0)), kind, r.GetString(3),
                    JsonSerializer.Deserialize<List<string>>(r.GetString(4)) ?? [], r.IsDBNull(2) ? null : r.GetString(2)));
            }
            return list;
        }
    }

    /// <summary>Programs the user chose not to see findings about (still recorded, just not shown or alerted).</summary>
    public HashSet<string> HiddenPrograms()
    {
        lock (_writeLock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT process FROM hidden";
            using var r = cmd.ExecuteReader();
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (r.Read()) set.Add(r.GetString(0));
            return set;
        }
    }

    public void SetHidden(string process, bool hidden)
    {
        lock (_writeLock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = hidden ? "INSERT OR IGNORE INTO hidden(process) VALUES($p)" : "DELETE FROM hidden WHERE process = $p";
            cmd.Parameters.AddWithValue("$p", process);
            cmd.ExecuteNonQuery();
        }
    }

    public List<WatchSession> Sessions(DateTime from, DateTime to)
    {
        lock (_writeLock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT id, start, end, command FROM sessions WHERE end >= $f AND start < $t ORDER BY start";
            cmd.Parameters.AddWithValue("$f", Iso(from));
            cmd.Parameters.AddWithValue("$t", Iso(to));
            using var r = cmd.ExecuteReader();
            var list = new List<WatchSession>();
            while (r.Read()) list.Add(new WatchSession(r.GetInt64(0), Parse(r.GetString(1)), Parse(r.GetString(2)), r.GetString(3)));
            return list;
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        Flush();
        lock (_writeLock)
        {
            _disposed = true;
            _db.Dispose();
        }
    }

    void Exec(string sql)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    // Local time, sortable as text.
    static string Iso(DateTime t) => t.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
    static DateTime Parse(string s) => DateTime.ParseExact(s, "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal);
}
