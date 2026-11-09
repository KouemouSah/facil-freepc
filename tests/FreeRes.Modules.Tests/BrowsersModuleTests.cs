using FreeRes.Core;
using FreeRes.Core.Actions;
using FreeRes.Core.Decisions;
using FreeRes.Core.Engine;
using FreeRes.Core.Platform;
using FreeRes.Modules.Browsers;

namespace FreeRes.Modules.Tests;

/// <summary>Plateforme simulee : fichiers, processus et fermetures pilotes ; AUCUN vrai navigateur touche.</summary>
internal sealed class FakeBrowserPlatform : IBrowserPlatform
{
    public Dictionary<string, string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, DateTime> WriteTimes { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<BrowserProcess> Running { get; } = [];

    public HashSet<string> SignedXpis { get; } = new(StringComparer.OrdinalIgnoreCase);

    public int CloseCode { get; set; }

    public bool QuitAccepted { get; set; } = true;

    /// <summary>Le navigateur se ferme-t-il quand on le lui demande (sinon : page qui demande confirmation...).</summary>
    public bool ExitsOnRequest { get; set; } = true;

    public List<int> CloseRequests { get; } = [];

    public bool FileExists(string path) => Files.ContainsKey(path);

    public string? ReadAllText(string path) => Files.GetValueOrDefault(path);

    public DateTime? LastWriteUtc(string path) => WriteTimes.TryGetValue(path, out var t) ? t : null;

    public IReadOnlyList<string> Directories(string path) =>
        WriteTimes.Keys.Concat(Files.Keys)
            .Where(f => f.StartsWith(path.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
            .Select(f => f[(path.TrimEnd('\\').Length + 1)..].Split('\\')[0])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(d => Path.Combine(path, d))
            .ToList();

    public IReadOnlyList<BrowserProcess> Processes(IReadOnlyCollection<string> exeNames) =>
        Running.Where(p => exeNames.Contains(Path.GetFileName(p.ImagePath ?? string.Empty), StringComparer.OrdinalIgnoreCase)).ToList();

    public int CloseChromium(int mainPid, DateTimeOffset startTime)
    {
        CloseRequests.Add(mainPid);
        if (CloseCode == 0 && ExitsOnRequest)
        {
            Running.Clear();
        }

        return CloseCode;
    }

    public bool SendFirefoxQuit(int mainPid)
    {
        CloseRequests.Add(mainPid);
        if (QuitAccepted && ExitsOnRequest)
        {
            Running.Clear();
        }

        return QuitAccepted;
    }

    public bool IsSignedXpi(string path) => SignedXpis.Contains(path);
}

internal sealed class FakeLauncher : IProcessLauncher
{
    public List<(string File, IReadOnlyList<string> Args)> Launches { get; } = [];

    public bool Launch(string fileName, IReadOnlyList<string> arguments)
    {
        Launches.Add((fileName, arguments));
        return true;
    }
}

public sealed class BrowsersModuleTests : IDisposable
{
    private const long GB = 1024L * 1024 * 1024;
    private const string ChromeExe = @"C:\B\Chrome\chrome.exe";
    private const string FirefoxExe = @"C:\B\Firefox\firefox.exe";
    private const string FirefoxDevExe = @"C:\B\FirefoxDev\firefox.exe";
    private const string BraveExe = @"C:\B\Brave\brave.exe";
    private const string ChromeData = @"C:\D\Chrome";
    private const string Profiles = @"C:\P\Profiles";
    private const string Xpi = @"C:\App\extensions\facil-freepc-firefox.xpi";

    private readonly TempDir _dir = new();
    private readonly FakeTime _time = new(Fixtures.Start);
    private readonly FakeBrowserPlatform _p = new();
    private readonly FakeLauncher _launcher = new();
    private readonly IReadOnlyList<BrowserDefinition> _catalog =
    [
        new("chrome", "Google Chrome", BrowserEngine.Chromium, "chrome.exe", [ChromeExe], ChromeData, true, "chrome://settings/performance", "chrome://settings/onStartup"),
        new("brave", "Brave", BrowserEngine.Chromium, "brave.exe", [BraveExe], @"C:\D\Brave", false, null, null),
        new("firefox", "Mozilla Firefox", BrowserEngine.Gecko, "firefox.exe", [FirefoxExe], null, true, null, "about:preferences#general"),
        new("firefox-dev", "Firefox Developer Edition", BrowserEngine.Gecko, "firefox.exe", [FirefoxDevExe], null, true, null, "about:preferences#general"),
    ];

    private BrowsersModule Module(int quitTimeoutSeconds = 2) =>
        new(new ModuleServices(new AppPaths(_dir.Sub("data")), new Core.Logging.EventLog(_dir.Sub("logs"), _time), _time, new NullProbe(), new FakeProcessRunner(), new MemoryState(), _launcher),
            new BrowsersOptions(QuitTimeoutSeconds: quitTimeoutSeconds), _p, _catalog, Profiles, Xpi);

    private void Install(string exe) => _p.Files[exe] = string.Empty;

    /// <summary>Instance de navigateur : 1 processus principal + enfants (meme image), memoire repartie.</summary>
    private void Run(string exe, int mainPid, long totalBytes, double hours, int children = 3)
    {
        var start = _time.Now.AddHours(-hours);
        _p.Running.Add(new BrowserProcess(mainPid, 4, exe, totalBytes / (children + 1), start));
        for (var i = 1; i <= children; i++)
        {
            _p.Running.Add(new BrowserProcess(mainPid + i, mainPid, exe, totalBytes / (children + 1), start.AddMinutes(1)));
        }
    }

    private void ChromeLocalState(int? memorySaver, params string[] activeProfiles)
    {
        var saver = memorySaver is null ? string.Empty : $"\"performance_tuning\":{{\"high_efficiency_mode\":{{\"state\":{memorySaver}}}}},";
        _p.Files[Path.Combine(ChromeData, "Local State")] = $"{{{saver}\"profile\":{{\"last_active_profiles\":[{string.Join(",", activeProfiles.Select(a => $"\"{a}\""))}]}}}}";
    }

    private string FirefoxProfile(string name, bool restoreSession, bool extensionActive = false, int minutesAgo = 1)
    {
        var dir = Path.Combine(Profiles, name);
        _p.WriteTimes[Path.Combine(dir, "sessionstore-backups", "recovery.jsonlz4")] = _time.Now.UtcDateTime.AddMinutes(-minutesAgo);
        _p.Files[Path.Combine(dir, "prefs.js")] = restoreSession ? "user_pref(\"browser.startup.page\", 3);" : "user_pref(\"browser.startup.page\", 1);";
        _p.Files[Path.Combine(dir, "extensions.json")] = extensionActive
            ? $"{{\"addons\":[{{\"id\":\"{BrowserCatalog.FirefoxExtensionId}\",\"active\":true}}]}}"
            : "{\"addons\":[]}";
        return dir;
    }

    private EngineContextBuilder Ctx => new(_time);

    [Fact]
    public void Seuls_les_navigateurs_installes_sont_inspectes()
    {
        Install(ChromeExe);
        var statuses = Module().Inspect();
        Assert.Equal(["chrome"], statuses.Select(s => s.Id));
    }

    [Fact]
    public void Editions_homonymes_de_Firefox_distinguees_par_dossier_et_processus_principal_par_parent()
    {
        Install(FirefoxExe);
        Install(FirefoxDevExe);
        FirefoxProfile("dev", restoreSession: true);
        Run(FirefoxDevExe, 100, 3 * GB, 20, children: 5);
        var statuses = Module().Inspect();

        var dev = statuses.Single(s => s.Id == "firefox-dev");
        Assert.True(dev.Running);
        Assert.Equal(6, dev.ProcessCount);
        Assert.Equal(1, dev.MainProcessCount);
        Assert.Equal(20, dev.UptimeHours);
        Assert.False(statuses.Single(s => s.Id == "firefox").Running);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(2, false)]
    [InlineData(null, false)]   // valeur par defaut du navigateur : on ne harcele pas
    public async Task Economiseur_de_memoire_Chromium_propose_seulement_s_il_est_explicitement_desactive(int? state, bool expected)
    {
        Install(ChromeExe);
        ChromeLocalState(state, "Default");
        var eval = await Module().EvaluateAsync(Ctx.Build(), default);
        Assert.Equal(expected, eval.Proposals.Any(p => p.Id == "browser-chrome-memory-saver"));
        if (expected)
        {
            var action = eval.Proposals.Single(p => p.Id == "browser-chrome-memory-saver").Action;
            Assert.Equal(BrowsersModule.OpenBrowserSettings, action.Type);
            Assert.Equal("performance", action.Parameters["page"]);
        }
    }

    [Fact]
    public async Task Chrome_lourd_et_ouvert_longtemps_redemarrage_propose_seuil_relatif_a_la_RAM()
    {
        Install(ChromeExe);
        ChromeLocalState(2, "Default");
        Run(ChromeExe, 200, 6 * GB, 13);
        var p = Assert.Single((await Module().EvaluateAsync(Ctx.Build(), default)).Proposals);
        Assert.Equal("browser-chrome-restart", p.Id);
        Assert.Equal(Severity.Warn, p.Severity);
        Assert.Equal(5.5, BrowsersOptions.Default.RestartThresholdGB(15.69));   // 35 % de 15,69 GB

        _p.Running.Clear();
        Run(ChromeExe, 200, 5 * GB, 13);   // sous 5,5 GB
        Assert.Empty((await Module().EvaluateAsync(Ctx.Build(), default)).Proposals);
        _p.Running.Clear();
        Run(ChromeExe, 200, 6 * GB, 11);   // ouvert depuis moins de 12 h
        Assert.Empty((await Module().EvaluateAsync(Ctx.Build(), default)).Proposals);
    }

    [Fact]
    public async Task Chrome_avec_plusieurs_profils_ouverts_jamais_redemarre()
    {
        Install(ChromeExe);
        ChromeLocalState(2, "Default", "Profile 1");
        Run(ChromeExe, 200, 8 * GB, 20);
        var m = Module();
        Assert.Empty((await m.EvaluateAsync(Ctx.Build(), default)).Proposals);
        Assert.StartsWith("Plusieurs profils ouverts", m.LastStatus.Single().RestartNote, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deux_instances_principales_jamais_redemarre()
    {
        Install(ChromeExe);
        ChromeLocalState(2, "Default");
        Run(ChromeExe, 200, 4 * GB, 20);
        Run(ChromeExe, 300, 4 * GB, 20);   // 2e instance (autre dossier de donnees)
        var m = Module();
        Assert.Empty((await m.EvaluateAsync(Ctx.Build(), default)).Proposals);
        Assert.Equal("Plusieurs instances : redemarrage non sur", m.LastStatus.Single().RestartNote);
    }

    [Fact]
    public async Task Navigateur_au_redemarrage_non_verifie_jamais_propose()
    {
        Install(BraveExe);
        Run(BraveExe, 400, 9 * GB, 30);
        var m = Module();
        Assert.Empty((await m.EvaluateAsync(Ctx.Build(), default)).Proposals);
        Assert.Equal("Redemarrage non verifie pour ce navigateur", m.LastStatus.Single().RestartNote);
    }

    [Fact]
    public async Task Firefox_sans_restauration_de_session_propose_le_reglage_jamais_le_redemarrage()
    {
        Install(FirefoxExe);
        FirefoxProfile("std", restoreSession: false, extensionActive: true);
        Run(FirefoxExe, 500, 7 * GB, 20);
        var eval = await Module().EvaluateAsync(Ctx.Build(), default);
        var p = Assert.Single(eval.Proposals);
        Assert.Equal("browser-firefox-session-restore", p.Id);
        Assert.Equal("startup", p.Action.Parameters["page"]);
    }

    [Fact]
    public async Task Firefox_extension_proposee_seulement_si_absente_et_signee()
    {
        Install(FirefoxExe);
        FirefoxProfile("std", restoreSession: true, extensionActive: false);
        Run(FirefoxExe, 500, 1 * GB, 1);
        Assert.Empty((await Module().EvaluateAsync(Ctx.Build(), default)).Proposals);   // extension non signee

        _p.SignedXpis.Add(Xpi);
        Assert.Equal("browser-firefox-extension", Assert.Single((await Module().EvaluateAsync(Ctx.Build(), default)).Proposals).Id);

        FirefoxProfile("std", restoreSession: true, extensionActive: true);
        Assert.Empty((await Module().EvaluateAsync(Ctx.Build(), default)).Proposals);
    }

    [Fact]
    public void Deux_editions_de_Firefox_ouvertes_profil_ambigu()
    {
        Install(FirefoxExe);
        Install(FirefoxDevExe);
        FirefoxProfile("std", restoreSession: true);
        Run(FirefoxExe, 500, 7 * GB, 20);
        Run(FirefoxDevExe, 600, 7 * GB, 20);
        Assert.All(Module().Inspect(), s => Assert.Equal("Plusieurs editions de Firefox ouvertes : profil ambigu", s.RestartNote));
    }

    // ------------------------------------------------------------------ actions

    private (ActionCatalog Catalog, BrowsersModule Module) Catalog(int quitTimeoutSeconds = 2)
    {
        var m = Module(quitTimeoutSeconds);
        var c = new ActionCatalog();
        m.RegisterActions(c);
        return (c, m);
    }

    private static ActionRequest Restart(string browser) => new(BrowsersModule.RestartBrowser, new Dictionary<string, string> { ["browser"] = browser });

    [Fact]
    public async Task Redemarrage_Chromium_fermeture_propre_puis_relance_avec_restauration_du_profil()
    {
        Install(ChromeExe);
        ChromeLocalState(2, "Profile 3");
        Run(ChromeExe, 200, 6 * GB, 20);
        var (c, _) = Catalog();
        var outcome = await c.Resolve(Restart("chrome")).Execute(Restart("chrome"), false, default);

        Assert.True(outcome.Ok, outcome.Message);
        Assert.Equal([200], _p.CloseRequests);   // processus PRINCIPAL seul (verifie : enregistrer les enfants fait echouer RM)
        var launch = Assert.Single(_launcher.Launches);
        Assert.Equal(ChromeExe, launch.File);
        Assert.Equal(["--profile-directory=Profile 3", "--restore-last-session"], launch.Args);
    }

    [Fact]
    public async Task Fermeture_refusee_par_le_navigateur_rien_n_est_force_ni_relance()
    {
        Install(ChromeExe);
        ChromeLocalState(2, "Default");
        Run(ChromeExe, 200, 6 * GB, 20);
        _p.CloseCode = 351;
        var (c, _) = Catalog();
        var outcome = await c.Resolve(Restart("chrome")).Execute(Restart("chrome"), false, default);
        Assert.False(outcome.Ok);
        Assert.Contains("rien n'a ete force", outcome.Message, StringComparison.Ordinal);
        Assert.Empty(_launcher.Launches);
        Assert.Equal(4, _p.Running.Count);   // toujours ouvert
    }

    [Fact]
    public async Task Navigateur_qui_ne_se_ferme_pas_dans_le_delai_echec_sans_relance()
    {
        Install(ChromeExe);
        ChromeLocalState(2, "Default");
        Run(ChromeExe, 200, 6 * GB, 20);
        _p.ExitsOnRequest = false;   // ex. « Quitter le site ? » affiche par une page
        var (c, _) = Catalog(quitTimeoutSeconds: 1);
        var outcome = await c.Resolve(Restart("chrome")).Execute(Restart("chrome"), false, default);
        Assert.False(outcome.Ok);
        Assert.Contains("ne s'est pas ferme", outcome.Message, StringComparison.Ordinal);
        Assert.Empty(_launcher.Launches);
    }

    [Fact]
    public async Task Redemarrage_Firefox_par_Ctrl_Maj_Q_puis_relance_sur_le_meme_profil()
    {
        Install(FirefoxExe);
        var profile = FirefoxProfile("std", restoreSession: true);
        Run(FirefoxExe, 500, 7 * GB, 20);
        var (c, _) = Catalog();
        var outcome = await c.Resolve(Restart("firefox")).Execute(Restart("firefox"), false, default);
        Assert.True(outcome.Ok, outcome.Message);
        Assert.Equal(["-profile", profile], Assert.Single(_launcher.Launches).Args);
    }

    [Fact]
    public async Task Firefox_impossible_a_mettre_au_premier_plan_aucune_touche_rien_ferme()
    {
        Install(FirefoxExe);
        FirefoxProfile("std", restoreSession: true);
        Run(FirefoxExe, 500, 7 * GB, 20);
        _p.QuitAccepted = false;
        var (c, _) = Catalog();
        var outcome = await c.Resolve(Restart("firefox")).Execute(Restart("firefox"), false, default);
        Assert.False(outcome.Ok);
        Assert.Contains("aucune touche envoyee", outcome.Message, StringComparison.Ordinal);
        Assert.Empty(_launcher.Launches);
    }

    [Fact]
    public async Task Conditions_reverifiees_au_moment_du_redemarrage()
    {
        Install(FirefoxExe);
        FirefoxProfile("std", restoreSession: true);
        Run(FirefoxExe, 500, 7 * GB, 20);
        var (c, _) = Catalog();
        FirefoxProfile("std", restoreSession: false);   // l'utilisateur a change le reglage entre-temps
        var outcome = await c.Resolve(Restart("firefox")).Execute(Restart("firefox"), false, default);
        Assert.False(outcome.Ok);
        Assert.Contains("Restauration de session desactivee", outcome.Message, StringComparison.Ordinal);
        Assert.Empty(_p.CloseRequests);
    }

    [Theory]
    [InlineData(BrowsersModule.RestartBrowser, "browser=inconnu")]
    [InlineData(BrowsersModule.RestartBrowser, "browser=chrome;extra=1")]
    [InlineData(BrowsersModule.InstallFirefoxExtension, "browser=chrome")]
    [InlineData(BrowsersModule.OpenBrowserSettings, "browser=brave;page=performance")]
    [InlineData(BrowsersModule.OpenBrowserSettings, "browser=chrome;page=../../etc")]
    public void Parametres_invalides_refuses(string type, string parameters)
    {
        var (c, _) = Catalog();
        var p = parameters.Split(';').Select(x => x.Split('=')).ToDictionary(x => x[0], x => x[1]);
        Assert.Throws<ActionRejectedException>(() => c.Resolve(new ActionRequest(type, p)));
    }

    [Fact]
    public async Task Page_de_reglage_ouverte_dans_le_bon_navigateur()
    {
        Install(ChromeExe);
        var (c, _) = Catalog();
        var req = new ActionRequest(BrowsersModule.OpenBrowserSettings, new Dictionary<string, string> { ["browser"] = "chrome", ["page"] = "performance" });
        Assert.True((await c.Resolve(req).Execute(req, false, default)).Ok);
        var launch = Assert.Single(_launcher.Launches);
        Assert.Equal(ChromeExe, launch.File);
        Assert.Equal(["chrome://settings/performance"], launch.Args);
    }

    [Fact]
    public void Etat_Chromium_illisible_ou_absent_traite_comme_inconnu()
    {
        _p.Files[Path.Combine(ChromeData, "Local State")] = "{ tronque";
        var reader = new BrowserStateReader(_p);
        Assert.Null(reader.ChromiumMemorySaver(ChromeData));
        Assert.Empty(reader.ChromiumActiveProfiles(ChromeData));
        Assert.Null(reader.ChromiumMemorySaver(@"C:\absent"));
    }

    public void Dispose() => _dir.Dispose();
}

/// <summary>Contexte de cycle : machine de reference (15,69 GB), memoire confortable.</summary>
internal sealed class EngineContextBuilder(FakeTime time)
{
    public EngineContext Build() => Fixtures.Context(time);
}
