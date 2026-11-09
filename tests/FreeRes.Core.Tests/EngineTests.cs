using FreeRes.Core.Actions;
using FreeRes.Core.Decisions;
using FreeRes.Core.Engine;
using FreeRes.Core.Policy;

namespace FreeRes.Core.Tests;

/// <summary>Module de test dont on pilote les propositions, les actions automatiques et les pannes.</summary>
internal sealed class ScriptedModule(string name) : IResourceModule
{
    public string Name { get; } = name;

    public List<Proposal> Proposals { get; } = [];

    public List<ActionRequest> AutoActions { get; } = [];

    public bool Fail { get; set; }

    public Task<ModuleEvaluation> EvaluateAsync(EngineContext context, CancellationToken cancellationToken) =>
        Fail ? throw new InvalidOperationException("module en panne") : Task.FromResult(new ModuleEvaluation([.. Proposals], [.. AutoActions]));
}

public sealed class MonitorEngineTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeTime _time = new(new DateTimeOffset(2026, 11, 9, 10, 0, 0, TimeSpan.Zero));
    private readonly FakeProbe _probe;
    private readonly ScriptedModule _disk = new("disk");
    private readonly ScriptedModule _browser = new("browser");
    private readonly FakeElevation _elevation = new(admin: false);
    private readonly ExecutionCounter _executions = new();
    private readonly FreeResRuntime _rt;

    public MonitorEngineTests()
    {
        _probe = new FakeProbe(_time);
        _rt = FreeResRuntime.Create(new AppPaths(_dir.Path), _probe, _ => new ModuleSetup([_disk, _browser], c => TestActions.Register(c, _executions)), _elevation, _time);
    }

    private static Proposal P(string id, ActionRequest? action = null) => new(id, "Titre", "Detail", 1, Severity.Warn, action ?? TestActions.Clean());

    [Fact]
    public async Task Cycle_historise_et_cree_les_propositions_une_fois()
    {
        _disk.Proposals.Add(P("disk-a"));
        _browser.Proposals.Add(P("browser-a"));

        var r1 = await _rt.Engine.RunCycleAsync(default);
        Assert.Equal(2, r1.NewProposals);
        Assert.Equal(2, r1.PendingCount);
        Assert.Equal(EscalationMode.Toast, r1.Escalation);

        var r2 = await _rt.Engine.RunCycleAsync(default);
        Assert.Equal(0, r2.NewProposals);
        Assert.Equal(2, r2.PendingCount);
        Assert.Equal(2, _rt.Database.MetricsSince(_time.Now.AddDays(-1)).Count);
    }

    [Fact]
    public async Task Proposition_retiree_quand_sa_condition_disparait()
    {
        _disk.Proposals.Add(P("disk-a"));
        await _rt.Engine.RunCycleAsync(default);
        _disk.Proposals.Clear();
        var r = await _rt.Engine.RunCycleAsync(default);
        Assert.Equal(1, r.StaleRemoved);
        Assert.Equal(0, r.PendingCount);
    }

    [Fact]
    public async Task Module_en_panne_ses_propositions_sont_conservees_et_les_autres_continuent()
    {
        _disk.Proposals.Add(P("disk-a"));
        _browser.Proposals.Add(P("browser-a"));
        await _rt.Engine.RunCycleAsync(default);

        _disk.Fail = true;
        _browser.Proposals.Add(P("browser-b"));
        var r = await _rt.Engine.RunCycleAsync(default);

        Assert.Equal(0, r.StaleRemoved);
        Assert.Contains(r.Errors, e => e.Contains("disk", StringComparison.Ordinal) && e.Contains("module en panne", StringComparison.Ordinal));
        Assert.Equal(["browser-a", "browser-b", "disk-a"], _rt.Engine.Queue.Pending().Select(p => p.Proposal.Id).Order());
    }

    [Fact]
    public async Task Proposition_invalide_d_un_module_signalee_sans_bloquer_le_cycle()
    {
        _disk.Proposals.Add(P("evil", ActionRequest.Of("RunScript")));
        _disk.Proposals.Add(P("disk-ok"));
        var r = await _rt.Engine.RunCycleAsync(default);
        Assert.Contains(r.Errors, e => e.Contains("RunScript", StringComparison.Ordinal));
        Assert.Equal(["disk-ok"], _rt.Engine.Queue.Pending().Select(p => p.Proposal.Id));
    }

    [Fact]
    public async Task Actions_automatiques_executees_et_admin_refusee_sans_droits()
    {
        _disk.AutoActions.Add(TestActions.Clean());
        _disk.AutoActions.Add(ActionRequest.Of("TestAdmin"));
        var r = await _rt.Engine.RunCycleAsync(default);
        Assert.Equal(1, _executions.Count);
        Assert.Single(r.AutoActions);
        Assert.Contains(r.Errors, e => e.Contains("administrateur", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Approuver_execute_l_action_et_enregistre_la_decision()
    {
        _disk.Proposals.Add(P("disk-a"));
        await _rt.Engine.RunCycleAsync(default);

        var result = await _rt.Engine.ApproveAsync("disk-a", default);
        Assert.True(result.Ok);
        Assert.Equal(1, _executions.Count);
        Assert.Empty(_rt.Engine.Queue.Pending());
        Assert.Equal(DecisionKind.Approved, _rt.Database.GetDecision("disk-a")!.Kind);
        Assert.Contains(_rt.Database.MetricsSince(_time.Now.AddDays(-1)), m => m.Source == "approve");
    }

    [Fact]
    public async Task Approuver_une_action_admin_sans_droits_garde_la_proposition()
    {
        _disk.Proposals.Add(P("disk-admin", ActionRequest.Of("TestAdmin")));
        await _rt.Engine.RunCycleAsync(default);
        await Assert.ThrowsAsync<ActionRejectedException>(() => _rt.Engine.ApproveAsync("disk-admin", default));
        Assert.Single(_rt.Engine.Queue.Pending());
        Assert.Null(_rt.Database.GetDecision("disk-admin"));
    }

    [Fact]
    public async Task Rejet_empeche_la_reapparition_au_cycle_suivant()
    {
        _disk.Proposals.Add(P("disk-a"));
        await _rt.Engine.RunCycleAsync(default);
        _rt.Engine.Reject("disk-a");
        var r = await _rt.Engine.RunCycleAsync(default);
        Assert.Equal(0, r.NewProposals);
        Assert.Equal(0, r.PendingCount);
    }

    [Fact]
    public async Task Approuver_une_proposition_inconnue_signale_clairement() =>
        await Assert.ThrowsAsync<KeyNotFoundException>(() => _rt.Engine.ApproveAsync("absente", default));

    [Fact]
    public async Task Retention_au_plus_une_fois_par_jour()
    {
        await _rt.Engine.RunCycleAsync(default);
        var first = _rt.Database.GetState(MonitorEngine.LastRetentionKey);
        _time.Advance(TimeSpan.FromHours(2));
        await _rt.Engine.RunCycleAsync(default);
        Assert.Equal(first, _rt.Database.GetState(MonitorEngine.LastRetentionKey));

        _time.Advance(TimeSpan.FromDays(200));
        await _rt.Engine.RunCycleAsync(default);
        Assert.NotEqual(first, _rt.Database.GetState(MonitorEngine.LastRetentionKey));
        Assert.Single(_rt.Database.MetricsSince(_time.Now.AddDays(-400)));   // les 2 mesures de plus de 180 j purgees
    }

    [Fact]
    public async Task Etat_critique_avec_utilisateur_present_ouvre_la_fenetre()
    {
        _disk.Proposals.Add(P("disk-a"));
        _probe.RamAvailGB = 0.9;
        var r = await _rt.Engine.RunCycleAsync(default);
        Assert.Equal(EscalationMode.Window, r.Escalation);
    }

    public void Dispose()
    {
        _rt.Dispose();
        _dir.Dispose();
    }
}

public sealed class RuntimeTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeTime _time = new(new DateTimeOffset(2026, 11, 9, 10, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Seuils_personnalises_incoherents_remplaces_par_les_seuils_calcules_et_signales()
    {
        File.WriteAllText(Path.Combine(_dir.Path, "settings.json"), """{ "thresholds": { "ramAvailWarnGB": 1, "ramAvailCritGB": 2 } }""");
        using var rt = FreeResRuntime.Create(new AppPaths(_dir.Path), new FakeProbe(_time), time: _time);
        Assert.Contains(rt.SettingsErrors, e => e.Contains("RAM", StringComparison.Ordinal));
        Assert.Equal(ThresholdCalculator.FromProfile(rt.Engine.Profile), rt.Engine.Thresholds);
    }

    [Fact]
    public void Reglages_valides_appliques_sans_erreur()
    {
        File.WriteAllText(Path.Combine(_dir.Path, "settings.json"), """{ "monitorIntervalMinutes": 15, "thresholds": { "diskFreeWarnGB": 50 } }""");
        using var rt = FreeResRuntime.Create(new AppPaths(_dir.Path), new FakeProbe(_time), time: _time);
        Assert.Empty(rt.SettingsErrors);
        Assert.Equal(15, rt.Engine.Settings.MonitorIntervalMinutes);
        Assert.Equal(50, rt.Engine.Thresholds.DiskFreeWarnGB);
    }

    public void Dispose() => _dir.Dispose();
}
