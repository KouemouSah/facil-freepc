using System.Net;
using System.Text;
using FreeRes.Core.Actions;
using FreeRes.Core.Decisions;
using FreeRes.Core.Engine;
using FreeRes.Modules.Ai;

namespace FreeRes.Modules.Tests;

/// <summary>Systeme simule : dossiers existants, tailles, processus par dossier.</summary>
internal sealed class FakeAiPlatform : IAiPlatform
{
    public HashSet<string> Dirs { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, long> Sizes { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, (int, long)> Processes { get; } = new(StringComparer.OrdinalIgnoreCase);

    public int SizeCalls { get; private set; }

    public bool DirectoryExists(string path) => Dirs.Contains(path);

    public long DirectorySize(string path)
    {
        SizeCalls++;
        return Sizes.GetValueOrDefault(path);
    }

    public (int Count, long Bytes) ProcessesUnder(string folder) => Processes.GetValueOrDefault(folder);
}

/// <summary>Serveur Ollama simule (null = injoignable).</summary>
internal sealed class FakeOllama : IOllamaClient
{
    public List<OllamaLoadedModel>? Loaded { get; set; }

    public List<OllamaInstalledModel>? Installed { get; set; } = [];

    public List<string> Unloads { get; } = [];

    public bool UnloadAccepted { get; set; } = true;

    public bool UnloadIgnored { get; set; }

    public string Endpoint => "http://127.0.0.1:11434/";

    public Task<IReadOnlyList<OllamaLoadedModel>?> LoadedAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<OllamaLoadedModel>?>(Loaded?.ToList());

    public Task<IReadOnlyList<OllamaInstalledModel>?> InstalledAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<OllamaInstalledModel>?>(Loaded is null ? null : Installed);

    public Task<bool> UnloadAsync(string model, CancellationToken cancellationToken)
    {
        Unloads.Add(model);
        if (UnloadAccepted && !UnloadIgnored)
        {
            Loaded?.RemoveAll(m => m.Name == model);
        }

        return Task.FromResult(UnloadAccepted);
    }
}

/// <summary>Gestionnaire HTTP simule : enregistre les requetes, repond selon le chemin.</summary>
internal sealed class FakeHttp(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<(HttpMethod Method, Uri Uri, string? Body)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        Requests.Add((request.Method, request.RequestUri!, body));
        return respond(request);
    }

    public static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

public sealed class OllamaClientTests
{
    [Theory]
    [InlineData(null, "http://127.0.0.1:11434/")]
    [InlineData("", "http://127.0.0.1:11434/")]
    [InlineData("127.0.0.1:11500", "http://127.0.0.1:11500/")]
    [InlineData("0.0.0.0", "http://127.0.0.1:11434/")]
    [InlineData("0.0.0.0:8080", "http://127.0.0.1:8080/")]
    [InlineData("http://localhost", "http://localhost:11434/")]
    [InlineData("http://localhost:80", "http://localhost/")]   // port 80 explicite conserve (Uri omet le port par defaut du schema)
    [InlineData("http://[::1]:11434", "http://[::1]:11434/")]
    public void Adresse_locale_retenue_port_par_defaut_11434(string? host, string expected) =>
        Assert.Equal(expected, HttpOllamaClient.ResolveEndpoint(host)?.ToString());

    [Theory]
    [InlineData("192.168.1.20:11434")]
    [InlineData("http://ollama.example.com")]
    [InlineData("http://127.0.0.1.evil.com:11434")]
    [InlineData("ftp://127.0.0.1")]
    [InlineData("http://user@10.0.0.1")]
    public async Task Adresse_hors_de_cette_machine_refusee_aucune_requete(string host)
    {
        Assert.Null(HttpOllamaClient.ResolveEndpoint(host));
        var http = new FakeHttp(_ => throw new InvalidOperationException("aucune requete ne doit partir"));
        using var client = new HttpOllamaClient(host, http);
        Assert.Null(await client.LoadedAsync(default));
        Assert.False(await client.UnloadAsync("x", default));
        Assert.Empty(http.Requests);
        Assert.Contains("ignore", client.Endpoint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Lecture_des_modeles_charges_et_installes_format_documente()
    {
        var http = new FakeHttp(r => r.RequestUri!.AbsolutePath switch
        {
            "/api/ps" => FakeHttp.Json("""{"models":[{"name":"llama3.2:3b","model":"llama3.2:3b","size":3400000000,"size_vram":2000000000,"expires_at":"2026-11-09T10:05:00+01:00"},{"name":"","size":1}]}"""),
            "/api/tags" => FakeHttp.Json("""{"models":[{"name":"llama3.2:3b","size":2019393189},{"name":"nomic-embed-text:latest","size":274302450}]}"""),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        using var client = new HttpOllamaClient(null, http);

        var loaded = Assert.Single((await client.LoadedAsync(default))!);
        Assert.Equal("llama3.2:3b", loaded.Name);
        Assert.Equal(3400000000, loaded.SizeBytes);
        Assert.Equal(2000000000, loaded.VramBytes);
        Assert.Equal(1400000000, loaded.SystemRamBytes);
        Assert.Equal(new DateTimeOffset(2026, 11, 9, 10, 5, 0, TimeSpan.FromHours(1)), loaded.ExpiresAt);

        var installed = (await client.InstalledAsync(default))!;
        Assert.Equal(2, installed.Count);
        Assert.Equal(274302450, installed[1].SizeBytes);
    }

    [Fact]
    public async Task Dechargement_par_keep_alive_zero_sans_prompt()
    {
        var http = new FakeHttp(_ => FakeHttp.Json("""{"model":"llama3.2:3b","done":true,"done_reason":"unload"}"""));
        using var client = new HttpOllamaClient(null, http);

        Assert.True(await client.UnloadAsync("llama3.2:3b", default));
        var req = Assert.Single(http.Requests);
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("http://127.0.0.1:11434/api/generate", req.Uri.ToString());
        Assert.Equal("""{"model":"llama3.2:3b","keep_alive":0}""", req.Body);
    }

    [Fact]
    public async Task Serveur_absent_ou_reponse_invalide_traite_comme_absent()
    {
        using var down = new HttpOllamaClient(null, new FakeHttp(_ => throw new HttpRequestException("refuse")));
        Assert.Null(await down.LoadedAsync(default));
        Assert.False(await down.UnloadAsync("m", default));

        using var garbage = new HttpOllamaClient(null, new FakeHttp(_ => FakeHttp.Json("<html>")));
        Assert.Null(await garbage.LoadedAsync(default));

        using var error = new HttpOllamaClient(null, new FakeHttp(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        Assert.Null(await error.InstalledAsync(default));
        Assert.False(await error.UnloadAsync("m", default));
    }
}

public sealed class AiModuleTests : IDisposable
{
    private const long GB = 1024L * 1024 * 1024;
    private const string OllamaInstall = @"C:\P\Ollama";
    private const string OllamaModels = @"C:\U\.ollama\models";
    private const string LmInstall = @"C:\P\LM Studio";
    private const string LmModels = @"C:\U\.lmstudio\models";

    private readonly TempDir _dir = new();
    private readonly FakeTime _time = new(Fixtures.Start);
    private readonly FakeAiPlatform _p = new();
    private readonly FakeOllama _ollama = new();
    private readonly FakeLauncher _launcher = new();

    private static readonly IReadOnlyList<AiAppDefinition> Apps =
    [
        new("ollama", "Ollama", [OllamaInstall], [OllamaModels], ActionsVerified: true),
        new("lmstudio", "LM Studio", [LmInstall], [LmModels], ActionsVerified: false),
    ];

    private AiModule Module() => new(Fixtures.Services(_dir, _time) with { Launcher = _launcher }, _p, _ollama, Apps);

    private EngineContext Context(double ramAvail = 6, double commitPct = 50, double diskFree = 40)
    {
        var c = Fixtures.Context(_time, diskFree);
        return c with { Snapshot = c.Snapshot with { RamAvailGB = ramAvail, CommitPct = commitPct } };
    }

    private static OllamaLoadedModel Model(string name, double gb, double vramGb = 0) => new(name, (long)(gb * GB), (long)(vramGb * GB), null);

    private static ActionRequest Unload(string model) => new(AiModule.UnloadOllamaModel, new Dictionary<string, string> { ["model"] = model });

    private static ActionCatalog Catalog(AiModule m)
    {
        var c = new ActionCatalog();
        m.RegisterActions(c);
        return c;
    }

    [Fact]
    public async Task Rien_d_installe_ni_serveur_aucun_statut_aucune_proposition()
    {
        var m = Module();
        var eval = await m.EvaluateAsync(Context(ramAvail: 1), default);
        Assert.Empty(eval.Proposals);
        Assert.Empty(eval.AutoActions);
        Assert.Empty(m.LastStatus);
    }

    [Fact]
    public async Task Detection_par_chemin_et_mesures_sans_action_pour_application_non_verifiee()
    {
        _p.Dirs.UnionWith([LmInstall, LmModels]);
        _p.Processes[LmInstall] = (3, 5 * GB);
        _p.Sizes[LmModels] = 9 * GB;
        var m = Module();

        var eval = await m.EvaluateAsync(Context(ramAvail: 1, diskFree: 40), default);

        var st = Assert.Single(m.LastStatus);
        Assert.Equal("lmstudio", st.Id);
        Assert.True(st.Installed);
        Assert.Equal(3, st.ProcessCount);
        Assert.Equal(5, st.WorkingSetGB);
        Assert.Equal(9, st.ModelsDiskGB);
        Assert.Contains("non verifie", st.Note, StringComparison.Ordinal);
        Assert.Empty(eval.Proposals);   // pression memoire, mais aucune action LM Studio non verifiee
    }

    [Fact]
    public async Task Ollama_dans_Docker_sans_installation_locale_est_detecte_par_son_API()
    {
        _ollama.Loaded = [Model("qwen2.5:7b", 5.5, 0)];
        _ollama.Installed = [new("qwen2.5:7b", 4 * GB)];
        var m = Module();

        await m.EvaluateAsync(Context(), default);

        var st = Assert.Single(m.LastStatus);
        Assert.False(st.Installed);
        Assert.Single(st.LoadedModels);
        Assert.Equal(1, st.InstalledModelCount);
        Assert.Contains("Serveur actif", st.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Memoire_confortable_aucune_proposition_de_dechargement()
    {
        _ollama.Loaded = [Model("llama3.2:3b", 3.2)];
        var eval = await Module().EvaluateAsync(Context(), default);
        Assert.Empty(eval.Proposals);
    }

    [Theory]
    [InlineData(2.5, 50, Severity.Warn)]    // RAM disponible sous le seuil d'alerte
    [InlineData(6, 88, Severity.Warn)]      // commit au-dessus du seuil d'alerte
    [InlineData(1.0, 50, Severity.Crit)]    // RAM critique
    [InlineData(6, 96, Severity.Crit)]      // commit critique
    public async Task Tension_memoire_propose_de_decharger_chaque_modele_charge(double ram, double commit, Severity expected)
    {
        _ollama.Loaded = [Model("llama3.2:3b", 3.2, 1.2), Model("Nomic/Embed:Latest", 0.5)];
        var eval = await Module().EvaluateAsync(Context(ramAvail: ram, commitPct: commit), default);

        Assert.Equal(2, eval.Proposals.Count);
        Assert.All(eval.Proposals, p => Assert.Equal(expected, p.Severity));
        var first = eval.Proposals[0];
        Assert.Equal(AiModule.UnloadOllamaModel, first.Action.Type);
        Assert.Equal("llama3.2:3b", first.Action.Parameters["model"]);
        Assert.Equal(3.2, first.EstGainGB);
        Assert.Contains("1.2 GB en memoire graphique", first.Detail, StringComparison.Ordinal);
        Assert.Contains("n'est pas supprime", first.Detail, StringComparison.Ordinal);
        Assert.StartsWith("ai-ollama-unload-nomic-embed-latest-", eval.Proposals[1].Id, StringComparison.Ordinal);
    }

    [Fact]
    public void Identifiants_de_proposition_valides_et_distincts_pour_des_noms_proches()
    {
        var a = AiModule.AppIdFragment("llama3.2:3b");
        var b = AiModule.AppIdFragment("llama3-2-3b");
        Assert.NotEqual(a, b);
        foreach (var name in new[] { "llama3.2:3b", ":::", new string('x', 200), "Modèle/éèà:latest" })
        {
            var id = "ai-ollama-unload-" + AiModule.AppIdFragment(name);
            Assert.Matches("^[a-z0-9-]{3,60}$", id);
            _ = new Proposal(id, "t", "d", 0, Severity.Info, Unload("m"));   // ne leve pas
        }
    }

    [Fact]
    public async Task Disque_presque_plein_propose_d_ouvrir_le_dossier_jamais_de_supprimer()
    {
        _p.Dirs.UnionWith([OllamaModels, LmModels]);
        _p.Sizes[OllamaModels] = 12 * GB;
        _p.Sizes[LmModels] = 1 * GB;   // < 2 GB : pas de proposition
        var m = Module();

        var eval = await m.EvaluateAsync(Context(diskFree: 20), default);

        var p = Assert.Single(eval.Proposals);
        Assert.Equal("ai-ollama-models-disk", p.Id);
        Assert.Equal(AiModule.OpenModelsFolder, p.Action.Type);
        Assert.Equal(0, p.EstGainGB);
        Assert.Contains("ne supprime JAMAIS", p.Detail, StringComparison.Ordinal);

        var c = Catalog(m);
        var outcome = await c.Resolve(p.Action).Execute(p.Action, false, default);
        Assert.True(outcome.Ok, outcome.Message);
        var launch = Assert.Single(_launcher.Launches);
        Assert.EndsWith("explorer.exe", launch.File, StringComparison.OrdinalIgnoreCase);
        Assert.Equal([OllamaModels], launch.Args);
    }

    [Fact]
    public async Task Taille_des_modeles_mise_en_cache_6_heures()
    {
        _p.Dirs.Add(OllamaModels);
        var m = Module();
        await m.EvaluateAsync(Context(), default);
        await m.EvaluateAsync(Context(), default);
        Assert.Equal(1, _p.SizeCalls);
        _time.Advance(TimeSpan.FromHours(6));
        await m.EvaluateAsync(Context(), default);
        Assert.Equal(2, _p.SizeCalls);
    }

    [Fact]
    public async Task Dechargement_reel_verifie_apres_coup()
    {
        _ollama.Loaded = [Model("llama3.2:3b", 3.2)];
        var c = Catalog(Module());

        var outcome = await c.Resolve(Unload("llama3.2:3b")).Execute(Unload("llama3.2:3b"), false, default);

        Assert.True(outcome.Ok, outcome.Message);
        Assert.Contains("3.2 GB liberes", outcome.Message, StringComparison.Ordinal);
        Assert.Equal(["llama3.2:3b"], _ollama.Unloads);
    }

    [Fact]
    public async Task Simulation_ne_decharge_rien()
    {
        _ollama.Loaded = [Model("llama3.2:3b", 3.2)];
        var c = Catalog(Module());
        var outcome = await c.Resolve(Unload("llama3.2:3b")).Execute(Unload("llama3.2:3b"), true, default);
        Assert.True(outcome.Ok);
        Assert.Empty(_ollama.Unloads);
    }

    [Fact]
    public async Task Modele_non_charge_aucun_appel_et_serveur_absent_echec()
    {
        _ollama.Loaded = [Model("autre", 1)];
        var c = Catalog(Module());
        var outcome = await c.Resolve(Unload("llama3.2:3b")).Execute(Unload("llama3.2:3b"), false, default);
        Assert.True(outcome.Ok);
        Assert.Contains("deja plus charge", outcome.Message, StringComparison.Ordinal);
        Assert.Empty(_ollama.Unloads);

        _ollama.Loaded = null;
        var down = await c.Resolve(Unload("autre")).Execute(Unload("autre"), false, default);
        Assert.False(down.Ok);
        Assert.Contains("injoignable", down.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dechargement_refuse_ou_sans_effet_signale_en_echec()
    {
        _ollama.Loaded = [Model("m", 2)];
        _ollama.UnloadAccepted = false;
        var c = Catalog(Module());
        var refused = await c.Resolve(Unload("m")).Execute(Unload("m"), false, default);
        Assert.False(refused.Ok);
        Assert.Contains("refuse", refused.Message, StringComparison.Ordinal);

        _ollama.UnloadAccepted = true;
        _ollama.UnloadIgnored = true;
        var still = await c.Resolve(Unload("m")).Execute(Unload("m"), false, default);
        Assert.False(still.Ok);
        Assert.Contains("toujours charge", still.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parametres_invalides_rejetes_avant_execution()
    {
        var c = Catalog(Module());
        Assert.Throws<ActionRejectedException>(() => c.Resolve(new ActionRequest(AiModule.UnloadOllamaModel, new Dictionary<string, string>())));
        Assert.Throws<ActionRejectedException>(() => c.Resolve(Unload("a\nb")));
        Assert.Throws<ActionRejectedException>(() => c.Resolve(Unload(new string('x', 201))));
        Assert.Throws<ActionRejectedException>(() => c.Resolve(new ActionRequest(AiModule.UnloadOllamaModel, new Dictionary<string, string> { ["model"] = "m", ["host"] = "10.0.0.1" })));
        Assert.Throws<ActionRejectedException>(() => c.Resolve(new ActionRequest(AiModule.OpenModelsFolder, new Dictionary<string, string> { ["app"] = "inconnue" })));
        Assert.Throws<ActionRejectedException>(() => c.Resolve(new ActionRequest(AiModule.OpenModelsFolder, new Dictionary<string, string> { ["app"] = @"C:\Windows" })));
    }

    [Fact]
    public void Aucune_action_ne_demande_les_droits_administrateur()
    {
        var c = Catalog(Module());
        Assert.False(c.Resolve(Unload("m")).RequiresAdmin);
        Assert.False(c.Resolve(new ActionRequest(AiModule.OpenModelsFolder, new Dictionary<string, string> { ["app"] = "ollama" })).RequiresAdmin);
    }

    [Fact]
    public void Emplacements_par_defaut_aucune_detection_par_nom_de_processus()
    {
        var apps = AiModule.DefaultApps();
        Assert.Equal(["ollama", "lmstudio", "gpt4all", "jan"], apps.Select(a => a.Id));
        Assert.All(apps, a => Assert.All(a.InstallDirs.Concat(a.ModelDirs), d => Assert.Contains('\\', d)));
        Assert.Equal(["ollama"], apps.Where(a => a.ActionsVerified).Select(a => a.Id));
    }

    public void Dispose() => _dir.Dispose();
}
