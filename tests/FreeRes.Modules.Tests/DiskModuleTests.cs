using FreeRes.Core.Actions;
using FreeRes.Core.Decisions;
using FreeRes.Modules.Disk;

namespace FreeRes.Modules.Tests;

internal sealed class FakeRecycleBin : IRecycleBin
{
    public long Bytes { get; set; }

    public int Emptied { get; private set; }

    public long SizeBytes() => Bytes;

    public void Empty()
    {
        Emptied++;
        Bytes = 0;
    }
}

/// <summary>Module disque sur des zones de TEST (jamais le vrai %TEMP%). Parite Pester : regle hors liste blanche refusee.</summary>
public sealed class DiskModuleTests : IDisposable
{
    private const long GB = 1024L * 1024 * 1024;
    private readonly TempDir _dir = new();
    private readonly FakeTime _time = new(Fixtures.Start);
    private readonly FakeRecycleBin _bin = new();
    private readonly DiskOptions _options;

    public DiskModuleTests()
    {
        _options = new DiskOptions(
            [_dir.Sub("temp"), _dir.Sub("sys"), _dir.Sub("npm")],
            [
                new("installer-leak", _dir.Sub("temp"), 1, @"^[a-z0-9]{8}\.[a-z0-9]{3}$", false, 1, "test"),
                new("user-temp", _dir.Sub("temp"), 7, null, false, 24, "test"),
                new("windows-temp", _dir.Sub("sys"), 7, null, true, 24, "test"),
            ],
            _dir.Sub("npm"),
            NpmCacheProposeGB: 0.000001,
            RecycleBinProposeGB: 2,
            SystemZonesProposeGB: 0.000001);
    }

    private DiskModule Module() => new(Fixtures.Services(_dir, _time), _options, _bin);

    [Fact]
    public void Regle_hors_liste_blanche_refusee_au_demarrage()
    {
        var bad = _options with { Rules = [new("evil", @"C:\Windows\System32", 1, null, false, 24, "x")] };
        var ex = Assert.Throws<InvalidOperationException>(() => new DiskModule(Fixtures.Services(_dir, _time), bad, _bin));
        Assert.Contains("hors liste blanche", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Regles_par_defaut_valides_et_toutes_sous_une_racine_autorisee()
    {
        var cleaner = new SafeCleaner(DiskOptions.Default.AllowedRoots, _time);
        Assert.Empty(DiskOptions.Default.Validate(cleaner));
        Assert.All(DiskOptions.Default.Rules.Where(r => r.RequiresAdmin), r => Assert.True(r.Path.Contains("SystemRoot", StringComparison.Ordinal) || r.Path.Contains("ProgramData", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Nettoyage_automatique_selon_la_frequence_de_chaque_regle_jamais_les_regles_admin()
    {
        var m = Module();
        var first = await m.EvaluateAsync(Fixtures.Context(_time), default);
        Assert.Equal(["installer-leak", "user-temp"], first.AutoActions.Select(a => a.Parameters["rule"]).Order());

        _time.Advance(TimeSpan.FromMinutes(30));
        Assert.Empty((await m.EvaluateAsync(Fixtures.Context(_time), default)).AutoActions);

        _time.Advance(TimeSpan.FromMinutes(31));   // > 1 h : seule la regle horaire revient
        Assert.Equal(["installer-leak"], (await m.EvaluateAsync(Fixtures.Context(_time), default)).AutoActions.Select(a => a.Parameters["rule"]));
    }

    [Fact]
    public async Task Aucune_proposition_tant_que_le_disque_est_au_dessus_du_seuil()
    {
        Fixtures.AgedFile(_dir.Sub(@"npm\blob"), 30, now: _time.Now);
        _bin.Bytes = 5 * GB;
        Assert.Empty((await Module().EvaluateAsync(Fixtures.Context(_time, diskFreeGB: 40), default)).Proposals);
    }

    [Fact]
    public async Task Disque_bas_propositions_npm_corbeille_zones_systeme_avec_severite()
    {
        // 12 MB : les tailles sont arrondies au centieme de GB, un fichier de quelques KB compterait pour 0.
        Fixtures.AgedFile(_dir.Sub(@"npm\blob"), 30, sizeKB: 12_000, now: _time.Now);
        Fixtures.AgedFile(_dir.Sub(@"sys\vieux.log"), 30, sizeKB: 12_000, now: _time.Now);
        _bin.Bytes = 3 * GB;

        var warn =await Module().EvaluateAsync(Fixtures.Context(_time, diskFreeGB: 20), default);
        Assert.Equal(["disk-npm-cache", "disk-recycle-bin", "disk-system-zones"], warn.Proposals.Select(p => p.Id).Order());
        Assert.All(warn.Proposals, p => Assert.Equal(Severity.Warn, p.Severity));
        Assert.Equal(3, warn.Proposals.Single(p => p.Id == "disk-recycle-bin").EstGainGB);

        var crit = await Module().EvaluateAsync(Fixtures.Context(_time, diskFreeGB: 8), default);
        Assert.All(crit.Proposals, p => Assert.Equal(Severity.Crit, p.Severity));
    }

    [Fact]
    public async Task Actions_executees_par_le_catalogue()
    {
        var m = Module();
        var catalog = new ActionCatalog();
        m.RegisterActions(catalog);
        var old = Fixtures.AgedFile(_dir.Sub(@"temp\vieux.tmp"), 30, now: _time.Now);
        var sys = Fixtures.AgedFile(_dir.Sub(@"sys\vieux.log"), 30, now: _time.Now);
        _bin.Bytes = 3 * GB;

        var user = await catalog.Resolve(new ActionRequest(DiskModule.CleanUserZone, new Dictionary<string, string> { ["rule"] = "user-temp" })).Execute(new ActionRequest(DiskModule.CleanUserZone, new Dictionary<string, string> { ["rule"] = "user-temp" }), false, default);
        Assert.True(user.Ok);
        Assert.True(user.Changed);
        Assert.False(File.Exists(old));

        // Zone deja propre : succes, mais « rien fait » (n'apparait pas dans « Fait automatiquement »).
        var again = await catalog.Resolve(new ActionRequest(DiskModule.CleanUserZone, new Dictionary<string, string> { ["rule"] = "user-temp" })).Execute(new ActionRequest(DiskModule.CleanUserZone, new Dictionary<string, string> { ["rule"] = "user-temp" }), false, default);
        Assert.True(again.Ok);
        Assert.False(again.Changed);

        var system = await catalog.Resolve(ActionRequest.Of(DiskModule.CleanSystemZones)).Execute(ActionRequest.Of(DiskModule.CleanSystemZones), false, default);
        Assert.True(system.Ok);
        Assert.False(File.Exists(sys));

        await catalog.Resolve(ActionRequest.Of(DiskModule.EmptyRecycleBin)).Execute(ActionRequest.Of(DiskModule.EmptyRecycleBin), false, default);
        Assert.Equal(1, _bin.Emptied);
    }

    [Theory]
    [InlineData("windows-temp")]   // regle admin : interdite via l'action utilisateur
    [InlineData("inconnue")]
    public void Nettoyage_utilisateur_refuse_une_regle_admin_ou_inconnue(string rule)
    {
        var catalog = new ActionCatalog();
        Module().RegisterActions(catalog);
        var ex = Assert.Throws<ActionRejectedException>(() => catalog.Resolve(new ActionRequest(DiskModule.CleanUserZone, new Dictionary<string, string> { ["rule"] = rule })));
        Assert.Contains("reservee a l'administrateur", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Zones_systeme_marquees_administrateur_et_sans_parametre()
    {
        var catalog = new ActionCatalog();
        Module().RegisterActions(catalog);
        Assert.True(catalog.Resolve(ActionRequest.Of(DiskModule.CleanSystemZones)).RequiresAdmin);
        Assert.False(catalog.Resolve(ActionRequest.Of(DiskModule.EmptyRecycleBin)).RequiresAdmin);
        Assert.Throws<ActionRejectedException>(() => catalog.Resolve(new ActionRequest(DiskModule.CleanSystemZones, new Dictionary<string, string> { ["path"] = @"C:\" })));
    }

    public void Dispose() => _dir.Dispose();
}
