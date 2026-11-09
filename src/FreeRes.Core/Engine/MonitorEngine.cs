using System.Globalization;
using FreeRes.Core.Actions;
using FreeRes.Core.Decisions;
using FreeRes.Core.Logging;
using FreeRes.Core.Machine;
using FreeRes.Core.Metrics;
using FreeRes.Core.Policy;
using FreeRes.Core.Storage;

namespace FreeRes.Core.Engine;

/// <summary>Ce que voit un module a chaque cycle.</summary>
public sealed record EngineContext(SystemSnapshot Snapshot, MachineProfile Profile, Thresholds Thresholds, AppSettings Settings);

/// <summary>
/// Resultat d'un module : propositions (soumises a l'utilisateur) et actions automatiques SURES
/// (executees sans demander, ex. purge d'une fuite de fichiers temporaires connue).
/// </summary>
public sealed record ModuleEvaluation(IReadOnlyList<Proposal> Proposals, IReadOnlyList<ActionRequest> AutoActions)
{
    public static ModuleEvaluation Empty { get; } = new([], []);
}

/// <summary>Un domaine surveille (disque, navigateurs, IA...). Les modules arrivent a partir de la phase 2.</summary>
public interface IResourceModule
{
    string Name { get; }

    Task<ModuleEvaluation> EvaluateAsync(EngineContext context, CancellationToken cancellationToken);
}

/// <summary>Module qui expose son dernier etat a l'interface (lecture seule, null avant le premier cycle).</summary>
public interface IStatusSource
{
    string Name { get; }

    object? CurrentStatus { get; }
}

/// <summary>
/// Module capable de designer l'executable dont l'icone represente un element de son etat (identifiant tel que
/// l'interface le connait). null = pas d'icone connue. Le chemin n'est jamais fourni par l'interface.
/// </summary>
public interface IIconSource
{
    string Name { get; }

    string? IconPath(string id);
}

/// <summary>Verification de mise a jour demandee par l'utilisateur ; renvoie le message a afficher.</summary>
public interface IUpdateCheck
{
    Task<string> CheckNowAsync(CancellationToken cancellationToken);
}

/// <summary>Compte rendu d'un cycle de surveillance.</summary>
public sealed record CycleReport(
    DateTimeOffset At,
    SystemSnapshot Snapshot,
    int NewProposals,
    int PendingCount,
    int StaleRemoved,
    EscalationMode Escalation,
    IReadOnlyList<ActionResult> AutoActions,
    IReadOnlyList<string> Errors);

/// <summary>
/// Moteur : mesure -> historique -> modules -> file de decisions -> escalade -> retention.
/// Un module en erreur ne bloque ni les autres ni le cycle, et ses propositions sont CONSERVEES
/// (sinon un incident passager les ferait disparaitre puis reapparaitre comme « nouvelles »).
/// </summary>
public sealed class MonitorEngine(
    ISystemProbe probe,
    MachineProfile profile,
    Thresholds thresholds,
    AppSettings settings,
    IReadOnlyList<IResourceModule> modules,
    DecisionInbox queue,
    ActionDispatcher dispatcher,
    FreeResDatabase db,
    EventLog log,
    TimeProvider time) : IDisposable
{
    internal const string LastRetentionKey = "lastRetentionAt";
    private readonly Dictionary<string, IReadOnlySet<string>> _lastIdsByModule = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _cycleGate = new(1, 1);

    // Decisions de l'utilisateur executees une par une : un double clic ne peut pas lancer deux fois une action.
    // Verrou distinct du cycle : une action longue (compactage) ne bloque pas la surveillance.
    private readonly SemaphoreSlim _decisionGate = new(1, 1);

    internal const string LastWindowKey = "lastWindowAt";
    internal const string LastToastKey = "lastToastAt";

    /// <summary>Fin de chaque cycle (thread d'arrière-plan) : l'application y applique l'escalade.</summary>
    public event EventHandler<CycleReport>? CycleCompleted;

    /// <summary>Modules exposant leur etat a l'interface.</summary>
    public IReadOnlyList<IStatusSource> StatusSources { get; } = modules.OfType<IStatusSource>().ToList();

    public IReadOnlyList<IIconSource> IconSources { get; } = modules.OfType<IIconSource>().ToList();

    public IUpdateCheck? UpdateCheck { get; } = modules.OfType<IUpdateCheck>().FirstOrDefault();

    public MachineProfile Profile => profile;

    public Thresholds Thresholds => thresholds;

    public AppSettings Settings => settings;

    public DecisionInbox Queue => queue;

    public CycleReport? LastReport { get; private set; }

    public SystemSnapshot CaptureNow() => probe.Capture();

    public async Task<CycleReport> RunCycleAsync(CancellationToken cancellationToken)
    {
        await _cycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RunCycleCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _cycleGate.Release();
        }
    }

    private async Task<CycleReport> RunCycleCoreAsync(CancellationToken cancellationToken)
    {
        var snapshot = probe.Capture();
        db.AddMetrics(snapshot, "monitor");
        var context = new EngineContext(snapshot, profile, thresholds, settings);
        var errors = new List<string>();
        var autoResults = new List<ActionResult>();
        var active = new HashSet<string>(StringComparer.Ordinal);
        var newCount = 0;

        foreach (var module in modules)
        {
            ModuleEvaluation evaluation;
            try
            {
                evaluation = await module.EvaluateAsync(context, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
#pragma warning disable CA1031 // Isolation des modules : l'erreur est journalisee et le cycle continue.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                errors.Add($"{module.Name} : {ex.Message}");
                log.Write(LogLevel.Error, module.Name, $"Évaluation en échec : {ex.Message}");
                if (_lastIdsByModule.TryGetValue(module.Name, out var previous))
                {
                    active.UnionWith(previous);   // on garde ses propositions en l'etat
                }

                continue;
            }

            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var proposal in evaluation.Proposals)
            {
                try
                {
                    if (queue.Propose(proposal))
                    {
                        newCount++;
                    }

                    ids.Add(proposal.Id);
                }
                catch (Exception ex) when (ex is ArgumentException or ActionRejectedException)
                {
                    errors.Add($"{module.Name} : proposition refusée ({ex.Message})");
                    log.Write(LogLevel.Error, module.Name, $"Proposition refusée : {ex.Message}");
                }
            }

            _lastIdsByModule[module.Name] = ids;
            active.UnionWith(ids);

            foreach (var auto in evaluation.AutoActions)
            {
                try
                {
                    var autoResult = await dispatcher.ExecuteAsync(auto, dryRun: false, cancellationToken, automatic: true).ConfigureAwait(false);
                    autoResults.Add(autoResult);
                    NotifyObservers(auto, autoResult);
                }
                catch (ActionRejectedException ex)
                {
                    errors.Add($"{module.Name} : action automatique refusée ({ex.Message})");
                    log.Write(LogLevel.Error, module.Name, $"Action automatique refusée : {ex.Message}");
                }
            }
        }

        var staleRemoved = queue.RemoveStale(active);
        var pending = queue.Pending();
        var now = time.GetLocalNow();
        var escalation = EscalationPolicy.Decide(
            snapshot,
            thresholds,
            settings.Notification,
            newCount,
            pending.Count,
            ReadDate(LastWindowKey),
            ReadDate(LastToastKey),
            now);

        ApplyRetentionIfDue(now);
        log.Write(
            LogLevel.Info,
            "monitor",
            string.Create(CultureInfo.InvariantCulture, $"RAM {snapshot.RamAvailGB} GB dispo, commit {snapshot.CommitPct} %, {snapshot.SystemDrive} {snapshot.SystemDriveFreeGB} GB libres, {pending.Count} en attente ({newCount} nouvelles), escalade {escalation}"));

        var report = new CycleReport(now, snapshot, newCount, pending.Count, staleRemoved, escalation, autoResults, errors);
        LastReport = report;
        try
        {
            CycleCompleted?.Invoke(this, report);
        }
#pragma warning disable CA1031 // Un abonne defaillant (interface) ne doit pas faire echouer la surveillance.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            log.Write(LogLevel.Error, "monitor", $"Abonné de fin de cycle en échec : {ex.Message}");
        }

        return report;
    }

    /// <summary>
    /// A appeler quand l'escalade a REELLEMENT ete presentee (notification affichee, fenetre ouverte) : demarre
    /// le delai anti-repetition. Une escalade non presentee (application sans interface) ne compte pas.
    /// </summary>
    public void RecordEscalation(EscalationMode mode)
    {
        var key = mode switch
        {
            EscalationMode.Toast => LastToastKey,
            EscalationMode.Window => LastWindowKey,
            _ => null,
        };
        if (key is not null)
        {
            db.SetState(key, FreeResDatabase.Iso(time.GetLocalNow()));
        }
    }

    /// <summary>
    /// Approuve une proposition : execute son action (avec demande UAC si elle exige l'administrateur), puis
    /// enregistre la decision si elle a ete tentee. Une elevation refusee ou une action rejetee laisse la proposition en place.
    /// Les decisions sont executees une par une ; la proposition est relue APRES l'attente (deja traitee = inconnue).
    /// </summary>
    public async Task<ActionResult> ApproveAsync(string id, CancellationToken cancellationToken)
    {
        await _decisionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var pending = queue.Get(id) ?? throw new KeyNotFoundException($"Proposition inconnue ou déjà traitée : {id}");
            var result = await dispatcher.ExecuteAsync(pending.Proposal.Action, dryRun: false, cancellationToken, allowElevation: true).ConfigureAwait(false);
            if (result.Message.StartsWith(ElevationCancelledPrefix, StringComparison.Ordinal))
            {
                return result;   // l'utilisateur a refuse l'UAC : rien n'a ete tente, la proposition reste
            }

            queue.Decide(id, DecisionKind.Approved);
            NotifyObservers(pending.Proposal.Action, result);
            db.AddMetrics(probe.Capture(), "approve", Math.Max(0d, result.DiskDeltaGB));
            return result;
        }
        finally
        {
            _decisionGate.Release();
        }
    }

    /// <summary>Une decision est-elle en cours d'execution (affichage « action en cours »).</summary>
    public bool DecisionInProgress => _decisionGate.CurrentCount == 0;

    /// <summary>Debut du message rendu quand l'utilisateur refuse la demande UAC.</summary>
    public const string ElevationCancelledPrefix = "Élévation refusée";

    private void NotifyObservers(ActionRequest request, ActionResult result)
    {
        foreach (var observer in modules.OfType<IActionObserver>())
        {
            try
            {
                observer.OnActionCompleted(request, result);
            }
#pragma warning disable CA1031 // Un observateur defaillant ne doit pas faire echouer l'action deja executee.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                log.Write(LogLevel.Error, "monitor", $"Observateur en échec après {request.Type} : {ex.Message}");
            }
        }
    }

    /// <summary>Ignore la proposition pendant RejectHours. KeyNotFoundException si elle n'est plus en attente.</summary>
    public void Reject(string id) => DecideWithoutAction(id, DecisionKind.Rejected, settings.RejectHours);

    /// <summary>Reporte la proposition de SnoozeHours. KeyNotFoundException si elle n'est plus en attente.</summary>
    public void Snooze(string id) => DecideWithoutAction(id, DecisionKind.Snoozed, settings.SnoozeHours);

    private void DecideWithoutAction(string id, DecisionKind kind, double hours)
    {
        _decisionGate.Wait();
        try
        {
            if (queue.Get(id) is null)
            {
                throw new KeyNotFoundException($"Proposition inconnue ou déjà traitée : {id}");
            }

            queue.Decide(id, kind, TimeSpan.FromHours(hours));
        }
        finally
        {
            _decisionGate.Release();
        }
    }

    /// <summary>Retention au plus une fois par 24 h : historique, decisions anciennes, journaux.</summary>
    private void ApplyRetentionIfDue(DateTimeOffset now)
    {
        var last = ReadDate(LastRetentionKey);
        if (last is not null && (now - last.Value).TotalHours < 24)
        {
            return;
        }

        var r = settings.Retention;
        var metrics = db.DeleteMetricsBefore(now.AddDays(-r.HistoryDays));
        var decisions = db.DeleteDecisionsBefore(now.AddDays(-r.DecisionsDays));
        var logs = log.Purge(r.LogDays);
        db.SetState(LastRetentionKey, FreeResDatabase.Iso(now));
        if (metrics + decisions + logs > 0)
        {
            log.Write(LogLevel.Info, "retention", $"Retention : {metrics} mesures, {decisions} décisions, {logs} journaux supprimés");
        }
    }

    private DateTimeOffset? ReadDate(string key) => db.GetState(key) is { } v ? FreeResDatabase.Parse(v) : null;

    public void Dispose()
    {
        _cycleGate.Dispose();
        _decisionGate.Dispose();
    }
}
