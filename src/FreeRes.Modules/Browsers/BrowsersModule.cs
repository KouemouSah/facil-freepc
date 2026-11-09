using System.Globalization;
using FreeRes.Core.Actions;
using FreeRes.Core.Decisions;
using FreeRes.Core.Engine;
using FreeRes.Core.Platform;

namespace FreeRes.Modules.Browsers;

/// <summary>
/// Navigateurs (tous ceux detectes, meme rigueur) :
///   * niveau 1 AUTOMATIQUE = la mise en veille des onglets inactifs du navigateur lui-meme (economiseur de memoire
///     Chromium, active par defaut ; extension compagnon pour Firefox, qui ne decharge que sous pression memoire).
///     FreePC verifie qu'elle est active et propose sinon d'ouvrir la page de reglage (jamais de strategie systeme) ;
///   * REDEMARRAGE = toujours un bouton, seulement si la restauration de TOUTES les fenetres est garantie par une
///     methode verifiee (voir <see cref="BrowserDefinition.RestartValidated"/>) ; jamais de fermeture forcee.
/// </summary>
public sealed class BrowsersModule : IResourceModule
{
    public const string OpenBrowserSettings = "OpenBrowserSettings";
    public const string RestartBrowser = "RestartBrowser";
    public const string InstallFirefoxExtension = "InstallFirefoxExtension";

    private readonly IBrowserPlatform _platform;
    private readonly BrowserStateReader _reader;
    private readonly IProcessLauncher _launcher;
    private readonly TimeProvider _time;
    private readonly BrowsersOptions _options;
    private readonly string _profilesRoot;
    private readonly string _extensionXpi;
    private readonly IReadOnlyList<BrowserDefinition> _catalog;
    private IReadOnlyList<BrowserStatus> _last = [];

    public BrowsersModule(
        ModuleServices services,
        BrowsersOptions? options = null,
        IBrowserPlatform? platform = null,
        IReadOnlyList<BrowserDefinition>? catalog = null,
        string? firefoxProfilesRoot = null,
        string? extensionXpiPath = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        _platform = platform ?? new WindowsBrowserPlatform();
        _reader = new BrowserStateReader(_platform);
        _launcher = services.EffectiveLauncher;
        _time = services.Time;
        _options = options ?? BrowsersOptions.Default;
        _catalog = catalog ?? BrowserCatalog.All;
        _profilesRoot = firefoxProfilesRoot ?? BrowserCatalog.FirefoxProfilesRoot;
        _extensionXpi = extensionXpiPath ?? Path.Combine(AppContext.BaseDirectory, "extensions", "facil-freepc-firefox.xpi");
    }

    public string Name => "browsers";

    /// <summary>Dernier etat des navigateurs (lu par l'interface).</summary>
    public IReadOnlyList<BrowserStatus> LastStatus => Volatile.Read(ref _last);

    public Task<ModuleEvaluation> EvaluateAsync(EngineContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var statuses = Inspect();
        Volatile.Write(ref _last, statuses);

        var s = context.Snapshot;
        var t = context.Thresholds;
        var pressure = s.RamAvailGB < t.RamAvailWarnGB || s.CommitPct >= t.CommitWarnPct;
        var critical = s.RamAvailGB < t.RamAvailCritGB || s.CommitPct >= t.CommitCritPct;
        var threshold = _options.RestartThresholdGB(s.RamTotalGB);
        var proposals = new List<Proposal>();

        foreach (var st in statuses)
        {
            var def = _catalog.First(d => d.Id == st.Id);
            if (st.MemorySaverEnabled == false && def.MemorySaverSettingsUrl is not null)
            {
                proposals.Add(new Proposal(
                    $"browser-{st.Id}-memory-saver",
                    $"Activer l'economiseur de memoire de {st.Name}",
                    "Il est desactive : les onglets inactifs restent en memoire. La page de reglage du navigateur s'ouvre ; activez « Economiseur de memoire ».",
                    0,
                    pressure ? Severity.Warn : Severity.Info,
                    Settings(st.Id, "performance")));
            }

            var heavy = st.Running && st.WorkingSetGB >= threshold && (st.UptimeHours ?? 0) >= _options.RestartMinUptimeHours;
            if (heavy && st.RestartPossible)
            {
                proposals.Add(new Proposal(
                    $"browser-{st.Id}-restart",
                    string.Create(CultureInfo.InvariantCulture, $"Redemarrer {st.Name} proprement ({st.WorkingSetGB} GB, ouvert depuis {Math.Round(st.UptimeHours ?? 0)} h)"),
                    "Fermeture propre (jamais forcee) puis relance avec restauration de toutes les fenetres et onglets, recharges seulement au clic. ENREGISTREZ vos formulaires en cours.",
                    Math.Round(Math.Max(0, st.WorkingSetGB - 1.5), 2),
                    critical ? Severity.Crit : Severity.Warn,
                    new ActionRequest(RestartBrowser, new Dictionary<string, string> { ["browser"] = st.Id })));
            }
            else if (heavy && def.Engine == BrowserEngine.Gecko && st.RestartNote.StartsWith(SessionRestoreOff, StringComparison.Ordinal) && def.StartupSettingsUrl is not null)
            {
                proposals.Add(new Proposal(
                    $"browser-{st.Id}-session-restore",
                    $"Activer la restauration de session de {st.Name}",
                    string.Create(CultureInfo.InvariantCulture, $"{st.Name} occupe {st.WorkingSetGB} GB, mais un redemarrage perdrait vos onglets : cochez « Ouvrir les fenetres et onglets precedents ». FreePC pourra alors proposer un redemarrage sur."),
                    0,
                    Severity.Info,
                    Settings(st.Id, "startup")));
            }

            if (def.Engine == BrowserEngine.Gecko && st.ActiveProfile is not null && st.ExtensionInstalled == false && _platform.IsSignedXpi(_extensionXpi))
            {
                proposals.Add(new Proposal(
                    $"browser-{st.Id}-extension",
                    $"Installer l'extension Facil FreePC dans {st.Name}",
                    "Firefox ne met en veille ses onglets que lorsque la memoire est deja epuisee. L'extension decharge automatiquement les onglets inactifs depuis 30 min (jamais l'onglet actif, epingle ou qui joue du son). Confirmez l'installation dans Firefox.",
                    0,
                    pressure ? Severity.Warn : Severity.Info,
                    new ActionRequest(InstallFirefoxExtension, new Dictionary<string, string> { ["browser"] = st.Id })));
            }
        }

        return Task.FromResult(new ModuleEvaluation(proposals, []));
    }

    internal const string SessionRestoreOff = "Restauration de session desactivee";

    /// <summary>Etat de chaque navigateur INSTALLE.</summary>
    public IReadOnlyList<BrowserStatus> Inspect()
    {
        var installed = _catalog
            .Select(d => (Def: d, Exe: d.ExeCandidates.Select(Environment.ExpandEnvironmentVariables).FirstOrDefault(_platform.FileExists)))
            .Where(x => x.Exe is not null)
            .ToList();
        if (installed.Count == 0)
        {
            return [];
        }

        var processes = _platform.Processes(installed.Select(i => i.Def.ExeName).ToList());
        var now = _time.GetLocalNow();
        var firefoxRunning = installed.Count(i => i.Def.Engine == BrowserEngine.Gecko && OwnProcesses(processes, i.Exe!).Count > 0);
        return installed.Select(i => Status(i.Def, i.Exe!, OwnProcesses(processes, i.Exe!), now, firefoxRunning)).ToList();
    }

    private BrowserStatus Status(BrowserDefinition def, string exe, List<BrowserProcess> own, DateTimeOffset now, int firefoxRunning)
    {
        var pids = own.Select(p => p.Pid).ToHashSet();
        var mains = own.Where(p => !pids.Contains(p.ParentPid)).ToList();
        var starts = mains.Where(m => m.StartTime is not null).Select(m => m.StartTime!.Value).ToList();
        double? uptime = starts.Count > 0 ? Math.Round((now - starts.Min()).TotalHours, 1) : null;
        var running = own.Count > 0;

        bool? memorySaver = null;
        var memoryNote = "Non verifiable pour ce navigateur";
        int activeProfiles = 0;
        string? activeProfile = null;
        bool? extension = null;
        string restartNote;

        if (def.Engine == BrowserEngine.Chromium && def.UserDataDir is { } rawDir)
        {
            var dir = Environment.ExpandEnvironmentVariables(rawDir);
            if (def.MemorySaverSettingsUrl is not null)
            {
                memorySaver = _reader.ChromiumMemorySaver(dir);
                memoryNote = memorySaver switch { true => "Active", false => "Desactive", null => "Valeur par defaut du navigateur" };
            }

            var profiles = _reader.ChromiumActiveProfiles(dir);
            activeProfiles = profiles.Count;
            activeProfile = profiles.Count == 1 ? profiles[0] : null;
            restartNote = !def.RestartValidated ? "Redemarrage non verifie pour ce navigateur"
                : !running ? "Non lance"
                : mains.Count != 1 ? "Plusieurs instances : redemarrage non sur"
                : profiles.Count > 1 ? "Plusieurs profils ouverts : la restauration de tous les profils n'est pas garantie"
                : "Possible";
            activeProfile ??= profiles.Count == 0 ? "Default" : null;
        }
        else if (def.Engine == BrowserEngine.Gecko)
        {
            memoryNote = "Dechargement integre seulement en cas de memoire basse : extension compagnon recommandee";
            activeProfile = running && firefoxRunning == 1 ? _reader.FirefoxActiveProfile(_profilesRoot) : null;
            extension = activeProfile is null ? null : _reader.FirefoxExtensionActive(activeProfile);
            activeProfiles = activeProfile is null ? 0 : 1;
            restartNote = !def.RestartValidated ? "Redemarrage non verifie pour ce navigateur"
                : !running ? "Non lance"
                : firefoxRunning > 1 ? "Plusieurs editions de Firefox ouvertes : profil ambigu"
                : mains.Count != 1 ? "Plusieurs instances : redemarrage non sur"
                : activeProfile is null ? "Profil actif introuvable"
                : !_reader.FirefoxRestoresSession(activeProfile) ? $"{SessionRestoreOff} : un redemarrage perdrait les onglets"
                : "Possible";
        }
        else
        {
            restartNote = !def.RestartValidated ? "Redemarrage non verifie pour ce navigateur" : "Non lance";
        }

        return new BrowserStatus(def.Id, def.Name, exe, running, own.Count, mains.Count, own.Sum(p => p.WorkingSetBytes), uptime,
            memorySaver, memoryNote, activeProfiles, activeProfile, restartNote == "Possible", restartNote, extension);
    }

    /// <summary>Processus de CE navigateur : image dans son dossier d'installation (pas une autre edition homonyme).</summary>
    private static List<BrowserProcess> OwnProcesses(IReadOnlyList<BrowserProcess> all, string exe)
    {
        var dir = Path.GetDirectoryName(exe)!.TrimEnd('\\') + "\\";
        var name = Path.GetFileName(exe);
        return all.Where(p => p.ImagePath is { } img
                && img.StartsWith(dir, StringComparison.OrdinalIgnoreCase)
                && string.Equals(Path.GetFileName(img), name, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static ActionRequest Settings(string browser, string page) =>
        new(OpenBrowserSettings, new Dictionary<string, string> { ["browser"] = browser, ["page"] = page });

    // ------------------------------------------------------------------ actions

    public void RegisterActions(ActionCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        catalog.Register(new ActionDefinition(OpenBrowserSettings, RequiresAdmin: false, ValidateSettings, (req, dryRun, _) => Task.FromResult(OpenSettings(req, dryRun))));
        catalog.Register(new ActionDefinition(RestartBrowser, RequiresAdmin: false, p => ValidateBrowser(p, onlyKey: true), RestartAsync));
        catalog.Register(new ActionDefinition(InstallFirefoxExtension, RequiresAdmin: false, p => ValidateBrowser(p, onlyKey: true, gecko: true), (req, dryRun, _) => Task.FromResult(InstallExtension(req, dryRun))));
    }

    private string? ValidateBrowser(IReadOnlyDictionary<string, string> p, bool onlyKey, bool gecko = false)
    {
        if ((onlyKey && p.Count != 1) || !p.TryGetValue("browser", out var id) || _catalog.FirstOrDefault(d => d.Id == id) is not { } def)
        {
            return "navigateur inconnu (parametre « browser »)";
        }

        return gecko && def.Engine != BrowserEngine.Gecko ? "action reservee a Firefox" : null;
    }

    private string? ValidateSettings(IReadOnlyDictionary<string, string> p)
    {
        if (p.Count != 2 || ValidateBrowser(p, onlyKey: false) is { } error)
        {
            return "parametres attendus : browser et page";
        }

        var def = _catalog.First(d => d.Id == p["browser"]);
        return p.TryGetValue("page", out var page) && (page == "performance" ? def.MemorySaverSettingsUrl : page == "startup" ? def.StartupSettingsUrl : null) is not null
            ? null
            : "page de reglage inconnue pour ce navigateur";
    }

    private ActionOutcome OpenSettings(ActionRequest req, bool dryRun)
    {
        var def = _catalog.First(d => d.Id == req.Parameters["browser"]);
        var url = req.Parameters["page"] == "performance" ? def.MemorySaverSettingsUrl! : def.StartupSettingsUrl!;
        var exe = Inspect().FirstOrDefault(s => s.Id == def.Id)?.ExePath;
        if (exe is null)
        {
            return new ActionOutcome(false, $"{def.Name} n'est pas installe");
        }

        if (dryRun)
        {
            return new ActionOutcome(true, $"simulation : ouverture de {url}");
        }

        return _launcher.Launch(exe, [url])
            ? new ActionOutcome(true, $"Page de reglage ouverte dans {def.Name} : {url}")
            : new ActionOutcome(false, $"Impossible de lancer {def.Name}");
    }

    private ActionOutcome InstallExtension(ActionRequest req, bool dryRun)
    {
        var st = Inspect().FirstOrDefault(s => s.Id == req.Parameters["browser"]);
        if (st is null)
        {
            return new ActionOutcome(false, "Firefox n'est pas installe");
        }

        if (!_platform.IsSignedXpi(_extensionXpi))
        {
            return new ActionOutcome(false, "Extension signee par Mozilla indisponible dans cette version");
        }

        if (dryRun)
        {
            return new ActionOutcome(true, "simulation : installation de l'extension proposee a Firefox");
        }

        return _launcher.Launch(st.ExePath, [_extensionXpi])
            ? new ActionOutcome(true, "Firefox demande de confirmer l'installation de l'extension Facil FreePC")
            : new ActionOutcome(false, "Impossible de lancer Firefox");
    }

    private async Task<ActionOutcome> RestartAsync(ActionRequest req, bool dryRun, CancellationToken ct)
    {
        var def = _catalog.First(d => d.Id == req.Parameters["browser"]);
        // Conditions RE-VERIFIEES au moment de l'action : l'etat a pu changer depuis la proposition.
        var st = Inspect().FirstOrDefault(s => s.Id == def.Id);
        if (st is null || !st.RestartPossible)
        {
            return new ActionOutcome(false, $"Redemarrage refuse : {st?.RestartNote ?? "navigateur non installe"}");
        }

        var main = MainProcess(def, st.ExePath);
        if (main is null)
        {
            return new ActionOutcome(false, "Processus principal introuvable");
        }

        if (dryRun)
        {
            return new ActionOutcome(true, $"simulation : redemarrage propre de {def.Name} ({st.WorkingSetGB} GB)");
        }

        if (def.Engine == BrowserEngine.Chromium)
        {
            var code = _platform.CloseChromium(main.Pid, main.StartTime ?? _time.GetLocalNow());
            if (code != 0)
            {
                return new ActionOutcome(false, $"{def.Name} a refuse la fermeture (code {code}) : rien n'a ete force");
            }
        }
        else if (!_platform.SendFirefoxQuit(main.Pid))
        {
            return new ActionOutcome(false, $"{def.Name} n'a pas pu etre mis au premier plan : aucune touche envoyee, rien n'a ete ferme");
        }

        // Attente REELLE (chronometre, pas l'horloge injectee : une horloge de test figee bloquerait la boucle).
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (OwnProcesses(_platform.Processes([def.ExeName]), st.ExePath).Count > 0)
        {
            if (waited.Elapsed.TotalSeconds >= _options.QuitTimeoutSeconds)
            {
                return new ActionOutcome(false, $"{def.Name} ne s'est pas ferme en {_options.QuitTimeoutSeconds} s (une page demande peut-etre confirmation). Rien n'a ete force.");
            }

            await Task.Delay(500, ct).ConfigureAwait(false);
        }

        IReadOnlyList<string> args = def.Engine == BrowserEngine.Chromium
            ? [$"--profile-directory={st.ActiveProfile ?? "Default"}", "--restore-last-session"]
            : ["-profile", st.ActiveProfile!];
        return _launcher.Launch(st.ExePath, args)
            ? new ActionOutcome(true, string.Create(CultureInfo.InvariantCulture, $"{def.Name} ({st.WorkingSetGB} GB) ferme proprement puis relance, fenetres et onglets restaures"))
            : new ActionOutcome(false, $"{def.Name} ferme, mais la relance a echoue : relancez-le (vos onglets seront restaures)");
    }

    private BrowserProcess? MainProcess(BrowserDefinition def, string exe)
    {
        var own = OwnProcesses(_platform.Processes([def.ExeName]), exe);
        var pids = own.Select(p => p.Pid).ToHashSet();
        var mains = own.Where(p => !pids.Contains(p.ParentPid)).ToList();
        return mains.Count == 1 ? mains[0] : null;
    }
}
