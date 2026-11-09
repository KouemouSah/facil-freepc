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
                log.Write(LogLevel.Error, module.Name, $"Evaluation en echec : {ex.Message}");
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
                    errors.Add($"{module.Name} : proposition refusee ({ex.Message})");
                    log.Write(LogLevel.Error, module.Name, $"Proposition refusee : {ex.Message}");
                }
            }

            _lastIdsByModule[module.Name] = ids;
            active.UnionWith(ids);

            foreach (var auto in evaluation.AutoActions)
            {
                try
                {
                    autoResults.Add(await dispatcher.ExecuteAsync(auto, dryRun: false, cancellationToken).ConfigureAwait(false));
                }
                catch (ActionRejectedException ex)
                {
                    errors.Add($"{module.Name} : action automatique refusee ({ex.Message})");
                    log.Write(LogLevel.Error, module.Name, $"Action automatique refusee : {ex.Message}");
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
            ReadDate("lastWindowAt"),
            ReadDate("lastToastAt"),
            now);

        ApplyRetentionIfDue(now);
        log.Write(
            LogLevel.Info,
            "monitor",
            string.Create(CultureInfo.InvariantCulture, $"RAM {snapshot.RamAvailGB} GB dispo, commit {snapshot.CommitPct} %, {snapshot.SystemDrive} {snapshot.SystemDriveFreeGB} GB libres, {pending.Count} en attente ({newCount} nouvelles), escalade {escalation}"));

        var report = new CycleReport(now, snapshot, newCount, pending.Count, staleRemoved, escalation, autoResults, errors);
        LastReport = report;
        return report;
    }

    /// <summary>Approuve une proposition : execute son action, puis enregistre la decision si elle a ete tentee.</summary>
    public async Task<ActionResult> ApproveAsync(string id, CancellationToken cancellationToken)
    {
        var pending = queue.Get(id) ?? throw new KeyNotFoundException($"Proposition inconnue ou deja traitee : {id}");
        var result = await dispatcher.ExecuteAsync(pending.Proposal.Action, dryRun: false, cancellationToken).ConfigureAwait(false);
        queue.Decide(id, DecisionKind.Approved);
        db.AddMetrics(probe.Capture(), "approve", Math.Max(0d, result.DiskDeltaGB));
        return result;
    }

    public void Reject(string id) => queue.Decide(id, DecisionKind.Rejected, TimeSpan.FromHours(settings.RejectHours));

    public void Snooze(string id) => queue.Decide(id, DecisionKind.Snoozed, TimeSpan.FromHours(settings.SnoozeHours));

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
            log.Write(LogLevel.Info, "retention", $"Retention : {metrics} mesures, {decisions} decisions, {logs} journaux supprimes");
        }
    }

    private DateTimeOffset? ReadDate(string key) => db.GetState(key) is { } v ? FreeResDatabase.Parse(v) : null;

    public void Dispose() => _cycleGate.Dispose();
}
