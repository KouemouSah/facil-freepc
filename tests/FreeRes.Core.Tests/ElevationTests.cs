using FreeRes.Core.Actions;
using FreeRes.Core.Decisions;
using FreeRes.Core.Engine;

namespace FreeRes.Core.Tests;

/// <summary>Faux executeur UAC : enregistre les demandes, peut simuler un refus de l'utilisateur.</summary>
internal sealed class FakeElevatedRunner : IElevatedRunner
{
    public List<ActionRequest> Requests { get; } = [];

    public bool UserCancels { get; set; }

    public Task<ActionResult> RunAsync(ActionRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(UserCancels
            ? new ActionResult(request.Type, false, $"{MonitorEngine.ElevationCancelledPrefix} par l'utilisateur", 0, false)
            : new ActionResult(request.Type, true, "fait en eleve", 1.5, false));
    }
}

/// <summary>Observateur de test (comme le module Windows qui memorise la date du dernier DISM).</summary>
internal sealed class ObservingModule : IResourceModule, IActionObserver
{
    public string Name => "observer";

    public List<Proposal> Proposals { get; } = [];

    public List<(ActionRequest Request, ActionResult Result)> Completed { get; } = [];

    public Task<ModuleEvaluation> EvaluateAsync(EngineContext context, CancellationToken cancellationToken) =>
        Task.FromResult(new ModuleEvaluation([.. Proposals], []));

    public void OnActionCompleted(ActionRequest request, ActionResult result) => Completed.Add((request, result));
}

public sealed class ElevatedActionProtocolTests : IDisposable
{
    private readonly TempDir _dir = new();

    [Fact]
    public void Requete_encodee_en_base64url_puis_relue_a_l_identique()
    {
        var req = new ActionRequest("CleanSystemZones", new Dictionary<string, string> { ["rule"] = "x \"y\" ; & | é" });
        var encoded = ElevatedActionProtocol.Encode(req);
        Assert.Matches("^[A-Za-z0-9_-]+$", encoded);   // aucun caractere interpretable par la ligne de commande
        var back = ElevatedActionProtocol.Decode(encoded);
        Assert.NotNull(back);
        Assert.Equal(req.Type, back.Type);
        Assert.Equal(req.Parameters, back.Parameters);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("!!!pas-du-base64!!!")]
    [InlineData("bnVsbA")]          // « null »
    [InlineData("eyJ0eXBlIjoxfQ")]  // {"type":1}
    public void Entree_hostile_ou_mal_formee_rejetee_sans_exception(string? encoded) =>
        Assert.Null(ElevatedActionProtocol.Decode(encoded));

    [Fact]
    public void Requete_trop_longue_rejetee() =>
        Assert.Null(ElevatedActionProtocol.Decode(new string('A', ElevatedActionProtocol.MaxEncodedLength + 1)));

    [Fact]
    public void Fichier_de_resultat_uniquement_dans_le_dossier_d_elevation_avec_nom_impose()
    {
        var paths = new AppPaths(_dir.Path);
        var ok = ElevatedActionProtocol.NewResultPath(paths);
        Assert.True(ElevatedActionProtocol.IsValidResultPath(paths, ok));

        var dir = ElevatedActionProtocol.ElevationDirectory(paths);
        Assert.False(ElevatedActionProtocol.IsValidResultPath(paths, Path.Combine(dir, "..", Path.GetFileName(ok))));
        Assert.False(ElevatedActionProtocol.IsValidResultPath(paths, Path.Combine(dir, "sous", Path.GetFileName(ok))));
        Assert.False(ElevatedActionProtocol.IsValidResultPath(paths, Path.Combine(dir, "evil.exe")));
        Assert.False(ElevatedActionProtocol.IsValidResultPath(paths, @"C:\Windows\System32\0123456789abcdef0123456789abcdef.json"));
        Assert.False(ElevatedActionProtocol.IsValidResultPath(paths, null));
    }

    [Fact]
    public void Resultat_ecrit_puis_relu()
    {
        var path = ElevatedActionProtocol.NewResultPath(new AppPaths(_dir.Path));
        ElevatedActionProtocol.WriteResult(path, new ActionResult("T", true, "ok", 2.5, false));
        Assert.Equal(new ActionResult("T", true, "ok", 2.5, false), ElevatedActionProtocol.ReadResult(path));
    }

    public void Dispose() => _dir.Dispose();
}

public sealed class ElevationDispatchTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeTime _time = new(new DateTimeOffset(2026, 11, 9, 10, 0, 0, TimeSpan.Zero));
    private readonly FakeElevatedRunner _runner = new();
    private readonly ObservingModule _module = new();
    private readonly FreeResRuntime _rt;

    public ElevationDispatchTests() =>
        _rt = FreeResRuntime.Create(new AppPaths(_dir.Path), new FakeProbe(_time), _ => new ModuleSetup([_module], c => TestActions.Register(c)), new FakeElevation(admin: false), _time, _runner);

    private static Proposal Admin(string id) => new(id, "Admin", "Detail", 2, Severity.Warn, ActionRequest.Of("TestAdmin"));

    [Fact]
    public async Task Action_admin_approuvee_passe_par_l_UAC_et_les_observateurs_sont_notifies()
    {
        _module.Proposals.Add(Admin("sys-a"));
        await _rt.Engine.RunCycleAsync(default);
        var r = await _rt.Engine.ApproveAsync("sys-a", default);

        Assert.True(r.Ok);
        Assert.Equal("TestAdmin", Assert.Single(_runner.Requests).Type);
        Assert.Empty(_rt.Engine.Queue.Pending());
        Assert.Equal("TestAdmin", Assert.Single(_module.Completed).Request.Type);
    }

    [Fact]
    public async Task UAC_refusee_la_proposition_reste_et_aucune_decision_n_est_enregistree()
    {
        _module.Proposals.Add(Admin("sys-a"));
        await _rt.Engine.RunCycleAsync(default);
        _runner.UserCancels = true;
        var r = await _rt.Engine.ApproveAsync("sys-a", default);

        Assert.False(r.Ok);
        Assert.StartsWith(MonitorEngine.ElevationCancelledPrefix, r.Message, StringComparison.Ordinal);
        Assert.Single(_rt.Engine.Queue.Pending());
        Assert.Null(_rt.Database.GetDecision("sys-a"));
        Assert.Empty(_module.Completed);
    }

    [Fact]
    public async Task Action_automatique_admin_jamais_elevee()
    {
        var dispatcher = new ActionDispatcher(_rt.Catalog, new FakeElevation(false), () => 10, _rt.Log, _runner);
        await Assert.ThrowsAsync<ActionRejectedException>(() => dispatcher.ExecuteAsync(ActionRequest.Of("TestAdmin"), false, default));
        Assert.Empty(_runner.Requests);
    }

    [Fact]
    public async Task Sans_executeur_UAC_action_admin_refusee_meme_approuvee()
    {
        var dispatcher = new ActionDispatcher(_rt.Catalog, new FakeElevation(false), () => 10, _rt.Log);
        await Assert.ThrowsAsync<ActionRejectedException>(() => dispatcher.ExecuteAsync(ActionRequest.Of("TestAdmin"), false, default, allowElevation: true));
    }

    [Fact]
    public async Task Action_utilisateur_jamais_elevee()
    {
        var dispatcher = new ActionDispatcher(_rt.Catalog, new FakeElevation(false), () => 10, _rt.Log, _runner);
        var r = await dispatcher.ExecuteAsync(TestActions.Clean(), false, default, allowElevation: true);
        Assert.True(r.Ok);
        Assert.Empty(_runner.Requests);
    }

    public void Dispose()
    {
        _rt.Dispose();
        _dir.Dispose();
    }
}
