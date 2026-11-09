using System.Globalization;
using System.ServiceProcess;
using FreeRes.Core.Actions;
using FreeRes.Core.Decisions;
using FreeRes.Core.Engine;
using FreeRes.Core.Metrics;
using FreeRes.Core.Platform;
using FreeRes.Core.Storage;
using FreeRes.Modules.Disk;

namespace FreeRes.Modules.Windows;

/// <summary>Reglages de la maintenance Windows (valeurs validees dans la version PowerShell).</summary>
public sealed record WindowsMaintenanceOptions(
    double RebootPendingProposeHours = 12,
    double WindowsUpdateCacheProposeGB = 2,
    int DismEveryDays = 30,
    int RebootDelayMinutes = 10)
{
    public static WindowsMaintenanceOptions Default { get; } = new();
}

/// <summary>
/// Maintenance Windows. Constat du 2026-11-08 : un redemarrage en attente bloquait Windows Update (TiWorker
/// retenait 2,4 GB), WMI et DISM ; le cache Windows Update occupait 10,5 GB.
///   * redemarrage en attente depuis longtemps -> proposition critique (redemarrage planifie, annulable) ;
///   * disque sous le seuil d'alerte -> cache Windows Update, nettoyage DISM (administrateur, UAC).
/// Jamais pendant un redemarrage en attente : nettoyer le cache ou lancer DISM pourrait casser la mise à jour.
/// </summary>
public sealed class WindowsMaintenanceModule : IResourceModule, IActionObserver
{
    public const string ScheduleReboot = "ScheduleReboot";
    public const string WindowsUpdateCacheClean = "WindowsUpdateCacheClean";
    public const string DismComponentCleanup = "DismComponentCleanup";
    internal const string WindowsUpdateDownload = @"%SystemRoot%\SoftwareDistribution\Download";

    private static readonly TimeSpan SizeCacheTtl = TimeSpan.FromHours(6);
    private readonly WindowsMaintenanceOptions _options;
    private readonly SafeCleaner _cleaner;
    private readonly IProcessRunner _processes;
    private readonly ModuleState? _state;
    private readonly TimeProvider _time;
    private readonly Func<bool> _rebootPending;
    private readonly Func<long> _windowsUpdateCacheBytes;
    private (DateTimeOffset At, double GB)? _wuSize;

    public WindowsMaintenanceModule(
        ModuleServices services,
        SafeCleaner cleaner,
        WindowsMaintenanceOptions? options = null,
        Func<bool>? rebootPending = null,
        Func<long>? windowsUpdateCacheBytes = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        _cleaner = cleaner ?? throw new ArgumentNullException(nameof(cleaner));
        _options = options ?? WindowsMaintenanceOptions.Default;
        _processes = services.Processes;
        _time = services.Time;
        _state = services.State is null ? null : new ModuleState(services.State, Name);
        _rebootPending = rebootPending ?? WindowsSystemProbe.IsRebootPending;
        _windowsUpdateCacheBytes = windowsUpdateCacheBytes ?? (() => SafeCleaner.FolderSize(SafeCleaner.Expand(WindowsUpdateDownload)).Bytes);
    }

    public string Name => "windows";

    public Task<ModuleEvaluation> EvaluateAsync(EngineContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var now = _time.GetLocalNow();
        var proposals = new List<Proposal>();
        var s = context.Snapshot;

        if (s.RebootPending)
        {
            var since = _state?.GetDate("rebootPendingSince");
            if (since is null)
            {
                since = now;
                _state?.SetDate("rebootPendingSince", now);
            }

            var hours = (now - since.Value).TotalHours;
            if (hours >= _options.RebootPendingProposeHours)
            {
                proposals.Add(new Proposal(
                    "windows-reboot-pending",
                    string.Create(CultureInfo.InvariantCulture, $"Redémarrage Windows en attente depuis {Math.Round(hours)} h"),
                    string.Create(CultureInfo.InvariantCulture, $"Une mise à jour attend le redémarrage : elle retient de la mémoire et bloque la maintenance. Redémarrage planifié dans {_options.RebootDelayMinutes} min (annulable : shutdown /a). Enregistrez votre travail."),
                    0,
                    Severity.Crit,
                    new ActionRequest(ScheduleReboot, new Dictionary<string, string> { ["minutes"] = _options.RebootDelayMinutes.ToString(CultureInfo.InvariantCulture) })));
            }
        }
        else
        {
            _state?.SetDate("rebootPendingSince", null);
        }

        if (s.SystemDriveFreeGB < context.Thresholds.DiskFreeWarnGB && !s.RebootPending)
        {
            var severity = s.SystemDriveFreeGB < context.Thresholds.DiskFreeCritGB ? Severity.Crit : Severity.Warn;
            var wu = WindowsUpdateCacheGB(now);
            if (wu >= _options.WindowsUpdateCacheProposeGB)
            {
                proposals.Add(new Proposal(
                    "windows-update-cache",
                    "Vider le cache de téléchargement de Windows Update",
                    "Fichiers de mises à jour déjà installées. Services Windows Update arrêtés puis relancés. Demande l'autorisation administrateur.",
                    wu,
                    severity,
                    ActionRequest.Of(WindowsUpdateCacheClean)));
            }

            var lastDism = _state?.GetDate("lastDismAt");
            if (lastDism is null || (now - lastDism.Value).TotalDays >= _options.DismEveryDays)
            {
                proposals.Add(new Proposal(
                    "windows-dism-cleanup",
                    "Nettoyer le magasin de composants Windows (DISM)",
                    "Supprime les anciennes versions de composants Windows remplacés par des mises à jour. 10 à 40 min en arrière-plan. Demande l'autorisation administrateur.",
                    2,
                    Severity.Info,
                    ActionRequest.Of(DismComponentCleanup)));
            }
        }

        return Task.FromResult(new ModuleEvaluation(proposals, []));
    }

    public void OnActionCompleted(ActionRequest request, ActionResult result)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(result);
        if (request.Type == DismComponentCleanup && result.Ok && !result.DryRun)
        {
            _state?.SetDate("lastDismAt", _time.GetLocalNow());
        }

        if (request.Type == WindowsUpdateCacheClean && result.Ok)
        {
            _wuSize = null;   // re-mesurer au prochain cycle
        }
    }

    public void RegisterActions(ActionCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        catalog.Register(new ActionDefinition(
            ScheduleReboot,
            RequiresAdmin: false,
            p => p.Count == 1 && p.TryGetValue("minutes", out var m) && int.TryParse(m, NumberStyles.None, CultureInfo.InvariantCulture, out var v) && v is >= 2 and <= 60
                ? null
                : "minutes doit être un entier entre 2 et 60",
            async (req, dryRun, ct) =>
            {
                var minutes = int.Parse(req.Parameters["minutes"], CultureInfo.InvariantCulture);
                if (dryRun)
                {
                    return new ActionOutcome(true, $"simulation : redémarrage dans {minutes} min");
                }

                var r = await _processes.RunAsync(
                    System32("shutdown.exe"),
                    ["/r", "/t", (minutes * 60).ToString(CultureInfo.InvariantCulture), "/c", "Facil FreePC : redémarrage planifié (mise à jour Windows en attente). Annuler : shutdown /a"],
                    TimeSpan.FromSeconds(30),
                    ct).ConfigureAwait(false);
                return new ActionOutcome(r.ExitCode == 0, r.ExitCode == 0 ? $"Redémarrage dans {minutes} min (annuler : shutdown /a)" : $"shutdown a échoué (code {r.ExitCode}) {r.StdErr}".Trim());
            }));

        catalog.Register(new ActionDefinition(
            WindowsUpdateCacheClean,
            RequiresAdmin: true,
            DiskModule.NoParameters,
            (_, dryRun, _) =>
            {
                if (_rebootPending())
                {
                    return Task.FromResult(new ActionOutcome(false, "Redémarrage en attente : nettoyer le cache maintenant risquerait de casser la mise à jour"));
                }

                if (dryRun)
                {
                    return Task.FromResult(new ActionOutcome(true, Gb(_cleaner.RemoveOlderThan(WindowsUpdateDownload, 1, dryRun: true).FreedBytes)));
                }

                StopServices("wuauserv", "bits", "dosvc");
                try
                {
                    return Task.FromResult(new ActionOutcome(true, Gb(_cleaner.RemoveOlderThan(WindowsUpdateDownload, 1).FreedBytes)));
                }
                finally
                {
                    StartServices("bits", "wuauserv");
                }
            }));

        catalog.Register(new ActionDefinition(
            DismComponentCleanup,
            RequiresAdmin: true,
            DiskModule.NoParameters,
            async (_, dryRun, ct) =>
            {
                if (_rebootPending())
                {
                    return new ActionOutcome(false, "Redémarrage en attente : DISM refuserait ou échouerait");
                }

                if (dryRun)
                {
                    return new ActionOutcome(true, "simulation : DISM /StartComponentCleanup");
                }

                var r = await _processes.RunAsync(System32("Dism.exe"), ["/Online", "/Cleanup-Image", "/StartComponentCleanup", "/English"], TimeSpan.FromMinutes(90), ct).ConfigureAwait(false);
                return new ActionOutcome(r.ExitCode == 0, r.TimedOut ? "DISM : délai de 90 min dépassé" : $"DISM terminé (code {r.ExitCode})");
            }));
    }

    private double WindowsUpdateCacheGB(DateTimeOffset now)
    {
        if (_wuSize is { } c && now - c.At < SizeCacheTtl)
        {
            return c.GB;
        }

        var gb = Math.Round(_windowsUpdateCacheBytes() / 1073741824d, 2);
        _wuSize = (now, gb);
        return gb;
    }

    private static string System32(string exe) => Path.Combine(Environment.SystemDirectory, exe);

    private static string Gb(long bytes) => string.Create(CultureInfo.InvariantCulture, $"{Math.Round(bytes / 1073741824d, 2)} GB");

    private static void StopServices(params string[] names)
    {
        foreach (var name in names)
        {
            try
            {
                using var sc = new ServiceController(name);
                if (sc.Status != ServiceControllerStatus.Stopped)
                {
                    sc.Stop();
                    sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(60));
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ServiceProcess.TimeoutException or System.ComponentModel.Win32Exception)
            {
                // service absent ou deja arrete : le nettoyage ignorera les fichiers verrouilles
            }
        }
    }

    private static void StartServices(params string[] names)
    {
        foreach (var name in names)
        {
            try
            {
                using var sc = new ServiceController(name);
                if (sc.Status == ServiceControllerStatus.Stopped)
                {
                    sc.Start();
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Windows relancera le service a la demande (demarrage declenche)
            }
        }
    }
}
