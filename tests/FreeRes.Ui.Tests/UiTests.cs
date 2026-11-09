using FreeRes.Core;
using FreeRes.Core.Actions;
using FreeRes.Core.Decisions;
using FreeRes.Core.Engine;
using FreeRes.Core.Metrics;
using FreeRes.Host;
using FreeRes.Modules.Ai;
using FreeRes.Modules.Apps;
using FreeRes.Modules.Browsers;
using FreeRes.Modules.Docker;
using Microsoft.Playwright;

namespace FreeRes.Ui.Tests;

/// <summary>Mesures fixes : machine de reference (16 GB, disque 235 GB).</summary>
internal sealed class StaticProbe : ISystemProbe
{
    public SystemSnapshot Capture() => new(
        DateTimeOffset.Now, 15.69, 2.4, 85, 23.69, 20.4, 86.1, "C:", 235.48, 43.9, 1, false,
        [new ProcessUsage("firefox", 30, 4.2), new ProcessUsage("<img src=x onerror=alert(1)>", 1, 0.5)]);
}

/// <summary>
/// Modules simules qui exposent les VRAIS types d'etat des modules (le JavaScript est donc verifie contre la
/// forme JSON reelle) et des propositions dont une contient du HTML (doit s'afficher en texte).
/// </summary>
internal sealed class UiModules : IResourceModule
{
    public string Name => "ui";

    public Task<ModuleEvaluation> EvaluateAsync(EngineContext context, CancellationToken cancellationToken) =>
        Task.FromResult(new ModuleEvaluation(
            [
                new Proposal("ui-conseil", "Vider la corbeille", "La corbeille occupe 1,2 GB.", 1.2, Severity.Info, ActionRequest.Of("UiCount")),
                new Proposal("ui-critique", "Redémarrer <b>Firefox</b>", "Firefox occupe 6 GB depuis 3 jours.", 5.5, Severity.Crit, ActionRequest.Of("UiCount")),
                new Proposal("ui-admin", "Passer un service en manuel", "Service tiers inutile au démarrage.", 0.2, Severity.Warn, ActionRequest.Of("UiAdmin")),
            ],
            []));
}

internal sealed class StatusOf(string name, object status) : IResourceModule, IStatusSource
{
    public string Name => name;

    public object? CurrentStatus => status;

    public Task<ModuleEvaluation> EvaluateAsync(EngineContext context, CancellationToken cancellationToken) => Task.FromResult(ModuleEvaluation.Empty);
}

/// <summary>Serveur reel + Edge installe, partages par les tests de la classe (le serveur est relance a chaque test).</summary>
public sealed class UiFixture : IAsyncLifetime
{
    public IPlaywright Playwright { get; private set; } = null!;

    public IBrowser Browser { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        // Delai d'attente porte a 15 s : la suite tourne en parallele des autres (build.ps1) avec un Edge demarre a froid ;
        // un defaut reel echoue toujours, seulement plus tard.
        Assertions.SetDefaultExpectTimeout(15000);
        Playwright = await Microsoft.Playwright.Playwright.CreateAsync();
        Browser = await Playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Channel = "msedge", Headless = true });
    }

    public async Task DisposeAsync()
    {
        await Browser.DisposeAsync();
        Playwright.Dispose();
    }
}

public sealed class UiTests(UiFixture fx) : IClassFixture<UiFixture>, IAsyncLifetime
{
    // Taille utile mesuree sur un portable 1920x1080 a 150 % (voir MainForm.FitToScreen) : 1177x618 logiques.
    private const int Width = 1177;
    private const int Height = 618;
    private const string Token = "jeton-ui-0123456789abcdef";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "freeres-ui-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _consoleErrors = [];
    private int _executions;
    private FreeResRuntime _runtime = null!;
    private LocalWebHost _host = null!;
    private IBrowserContext _context = null!;
    private IPage _page = null!;

    public async Task InitializeAsync()
    {
        var now = DateTimeOffset.Now;
        var apps = new AppsInventory(
            now,
            [new InstalledApp("app-docker", "Docker Desktop", "Docker Inc.", "4.50", @"C:\Program Files\Docker", 2_000_000, null, AppScope.Machine, AppSource.Registry, @"C:\Program Files\Docker", null)],
            [
                new StartupItem("su-teams", StartupKind.Run, AppScope.User, "Teams <script>", @"HKCU\...\Run", "teams.exe", @"C:\T\teams.exe", true, "Run", null, null),
                new StartupItem("su-defender", StartupKind.Run, AppScope.Machine, "SecurityHealth", @"HKLM\...\Run", "SecurityHealthSystray.exe", null, true, "Run", null, "Sécurité Windows"),
                new StartupItem("su-old", StartupKind.StartupFolder, AppScope.User, "Ancien.lnk", @"C:\Startup", null, null, false, "StartupFolder", null, null),
            ],
            [new AppUsage("app-docker", "Docker Desktop", 6, 0, 1_600_000_000), new AppUsage("app-code", "Visual Studio Code", 12, 1, 2_100_000_000)],
            [],
            []);
        IReadOnlyList<BrowserStatus> browsers =
        [
            new("firefox", "Mozilla Firefox", @"C:\F\firefox.exe", true, 30, 1, 6_000_000_000, 72, null, "Extension FreePC requise", 1, "default-release", true, "Possible : restauration de session activee", false),
            new("edge", "Microsoft Edge", @"C:\E\msedge.exe", false, 0, 0, 0, null, true, "Onglets en veille actifs", 0, null, false, "Navigateur ferme", null),
        ];
        IReadOnlyList<AiAppStatus> ai =
        [
            new("ollama", "Ollama", true, 1, 300_000_000, @"C:\U\.ollama\models", 12_000_000_000, [new OllamaLoadedModel("llama3.2:3b", 3_400_000_000, 2_000_000_000, null)], 3, "Serveur actif"),
        ];
        var docker = new DockerStatus(true, true, new DockerDiskUsage(now.AddHours(-1), 5.74, 7.74, 3.1, 1.2, 0.1, 2.3, 0.4, 0.8, 0.8), now.AddHours(-20), null);

        _runtime = FreeResRuntime.Create(
            new AppPaths(_dir),
            new StaticProbe(),
            _ => new ModuleSetup(
                [new UiModules(), new StatusOf("apps", apps), new StatusOf("browsers", browsers), new StatusOf("ai", ai), new StatusOf("docker", docker)],
                c =>
                {
                    c.Register(new ActionDefinition("UiCount", false, _ => null, (_, _, _) =>
                    {
                        Interlocked.Increment(ref _executions);
                        return Task.FromResult(new ActionOutcome(true, "Corbeille videe"));
                    }));
                    c.Register(new ActionDefinition("UiAdmin", false, _ => null, (_, _, _) => Task.FromResult(new ActionOutcome(false, "Service introuvable"))));
                }));
        // Historique : 12 mesures sur les 6 dernieres heures (memoire qui baisse, disque stable).
        for (var i = 12; i >= 1; i--)
        {
            _runtime.Database.AddMetrics(new SystemSnapshot(now.AddMinutes(-30 * i), 15.69, 2 + (i * 0.4), 70, 23.69, 15, 63, "C:", 235.48, 44 - (i * 0.05), 1, false, []), "monitor");
        }

        _host = LocalWebHost.Create(new LocalWebHostOptions { Token = Token }, _runtime);
        await _host.StartAsync();
        for (var i = 0; i < 50 && _runtime.Engine.LastReport is null; i++)
        {
            await Task.Delay(100);
        }

        _context = await fx.Browser.NewContextAsync(new BrowserNewContextOptions { ViewportSize = new ViewportSize { Width = Width, Height = Height }, Locale = "fr-FR" });
        await _context.AddCookiesAsync(
        [
            new Cookie { Name = LocalWebHostOptions.TokenCookieName, Value = Token, Url = _host.BaseAddress.ToString(), HttpOnly = true, SameSite = SameSiteAttribute.Strict },
        ]);
        _context.SetDefaultTimeout(15000);
        _page = await _context.NewPageAsync();
        _page.Console += (_, m) =>
        {
            if (m.Type == "error")
            {
                _consoleErrors.Add(m.Text + " @ " + m.Location);
            }
        };
        _page.PageError += (_, e) => _consoleErrors.Add(e);
        _page.Response += (_, r) =>
        {
            if (r.Status >= 400)
            {
                _consoleErrors.Add($"{r.Status} {r.Url}");
            }
        };
        await _page.GotoAsync(_host.BaseAddress.ToString());
        await _page.Locator(".decision").First.WaitForAsync();
    }

    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
        await _host.DisposeAsync();
        _runtime.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private async Task AssertNoPageScroll(string view)
    {
        var overflow = await _page.EvaluateAsync<int[]>("() => [document.documentElement.scrollHeight - innerHeight, document.documentElement.scrollWidth - innerWidth]");
        Assert.True(overflow[0] <= 0 && overflow[1] <= 0, $"{view} : la page deborde de {overflow[0]} px en hauteur et {overflow[1]} px en largeur");
    }

    [Fact]
    public async Task Tableau_de_bord_mesures_seuils_et_decisions_critiques_d_abord()
    {
        Assert.Equal("2,4 GB", await _page.Locator("#m-ram [data-v]").TextContentAsync());
        Assert.Equal("warn", await _page.Locator("#m-ram").GetAttributeAsync("data-level"));
        Assert.Equal("warn", await _page.Locator("#m-commit").GetAttributeAsync("data-level"));
        Assert.Equal("À surveiller", await _page.Locator("#health").TextContentAsync());
        Assert.Equal("3", await _page.Locator("#pending-count").TextContentAsync());

        var titles = await _page.Locator(".decision .title").AllTextContentsAsync();
        Assert.Equal(["Redémarrer <b>Firefox</b>", "Passer un service en manuel", "Vider la corbeille"], titles);
        Assert.Equal("Crit", await _page.Locator(".decision").First.GetAttributeAsync("data-severity"));

        // Defaut constate sur capture : l'etat vide s'affichait malgre 3 decisions et ecrasait la file.
        await Assertions.Expect(_page.Locator("#pending-empty")).ToBeHiddenAsync();
        foreach (var d in await _page.Locator(".decision").AllAsync())
        {
            var box = await d.BoundingBoxAsync();
            Assert.True(box is not null && box.Y + box.Height <= Height, "décision hors de la fenêtre sans defilement");
        }

        Assert.True(_consoleErrors.Count == 0, string.Join(" | ", _consoleErrors));
    }

    [Fact]
    public async Task Donnees_affichees_en_texte_jamais_interpretees()
    {
        Assert.Equal(0, await _page.Locator(".decision b, #top img").CountAsync());
        Assert.Contains("<img src=x onerror=alert(1)>", await _page.Locator("#top").TextContentAsync(), StringComparison.Ordinal);
        await _page.ClickAsync("#tab-apps");
        await _page.Locator("#apps-startup tr").First.WaitForAsync();
        Assert.Equal(0, await _page.Locator("#apps-startup script").CountAsync());
        Assert.Contains("Teams <script>", await _page.Locator("#apps-startup").TextContentAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Appliquer_affiche_le_resultat_reel_et_retire_la_decision()
    {
        await _page.Locator(".decision[data-id='ui-conseil'] button[data-act='approve']").ClickAsync();
        await Assertions.Expect(_page.Locator("#feedback")).ToHaveTextAsync("Appliqué : Corbeille videe");
        await Assertions.Expect(_page.Locator(".decision")).ToHaveCountAsync(2);
        Assert.Equal(1, _executions);
        await Assertions.Expect(_page.Locator("#pending-count")).ToHaveTextAsync("2");
    }

    [Fact]
    public async Task Echec_d_action_signale_comme_tel()
    {
        await _page.Locator(".decision[data-id='ui-admin'] button[data-act='approve']").ClickAsync();
        await Assertions.Expect(_page.Locator("#feedback")).ToHaveTextAsync("Échec : Service introuvable");
        Assert.Equal("false", await _page.Locator("#feedback").GetAttributeAsync("data-ok"));
    }

    [Fact]
    public async Task Plus_tard_et_ignorer_indiquent_quand_la_decision_reviendra()
    {
        await _page.Locator(".decision[data-id='ui-conseil'] button[data-act='snooze']").ClickAsync();
        await Assertions.Expect(_page.Locator("#feedback")).ToHaveTextAsync("« Vider la corbeille » reviendra dans 24 h.");
        await _page.Locator(".decision[data-id='ui-admin'] button[data-act='reject']").ClickAsync();
        await Assertions.Expect(_page.Locator("#feedback")).ToHaveTextAsync("« Passer un service en manuel » est ignorée pendant 7 jours.");
        await Assertions.Expect(_page.Locator(".decision")).ToHaveCountAsync(1);
        Assert.Equal(0, _executions);
    }

    [Fact]
    public async Task Analyser_maintenant()
    {
        await _page.ClickAsync("#analyze");
        await Assertions.Expect(_page.Locator("#feedback")).ToHaveTextAsync("Analyse terminée : rien de nouveau.");
        await Assertions.Expect(_page.Locator("#analyze")).ToBeEnabledAsync();
    }

    [Fact]
    public async Task Chaque_vue_tient_dans_la_fenetre_sans_defilement()
    {
        foreach (var view in new[] { "dashboard", "apps", "browsers", "ai", "docker", "history", "help" })
        {
            await _page.ClickAsync("#tab-" + view);
            await Assertions.Expect(_page.Locator("#view-" + view)).ToBeVisibleAsync();
            Assert.Equal("true", await _page.Locator("#tab-" + view).GetAttributeAsync("aria-selected"));
            await _page.WaitForTimeoutAsync(150);   // chargement de l'etat du module
            await AssertNoPageScroll(view);
        }

        Assert.True(_consoleErrors.Count == 0, string.Join(" | ", _consoleErrors));
    }

    [Fact]
    public async Task Vues_des_modules_avec_les_etats_reels()
    {
        await _page.ClickAsync("#tab-apps");
        await Assertions.Expect(_page.Locator("#apps-usage tr")).ToHaveCountAsync(2);
        Assert.Equal("Visual Studio Code", await _page.Locator("#apps-usage tr td").First.TextContentAsync());   // tri par memoire
        Assert.Contains("Arrière-plan", await _page.Locator("#apps-usage").TextContentAsync(), StringComparison.Ordinal);
        Assert.Contains("Protégé", await _page.Locator("#apps-startup").TextContentAsync(), StringComparison.Ordinal);
        Assert.Equal("2 activés sur 3", await _page.Locator("#startup-meta").TextContentAsync());

        await _page.ClickAsync("#tab-browsers");
        await Assertions.Expect(_page.Locator("#browsers .card")).ToHaveCountAsync(2);
        Assert.Contains("3 jours", await _page.Locator("#browsers").TextContentAsync(), StringComparison.Ordinal);

        await _page.ClickAsync("#tab-ai");
        await Assertions.Expect(_page.Locator("#ai .card")).ToHaveCountAsync(1);
        Assert.Contains("llama3.2:3b", await _page.Locator("#ai").TextContentAsync(), StringComparison.Ordinal);

        await _page.ClickAsync("#tab-docker");
        await Assertions.Expect(_page.Locator("#docker .card")).ToHaveCountAsync(1);
        var docker = await _page.Locator("#docker").TextContentAsync();
        Assert.Contains("7,74 GB", docker, StringComparison.Ordinal);
        Assert.Contains("Volumes (jamais purgés)", docker, StringComparison.Ordinal);
        Assert.True(_consoleErrors.Count == 0, string.Join(" | ", _consoleErrors));
    }

    [Fact]
    public async Task Historique_apres_une_decision()
    {
        await _page.Locator(".decision[data-id='ui-conseil'] button[data-act='approve']").ClickAsync();
        await Assertions.Expect(_page.Locator("#feedback")).ToHaveTextAsync("Appliqué : Corbeille videe");
        await _page.ClickAsync("#tab-history");
        await Assertions.Expect(_page.Locator("#history li").First).ToContainTextAsync("Appliquée : Vider la corbeille");
        Assert.Equal("#history", new Uri(_page.Url).Fragment);

        // Courbes : deux series tracees, echelle et heures affichees.
        await Assertions.Expect(_page.Locator("#chart polyline")).ToHaveCountAsync(2);
        Assert.True((await _page.Locator("#chart polyline.ram").GetAttributeAsync("points"))!.Split(' ').Length >= 12);
        await Assertions.Expect(_page.Locator("#key-ram")).ToHaveTextAsync("Mémoire disponible, max 6,8 GB");
        await Assertions.Expect(_page.Locator("#chart-start")).Not.ToBeEmptyAsync();
        await Assertions.Expect(_page.Locator("#chart-empty")).ToBeHiddenAsync();
    }

    [Fact]
    public async Task Navigation_au_clavier_et_lien_direct_vers_une_vue()
    {
        await _page.FocusAsync("#tab-dashboard");
        await _page.Keyboard.PressAsync("ArrowRight");
        Assert.Equal("true", await _page.Locator("#tab-apps").GetAttributeAsync("aria-selected"));
        Assert.Equal("tab-apps", await _page.EvaluateAsync<string>("() => document.activeElement.id"));

        await _page.GotoAsync(_host.BaseAddress + "#help");
        await Assertions.Expect(_page.Locator("#view-help")).ToBeVisibleAsync();
        await Assertions.Expect(_page.Locator(".steps li")).ToHaveCountAsync(3);
        await Assertions.Expect(_page.Locator("#data-dir")).ToHaveTextAsync(_runtime.Paths.DataDirectory);
    }

    /// <summary>
    /// Theme sombre : meme exigence de non-debordement ; les captures (clair et sombre) sont deposees dans
    /// artifacts\ui pour la revue visuelle.
    /// </summary>
    [Theory]
    [InlineData(ColorScheme.Light)]
    [InlineData(ColorScheme.Dark)]
    public async Task Captures_de_chaque_vue_dans_les_deux_themes(ColorScheme scheme)
    {
        await _page.EmulateMediaAsync(new PageEmulateMediaOptions { ColorScheme = scheme });
        var dir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "artifacts", "ui");
        Directory.CreateDirectory(dir);
        foreach (var view in new[] { "dashboard", "apps", "browsers", "ai", "docker", "history", "help" })
        {
            await _page.ClickAsync("#tab-" + view);
            await _page.WaitForTimeoutAsync(200);
            await AssertNoPageScroll(view + " " + scheme);
            var file = Path.Combine(dir, $"{view}-{scheme.ToString().ToLowerInvariant()}.png");
            await _page.ScreenshotAsync(new PageScreenshotOptions { Path = file });
            Assert.True(new FileInfo(file).Length > 10_000, file);
        }
    }

    [Fact]
    public async Task Sans_jeton_de_session_l_interface_est_refusee()
    {
        await using var anonymous = await fx.Browser.NewContextAsync();
        var page = await anonymous.NewPageAsync();
        var res = await page.GotoAsync(_host.BaseAddress.ToString());
        Assert.Equal(401, res!.Status);
        Assert.Equal(0, await page.Locator(".app").CountAsync());
    }

    [Fact]
    public async Task Une_page_tierce_ne_peut_pas_decider()
    {
        // Simule un site malveillant ouvert dans le meme navigateur (cookie present) qui poste vers l'interface.
        var res = await _context.APIRequest.PostAsync(_host.BaseAddress + "api/proposals/ui-conseil/approve", new APIRequestContextOptions
        {
            Headers = new Dictionary<string, string> { ["Origin"] = "https://site-malveillant.example" },
        });
        Assert.Equal(403, res.Status);
        Assert.Equal(0, _executions);
    }
}
