using System.Text.Json;
using System.Text.RegularExpressions;
using FreeRes.Core.Actions;
using FreeRes.Core.Decisions;
using FreeRes.Core.Engine;
using FreeRes.Core.Logging;
using FreeRes.Core.Machine;
using FreeRes.Core.Metrics;
using FreeRes.Core.Policy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;

namespace FreeRes.Host;

/// <summary>Proposition telle qu'exposee a l'interface.</summary>
public sealed record PendingDto(string Id, string Title, string Detail, Severity Severity, double EstGainGB, bool RequiresAdmin, DateTimeOffset CreatedAt);

/// <summary>Resultat d'une verification de mise a jour demandee par l'utilisateur.</summary>
public sealed record UpdateCheckResponse(string Message);

/// <summary>Dernier cycle de surveillance.</summary>
public sealed record CycleDto(DateTimeOffset At, int NewProposals, int PendingCount, EscalationMode Escalation, IReadOnlyList<string> Errors);

/// <summary>Evenement de l'historique (action, decision, proposition, erreur).</summary>
public sealed record HistoryEntry(DateTimeOffset At, string Kind, string Message, bool Ok, bool Auto, double? DiskDeltaGB);

/// <summary>Point de mesure reduit pour les courbes.</summary>
public sealed record MetricPoint(DateTimeOffset At, double RamAvailGB, double CommitPct, double DiskFreeGB);

/// <summary>Historique : derniers evenements utiles et mesures des 24 dernieres heures.</summary>
public sealed record HistoryResponse(IReadOnlyList<HistoryEntry> Events, IReadOnlyList<MetricPoint> Metrics);

/// <summary>Etat complet affiche par le tableau de bord.</summary>
public sealed record StateResponse(
    MachineProfile Profile,
    Thresholds Thresholds,
    SystemSnapshot Snapshot,
    bool Critical,
    IReadOnlyList<PendingDto> Pending,
    CycleDto? LastCycle,
    int MonitorIntervalMinutes,
    IReadOnlyList<string> SettingsErrors,
    IReadOnlyList<HistoryEntry> RecentAuto,
    bool DecisionInProgress,
    IReadOnlyList<string> Modules,
    double SnoozeHours,
    double RejectHours,
    string DataDirectory);

/// <summary>Resultat d'une decision « Appliquer ».</summary>
public sealed record ApproveResponse(bool Ok, string Message, double DiskDeltaGB, bool StillPending);

/// <summary>
/// API de l'interface. L'interface ne peut QUE decider des propositions du moteur (catalogue ferme) : aucune
/// route n'accepte un type d'action ou des parametres venant de la page.
/// </summary>
internal static partial class StateApi
{
    private const int HistoryMax = 60;
    private const int MetricsMaxPoints = 144;

    // Meme format que Proposal.Id : l'identifiant ne sert qu'a une recherche exacte.
    [GeneratedRegex("^[a-z0-9-]{3,60}$")]
    private static partial Regex IdPattern();

    public static void Map(IEndpointRouteBuilder app, FreeResRuntime? runtime)
    {
        app.MapGet("/api/state", () => runtime is null ? NotStarted() : Results.Ok(BuildState(runtime)));

        app.MapPost("/api/proposals/{id}/approve", async (string id, IHostApplicationLifetime life) =>
        {
            if (runtime is null)
            {
                return NotStarted();
            }

            if (!IdPattern().IsMatch(id))
            {
                return Results.BadRequest("identifiant invalide");
            }

            try
            {
                // Jeton de l'APPLICATION, pas de la requete : fermer la fenetre n'interrompt pas une action en cours.
                var r = await runtime.Engine.ApproveAsync(id, life.ApplicationStopping).ConfigureAwait(false);
                return Results.Ok(new ApproveResponse(r.Ok, r.Message, r.DiskDeltaGB, runtime.Engine.Queue.Get(id) is not null));
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(ex.Message);
            }
            catch (ActionRejectedException ex)
            {
                return Results.UnprocessableEntity(ex.Message);
            }
        });

        app.MapPost("/api/proposals/{id}/snooze", (string id) => Decide(runtime, id, e => e.Snooze(id)));
        app.MapPost("/api/proposals/{id}/reject", (string id) => Decide(runtime, id, e => e.Reject(id)));

        // Verification de mise a jour demandee par l'utilisateur, puis un cycle : la decision apparait aussitot.
        app.MapPost("/api/updates/check", async (IHostApplicationLifetime life) =>
        {
            if (runtime is null)
            {
                return NotStarted();
            }

            if (runtime.Engine.UpdateCheck is not { } check)
            {
                return Results.NotFound("mises à jour indisponibles");
            }

            var message = await check.CheckNowAsync(life.ApplicationStopping).ConfigureAwait(false);
            await runtime.Engine.RunCycleAsync(life.ApplicationStopping).ConfigureAwait(false);
            return Results.Ok(new UpdateCheckResponse(message));
        });

        app.MapPost("/api/cycle", async (IHostApplicationLifetime life) =>
        {
            if (runtime is null)
            {
                return NotStarted();
            }

            var r = await runtime.Engine.RunCycleAsync(life.ApplicationStopping).ConfigureAwait(false);
            return Results.Ok(new CycleDto(r.At, r.NewProposals, r.PendingCount, r.Escalation, r.Errors));
        });

        app.MapGet("/api/modules/{name}", (string name) =>
        {
            if (runtime is null)
            {
                return NotStarted();
            }

            var source = runtime.Engine.StatusSources.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.Ordinal));
            return source is null ? Results.NotFound("module inconnu") : Results.Ok(new ModuleStatusResponse(source.Name, source.CurrentStatus));
        });

        app.MapGet("/api/history", () =>
        {
            if (runtime is null)
            {
                return NotStarted();
            }

            var events = runtime.Log.ReadRecent(HistoryMax, IsHistory).Select(ToHistory).Reverse().ToList();
            var rows = runtime.Database.MetricsSince(runtime.Time.GetLocalNow().AddHours(-24));
            var step = Math.Max(1, (int)Math.Ceiling(rows.Count / (double)MetricsMaxPoints));
            var metrics = rows.Where((_, i) => i % step == 0)
                .Select(m => new MetricPoint(m.Timestamp, m.RamAvailGB, m.CommitPct, m.DiskFreeGB)).ToList();
            return Results.Ok(new HistoryResponse(events, metrics));
        });
    }

    internal static StateResponse BuildState(FreeResRuntime runtime)
    {
        var engine = runtime.Engine;
        var snapshot = engine.CaptureNow();   // mesure instantanee : l'affichage ne depend pas de l'intervalle du cycle
        var pending = engine.Queue.Pending()
            .Select(p => new PendingDto(p.Proposal.Id, p.Proposal.Title, p.Proposal.Detail, p.Proposal.Severity, p.Proposal.EstGainGB, p.RequiresAdmin, p.CreatedAt))
            .ToList();
        var last = engine.LastReport is { } r ? new CycleDto(r.At, r.NewProposals, r.PendingCount, r.Escalation, r.Errors) : null;
        var auto = runtime.Log.ReadRecent(5, e => e.Source == "action" && IsAuto(e) && Changed(e)).Select(ToHistory).Reverse().ToList();

        return new StateResponse(
            engine.Profile,
            engine.Thresholds,
            snapshot,
            EscalationPolicy.IsCritical(snapshot, engine.Thresholds),
            pending,
            last,
            engine.Settings.MonitorIntervalMinutes,
            runtime.SettingsErrors,
            auto,
            engine.DecisionInProgress,
            engine.StatusSources.Select(s => s.Name).ToList(),
            engine.Settings.SnoozeHours,
            engine.Settings.RejectHours,
            runtime.Paths.DataDirectory);
    }

    private static IResult Decide(FreeResRuntime? runtime, string id, Action<MonitorEngine> decide)
    {
        if (runtime is null)
        {
            return NotStarted();
        }

        if (!IdPattern().IsMatch(id))
        {
            return Results.BadRequest("identifiant invalide");
        }

        try
        {
            decide(runtime.Engine);
            return Results.NoContent();
        }
        catch (KeyNotFoundException ex)
        {
            return Results.NotFound(ex.Message);
        }
    }

    private static IResult NotStarted() => Results.Problem("Moteur non démarré", statusCode: StatusCodes.Status503ServiceUnavailable);

    /// <summary>Ce que l'utilisateur doit retrouver : actions, decisions, nouvelles propositions, erreurs.</summary>
    internal static bool IsHistory(LogEntry e) =>
        e.Source is "action" or "decision"
        || (e.Source == "proposal" && e.Message.StartsWith("Nouvelle", StringComparison.Ordinal))
        || e.Level == LogLevel.Error;

    // Les donnees relues du journal sont des JsonElement.
    private static bool IsAuto(LogEntry e) =>
        e.Data is not null && e.Data.TryGetValue("auto", out var v) && v is JsonElement { ValueKind: JsonValueKind.True };

    // Ancienne entree sans l'information : consideree comme ayant agi.
    private static bool Changed(LogEntry e) =>
        e.Data is null || !e.Data.TryGetValue("changed", out var v) || v is not JsonElement { ValueKind: JsonValueKind.False };

    private static HistoryEntry ToHistory(LogEntry e)
    {
        double? delta = e.Data is not null && e.Data.TryGetValue("diskDeltaGB", out var d) && d is JsonElement { ValueKind: JsonValueKind.Number } n
            ? n.GetDouble()
            : null;
        var kind = e.Source switch
        {
            "action" => "action",
            "decision" => "decision",
            "proposal" => "proposal",
            _ => "error",
        };
        var message = e.Data is not null && e.Data.TryGetValue("type", out var t) && t is JsonElement { ValueKind: JsonValueKind.String } type
            ? ActionLabels.Rewrite(type.GetString()!, e.Message)
            : e.Message;
        return new HistoryEntry(e.Ts, kind, message, e.Level != LogLevel.Error, IsAuto(e), delta);
    }
}

/// <summary>Etat d'un module (structure propre a chaque module).</summary>
public sealed record ModuleStatusResponse(string Name, object? Status);
