using FreeRes.Core.Actions;
using FreeRes.Core.Decisions;
using FreeRes.Core.Platform;
using FreeRes.Modules.Docker;

namespace FreeRes.Modules.Tests;

/// <summary>Machine Docker simulee : aucun processus, aucun fichier reel.</summary>
internal sealed class FakeDockerHost : IDockerHost
{
    public const string Vhdx = @"C:\Users\Test\AppData\Local\Docker\wsl\disk\docker_data.vhdx";

    public bool IsInstalled { get; set; } = true;

    public string? DockerCli { get; set; } = @"C:\fake\docker.exe";

    public string? WslExe { get; set; } = @"C:\fake\wsl.exe";

    public bool Running { get; set; }

    public string? VhdxPath { get; set; } = Vhdx;

    public long? VhdxBytes { get; set; }

    public bool IsDockerRunning() => Running;

    public string? DetectVhdx() => VhdxPath;

    public long? FileBytes(string path) => path == VhdxPath ? VhdxBytes : null;
}

/// <summary>
/// Module Docker/WSL. Parite avec le bloc Pester « Disque virtuel Docker ». Docker, wsl, fstrim et diskpart sont
/// TOUS simules (FakeProcessRunner) : le Docker reel de la machine (volumes Postgres, MinIO, Ollama) n'est jamais touche.
/// </summary>
public sealed class DockerModuleTests : IDisposable
{
    private const long GB = 1024L * 1024 * 1024;
    private const string DfOutput = "Filesystem         1B-blocks       Used     Available Use% Mounted on\n/dev/sdd 1081101176832 6164467712 1019944353792   1% /mnt/docker-desktop-disk\n";
    private const string SystemDfOutput = "Images|3.67GB|2.5GB (68%)\nContainers|12kB|0B (0%)\nLocal Volumes|1.2GB|300MB (25%)\nBuild Cache|1.5GB|1.5GB\n";

    private readonly TempDir _dir = new();
    private readonly FakeTime _time = new(Fixtures.Start);
    private readonly FakeProcessRunner _processes = new();
    private readonly FakeDockerHost _host = new();
    private readonly MemoryState _state = new();
    private readonly DockerOptions _options = DockerOptions.Default with { WslShutdownSettleSeconds = 0 };

    public DockerModuleTests()
    {
        _processes.Respond = (file, args) => file switch
        {
            @"C:\fake\wsl.exe" when args.Contains("df") => new ProcessResult(0, DfOutput, string.Empty, false),
            @"C:\fake\wsl.exe" when args.Contains("--list") => new ProcessResult(0, "docker-desktop\r\n", string.Empty, false),
            @"C:\fake\docker.exe" when args.Contains("system") => new ProcessResult(0, SystemDfOutput, string.Empty, false),
            _ => new ProcessResult(0, string.Empty, string.Empty, false),
        };
    }

    private DockerModule Module() => new(Fixtures.Services(_dir, _time, _processes, _state), _options, _host);

    private static ActionResult Done(string type, bool ok) => new(type, ok, ok ? "ok" : "echec", 0, false);

    private int Calls(string arg) => _processes.Calls.Count(c => c.Args.Contains(arg));

    // ------------------------------------------------------------ lecture des sorties

    [Fact]
    public void Lit_les_octets_utilises_dans_la_sortie_df()
    {
        Assert.Equal(6164467712, DockerParsers.ParseDfUsedBytes(DfOutput));
    }

    [Theory]
    [InlineData("df: /mnt/x: No such file or directory")]
    [InlineData("Filesystem 1B-blocks Used\n/dev/sdd abc xyz 1 /mnt")]
    [InlineData("")]
    [InlineData(null)]
    public void Sortie_df_illisible_rend_null(string? text)
    {
        Assert.Null(DockerParsers.ParseDfUsedBytes(text));
    }

    [Fact]
    public void Espace_mort_vhdx_moins_donnees_moins_plancher_jamais_negatif()
    {
        Assert.Equal(6.32, DockerParsers.VhdxSlackGB(14.06, 5.74, 2));
        Assert.Equal(0, DockerParsers.VhdxSlackGB(7.70, 5.74, 2));   // mesure reelle du 2026-11-09
        Assert.Equal(0, DockerParsers.VhdxSlackGB(1, 5, 2));
    }

    [Fact]
    public void Lit_la_repartition_de_docker_system_df()
    {
        var df = DockerParsers.ParseSystemDf(SystemDfOutput + "ligne parasite\n");
        Assert.Equal(3.67, df.ImagesGB);
        Assert.Equal(2.5, df.ImagesReclaimableGB);
        Assert.Equal(0, df.ContainersGB);
        Assert.Equal(1.2, df.VolumesGB);
        Assert.Equal(0.29, df.VolumesUnusedGB);
        Assert.Equal(1.5, df.BuildCacheGB);
        Assert.Equal(1.5, df.BuildCacheReclaimableGB);
        Assert.Null(DockerParsers.ParseSystemDf("erreur du daemon").ImagesGB);
    }

    [Theory]
    [InlineData("1.234GB", 1.234 * GB)]
    [InlineData("512MB", 512d * 1024 * 1024)]
    [InlineData("0B", 0)]
    [InlineData("1,5GB (35%)", 1.5 * GB)]
    [InlineData("12kB", 12 * 1024)]
    [InlineData("n/a", 0)]
    public void Lit_les_tailles_affichees_par_docker(string text, double bytes)
    {
        Assert.Equal(bytes, DockerParsers.ParseSizeBytes(text), 3);
    }

    [Fact]
    public void Lit_les_distributions_wsl_en_UTF16_ou_avec_octets_nuls()
    {
        Assert.Equal(["docker-desktop", "Ubuntu"], DockerParsers.ParseWslRunning("\uFEFFd\0o\0c\0k\0e\0r\0-\0d\0e\0s\0k\0t\0o\0p\0\r\0\n\0Ubuntu\r\n\r\n"));
        Assert.Empty(DockerParsers.ParseWslRunning("There is no running distribution."));   // phrase = ligne avec espaces, ignoree
    }

    // ------------------------------------------------------------ evaluation

    [Fact]
    public async Task Inactif_sans_Docker_Desktop_aucune_action_ni_programme()
    {
        _host.IsInstalled = false;
        _host.VhdxBytes = 50 * GB;
        var e = await Module().EvaluateAsync(Fixtures.Context(_time, diskFreeGB: 5), default);
        Assert.Same(Core.Engine.ModuleEvaluation.Empty, e);
        Assert.Empty(_processes.Calls);
    }

    [Fact]
    public async Task VM_orpheline_arretee_seulement_a_la_2e_observation_espacee_de_10_min()
    {
        var m = Module();
        Assert.Empty((await m.EvaluateAsync(Fixtures.Context(_time), default)).AutoActions);
        Assert.NotNull(_state.GetState("module.docker." + DockerModule.OrphanSinceKey));

        _time.Advance(TimeSpan.FromMinutes(9));
        Assert.Empty((await m.EvaluateAsync(Fixtures.Context(_time), default)).AutoActions);   // trop tot

        _time.Advance(TimeSpan.FromMinutes(1));
        var auto = Assert.Single((await m.EvaluateAsync(Fixtures.Context(_time), default)).AutoActions);
        Assert.Equal(DockerModule.WslTerminate, auto.Type);
        Assert.Equal("docker-desktop", auto.Parameters["distro"]);
        Assert.Null(_state.GetState("module.docker." + DockerModule.OrphanSinceKey));   // compteur remis a zero
        Assert.Contains(_processes.Calls, c => c.Args.SequenceEqual(["--list", "--running", "--quiet"]));
    }

    [Fact]
    public async Task VM_non_orpheline_si_Docker_demarre_entre_deux_observations()
    {
        var m = Module();
        await m.EvaluateAsync(Fixtures.Context(_time), default);
        _host.Running = true;
        _time.Advance(TimeSpan.FromMinutes(5));
        await m.EvaluateAsync(Fixtures.Context(_time), default);
        _host.Running = false;
        _time.Advance(TimeSpan.FromMinutes(10));
        Assert.DoesNotContain((await m.EvaluateAsync(Fixtures.Context(_time), default)).AutoActions, a => a.Type == DockerModule.WslTerminate);   // nouvelle 1re observation
    }

    [Fact]
    public async Task VM_absente_de_la_liste_wsl_jamais_arretee()
    {
        _processes.Respond = (_, _) => new ProcessResult(0, "Ubuntu\r\n", string.Empty, false);
        var m = Module();
        await m.EvaluateAsync(Fixtures.Context(_time), default);
        _time.Advance(TimeSpan.FromMinutes(30));
        Assert.Empty((await m.EvaluateAsync(Fixtures.Context(_time), default)).AutoActions);
        Assert.Null(_state.GetState("module.docker." + DockerModule.OrphanSinceKey));
    }

    [Fact]
    public async Task Docker_demarre_trim_et_mesure_seulement_s_ils_sont_perimes()
    {
        _host.Running = true;
        _host.VhdxBytes = (long)(7.70 * GB);
        var m = Module();

        var first = await m.EvaluateAsync(Fixtures.Context(_time), default);
        Assert.Equal(DockerModule.DockerTrim, Assert.Single(first.AutoActions).Type);
        m.OnActionCompleted(ActionRequest.Of(DockerModule.DockerTrim), Done(DockerModule.DockerTrim, true));
        Assert.Equal(1, Calls("-B1"));
        Assert.Equal(1, Calls("system"));
        var usage = m.LastUsage!;
        Assert.Equal(5.74, usage.InsideUsedGB);
        Assert.Equal(7.7, usage.VhdxGB);
        Assert.Equal(3.67, usage.ImagesGB);
        Assert.Equal(1.5, usage.BuildCacheReclaimableGB);

        _time.Advance(TimeSpan.FromHours(5));
        Assert.Empty((await m.EvaluateAsync(Fixtures.Context(_time), default)).AutoActions);
        Assert.Equal(1, Calls("-B1"));   // mesure encore fraiche

        _time.Advance(TimeSpan.FromHours(1));
        Assert.Empty((await m.EvaluateAsync(Fixtures.Context(_time), default)).AutoActions);   // trim frais (6 h < 24 h)
        Assert.Equal(2, Calls("-B1"));   // mesure perimee (6 h)

        _time.Advance(TimeSpan.FromHours(18));
        Assert.Equal(DockerModule.DockerTrim, Assert.Single((await m.EvaluateAsync(Fixtures.Context(_time), default)).AutoActions).Type);
    }

    [Fact]
    public async Task Mesure_impossible_conserve_l_ancienne_et_reessaie()
    {
        _host.Running = true;
        var m = Module();
        await m.EvaluateAsync(Fixtures.Context(_time), default);
        var before = m.LastUsage!;
        _processes.Respond = (_, _) => new ProcessResult(1, string.Empty, "erreur", false);
        _time.Advance(TimeSpan.FromHours(7));
        await m.EvaluateAsync(Fixtures.Context(_time), default);
        Assert.Equal(before, m.LastUsage);
        _time.Advance(TimeSpan.FromMinutes(5));
        await m.EvaluateAsync(Fixtures.Context(_time), default);
        Assert.Equal(3, Calls("-B1"));   // toujours perimee : nouvelle tentative
    }

    [Fact]
    public async Task Trim_en_echec_pas_de_date_de_trim_donc_pas_de_compactage_propose_a_tort()
    {
        _host.Running = true;
        var m = Module();
        await m.EvaluateAsync(Fixtures.Context(_time), default);   // mesure : 5,74 GB de donnees
        m.OnActionCompleted(ActionRequest.Of(DockerModule.DockerTrim), Done(DockerModule.DockerTrim, false));
        Assert.Null(_state.GetState("module.docker." + DockerModule.TrimmedAtKey));

        _time.Advance(TimeSpan.FromMinutes(30));
        Assert.Empty((await m.EvaluateAsync(Fixtures.Context(_time), default)).AutoActions);   // pas de nouvel essai avant 1 h
        _time.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(DockerModule.DockerTrim, Assert.Single((await m.EvaluateAsync(Fixtures.Context(_time), default)).AutoActions).Type);

        _host.Running = false;
        _host.VhdxBytes = (long)(9.5 * GB);   // espace mort apparent 1,76 GB, mais aucun trim reussi
        _processes.Respond = (_, _) => new ProcessResult(0, string.Empty, string.Empty, false);
        Assert.Empty((await m.EvaluateAsync(Fixtures.Context(_time), default)).Proposals);
    }

    [Fact]
    public async Task Compactage_propose_selon_l_espace_mort_reel()
    {
        _host.Running = true;
        var m = Module();
        await m.EvaluateAsync(Fixtures.Context(_time), default);
        m.OnActionCompleted(ActionRequest.Of(DockerModule.DockerTrim), Done(DockerModule.DockerTrim, true));
        _host.Running = false;

        _host.VhdxBytes = (long)(14.06 * GB);
        var p = Assert.Single((await m.EvaluateAsync(Fixtures.Context(_time, diskFreeGB: 40), default)).Proposals);
        Assert.Equal("docker-vhdx", p.Id);
        Assert.Equal(6.32, p.EstGainGB);
        Assert.Equal(Severity.Info, p.Severity);
        Assert.Equal(DockerModule.CompactDockerVhdx, p.Action.Type);
        Assert.Contains("6.32 GB d'espace mort", p.Title, StringComparison.Ordinal);

        Assert.Equal(Severity.Warn, Assert.Single((await m.EvaluateAsync(Fixtures.Context(_time, diskFreeGB: 20), default)).Proposals).Severity);

        _host.VhdxBytes = (long)(7.70 * GB);   // mesure reelle du 2026-11-09 : sous le plancher, rien a gagner
        Assert.Empty((await m.EvaluateAsync(Fixtures.Context(_time), default)).Proposals);

        _host.VhdxBytes = (long)(9.2 * GB);   // espace mort 1,46 GB < 1,5 GB, et pas de repli (mesure existante)
        Assert.Empty((await m.EvaluateAsync(Fixtures.Context(_time), default)).Proposals);
    }

    [Fact]
    public async Task Compactage_repli_sur_10_GB_sans_mesure()
    {
        var m = Module();
        _host.VhdxBytes = 9 * GB;
        Assert.Empty((await m.EvaluateAsync(Fixtures.Context(_time), default)).Proposals);

        _host.VhdxBytes = 12 * GB;
        var p = Assert.Single((await m.EvaluateAsync(Fixtures.Context(_time), default)).Proposals);
        Assert.Equal("docker-vhdx", p.Id);
        Assert.Equal(3, p.EstGainGB);
        Assert.Contains("gain non mesure", p.Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Compactage_non_repropose_avant_un_nouveau_trim()
    {
        _host.Running = true;
        var m = Module();
        await m.EvaluateAsync(Fixtures.Context(_time), default);
        m.OnActionCompleted(ActionRequest.Of(DockerModule.DockerTrim), Done(DockerModule.DockerTrim, true));
        _host.Running = false;
        _host.VhdxBytes = 14 * GB;
        _time.Advance(TimeSpan.FromMinutes(1));
        m.OnActionCompleted(ActionRequest.Of(DockerModule.CompactDockerVhdx), Done(DockerModule.CompactDockerVhdx, true));
        Assert.Empty((await m.EvaluateAsync(Fixtures.Context(_time), default)).Proposals);

        _host.Running = true;
        _time.Advance(TimeSpan.FromDays(1));
        await m.EvaluateAsync(Fixtures.Context(_time), default);
        m.OnActionCompleted(ActionRequest.Of(DockerModule.DockerTrim), Done(DockerModule.DockerTrim, true));
        _host.Running = false;
        Assert.Single((await m.EvaluateAsync(Fixtures.Context(_time), default)).Proposals);
    }

    [Fact]
    public async Task Purges_proposees_sur_la_mesure_et_jamais_les_volumes()
    {
        _host.Running = true;
        var proposals = (await Module().EvaluateAsync(Fixtures.Context(_time), default)).Proposals;
        Assert.Equal(["docker-builder", "docker-images"], proposals.Select(p => p.Id).Order());
        Assert.Equal(1.5, proposals.Single(p => p.Id == "docker-builder").EstGainGB);
        Assert.Equal(2.5, proposals.Single(p => p.Id == "docker-images").EstGainGB);
        Assert.All(proposals, p => Assert.Contains(p.Action.Parameters["level"], DockerModule.PruneLevels));
    }

    [Fact]
    public async Task Purge_reussie_force_un_trim_et_une_nouvelle_mesure()
    {
        _host.Running = true;
        var m = Module();
        await m.EvaluateAsync(Fixtures.Context(_time), default);
        m.OnActionCompleted(ActionRequest.Of(DockerModule.DockerTrim), Done(DockerModule.DockerTrim, true));
        var prune = new ActionRequest(DockerModule.DockerPrune, new Dictionary<string, string> { ["level"] = "builder" });
        m.OnActionCompleted(prune, Done(DockerModule.DockerPrune, true));
        _time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(DockerModule.DockerTrim, Assert.Single((await m.EvaluateAsync(Fixtures.Context(_time), default)).AutoActions).Type);
        Assert.Equal(2, Calls("-B1"));
    }

    // ------------------------------------------------------------ actions

    private ActionCatalog Catalog()
    {
        var catalog = new ActionCatalog();
        Module().RegisterActions(catalog);
        return catalog;
    }

    private static ActionRequest Req(string type, string key, string value) => new(type, new Dictionary<string, string> { [key] = value });

    [Theory]
    [InlineData("volumes")]
    [InlineData("system")]
    [InlineData("builder; docker volume prune -f")]
    [InlineData("")]
    public void DockerPrune_refuse_volumes_et_tout_niveau_inconnu(string level)
    {
        var ex = Assert.Throws<ActionRejectedException>(() => Catalog().Resolve(Req(DockerModule.DockerPrune, "level", level)));
        Assert.Contains("volumes ne sont JAMAIS purgés", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DockerPrune_refuse_un_parametre_manquant_ou_supplementaire()
    {
        var catalog = Catalog();
        Assert.Contains("level", Assert.Throws<ActionRejectedException>(() => catalog.Resolve(ActionRequest.Of(DockerModule.DockerPrune))).Message, StringComparison.Ordinal);
        var extra = new ActionRequest(DockerModule.DockerPrune, new Dictionary<string, string> { ["level"] = "builder", ["volumes"] = "true" });
        Assert.Contains("un seul paramètre", Assert.Throws<ActionRejectedException>(() => catalog.Resolve(extra)).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("builder", new[] { "builder", "prune", "-f", "--filter", "until=168h" })]
    [InlineData("unused-images", new[] { "image", "prune", "-a", "-f", "--filter", "until=720h" })]
    [InlineData("dangling", new[] { "image", "prune", "-f" })]
    public async Task DockerPrune_lance_docker_avec_des_arguments_separes(string level, string[] expected)
    {
        _host.Running = true;
        var req = Req(DockerModule.DockerPrune, "level", level);
        var outcome = await Catalog().Resolve(req).Execute(req, false, default);
        Assert.True(outcome.Ok, outcome.Message);
        var call = Assert.Single(_processes.Calls);
        Assert.Equal(@"C:\fake\docker.exe", call.File);
        Assert.Equal(expected, call.Args);
        Assert.DoesNotContain(call.Args, a => a.Contains("volume", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DockerPrune_et_DockerTrim_echouent_proprement_si_Docker_est_arrete()
    {
        var catalog = Catalog();
        var prune = Req(DockerModule.DockerPrune, "level", "builder");
        var p = await catalog.Resolve(prune).Execute(prune, false, default);
        Assert.False(p.Ok);
        Assert.Equal("Docker Desktop ne tourne pas", p.Message);
        var t = await catalog.Resolve(ActionRequest.Of(DockerModule.DockerTrim)).Execute(ActionRequest.Of(DockerModule.DockerTrim), false, default);
        Assert.False(t.Ok);
        Assert.Contains("disque de données non monté", t.Message, StringComparison.Ordinal);
        Assert.False(catalog.Resolve(ActionRequest.Of(DockerModule.DockerTrim)).RequiresAdmin);
        Assert.Empty(_processes.Calls);
    }

    [Fact]
    public async Task DockerTrim_lance_fstrim_dans_la_distribution_docker_desktop()
    {
        _host.Running = true;
        var outcome = await Catalog().Resolve(ActionRequest.Of(DockerModule.DockerTrim)).Execute(ActionRequest.Of(DockerModule.DockerTrim), false, default);
        Assert.True(outcome.Ok);
        Assert.Equal(["-d", "docker-desktop", "-u", "root", "--", "fstrim", "/mnt/docker-desktop-disk"], Assert.Single(_processes.Calls).Args);
    }

    [Theory]
    [InlineData("Ubuntu")]
    [InlineData("docker-desktop-data")]
    [InlineData("Docker-Desktop")]
    [InlineData("docker-desktop && calc")]
    public void WslTerminate_refuse_une_autre_distribution(string distro)
    {
        var ex = Assert.Throws<ActionRejectedException>(() => Catalog().Resolve(Req(DockerModule.WslTerminate, "distro", distro)));
        Assert.Contains("distribution non autorisée", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WslTerminate_arrete_docker_desktop_mais_jamais_si_Docker_tourne()
    {
        var catalog = Catalog();
        var req = Req(DockerModule.WslTerminate, "distro", "docker-desktop");
        Assert.True((await catalog.Resolve(req).Execute(req, false, default)).Ok);
        Assert.Equal(["--terminate", "docker-desktop"], Assert.Single(_processes.Calls).Args);

        _host.Running = true;
        var refused = await catalog.Resolve(req).Execute(req, false, default);
        Assert.False(refused.Ok);
        Assert.Contains("n'est pas orpheline", refused.Message, StringComparison.Ordinal);
        Assert.Single(_processes.Calls);
    }

    [Fact]
    public async Task CompactDockerVhdx_est_admin_et_refuse_si_Docker_tourne()
    {
        _host.Running = true;
        _host.VhdxBytes = 14 * GB;
        var catalog = Catalog();
        var def = catalog.Resolve(ActionRequest.Of(DockerModule.CompactDockerVhdx));
        Assert.True(def.RequiresAdmin);
        var outcome = await def.Execute(ActionRequest.Of(DockerModule.CompactDockerVhdx), false, default);
        Assert.False(outcome.Ok);
        Assert.Contains("arrêtez-le avant le compactage", outcome.Message, StringComparison.Ordinal);
        Assert.Empty(_processes.Calls);
    }

    [Fact]
    public void CompactDockerVhdx_refuse_tout_chemin_passe_en_parametre()
    {
        var ex = Assert.Throws<ActionRejectedException>(() => Catalog().Resolve(Req(DockerModule.CompactDockerVhdx, "path", @"C:\Windows\System32\config\SYSTEM")));
        Assert.Contains("aucun paramètre attendu", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompactDockerVhdx_arrete_WSL_puis_lance_diskpart_avec_le_bon_script()
    {
        _host.VhdxBytes = 14 * GB;
        _processes.Respond = (file, _) =>
        {
            if (file.EndsWith("diskpart.exe", StringComparison.OrdinalIgnoreCase))
            {
                _host.VhdxBytes = 8 * GB;
            }

            return new ProcessResult(0, string.Empty, string.Empty, false);
        };

        var outcome = await Catalog().Resolve(ActionRequest.Of(DockerModule.CompactDockerVhdx)).Execute(ActionRequest.Of(DockerModule.CompactDockerVhdx), false, default);
        Assert.True(outcome.Ok, outcome.Message);
        Assert.Equal("VHDX 14 -> 8 GB", outcome.Message);
        Assert.Equal(2, _processes.Calls.Count);
        Assert.Equal(["--shutdown"], _processes.Calls[0].Args);
        Assert.Equal(@"C:\fake\wsl.exe", _processes.Calls[0].File);
        Assert.Equal(Path.Combine(Environment.SystemDirectory, "diskpart.exe"), _processes.Calls[1].File);
        // Securite : AUCUN fichier de script (un fichier dans %TEMP% pourrait etre remplace avant sa lecture par
        // diskpart lance en admin) : pas d'argument « /s », script transmis par l'entree standard.
        Assert.Empty(_processes.Calls[1].Args);
        Assert.Equal(
            $"select vdisk file=\"{FakeDockerHost.Vhdx}\"\r\nattach vdisk readonly\r\ncompact vdisk\r\ndetach vdisk\r\n",
            _processes.Inputs[1]);
    }

    [Fact]
    public async Task CompactDockerVhdx_en_echec_detache_le_VHDX()
    {
        _host.VhdxBytes = 14 * GB;
        var diskpartCalls = 0;
        _processes.Respond = (file, _) =>
        {
            if (file.EndsWith("diskpart.exe", StringComparison.OrdinalIgnoreCase))
            {
                diskpartCalls++;
                return new ProcessResult(diskpartCalls == 1 ? 2 : 0, string.Empty, string.Empty, false);
            }

            return new ProcessResult(0, string.Empty, string.Empty, false);
        };

        var outcome = await Catalog().Resolve(ActionRequest.Of(DockerModule.CompactDockerVhdx)).Execute(ActionRequest.Of(DockerModule.CompactDockerVhdx), false, default);
        Assert.False(outcome.Ok);
        Assert.Contains("diskpart a échoué (code 2", outcome.Message, StringComparison.Ordinal);
        Assert.Equal(2, diskpartCalls);
        Assert.Equal($"select vdisk file=\"{FakeDockerHost.Vhdx}\"\r\ndetach vdisk\r\n", _processes.Inputs[^1]);
    }

    [Fact]
    public async Task CompactDockerVhdx_annule_si_wsl_shutdown_echoue_ou_VHDX_absent()
    {
        _host.VhdxBytes = 14 * GB;
        _processes.Respond = (_, _) => new ProcessResult(1, string.Empty, string.Empty, false);
        var catalog = Catalog();
        var outcome = await catalog.Resolve(ActionRequest.Of(DockerModule.CompactDockerVhdx)).Execute(ActionRequest.Of(DockerModule.CompactDockerVhdx), false, default);
        Assert.False(outcome.Ok);
        Assert.Contains("wsl --shutdown a échoué", outcome.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_processes.Calls, c => c.File.EndsWith("diskpart.exe", StringComparison.OrdinalIgnoreCase));

        _host.VhdxPath = null;
        var missing = await catalog.Resolve(ActionRequest.Of(DockerModule.CompactDockerVhdx)).Execute(ActionRequest.Of(DockerModule.CompactDockerVhdx), false, default);
        Assert.Equal("Disque Docker (VHDX) introuvable", missing.Message);
    }

    [Fact]
    public async Task CompactDockerVhdx_refuse_un_chemin_que_diskpart_ne_peut_pas_lire()
    {
        _host.VhdxPath = @"C:\Users\Élodie\AppData\Local\Docker\wsl\disk\docker_data.vhdx";
        _host.VhdxBytes = 14 * GB;
        var outcome = await Catalog().Resolve(ActionRequest.Of(DockerModule.CompactDockerVhdx)).Execute(ActionRequest.Of(DockerModule.CompactDockerVhdx), false, default);
        Assert.False(outcome.Ok);
        Assert.Contains("non pris en charge par diskpart", outcome.Message, StringComparison.Ordinal);
        Assert.Empty(_processes.Calls);
    }

    // ------------------------------------------------------------ configuration et assemblage

    [Theory]
    [InlineData("docker-desktop && calc", "/mnt/docker-desktop-disk", "WslDistro invalide")]
    [InlineData("docker-desktop", "/mnt/x; rm -rf /", "DataMount invalide")]
    [InlineData("docker-desktop", "/etc", "DataMount invalide")]
    public void Configuration_injectee_refusee(string distro, string mount, string expected)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new DockerModule(Fixtures.Services(_dir, _time, _processes), _options with { WslDistro = distro, DataMount = mount }, _host));
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Periodicite_nulle_refusee_et_reglages_par_defaut_valides()
    {
        Assert.Contains("TrimEveryHours doit être > 0", (DockerOptions.Default with { TrimEveryHours = 0 }).Validate());
        Assert.Empty(DockerOptions.Default.Validate());
        Assert.Equal(6, DockerOptions.Default.MeasureEveryHours);
        Assert.Equal(1.5, DockerOptions.Default.CompactMinGainGB);
        Assert.Equal(10, DockerOptions.Default.CompactFallbackVhdxGB);
    }

    [Fact]
    public void ModulePack_enregistre_le_module_Docker_et_ses_actions()
    {
        var setup = ModulePack.Create(Fixtures.Services(_dir, _time, _processes));
        Assert.Contains(setup.Modules, m => m is DockerModule);
        var catalog = new ActionCatalog();
        setup.RegisterActions(catalog);
        foreach (var type in new[] { DockerModule.DockerPrune, DockerModule.DockerTrim, DockerModule.WslTerminate, DockerModule.CompactDockerVhdx })
        {
            Assert.True(catalog.IsKnown(type), type);
        }

        Assert.Empty(_processes.Calls);
    }

    public void Dispose() => _dir.Dispose();
}
