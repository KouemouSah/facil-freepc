using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using FreeRes.Core.Decisions;
using FreeRes.Core.Metrics;
using Microsoft.Data.Sqlite;

namespace FreeRes.Core.Storage;

/// <summary>Une mesure historisee (sert aux tendances : pente disque, pression memoire).</summary>
public sealed record MetricsRow(DateTimeOffset Timestamp, double RamAvailGB, double CommitPct, double DiskFreeGB, string TopProcess, double TopProcessGB, string Source, double FreedGB);

/// <summary>Base dont le schema est plus recent que cette version de l'application.</summary>
public sealed class DatabaseVersionException(string message) : Exception(message);

/// <summary>
/// Stockage local SQLite (%LOCALAPPDATA%\FacilFreePC\freeres.db) : historique des mesures, propositions en
/// attente, decisions, etat du moteur. Schema versionne (PRAGMA user_version) avec migrations ; une base
/// creee par une version PLUS RECENTE est refusee plutot que corrompue.
/// Acces serialise (verrou) : le moteur et le serveur web l'utilisent depuis des threads differents.
/// </summary>
public sealed class FreeResDatabase : IDisposable
{
    public const int SchemaVersion = 1;

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly SqliteConnection _db;
    private readonly Lock _gate = new();

    public FreeResDatabase(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = path;
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        _db.Open();
        Execute("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON;");
        Migrate();
    }

    public string Path { get; }

    private void Migrate()
    {
        var version = Convert.ToInt32(Scalar("PRAGMA user_version"), CultureInfo.InvariantCulture);
        if (version > SchemaVersion)
        {
            throw new DatabaseVersionException(
                $"La base {Path} a ete creee par une version plus recente (schema {version} > {SchemaVersion}). Mettez Facil FreePC a jour.");
        }

        if (version < 1)
        {
            using var tx = _db.BeginTransaction();
            Execute("""
                CREATE TABLE metrics (
                  ts TEXT NOT NULL, ram_avail_gb REAL NOT NULL, commit_pct REAL NOT NULL, disk_free_gb REAL NOT NULL,
                  top_process TEXT NOT NULL, top_process_gb REAL NOT NULL, source TEXT NOT NULL, freed_gb REAL NOT NULL);
                CREATE INDEX ix_metrics_ts ON metrics(ts);
                CREATE TABLE proposals (
                  id TEXT PRIMARY KEY, json TEXT NOT NULL, requires_admin INTEGER NOT NULL, created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
                CREATE TABLE decisions (
                  id TEXT PRIMARY KEY, kind TEXT NOT NULL, at TEXT NOT NULL, until TEXT NULL);
                CREATE TABLE state (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                PRAGMA user_version = 1;
                """, tx);
            tx.Commit();
        }
    }

    // ------------------------------------------------------------------ mesures

    public void AddMetrics(SystemSnapshot s, string source, double freedGB = 0)
    {
        ArgumentNullException.ThrowIfNull(s);
        var top = s.TopProcesses.Count > 0 ? s.TopProcesses[0] : new ProcessUsage(string.Empty, 0, 0);
        Execute(
            "INSERT INTO metrics VALUES ($ts, $ram, $commit, $disk, $top, $topgb, $src, $freed)",
            null,
            ("$ts", Iso(s.Timestamp)),
            ("$ram", s.RamAvailGB),
            ("$commit", s.CommitPct),
            ("$disk", s.SystemDriveFreeGB),
            ("$top", top.Name),
            ("$topgb", top.WorkingSetGB),
            ("$src", source),
            ("$freed", Math.Round(freedGB, 3)));
    }

    public IReadOnlyList<MetricsRow> MetricsSince(DateTimeOffset since)
    {
        lock (_gate)
        {
            using var cmd = Command("SELECT ts, ram_avail_gb, commit_pct, disk_free_gb, top_process, top_process_gb, source, freed_gb FROM metrics WHERE ts >= $since ORDER BY ts", null, ("$since", Iso(since)));
            using var r = cmd.ExecuteReader();
            var rows = new List<MetricsRow>();
            while (r.Read())
            {
                rows.Add(new MetricsRow(Parse(r.GetString(0)), r.GetDouble(1), r.GetDouble(2), r.GetDouble(3), r.GetString(4), r.GetDouble(5), r.GetString(6), r.GetDouble(7)));
            }

            return rows;
        }
    }

    public int DeleteMetricsBefore(DateTimeOffset cut) => Execute("DELETE FROM metrics WHERE ts < $cut", null, ("$cut", Iso(cut)));

    // ------------------------------------------------------------------ propositions

    public PendingProposal? GetPending(string id)
    {
        lock (_gate)
        {
            using var cmd = Command("SELECT json, requires_admin, created_at, updated_at FROM proposals WHERE id = $id", null, ("$id", id));
            using var r = cmd.ExecuteReader();
            return r.Read() ? ReadPending(r) : null;
        }
    }

    public IReadOnlyList<PendingProposal> ListPending()
    {
        lock (_gate)
        {
            using var cmd = Command("SELECT json, requires_admin, created_at, updated_at FROM proposals", null);
            using var r = cmd.ExecuteReader();
            var list = new List<PendingProposal>();
            while (r.Read())
            {
                list.Add(ReadPending(r));
            }

            return list;
        }
    }

    public void UpsertPending(PendingProposal p)
    {
        ArgumentNullException.ThrowIfNull(p);
        Execute(
            """
            INSERT INTO proposals (id, json, requires_admin, created_at, updated_at) VALUES ($id, $json, $admin, $created, $updated)
            ON CONFLICT(id) DO UPDATE SET json = excluded.json, requires_admin = excluded.requires_admin, updated_at = excluded.updated_at
            """,
            null,
            ("$id", p.Proposal.Id),
            ("$json", JsonSerializer.Serialize(p.Proposal, Json)),
            ("$admin", p.RequiresAdmin ? 1 : 0),
            ("$created", Iso(p.CreatedAt)),
            ("$updated", Iso(p.UpdatedAt)));
    }

    public bool RemovePending(string id) => Execute("DELETE FROM proposals WHERE id = $id", null, ("$id", id)) > 0;

    private static PendingProposal ReadPending(SqliteDataReader r) => new(
        JsonSerializer.Deserialize<Proposal>(r.GetString(0), Json) ?? throw new InvalidDataException("Proposition illisible"),
        r.GetInt64(1) != 0,
        Parse(r.GetString(2)),
        Parse(r.GetString(3)));

    // ------------------------------------------------------------------ decisions

    public DecisionRecord? GetDecision(string id)
    {
        lock (_gate)
        {
            using var cmd = Command("SELECT kind, at, until FROM decisions WHERE id = $id", null, ("$id", id));
            using var r = cmd.ExecuteReader();
            if (!r.Read())
            {
                return null;
            }

            return new DecisionRecord(id, Enum.Parse<DecisionKind>(r.GetString(0)), Parse(r.GetString(1)), r.IsDBNull(2) ? null : Parse(r.GetString(2)));
        }
    }

    public void SetDecision(DecisionRecord d)
    {
        ArgumentNullException.ThrowIfNull(d);
        Execute(
            "INSERT INTO decisions (id, kind, at, until) VALUES ($id, $kind, $at, $until) ON CONFLICT(id) DO UPDATE SET kind = excluded.kind, at = excluded.at, until = excluded.until",
            null,
            ("$id", d.ProposalId),
            ("$kind", d.Kind.ToString()),
            ("$at", Iso(d.At)),
            ("$until", d.Until is { } u ? Iso(u) : DBNull.Value));
    }

    public int DeleteDecisionsBefore(DateTimeOffset cut) => Execute("DELETE FROM decisions WHERE at < $cut", null, ("$cut", Iso(cut)));

    // ------------------------------------------------------------------ etat cle/valeur

    public string? GetState(string key)
    {
        lock (_gate)
        {
            using var cmd = Command("SELECT value FROM state WHERE key = $k", null, ("$k", key));
            return cmd.ExecuteScalar() as string;
        }
    }

    public void SetState(string key, string? value)
    {
        if (value is null)
        {
            Execute("DELETE FROM state WHERE key = $k", null, ("$k", key));
            return;
        }

        Execute("INSERT INTO state (key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value", null, ("$k", key), ("$v", value));
    }

    // ------------------------------------------------------------------ outils

    internal static string Iso(DateTimeOffset value) => value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    internal static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    private object? Scalar(string sql)
    {
        lock (_gate)
        {
            using var cmd = Command(sql, null);
            return cmd.ExecuteScalar();
        }
    }

    private int Execute(string sql, SqliteTransaction? tx = null, params (string Name, object Value)[] parameters)
    {
        lock (_gate)
        {
            using var cmd = Command(sql, tx, parameters);
            return cmd.ExecuteNonQuery();
        }
    }

    private SqliteCommand Command(string sql, SqliteTransaction? tx, params (string Name, object Value)[] parameters)
    {
        var cmd = _db.CreateCommand();
#pragma warning disable CA2100 // Requetes constantes du code ; toute valeur passe par des parametres.
        cmd.CommandText = sql;
#pragma warning restore CA2100
        cmd.Transaction = tx;
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        return cmd;
    }

    public void Dispose()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();
    }
}
