using FreeRes.Core.Decisions;
using FreeRes.Core.Logging;
using FreeRes.Core.Storage;
using Microsoft.Data.Sqlite;

namespace FreeRes.Core.Tests;

/// <summary>
/// Parite Pester : « Historique : evolution du schema » (un schema incompatible ne doit jamais desaligner ni
/// corrompre les donnees) et « sauvegarde l'etat plusieurs fois de suite ».
/// </summary>
public sealed class FreeResDatabaseTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeTime _time = new(new DateTimeOffset(2026, 11, 9, 10, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Migration_idempotente_et_donnees_persistees_entre_ouvertures()
    {
        var path = _dir.File("db.sqlite");
        using (var db = new FreeResDatabase(path))
        {
            db.SetState("k", "v1");
            db.SetState("k", "v2");   // regression PowerShell : sauvegardes successives
        }

        using (var db = new FreeResDatabase(path))
        {
            Assert.Equal("v2", db.GetState("k"));
            db.SetState("k", null);
            Assert.Null(db.GetState("k"));
        }
    }

    [Fact]
    public void Base_creee_par_une_version_plus_recente_refusee_sans_modification()
    {
        var path = _dir.File("future.sqlite");
        using (var c = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "CREATE TABLE futur (x INTEGER); PRAGMA user_version = 99;";
            cmd.ExecuteNonQuery();
        }

        var ex = Assert.Throws<DatabaseVersionException>(() => new FreeResDatabase(path));
        Assert.Contains("plus recente", ex.Message, StringComparison.Ordinal);

        using var check = new SqliteConnection($"Data Source={path};Pooling=False");
        check.Open();
        using var q = check.CreateCommand();
        q.CommandText = "SELECT count(*) FROM sqlite_master WHERE name = 'metrics'";
        Assert.Equal(0L, q.ExecuteScalar());   // aucune table creee : la base n'a pas ete touchee
    }

    [Fact]
    public void Historique_insere_lu_et_purge_par_date()
    {
        using var db = new FreeResDatabase(_dir.File("db.sqlite"));
        var probe = new FakeProbe(_time);
        db.AddMetrics(probe.Capture(), "monitor");
        _time.Advance(TimeSpan.FromDays(10));
        db.AddMetrics(probe.Capture(), "approve", 1.234567);

        var all = db.MetricsSince(_time.Now.AddDays(-30));
        Assert.Equal(2, all.Count);
        Assert.Equal("firefox", all[0].TopProcess);
        Assert.Equal(1.235, all[1].FreedGB);

        Assert.Equal(1, db.DeleteMetricsBefore(_time.Now.AddDays(-5)));
        Assert.Single(db.MetricsSince(_time.Now.AddDays(-30)));
    }

    [Fact]
    public void Proposition_avec_parametres_relue_a_l_identique()
    {
        // Regression : ActionRequest a 2 constructeurs n'etait pas relisible en JSON (aucune proposition relue).
        using var db = new FreeResDatabase(_dir.File("db.sqlite"));
        var action = new Actions.ActionRequest("TestClean", new Dictionary<string, string> { ["days"] = "7", ["path"] = @"%TEMP%\x" });
        var p = new Proposal("disk-temp", "Titre « accentue »", "Detail", 2.5, Severity.Crit, action);
        db.UpsertPending(new PendingProposal(p, true, _time.Now, _time.Now));

        var back = db.GetPending("disk-temp");
        Assert.NotNull(back);
        Assert.Equal(p with { Action = back.Proposal.Action }, back.Proposal);
        Assert.Equal("TestClean", back.Proposal.Action.Type);
        Assert.Equal(action.Parameters, back.Proposal.Action.Parameters);
        Assert.True(back.RequiresAdmin);
        Assert.Equal(_time.Now, back.CreatedAt);
    }

    [Fact]
    public void Decision_ecrasee_par_la_plus_recente()
    {
        using var db = new FreeResDatabase(_dir.File("db.sqlite"));
        db.SetDecision(new DecisionRecord("abc", DecisionKind.Rejected, _time.Now, _time.Now.AddDays(7)));
        db.SetDecision(new DecisionRecord("abc", DecisionKind.Approved, _time.Now, null));
        var d = db.GetDecision("abc");
        Assert.NotNull(d);
        Assert.Equal(DecisionKind.Approved, d.Kind);
        Assert.Null(d.Until);
    }

    public void Dispose() => _dir.Dispose();
}

public sealed class EventLogTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeTime _time = new(new DateTimeOffset(2026, 11, 9, 10, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Ecrit_en_JSONL_mensuel_et_relit_dans_l_ordre()
    {
        var log = TestActions.Log(_dir, _time);
        log.Write(LogLevel.Info, "a", "premier");
        log.Write(LogLevel.Error, "b", "second", new Dictionary<string, object?> { ["x"] = 1 });
        Assert.True(File.Exists(Path.Combine(_dir.Path, "logs", "events-2026-11.jsonl")));

        var recent = log.ReadRecent(10);
        Assert.Equal(["premier", "second"], recent.Select(e => e.Message));
        Assert.Equal(LogLevel.Error, recent[1].Level);
    }

    [Fact]
    public void Ligne_tronquee_par_un_arret_brutal_ignoree()
    {
        var log = TestActions.Log(_dir, _time);
        log.Write(LogLevel.Info, "a", "valide");
        File.AppendAllText(log.PathFor(_time.Now), "{\"ts\":\"2026-11-09T10:00");
        Assert.Equal(["valide"], log.ReadRecent(10).Select(e => e.Message));
    }

    [Fact]
    public void Purge_les_journaux_trop_anciens_seulement()
    {
        var log = TestActions.Log(_dir, _time);
        log.Write(LogLevel.Info, "a", "recent");
        var old = Path.Combine(_dir.Path, "logs", "events-2026-01.jsonl");
        File.WriteAllText(old, "{}\n");
        File.SetLastWriteTimeUtc(old, _time.Now.UtcDateTime.AddDays(-90));

        Assert.Equal(1, log.Purge(60));
        Assert.False(File.Exists(old));
        Assert.Single(log.ReadRecent(10));
    }

    public void Dispose() => _dir.Dispose();
}
