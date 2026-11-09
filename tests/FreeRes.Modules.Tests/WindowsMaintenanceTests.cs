using FreeRes.Core.Actions;
using FreeRes.Core.Decisions;
using FreeRes.Core.Platform;
using FreeRes.Modules.Disk;
using FreeRes.Modules.Windows;

namespace FreeRes.Modules.Tests;

/// <summary>Maintenance Windows : AUCUN programme reel n'est lance (shutdown/DISM simules).</summary>
public sealed class WindowsMaintenanceTests : IDisposable
{
    private const long GB = 1024L * 1024 * 1024;
    private readonly TempDir _dir = new();
    private readonly FakeTime _time = new(Fixtures.Start);
    private readonly FakeProcessRunner _processes = new();
    private bool _rebootPending;
    private long _wuBytes;

    private WindowsMaintenanceModule Module(MemoryState? state = null) => new(
        Fixtures.Services(_dir, _time, _processes, state ?? new MemoryState()),
        new SafeCleaner([_dir.Path], _time),
        rebootPending: () => _rebootPending,
        windowsUpdateCacheBytes: () => _wuBytes);

    [Fact]
    public async Task Redemarrage_en_attente_propose_seulement_apres_12_h_puis_oublie_une_fois_fait()
    {
        var state = new MemoryState();
        var m = Module(state);
        Assert.Empty((await m.EvaluateAsync(Fixtures.Context(_time, rebootPending: true), default)).Proposals);

        _time.Advance(TimeSpan.FromHours(12));
        var p = Assert.Single((await m.EvaluateAsync(Fixtures.Context(_time, rebootPending: true), default)).Proposals);
        Assert.Equal("windows-reboot-pending", p.Id);
        Assert.Equal(Severity.Crit, p.Severity);
        Assert.Equal("10", p.Action.Parameters["minutes"]);

        await m.EvaluateAsync(Fixtures.Context(_time, rebootPending: false), default);   // redemarre
        _time.Advance(TimeSpan.FromHours(1));
        Assert.Empty((await m.EvaluateAsync(Fixtures.Context(_time, rebootPending: true), default)).Proposals);   // compteur repart de zero
    }

    [Fact]
    public async Task Disque_bas_cache_WU_et_DISM_proposes_jamais_pendant_un_redemarrage_en_attente()
    {
        _wuBytes = 5 * GB;
        var low = await Module().EvaluateAsync(Fixtures.Context(_time, diskFreeGB: 20), default);
        Assert.Equal(["windows-dism-cleanup", "windows-update-cache"], low.Proposals.Select(p => p.Id).Order());
        Assert.Equal(5, low.Proposals.Single(p => p.Id == "windows-update-cache").EstGainGB);

        var pending = await Module().EvaluateAsync(Fixtures.Context(_time, diskFreeGB: 20, rebootPending: true), default);
        Assert.DoesNotContain(pending.Proposals, p => p.Id is "windows-update-cache" or "windows-dism-cleanup");

        Assert.Empty((await Module().EvaluateAsync(Fixtures.Context(_time, diskFreeGB: 40), default)).Proposals);
    }

    [Fact]
    public async Task DISM_reussi_n_est_plus_propose_pendant_30_jours()
    {
        var state = new MemoryState();
        var m = Module(state);
        m.OnActionCompleted(ActionRequest.Of(WindowsMaintenanceModule.DismComponentCleanup), new ActionResult(WindowsMaintenanceModule.DismComponentCleanup, true, "ok", 3, false));
        _time.Advance(TimeSpan.FromDays(29));
        Assert.DoesNotContain((await m.EvaluateAsync(Fixtures.Context(_time, diskFreeGB: 20), default)).Proposals, p => p.Id == "windows-dism-cleanup");
        _time.Advance(TimeSpan.FromDays(2));
        Assert.Contains((await m.EvaluateAsync(Fixtures.Context(_time, diskFreeGB: 20), default)).Proposals, p => p.Id == "windows-dism-cleanup");
    }

    [Fact]
    public async Task DISM_en_echec_reste_propose()
    {
        var m = Module();
        m.OnActionCompleted(ActionRequest.Of(WindowsMaintenanceModule.DismComponentCleanup), new ActionResult(WindowsMaintenanceModule.DismComponentCleanup, false, "code 87", 0, false));
        Assert.Contains((await m.EvaluateAsync(Fixtures.Context(_time, diskFreeGB: 20), default)).Proposals, p => p.Id == "windows-dism-cleanup");
    }

    [Theory]
    [InlineData("1")]
    [InlineData("61")]
    [InlineData("10; shutdown /s")]
    [InlineData("-5")]
    public void Delai_de_redemarrage_hors_bornes_ou_injecte_refuse(string minutes)
    {
        var catalog = new ActionCatalog();
        Module().RegisterActions(catalog);
        Assert.Throws<ActionRejectedException>(() => catalog.Resolve(new ActionRequest(WindowsMaintenanceModule.ScheduleReboot, new Dictionary<string, string> { ["minutes"] = minutes })));
    }

    [Fact]
    public async Task Redemarrage_planifie_appelle_shutdown_avec_des_arguments_separes()
    {
        var catalog = new ActionCatalog();
        Module().RegisterActions(catalog);
        var req = new ActionRequest(WindowsMaintenanceModule.ScheduleReboot, new Dictionary<string, string> { ["minutes"] = "10" });
        var outcome = await catalog.Resolve(req).Execute(req, false, default);
        Assert.True(outcome.Ok);
        var call = Assert.Single(_processes.Calls);
        Assert.EndsWith("shutdown.exe", call.File, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["/r", "/t", "600"], call.Args.Take(3));
    }

    [Fact]
    public async Task Cache_WU_et_DISM_refuses_pendant_un_redemarrage_en_attente_sans_rien_lancer()
    {
        var catalog = new ActionCatalog();
        Module().RegisterActions(catalog);
        _rebootPending = true;
        foreach (var type in new[] { WindowsMaintenanceModule.WindowsUpdateCacheClean, WindowsMaintenanceModule.DismComponentCleanup })
        {
            var outcome = await catalog.Resolve(ActionRequest.Of(type)).Execute(ActionRequest.Of(type), false, default);
            Assert.False(outcome.Ok);
            Assert.Contains("Redémarrage en attente", outcome.Message, StringComparison.Ordinal);
            Assert.True(catalog.Resolve(ActionRequest.Of(type)).RequiresAdmin);
        }

        Assert.Empty(_processes.Calls);
    }

    [Fact]
    public async Task DISM_lance_avec_les_bons_arguments_et_code_retour_respecte()
    {
        var catalog = new ActionCatalog();
        Module().RegisterActions(catalog);
        _processes.Respond = (_, _) => new ProcessResult(87, string.Empty, "erreur", false);
        var outcome = await catalog.Resolve(ActionRequest.Of(WindowsMaintenanceModule.DismComponentCleanup)).Execute(ActionRequest.Of(WindowsMaintenanceModule.DismComponentCleanup), false, default);
        Assert.False(outcome.Ok);
        Assert.Equal(["/Online", "/Cleanup-Image", "/StartComponentCleanup", "/English"], Assert.Single(_processes.Calls).Args);
    }

    public void Dispose() => _dir.Dispose();
}
