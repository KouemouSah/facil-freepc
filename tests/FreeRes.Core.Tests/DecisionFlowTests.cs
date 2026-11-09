using FreeRes.Core.Actions;
using FreeRes.Core.Decisions;
using FreeRes.Core.Engine;
using FreeRes.Core.Logging;
using FreeRes.Core.Policy;

namespace FreeRes.Core.Tests;

/// <summary>Module exposant un etat a l'interface.</summary>
internal sealed class StatusModule : IResourceModule, IStatusSource
{
    public string Name => "statut";

    public object? CurrentStatus { get; set; } = new { valeur = 42 };

    public Task<ModuleEvaluation> EvaluateAsync(EngineContext context, CancellationToken cancellationToken) => Task.FromResult(ModuleEvaluation.Empty);
}

/// <summary>Escalade reellement appliquee, decisions une par une, journal exploitable par l'historique.</summary>
public sealed class DecisionFlowTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeTime _time = new(new DateTimeOffset(2026, 11, 9, 10, 0, 0, TimeSpan.Zero));
    private readonly FakeProbe _probe;
    private readonly ScriptedModule _module = new("disk");
    private readonly StatusModule _status = new();
    private readonly ExecutionCounter _executions = new();
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly FreeResRuntime _rt;

    public DecisionFlowTests()
    {
        _probe = new FakeProbe(_time);
        _rt = FreeResRuntime.Create(new AppPaths(_dir.Path), _probe, _ => new ModuleSetup([_module, _status], c =>
        {
            TestActions.Register(c, _executions);
            c.Register(new ActionDefinition("TestSlow", false, _ => null, async (_, _, ct) =>
            {
                _executions.Increment();
                await _release.Task.WaitAsync(ct);
                return new ActionOutcome(true, "lent fait");
            }));
        }), new FakeElevation(admin: false), _time);
    }

    private static Proposal P(string id, string action = "TestClean") => new(id, "Titre " + id, "Detail", 1, Severity.Warn, ActionRequest.Of(action));

    [Fact]
    public async Task Notification_presentee_demarre_le_delai_anti_repetition()
    {
        _module.Proposals.Add(P("disk-a"));
        Assert.Equal(EscalationMode.Toast, (await _rt.Engine.RunCycleAsync(default)).Escalation);

        // Sans presentation (application sans interface), le rappel reste du : le delai ne demarre pas.
        _time.Advance(TimeSpan.FromHours(3));
        Assert.Equal(EscalationMode.Toast, (await _rt.Engine.RunCycleAsync(default)).Escalation);

        _rt.Engine.RecordEscalation(EscalationMode.Toast);
        _time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(EscalationMode.None, (await _rt.Engine.RunCycleAsync(default)).Escalation);
    }

    [Fact]
    public async Task Fenetre_critique_presentee_n_est_pas_rouverte_a_chaque_cycle()
    {
        _probe.RamAvailGB = 0.5;
        _module.Proposals.Add(P("disk-a"));
        Assert.Equal(EscalationMode.Window, (await _rt.Engine.RunCycleAsync(default)).Escalation);
        _rt.Engine.RecordEscalation(EscalationMode.Window);
        _time.Advance(TimeSpan.FromMinutes(5));
        Assert.NotEqual(EscalationMode.Window, (await _rt.Engine.RunCycleAsync(default)).Escalation);
    }

    [Fact]
    public async Task Fin_de_cycle_notifiee_et_abonne_defaillant_sans_effet_sur_le_cycle()
    {
        CycleReport? seen = null;
        _rt.Engine.CycleCompleted += (_, r) => seen = r;
        _rt.Engine.CycleCompleted += (_, _) => throw new InvalidOperationException("interface en panne");

        var report = await _rt.Engine.RunCycleAsync(default);

        Assert.Same(report, seen);
        Assert.Contains(_rt.Log.ReadRecent(20), e => e.Level == LogLevel.Error && e.Message.Contains("interface en panne", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Double_approbation_simultanee_une_seule_execution()
    {
        _module.Proposals.Add(P("disk-slow", "TestSlow"));
        await _rt.Engine.RunCycleAsync(default);

        var first = _rt.Engine.ApproveAsync("disk-slow", default);
        Assert.True(_rt.Engine.DecisionInProgress);
        var second = _rt.Engine.ApproveAsync("disk-slow", default);
        _release.SetResult();

        Assert.True((await first).Ok);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => second);
        Assert.Equal(1, _executions.Count);
        Assert.False(_rt.Engine.DecisionInProgress);
    }

    [Fact]
    public async Task Report_et_rejet_d_une_proposition_deja_traitee_refuses()
    {
        _module.Proposals.Add(P("disk-a"));
        _module.Proposals.Add(P("disk-b"));
        await _rt.Engine.RunCycleAsync(default);

        _rt.Engine.Snooze("disk-a");
        _rt.Engine.Reject("disk-b");

        Assert.Empty(_rt.Engine.Queue.Pending());
        Assert.Throws<KeyNotFoundException>(() => _rt.Engine.Snooze("disk-a"));
        Assert.Throws<KeyNotFoundException>(() => _rt.Engine.Reject("inconnue"));
        Assert.Equal(0, _executions.Count);
    }

    [Fact]
    public async Task Journal_distingue_actions_automatiques_et_decisions_avec_leur_titre()
    {
        _module.AutoActions.Add(TestActions.Clean());
        _module.Proposals.Add(P("disk-a"));
        await _rt.Engine.RunCycleAsync(default);
        await _rt.Engine.ApproveAsync("disk-a", default);

        var actions = _rt.Log.ReadRecent(50, e => e.Source == "action");
        Assert.Equal(2, actions.Count);
        Assert.Equal("True", actions[0].Data!["auto"]!.ToString());    // cycle : automatique
        Assert.Equal("False", actions[1].Data!["auto"]!.ToString());   // approbation : utilisateur

        var decision = Assert.Single(_rt.Log.ReadRecent(50, e => e.Source == "decision"));
        Assert.Equal("Appliquée : Titre disk-a", decision.Message);
    }

    [Fact]
    public void Lecture_filtree_du_journal_ignore_les_evenements_non_retenus()
    {
        for (var i = 0; i < 30; i++)
        {
            _rt.Log.Write(LogLevel.Info, "monitor", "cycle " + i);
        }

        _rt.Log.Write(LogLevel.Action, "action", "ancienne action");
        for (var i = 0; i < 30; i++)
        {
            _rt.Log.Write(LogLevel.Info, "monitor", "cycle bis " + i);
        }

        var found = Assert.Single(_rt.Log.ReadRecent(5, e => e.Source == "action"));
        Assert.Equal("ancienne action", found.Message);
    }

    [Fact]
    public void Sources_d_etat_exposees_par_le_moteur()
    {
        var source = Assert.Single(_rt.Engine.StatusSources);
        Assert.Equal("statut", source.Name);
        Assert.NotNull(source.CurrentStatus);
    }

    public void Dispose()
    {
        _rt.Dispose();
        _dir.Dispose();
    }
}
