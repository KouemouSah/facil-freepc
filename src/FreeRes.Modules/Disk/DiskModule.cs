using System.Globalization;
using FreeRes.Core.Actions;
using FreeRes.Core.Decisions;
using FreeRes.Core.Engine;
using FreeRes.Core.Storage;

namespace FreeRes.Modules.Disk;

/// <summary>
/// Disque systeme :
///   * AUTOMATIQUE (sans risque) : zones sures de l'utilisateur, selon la frequence de chaque regle ;
///   * PROPOSITIONS quand l'espace libre passe sous le seuil d'alerte : cache npm, corbeille,
///     zones systeme (administrateur).
/// </summary>
public sealed class DiskModule : IResourceModule
{
    public const string CleanUserZone = "CleanUserZone";
    public const string CleanSystemZones = "CleanSystemZones";
    public const string NpmCacheClean = "NpmCacheClean";
    public const string EmptyRecycleBin = "EmptyRecycleBin";

    private static readonly TimeSpan SizeCacheTtl = TimeSpan.FromHours(6);
    private readonly DiskOptions _options;
    private readonly SafeCleaner _cleaner;
    private readonly IRecycleBin _recycleBin;
    private readonly ModuleState? _state;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, (DateTimeOffset At, double GB)> _sizeCache = new(StringComparer.Ordinal);

    public DiskModule(ModuleServices services, DiskOptions? options = null, IRecycleBin? recycleBin = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        _options = options ?? DiskOptions.Default;
        _time = services.Time;
        _cleaner = new SafeCleaner(_options.AllowedRoots, _time);
        _recycleBin = recycleBin ?? new ShellRecycleBin();
        _state = services.State is null ? null : new ModuleState(services.State, Name);
        var errors = _options.Validate(_cleaner);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException("Configuration du module disque invalide : " + string.Join(" ; ", errors));
        }
    }

    public string Name => "disk";

    public SafeCleaner Cleaner => _cleaner;

    public Task<ModuleEvaluation> EvaluateAsync(EngineContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var now = _time.GetLocalNow();
        var auto = new List<ActionRequest>();
        foreach (var rule in _options.Rules.Where(r => !r.RequiresAdmin))
        {
            var last = _state?.GetDate("lastRun." + rule.Id);
            if (last is null || (now - last.Value).TotalHours >= rule.IntervalHours)
            {
                auto.Add(new ActionRequest(CleanUserZone, new Dictionary<string, string> { ["rule"] = rule.Id }));
                _state?.SetDate("lastRun." + rule.Id, now);
            }
        }

        var proposals = new List<Proposal>();
        var snapshot = context.Snapshot;
        var t = context.Thresholds;
        if (snapshot.SystemDriveFreeGB < t.DiskFreeWarnGB)
        {
            var severity = snapshot.SystemDriveFreeGB < t.DiskFreeCritGB ? Severity.Crit : Severity.Warn;

            var npm = CachedSize("npm", () => SafeCleaner.FolderSize(SafeCleaner.Expand(_options.NpmCachePath)).Bytes);
            if (npm >= _options.NpmCacheProposeGB)
            {
                proposals.Add(new Proposal("disk-npm-cache", "Vider le cache npm", "Re-telecharge a la demande lors des prochains npm install.", npm, severity, ActionRequest.Of(NpmCacheClean)));
            }

            var bin = CachedSize("recycle", _recycleBin.SizeBytes);
            if (bin >= _options.RecycleBinProposeGB)
            {
                proposals.Add(new Proposal("disk-recycle-bin", "Vider la corbeille", "Suppression definitive du contenu de la corbeille.", bin, severity, ActionRequest.Of(EmptyRecycleBin)));
            }

            var system = CachedSize("system-zones", () => _options.Rules.Where(r => r.RequiresAdmin)
                .Sum(r => _cleaner.RemoveOlderThan(r.Path, r.OlderThanDays, r.DirPattern, dryRun: true).FreedBytes));
            if (system >= _options.SystemZonesProposeGB)
            {
                proposals.Add(new Proposal(
                    "disk-system-zones",
                    "Nettoyer les fichiers temporaires du systeme",
                    "Temporaires de Windows, rapports d'erreurs et journaux de maintenance anciens. Demande l'autorisation administrateur.",
                    system,
                    severity,
                    ActionRequest.Of(CleanSystemZones)));
            }
        }

        return Task.FromResult(new ModuleEvaluation(proposals, auto));
    }

    public void RegisterActions(ActionCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        catalog.Register(new ActionDefinition(
            CleanUserZone,
            RequiresAdmin: false,
            p => p.TryGetValue("rule", out var id) && _options.Rules.Any(r => r.Id == id && !r.RequiresAdmin) ? null : "regle inconnue ou reservee a l'administrateur",
            (req, dryRun, _) =>
            {
                var rule = _options.Rules.Single(r => r.Id == req.Parameters["rule"]);
                return Task.FromResult(Outcome(rule.Id, _cleaner.RemoveOlderThan(rule.Path, rule.OlderThanDays, rule.DirPattern, dryRun: dryRun)));
            }));

        catalog.Register(new ActionDefinition(
            CleanSystemZones,
            RequiresAdmin: true,
            NoParameters,
            (_, dryRun, _) =>
            {
                long freed = 0;
                var details = new List<string>();
                foreach (var rule in _options.Rules.Where(r => r.RequiresAdmin))
                {
                    var r = _cleaner.RemoveOlderThan(rule.Path, rule.OlderThanDays, rule.DirPattern, dryRun: dryRun);
                    freed += r.FreedBytes;
                    details.Add(string.Create(CultureInfo.InvariantCulture, $"{rule.Id} {r.FreedGB} GB"));
                }

                return Task.FromResult(new ActionOutcome(true, string.Create(CultureInfo.InvariantCulture, $"{Math.Round(freed / 1073741824d, 2)} GB ({string.Join(", ", details)})")));
            }));

        catalog.Register(new ActionDefinition(
            NpmCacheClean,
            RequiresAdmin: false,
            NoParameters,
            (_, dryRun, _) => Task.FromResult(Outcome("npm-cache", _cleaner.RemoveOlderThan(_options.NpmCachePath, 1, dryRun: dryRun)))));

        catalog.Register(new ActionDefinition(
            EmptyRecycleBin,
            RequiresAdmin: false,
            NoParameters,
            (_, dryRun, _) =>
            {
                var size = _recycleBin.SizeBytes();
                if (!dryRun)
                {
                    _recycleBin.Empty();
                }

                return Task.FromResult(new ActionOutcome(true, string.Create(CultureInfo.InvariantCulture, $"Corbeille : {Math.Round(size / 1073741824d, 2)} GB")));
            }));
    }

    internal static string? NoParameters(IReadOnlyDictionary<string, string> p) => p.Count == 0 ? null : "aucun parametre attendu";

    // Le repartiteur journalise deja chaque action (avec son gain disque) : pas de double ecriture ici.
    private static ActionOutcome Outcome(string id, CleanResult r) =>
        new(true, string.Create(CultureInfo.InvariantCulture, $"{id} : {r.FreedGB} GB, {r.Files} fichiers, {r.Dirs} dossiers, {r.Skipped} ignores"));

    private double CachedSize(string key, Func<long> measure)
    {
        var now = _time.GetLocalNow();
        if (_sizeCache.TryGetValue(key, out var c) && now - c.At < SizeCacheTtl)
        {
            return c.GB;
        }

        var gb = Math.Round(measure() / 1073741824d, 2);
        _sizeCache[key] = (now, gb);
        return gb;
    }
}
