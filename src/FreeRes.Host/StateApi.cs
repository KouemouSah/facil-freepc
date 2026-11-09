using FreeRes.Core.Decisions;
using FreeRes.Core.Engine;
using FreeRes.Core.Machine;
using FreeRes.Core.Metrics;
using FreeRes.Core.Policy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FreeRes.Host;

/// <summary>Proposition telle qu'exposee a l'interface.</summary>
public sealed record PendingDto(string Id, string Title, string Detail, Severity Severity, double EstGainGB, bool RequiresAdmin, DateTimeOffset CreatedAt);

/// <summary>Dernier cycle de surveillance.</summary>
public sealed record CycleDto(DateTimeOffset At, int NewProposals, int PendingCount, EscalationMode Escalation, IReadOnlyList<string> Errors);

/// <summary>Etat complet affiche par le tableau de bord.</summary>
public sealed record StateResponse(
    MachineProfile Profile,
    Thresholds Thresholds,
    SystemSnapshot Snapshot,
    bool Critical,
    IReadOnlyList<PendingDto> Pending,
    CycleDto? LastCycle,
    int MonitorIntervalMinutes,
    IReadOnlyList<string> SettingsErrors);

internal static class StateApi
{
    public static void Map(IEndpointRouteBuilder app, FreeResRuntime? runtime)
    {
        app.MapGet("/api/state", () =>
        {
            if (runtime is null)
            {
                return Results.Problem("Moteur non demarre", statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var engine = runtime.Engine;
            var snapshot = engine.CaptureNow();   // mesure instantanee : l'affichage ne depend pas de l'intervalle du cycle
            var pending = engine.Queue.Pending()
                .Select(p => new PendingDto(p.Proposal.Id, p.Proposal.Title, p.Proposal.Detail, p.Proposal.Severity, p.Proposal.EstGainGB, p.RequiresAdmin, p.CreatedAt))
                .ToList();
            var last = engine.LastReport is { } r ? new CycleDto(r.At, r.NewProposals, r.PendingCount, r.Escalation, r.Errors) : null;

            return Results.Ok(new StateResponse(
                engine.Profile,
                engine.Thresholds,
                snapshot,
                EscalationPolicy.IsCritical(snapshot, engine.Thresholds),
                pending,
                last,
                engine.Settings.MonitorIntervalMinutes,
                runtime.SettingsErrors));
        });
    }
}
