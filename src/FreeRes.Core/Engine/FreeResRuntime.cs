using System.Globalization;
using FreeRes.Core.Actions;
using FreeRes.Core.Decisions;
using FreeRes.Core.Logging;
using FreeRes.Core.Machine;
using FreeRes.Core.Metrics;
using FreeRes.Core.Platform;
using FreeRes.Core.Policy;
using FreeRes.Core.Storage;

namespace FreeRes.Core.Engine;

/// <summary>
/// Assemblage de l'application (composition) : un seul endroit cree et relie les composants.
/// Les modules (phase 2+) s'enregistrent via <paramref name="modules"/> et declarent leurs actions dans le catalogue.
/// </summary>
public sealed class FreeResRuntime : IDisposable
{
    private FreeResRuntime(AppPaths paths, EventLog log, FreeResDatabase db, ActionCatalog catalog, MonitorEngine engine, IReadOnlyList<string> settingsErrors, TimeProvider time)
    {
        Paths = paths;
        Log = log;
        Database = db;
        Catalog = catalog;
        Engine = engine;
        SettingsErrors = settingsErrors;
        Time = time;
    }

    public TimeProvider Time { get; }

    public AppPaths Paths { get; }

    public EventLog Log { get; }

    public FreeResDatabase Database { get; }

    public ActionCatalog Catalog { get; }

    public MonitorEngine Engine { get; }

    /// <summary>Problemes de settings.json (affiches a l'utilisateur ; les valeurs par defaut s'appliquent).</summary>
    public IReadOnlyList<string> SettingsErrors { get; }

    public string SettingsPath => Path.Combine(Paths.DataDirectory, "settings.json");

    /// <param name="modules">Fabrique des modules (recoit les services, dont l'etat persistant) ; null = aucun module.</param>
    /// <param name="elevatedRunner">Execution UAC des actions admin approuvees ; null = actions admin refusees sans droits.</param>
    public static FreeResRuntime Create(
        AppPaths paths,
        ISystemProbe? probe = null,
        Func<ModuleServices, ModuleSetup>? modules = null,
        IElevation? elevation = null,
        TimeProvider? time = null,
        IElevatedRunner? elevatedRunner = null,
        IProcessRunner? processes = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        time ??= TimeProvider.System;
        probe ??= new WindowsSystemProbe(time);
        processes ??= new ProcessRunner();
        paths.EnsureCreated();

        var log = new EventLog(paths.LogDirectory, time);
        var loaded = SettingsStore.Load(Path.Combine(paths.DataDirectory, "settings.json"));
        var errors = loaded.Errors.ToList();
        var settings = loaded.Settings;

        var profile = MachineProfileReader.Read(probe);
        var thresholds = ThresholdCalculator.FromProfile(profile, settings.Thresholds);
        var thresholdErrors = ThresholdCalculator.Validate(thresholds);
        if (thresholdErrors.Count > 0)
        {
            // Seuils personnalises incoherents : on revient aux seuils calcules pour la machine.
            errors.AddRange(thresholdErrors.Select(e => e + " Seuils calculés pour la machine appliqués."));
            thresholds = ThresholdCalculator.FromProfile(profile);
        }

        foreach (var e in errors)
        {
            log.Write(LogLevel.Warn, "settings", e);
        }

        var db = new FreeResDatabase(Path.Combine(paths.DataDirectory, "freeres.db"));
        try
        {
            var catalog = new ActionCatalog();
            var setup = modules?.Invoke(new ModuleServices(paths, log, time, probe, processes, db, Settings: settings)) ?? ModuleSetup.None;
            setup.RegisterActions(catalog);
            var queue = new DecisionInbox(db, catalog, time, log);
            // Gain disque mesure avant/apres chaque action : lecture directe du volume (pas une capture complete).
            var dispatcher = new ActionDispatcher(catalog, elevation ?? new WindowsElevation(), WindowsSystemProbe.SystemDriveFreeGB, log, elevatedRunner);
            var engine = new MonitorEngine(probe, profile, thresholds, settings, setup.Modules, queue, dispatcher, db, log, time);
            // Journal en format invariant (separateur decimal « . ») : homogene et exploitable par des outils.
            log.Write(LogLevel.Info, "runtime", string.Create(CultureInfo.InvariantCulture, $"Démarrage {AppInfo.Name} {AppInfo.Version} sur {profile.Manufacturer} {profile.Model}, {profile.OsName} build {profile.OsBuild}, {profile.RamTotalGB} GB RAM"));
            return new FreeResRuntime(paths, log, db, catalog, engine, errors, time);
        }
        catch
        {
            db.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        Engine.Dispose();
        Database.Dispose();
    }
}
