using FreeRes.Core.Actions;
using FreeRes.Core.Logging;
using FreeRes.Core.Storage;

namespace FreeRes.Core.Decisions;

/// <summary>
/// File des decisions. Regles reprises de la version PowerShell (et de ses tests) :
///   * une proposition n'est creee qu'une fois (dedoublonnage par identifiant) ;
///   * un rejet ou un report est respecte jusqu'a son echeance ;
///   * une proposition dont la condition a disparu est retiree (« perimee ») ;
///   * seules les actions de la liste fermee sont acceptees.
/// </summary>
public sealed class DecisionInbox(FreeResDatabase db, ActionCatalog catalog, TimeProvider time, EventLog log)
{
    /// <summary>Cree ou rafraichit une proposition. true si elle est NOUVELLE (sert a la notification).</summary>
    public bool Propose(Proposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        var error = proposal.Validate();
        if (error is not null)
        {
            throw new ArgumentException(error, nameof(proposal));
        }

        var def = catalog.Resolve(proposal.Action);   // refuse un type inconnu ou des parametres invalides
        var now = time.GetLocalNow();

        var decision = db.GetDecision(proposal.Id);
        if (decision is { Until: { } until } && until > now)
        {
            return false;   // rejetee ou reportee : on respecte le choix jusqu'a l'echeance
        }

        var existing = db.GetPending(proposal.Id);
        db.UpsertPending(new PendingProposal(proposal, def.RequiresAdmin, existing?.CreatedAt ?? now, now));
        if (existing is null)
        {
            log.Write(LogLevel.Info, "proposal", $"Nouvelle proposition : {proposal.Title}", new Dictionary<string, object?> { ["id"] = proposal.Id });
            return true;
        }

        return false;
    }

    /// <summary>Propositions en attente : critiques d'abord, puis par gain estime decroissant.</summary>
    public IReadOnlyList<PendingProposal> Pending() =>
        db.ListPending()
            .OrderByDescending(p => p.Proposal.Severity)
            .ThenByDescending(p => p.Proposal.EstGainGB)
            .ThenBy(p => p.Proposal.Id, StringComparer.Ordinal)
            .ToList();

    public PendingProposal? Get(string id) => db.GetPending(id);

    /// <summary>Enregistre la decision et retire la proposition. Rejet/report : bornes par <paramref name="duration"/>.</summary>
    public void Decide(string id, DecisionKind kind, TimeSpan? duration = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (kind != DecisionKind.Approved && (duration is null || duration <= TimeSpan.Zero))
        {
            throw new ArgumentException("Un rejet ou un report exige une duree positive", nameof(duration));
        }

        var now = time.GetLocalNow();
        var title = db.GetPending(id)?.Proposal.Title;
        db.SetDecision(new DecisionRecord(id, kind, now, kind == DecisionKind.Approved ? null : now + duration));
        db.RemovePending(id);
        log.Write(
            LogLevel.Info,
            "decision",
            $"{Verb(kind)} : {title ?? id}",
            new Dictionary<string, object?> { ["id"] = id, ["kind"] = kind.ToString(), ["until"] = kind == DecisionKind.Approved ? null : now + duration });
    }

    /// <summary>Libelle de la decision dans le journal (affiche tel quel dans l'historique).</summary>
    private static string Verb(DecisionKind kind) => kind switch
    {
        DecisionKind.Approved => "Appliquée",
        DecisionKind.Snoozed => "Reportée",
        DecisionKind.Rejected => "Ignorée",
        _ => kind.ToString(),
    };

    /// <summary>Retire les propositions dont la condition a disparu. Retourne le nombre retire.</summary>
    public int RemoveStale(IReadOnlySet<string> activeIds)
    {
        ArgumentNullException.ThrowIfNull(activeIds);
        var removed = 0;
        foreach (var p in db.ListPending())
        {
            if (!activeIds.Contains(p.Proposal.Id) && db.RemovePending(p.Proposal.Id))
            {
                removed++;
                log.Write(LogLevel.Info, "proposal", $"Proposition retiree (condition disparue) : {p.Proposal.Id}");
            }
        }

        return removed;
    }
}
