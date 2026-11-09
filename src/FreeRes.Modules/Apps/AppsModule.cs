using System.Globalization;
using System.Text.RegularExpressions;
using FreeRes.Core.Actions;
using FreeRes.Core.Decisions;
using FreeRes.Core.Engine;
using FreeRes.Core.Logging;
using FreeRes.Core.Platform;
using Microsoft.Win32;

namespace FreeRes.Modules.Apps;

/// <summary>
/// Applications installees et demarrage automatique.
///   * Inventaire (registre, dossiers Demarrage, services tiers, taches a l'ouverture de session) rafraichi toutes
///     les <see cref="AppsOptions.InventoryRefreshMinutes"/> min ; consommation des processus mesuree a chaque cycle.
///   * Propositions : desactiver le demarrage d'une application d'arriere-plan gourmande ; sous pression memoire,
///     arreter ses processus sans fenetre ; passer un service tiers gourmand en demarrage manuel (administrateur).
///   * Refus inconditionnel des composants Windows, logiciels de securite, pilotes et de Facil FreePC (<see cref="AppProtection"/>).
///
/// Actions de demarrage : <see cref="AppScope.User"/> (HKCU, dossier de l'utilisateur) et <see cref="AppScope.Machine"/>
/// (HKLM, dossier commun) ont des TYPES distincts, car RequiresAdmin est fixe par type : le repartiteur s'en sert pour
/// demander l'UAC et le mode eleve refuse toute action non administrateur. Un type unique devrait soit toujours
/// demander l'UAC (inutile pour HKCU, et ecrirait dans le HKCU du compte administrateur si un autre compte eleve), soit
/// jamais (echec d'acces sur HKLM).
/// </summary>
public sealed partial class AppsModule : IResourceModule, IActionObserver
{
    public const string DisableStartupItem = "DisableStartupItem";
    public const string EnableStartupItem = "EnableStartupItem";
    public const string DisableMachineStartupItem = "DisableMachineStartupItem";
    public const string EnableMachineStartupItem = "EnableMachineStartupItem";
    public const string StopAppProcesses = "StopAppProcesses";
    public const string SetServiceManual = "SetServiceManual";

    private static readonly string[] OwnTypes = [DisableStartupItem, EnableStartupItem, DisableMachineStartupItem, EnableMachineStartupItem, StopAppProcesses, SetServiceManual];
    private readonly AppsOptions _options;
    private readonly AppsPlatform _platform;
    private readonly AppsScanner _scanner;
    private readonly IProcessRunner _runner;
    private readonly EventLog _log;
    private readonly TimeProvider _time;
    private readonly Lock _cacheLock = new();
    private (DateTimeOffset At, StaticInventory Inventory)? _cache;
    private AppsInventory? _last;

    public AppsModule(ModuleServices services, AppsOptions? options = null, AppsPlatform? platform = null, AppsEnvironment? environment = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        _options = options ?? AppsOptions.Default;
        var errors = _options.Validate();
        if (errors.Count > 0)
        {
            throw new InvalidOperationException("Configuration du module applications invalide : " + string.Join(" ; ", errors));
        }

        _platform = platform ?? AppsPlatform.Windows();
        _scanner = new AppsScanner(_platform, new AppProtection(environment ?? AppsEnvironment.Current()));
        _runner = services.Processes;
        _log = services.Log;
        _time = services.Time;
    }

    public string Name => "apps";

    /// <summary>Dernier inventaire (applications, elements de demarrage, consommation), null avant le premier cycle.</summary>
    public AppsInventory? LastInventory => Volatile.Read(ref _last);

    [GeneratedRegex("^su-[0-9a-f]{16}$")]
    private static partial Regex StartupIdPattern();

    [GeneratedRegex("^app-[0-9a-f]{16}$")]
    private static partial Regex AppIdPattern();

    [GeneratedRegex(@"^[A-Za-z0-9 _.$@(){}\-]{1,128}$")]
    private static partial Regex ServiceNamePattern();

    public Task<ModuleEvaluation> EvaluateAsync(EngineContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var (inventory, processes) = Refresh();
        var proposals = new List<Proposal>();
        var s = context.Snapshot;
        var t = context.Thresholds;
        var pressure = s.RamAvailGB < t.RamAvailWarnGB || s.CommitPct > t.CommitWarnPct;
        var severity = pressure ? Severity.Warn : Severity.Info;
        var apps = inventory.Apps.ToDictionary(a => a.Id, StringComparer.Ordinal);
        var usage = inventory.Usage.ToDictionary(u => u.AppId, StringComparer.Ordinal);
        var backgroundBytes = _options.BackgroundProposeMB * 1024 * 1024;
        var heavyApps = new List<InstalledApp>();

        foreach (var item in inventory.StartupItems.Where(i => i.Kind is StartupKind.Run or StartupKind.StartupFolder && i.Enabled && i.Protection is null && i.AppId is not null))
        {
            if (!apps.TryGetValue(item.AppId!, out var app) || app.Protection is not null
                || !usage.TryGetValue(app.Id, out var u) || !u.Background || u.WorkingSetBytes < backgroundBytes)
            {
                continue;
            }

            var machine = item.Scope == AppScope.Machine;
            proposals.Add(new Proposal(
                "apps-disable-" + item.Id,
                $"Desactiver le demarrage automatique de {Short(app.Name)}",
                string.Create(CultureInfo.InvariantCulture, $"{Short(app.Name)} tourne en arriere-plan (aucune fenetre) et occupe {Mb(u.WorkingSetBytes)} MB. Il ne sera plus lance a l'ouverture de session ; l'entree « {Short(item.Name)} » est conservee et reactivable (meme mecanisme que le Gestionnaire des taches).")
                    + (machine ? " Element commun a tous les utilisateurs : demande l'autorisation administrateur." : string.Empty),
                u.WorkingSetGB,
                severity,
                new ActionRequest(machine ? DisableMachineStartupItem : DisableStartupItem, new Dictionary<string, string> { ["id"] = item.Id })));
            if (!heavyApps.Contains(app))
            {
                heavyApps.Add(app);
            }
        }

        if (pressure)
        {
            foreach (var app in heavyApps.Where(a => a.Folder is not null))
            {
                var targets = StopTargets(app, processes);
                if (targets.Count == 0)
                {
                    continue;
                }

                var bytes = targets.Sum(p => p.WorkingSetBytes);
                proposals.Add(new Proposal(
                    "apps-stop-" + app.Id,
                    $"Arreter les processus d'arriere-plan de {Short(app.Name)}",
                    string.Create(CultureInfo.InvariantCulture, $"Memoire sous pression. Arrete {targets.Count} processus sans fenetre de {Short(app.Name)} ({Mb(bytes)} MB), situes dans {app.Folder}. Les processus avec une fenetre ne sont pas touches ; une operation en cours dans ces processus serait interrompue."),
                    Math.Round(bytes / 1073741824d, 2),
                    Severity.Warn,
                    new ActionRequest(StopAppProcesses, new Dictionary<string, string> { ["appId"] = app.Id })));
            }
        }

        var serviceBytes = _options.ServiceProposeMB * 1024 * 1024;
        var serviceUsage = inventory.Services.ToDictionary(x => x.ServiceName, StringComparer.OrdinalIgnoreCase);
        foreach (var item in inventory.StartupItems.Where(i => i.Kind == StartupKind.Service && i.Protection is null && i.ServiceName is not null))
        {
            if (!serviceUsage.TryGetValue(item.ServiceName!, out var su) || su.WorkingSetBytes < serviceBytes)
            {
                continue;
            }

            proposals.Add(new Proposal(
                "apps-manual-" + item.Id,
                $"Passer le service {Short(item.Name)} en demarrage manuel",
                string.Create(CultureInfo.InvariantCulture, $"Service tiers lance automatiquement au demarrage de Windows ; il occupe {Mb(su.WorkingSetBytes)} MB. En demarrage manuel, il ne sera lance qu'a la demande (par son application ou par Windows). Le service en cours n'est pas arrete : effet au prochain demarrage. Demande l'autorisation administrateur."),
                Math.Round(su.WorkingSetBytes / 1073741824d, 2),
                severity,
                new ActionRequest(SetServiceManual, new Dictionary<string, string> { ["service"] = item.ServiceName! })));
        }

        return Task.FromResult(new ModuleEvaluation(proposals, []));
    }

    public void OnActionCompleted(ActionRequest request, ActionResult result)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(result);
        if (result.Ok && !result.DryRun && OwnTypes.Contains(request.Type))
        {
            lock (_cacheLock)
            {
                _cache = null;   // relire le registre au prochain cycle
            }
        }
    }

    public void RegisterActions(ActionCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        RegisterStartup(catalog, DisableStartupItem, AppScope.User, enable: false);
        RegisterStartup(catalog, EnableStartupItem, AppScope.User, enable: true);
        RegisterStartup(catalog, DisableMachineStartupItem, AppScope.Machine, enable: false);
        RegisterStartup(catalog, EnableMachineStartupItem, AppScope.Machine, enable: true);

        catalog.Register(new ActionDefinition(
            StopAppProcesses,
            RequiresAdmin: false,
            p => FindStoppableApp(p, out _),
            (req, dryRun, _) =>
            {
                if (FindStoppableApp(req.Parameters, out var app) is { } error)
                {
                    return Task.FromResult(new ActionOutcome(false, error));
                }

                var targets = StopTargets(app!, _platform.Processes.Snapshot());
                if (targets.Count == 0)
                {
                    return Task.FromResult(new ActionOutcome(true, $"{app!.Name} : aucun processus d'arriere-plan a arreter"));
                }

                var bytes = targets.Sum(p => p.WorkingSetBytes);
                if (dryRun)
                {
                    return Task.FromResult(new ActionOutcome(true, string.Create(CultureInfo.InvariantCulture, $"simulation : {targets.Count} processus de {app!.Name} ({Mb(bytes)} MB)")));
                }

                var failures = new List<string>();
                long freed = 0;
                foreach (var p in targets)
                {
                    if (_platform.Processes.Kill(p.Pid, p.ImagePath!) is { } why)
                    {
                        failures.Add($"{p.Name} ({p.Pid}) : {why}");
                    }
                    else
                    {
                        freed += p.WorkingSetBytes;
                    }
                }

                var message = string.Create(CultureInfo.InvariantCulture, $"{app!.Name} : {targets.Count - failures.Count}/{targets.Count} processus arretes, {Mb(freed)} MB");
                return Task.FromResult(new ActionOutcome(failures.Count == 0, failures.Count == 0 ? message : message + " ; echecs : " + string.Join(", ", failures)));
            }));

        catalog.Register(new ActionDefinition(
            SetServiceManual,
            RequiresAdmin: true,
            p => FindService(p, out _),
            async (req, dryRun, ct) =>
            {
                if (FindService(req.Parameters, out var service) is { } error)
                {
                    return new ActionOutcome(false, error);
                }

                if (dryRun)
                {
                    return new ActionOutcome(true, $"simulation : service {service!.Name} en demarrage manuel");
                }

                var r = await _runner.RunAsync(
                    Path.Combine(Environment.SystemDirectory, "sc.exe"),
                    ["config", service!.Name, "start=", "demand"],
                    TimeSpan.FromSeconds(30),
                    ct).ConfigureAwait(false);
                return new ActionOutcome(
                    r.ExitCode == 0,
                    r.ExitCode == 0
                        ? $"Service {service.DisplayName} en demarrage manuel (effet au prochain demarrage)"
                        : $"sc.exe a echoue (code {r.ExitCode}) {r.StdOut} {r.StdErr}".Trim());
            }));
    }

    /// <summary>Inventaire statique en cache + mesures des processus et etats des services du moment.</summary>
    internal (AppsInventory Inventory, IReadOnlyList<ProcessSample> Processes) Refresh()
    {
        var now = _time.GetLocalNow();
        StaticInventory stat;
        lock (_cacheLock)
        {
            if (_cache is { } c && (now - c.At).TotalMinutes < _options.InventoryRefreshMinutes)
            {
                stat = c.Inventory;
            }
            else
            {
                stat = _scanner.Scan(includeServicesAndTasks: true, TimeSpan.FromSeconds(_options.ScheduledTasksTimeoutSeconds));
                _cache = (now, stat);
                foreach (var w in stat.Warnings)
                {
                    _log.Write(LogLevel.Warn, Name, w);
                }
            }
        }

        var items = stat.Items
            .Select(i => i.Kind == StartupKind.Service && i.ServiceName is not null ? i with { ServiceStatus = _platform.Services.Status(i.ServiceName) } : i)
            .ToList();
        var processes = _platform.Processes.Snapshot();
        var (usage, services) = ProcessAttribution.Measure(stat.Apps, items, processes, _platform.Processes.CurrentSessionId);
        var inventory = new AppsInventory(now, stat.Apps, items, usage, services, stat.Warnings);
        Volatile.Write(ref _last, inventory);
        return (inventory, processes);
    }

    private void RegisterStartup(ActionCatalog catalog, string type, AppScope scope, bool enable) =>
        catalog.Register(new ActionDefinition(
            type,
            RequiresAdmin: scope == AppScope.Machine,
            p => FindStartupItem(p, scope, out _),
            (req, dryRun, _) =>
            {
                if (FindStartupItem(req.Parameters, scope, out var item) is { } error)
                {
                    return Task.FromResult(new ActionOutcome(false, error));
                }

                var verb = enable ? "reactive" : "desactive";
                if (dryRun)
                {
                    return Task.FromResult(new ActionOutcome(true, $"simulation : demarrage automatique {verb} pour {item!.Name}"));
                }

                _platform.Registry.SetBinary(
                    scope == AppScope.User ? RegistryHive.CurrentUser : RegistryHive.LocalMachine,
                    RegistryView.Registry64,
                    StartupApproved.RootPath + "\\" + item!.ApprovedSubKey,
                    item.Name,
                    StartupApproved.Encode(enable, _time.GetUtcNow()));
                return Task.FromResult(new ActionOutcome(true, $"Demarrage automatique {verb} : {item.Name} (entree conservee, reversible)"));
            }));

    /// <summary>Validation stricte (relecture du registre) : element Run/dossier Demarrage existant, de la bonne portee, non protege.</summary>
    private string? FindStartupItem(IReadOnlyDictionary<string, string> p, AppScope scope, out StartupItem? item)
    {
        item = null;
        if (p.Count != 1 || !p.TryGetValue("id", out var id) || !StartupIdPattern().IsMatch(id))
        {
            return "id d'element de demarrage invalide";
        }

        var found = _scanner.Scan(includeServicesAndTasks: false, TimeSpan.Zero).Items.FirstOrDefault(i => i.Id == id);
        if (found is null || found.ApprovedSubKey is null)
        {
            return "element de demarrage introuvable";
        }

        if (found.Scope != scope)
        {
            return found.Scope == AppScope.Machine
                ? "element commun a tous les utilisateurs : action administrateur requise"
                : "element de l'utilisateur : action utilisateur requise";
        }

        if (found.Protection is not null)
        {
            return "element protege : " + found.Protection;
        }

        item = found;
        return null;
    }

    private string? FindStoppableApp(IReadOnlyDictionary<string, string> p, out InstalledApp? app)
    {
        app = null;
        if (p.Count != 1 || !p.TryGetValue("appId", out var id) || !AppIdPattern().IsMatch(id))
        {
            return "appId invalide";
        }

        var found = _scanner.Scan(includeServicesAndTasks: false, TimeSpan.Zero).Apps.FirstOrDefault(a => a.Id == id);
        if (found is null)
        {
            return "application introuvable";
        }

        if (found.Protection is not null)
        {
            return "application protegee : " + found.Protection;
        }

        if (found.Folder is null)
        {
            return "dossier de l'application inconnu ou trop large";
        }

        app = found;
        return null;
    }

    private string? FindService(IReadOnlyDictionary<string, string> p, out ServiceEntry? service)
    {
        service = null;
        if (p.Count != 1 || !p.TryGetValue("service", out var name) || !ServiceNamePattern().IsMatch(name))
        {
            return "nom de service invalide";
        }

        var found = _scanner.ReadService(name);
        if (found is null)
        {
            return "service introuvable";
        }

        if (_scanner.ServiceRefusal(found) is { } refusal)
        {
            return refusal;
        }

        if (found.Start != AppsScanner.ServiceAutoStart)
        {
            return "le service n'est pas en demarrage automatique";
        }

        service = found;
        return null;
    }

    /// <summary>
    /// Processus que <see cref="StopAppProcesses"/> arreterait : session de l'utilisateur, SANS fenetre, image dans le
    /// dossier de l'application, ni Facil FreePC ni composant Windows ni logiciel protege.
    /// </summary>
    private List<ProcessSample> StopTargets(InstalledApp app, IReadOnlyList<ProcessSample> processes) =>
        processes
            .Where(p => p.SessionId == _platform.Processes.CurrentSessionId
                && !p.HasWindow
                && p.Pid != _platform.Processes.CurrentProcessId
                && AppPathRules.Normalize(p.ImagePath) is { } image
                && AppPathRules.IsUnderNormalized(image, app.Folder!)
                && _scanner.Protection.Check(null, null, image) is null)
            .ToList();

    private static string Short(string text) => text.Length <= 80 ? text : text[..77] + "...";

    private static double Mb(long bytes) => Math.Round(bytes / 1048576d);
}
