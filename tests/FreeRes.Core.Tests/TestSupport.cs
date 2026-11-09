using FreeRes.Core.Actions;
using FreeRes.Core.Logging;
using FreeRes.Core.Metrics;

namespace FreeRes.Core.Tests;

/// <summary>Horloge pilotable : les regles de delai (rejet, report, retention) se testent sans attendre.</summary>
internal sealed class FakeTime(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public void Advance(TimeSpan by) => Now += by;
}

/// <summary>Mesures fixes (modifiables) : machine de reference du diagnostic 2026-11-08.</summary>
internal sealed class FakeProbe(TimeProvider time) : ISystemProbe
{
    public double RamAvailGB { get; set; } = 6.0;

    public double CommitPct { get; set; } = 50;

    public double DiskFreeGB { get; set; } = 43.9;

    public double IdleMinutes { get; set; } = 1;

    public int Captures { get; private set; }

    public SystemSnapshot Capture()
    {
        Captures++;
        return new SystemSnapshot(
            time.GetLocalNow(), 15.69, RamAvailGB, 60, 23.69, Math.Round(23.69 * CommitPct / 100, 2), CommitPct,
            "C:", 235.48, DiskFreeGB, IdleMinutes, false,
            [new ProcessUsage("firefox", 30, 4.2), new ProcessUsage("node", 3, 0.9)]);
    }
}

/// <summary>Droits pilotables.</summary>
internal sealed class FakeElevation(bool admin) : IElevation
{
    public bool IsAdministrator { get; set; } = admin;
}

/// <summary>Dossier temporaire supprime a la fin du test.</summary>
internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "freeres-tests-" + Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // fichier encore ouvert par un test en echec : le dossier temporaire sera nettoye par le systeme
        }
    }
}

/// <summary>Compteur d'executions PROPRE a chaque test (xUnit execute les classes en parallele).</summary>
internal sealed class ExecutionCounter
{
    private int _count;

    public int Count => Volatile.Read(ref _count);

    public void Increment() => Interlocked.Increment(ref _count);
}

/// <summary>Actions de test enregistrees dans un catalogue : « TestClean » (utilisateur), « TestAdmin » (admin), « TestBoom » (panne).</summary>
internal static class TestActions
{
    public static void Register(ActionCatalog catalog) => Register(catalog, new ExecutionCounter());

    public static void Register(ActionCatalog catalog, ExecutionCounter counter)
    {
        catalog.Register(new ActionDefinition(
            "TestClean",
            RequiresAdmin: false,
            p => p.TryGetValue("days", out var d) && !int.TryParse(d, out _) ? "days doit etre un entier" : null,
            (_, dryRun, _) =>
            {
                if (!dryRun)
                {
                    counter.Increment();
                }

                return Task.FromResult(new ActionOutcome(true, dryRun ? "simulation" : "fait"));
            }));
        catalog.Register(new ActionDefinition(
            "TestAdmin",
            RequiresAdmin: true,
            _ => null,
            (_, _, _) => Task.FromResult(new ActionOutcome(true, "admin fait"))));
        catalog.Register(new ActionDefinition(
            "TestBoom",
            RequiresAdmin: false,
            _ => null,
            (_, _, _) => throw new InvalidOperationException("panne du module")));
    }

    public static ActionRequest Clean(string? days = null) =>
        days is null ? ActionRequest.Of("TestClean") : new ActionRequest("TestClean", new Dictionary<string, string> { ["days"] = days });

    public static EventLog Log(TempDir dir, TimeProvider time) => new(System.IO.Path.Combine(dir.Path, "logs"), time);
}
