using FreeRes.Core.Actions;
using FreeRes.Core.Decisions;
using FreeRes.Core.Storage;

namespace FreeRes.Core.Tests;

/// <summary>
/// Parite Pester : « File d'approbation » (dedoublonnage, rejet respecte, type non allowliste refuse,
/// identifiant non conforme refuse, marquage admin, propositions perimees retirees) et
/// « Dispatcher d'actions » (action inconnue refusee).
/// </summary>
public sealed class DecisionInboxTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeTime _time = new(new DateTimeOffset(2026, 11, 9, 10, 0, 0, TimeSpan.Zero));
    private readonly FreeResDatabase _db;
    private readonly DecisionInbox _inbox;

    public DecisionInboxTests()
    {
        _db = new FreeResDatabase(_dir.File("db.sqlite"));
        var catalog = new ActionCatalog();
        TestActions.Register(catalog);
        _inbox = new DecisionInbox(_db, catalog, _time, TestActions.Log(_dir, _time));
    }

    private static Proposal P(string id, Severity sev = Severity.Warn, double gain = 1, ActionRequest? action = null) =>
        new(id, "Titre " + id, "Detail", gain, sev, action ?? TestActions.Clean());

    [Fact]
    public void Cree_une_proposition_une_seule_fois()
    {
        Assert.True(_inbox.Propose(P("test-a")));
        _time.Advance(TimeSpan.FromMinutes(5));
        Assert.False(_inbox.Propose(P("test-a")));
        var p = Assert.Single(_inbox.Pending());
        Assert.True(p.UpdatedAt > p.CreatedAt);   // rafraichie, date de creation conservee
    }

    [Fact]
    public void Rejet_respecte_jusqu_a_echeance_puis_proposable_a_nouveau()
    {
        _inbox.Propose(P("test-a"));
        _inbox.Decide("test-a", DecisionKind.Rejected, TimeSpan.FromHours(24));
        Assert.Empty(_inbox.Pending());

        _time.Advance(TimeSpan.FromHours(23));
        Assert.False(_inbox.Propose(P("test-a")));
        Assert.Empty(_inbox.Pending());

        _time.Advance(TimeSpan.FromHours(2));
        Assert.True(_inbox.Propose(P("test-a")));
    }

    [Fact]
    public void Report_respecte_jusqu_a_echeance()
    {
        _inbox.Propose(P("test-b"));
        _inbox.Decide("test-b", DecisionKind.Snoozed, TimeSpan.FromHours(1));
        Assert.False(_inbox.Propose(P("test-b")));
        _time.Advance(TimeSpan.FromMinutes(61));
        Assert.True(_inbox.Propose(P("test-b")));
    }

    [Fact]
    public void Rejet_sans_duree_refuse() =>
        Assert.Throws<ArgumentException>(() => _inbox.Decide("x-y-z", DecisionKind.Rejected));

    [Fact]
    public void Type_d_action_non_allowliste_refuse()
    {
        var ex = Assert.Throws<ActionRejectedException>(() => _inbox.Propose(P("evil", action: new ActionRequest("RunScript", new Dictionary<string, string> { ["code"] = "rm -r C:\\" }))));
        Assert.Contains("inconnue", ex.Message, StringComparison.Ordinal);
        Assert.Empty(_inbox.Pending());
    }

    [Fact]
    public void Parametres_invalides_refuses()
    {
        var ex = Assert.Throws<ActionRejectedException>(() => _inbox.Propose(P("test-c", action: TestActions.Clean("abc"))));
        Assert.Contains("days doit être un entier", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"..\..\x")]
    [InlineData("AB")]
    [InlineData("Majuscules")]
    [InlineData("")]
    public void Identifiant_non_conforme_refuse(string id)
    {
        var ex = Assert.Throws<ArgumentException>(() => _inbox.Propose(P(id)));
        Assert.Contains("Identifiant de proposition invalide", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Marque_admin_selon_le_catalogue()
    {
        _inbox.Propose(P("test-admin", action: ActionRequest.Of("TestAdmin")));
        _inbox.Propose(P("test-user"));
        Assert.True(_inbox.Get("test-admin")!.RequiresAdmin);
        Assert.False(_inbox.Get("test-user")!.RequiresAdmin);
    }

    [Fact]
    public void Retire_les_propositions_dont_la_condition_a_disparu()
    {
        _inbox.Propose(P("garde"));
        _inbox.Propose(P("perimee"));
        Assert.Equal(1, _inbox.RemoveStale(new HashSet<string> { "garde" }));
        Assert.Equal(["garde"], _inbox.Pending().Select(p => p.Proposal.Id));
    }

    [Fact]
    public void Ordre_critique_d_abord_puis_gain_decroissant()
    {
        _inbox.Propose(P("info-gros", Severity.Info, 50));
        _inbox.Propose(P("warn-petit", Severity.Warn, 1));
        _inbox.Propose(P("crit-x", Severity.Crit, 0));
        _inbox.Propose(P("warn-gros", Severity.Warn, 9));
        Assert.Equal(["crit-x", "warn-gros", "warn-petit", "info-gros"], _inbox.Pending().Select(p => p.Proposal.Id));
    }

    public void Dispose()
    {
        _db.Dispose();
        _dir.Dispose();
    }
}

public sealed class ActionDispatcherTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeTime _time = new(new DateTimeOffset(2026, 11, 9, 10, 0, 0, TimeSpan.Zero));
    private readonly ActionCatalog _catalog = new();
    private readonly FakeElevation _elevation = new(admin: false);
    private double _free = 40;

    public ActionDispatcherTests() => TestActions.Register(_catalog);

    private ActionDispatcher Dispatcher() => new(_catalog, _elevation, () => _free, TestActions.Log(_dir, _time));

    [Fact]
    public async Task Action_inconnue_refusee_avant_execution()
    {
        var ex = await Assert.ThrowsAsync<ActionRejectedException>(() => Dispatcher().ExecuteAsync(ActionRequest.Of("Format"), false, default));
        Assert.Contains("inconnue", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Action_admin_refusee_sans_droits_mais_simulable()
    {
        var ex = await Assert.ThrowsAsync<ActionRejectedException>(() => Dispatcher().ExecuteAsync(ActionRequest.Of("TestAdmin"), false, default));
        Assert.Contains("administrateur", ex.Message, StringComparison.Ordinal);

        var sim = await Dispatcher().ExecuteAsync(ActionRequest.Of("TestAdmin"), dryRun: true, default);
        Assert.True(sim.Ok);
        Assert.True(sim.DryRun);

        _elevation.IsAdministrator = true;
        Assert.True((await Dispatcher().ExecuteAsync(ActionRequest.Of("TestAdmin"), false, default)).Ok);
    }

    [Fact]
    public async Task Panne_du_module_rend_un_echec_sans_planter_et_journalise()
    {
        var r = await Dispatcher().ExecuteAsync(ActionRequest.Of("TestBoom"), false, default);
        Assert.False(r.Ok);
        Assert.Contains("panne du module", r.Message, StringComparison.Ordinal);
        Assert.Contains(TestActions.Log(_dir, _time).ReadRecent(5), e => e.Level == Logging.LogLevel.Error && e.Message.Contains("TestBoom", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Gain_disque_mesure_avant_apres()
    {
        _catalog.Register(new ActionDefinition("TestFree", false, _ => null, (_, _, _) =>
        {
            _free += 2.5;
            return Task.FromResult(new ActionOutcome(true, "libere"));
        }));
        var r = await Dispatcher().ExecuteAsync(ActionRequest.Of("TestFree"), false, default);
        Assert.Equal(2.5, r.DiskDeltaGB);
    }

    [Theory]
    [InlineData("minuscule")]
    [InlineData("Ab")]
    [InlineData("Avec Espace")]
    public void Type_d_action_mal_forme_refuse_a_l_enregistrement(string type) =>
        Assert.Throws<ArgumentException>(() => _catalog.Register(new ActionDefinition(type, false, _ => null, (_, _, _) => Task.FromResult(new ActionOutcome(true, "")))));

    [Fact]
    public void Type_d_action_en_double_refuse() =>
        Assert.Throws<InvalidOperationException>(() => TestActions.Register(_catalog));

    public void Dispose() => _dir.Dispose();
}
