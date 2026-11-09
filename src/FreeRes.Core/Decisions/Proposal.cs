using System.Text.RegularExpressions;
using FreeRes.Core.Actions;

namespace FreeRes.Core.Decisions;

public enum Severity
{
    Info,
    Warn,
    Crit,
}

public enum DecisionKind
{
    Approved,
    Rejected,
    Snoozed,
}

/// <summary>Proposition d'un module, soumise a la decision de l'utilisateur.</summary>
public sealed partial record Proposal(string Id, string Title, string Detail, double EstGainGB, Severity Severity, ActionRequest Action)
{
    [GeneratedRegex("^[a-z0-9-]{3,60}$")]
    private static partial Regex IdPattern();

    /// <summary>Identifiant sur (sert de cle de stockage) ; titre obligatoire et borne.</summary>
    public string? Validate()
    {
        if (!IdPattern().IsMatch(Id))
        {
            return $"Identifiant de proposition invalide : '{Id}'";
        }

        if (string.IsNullOrWhiteSpace(Title) || Title.Length > 200)
        {
            return "Titre de proposition vide ou trop long (200 caractères au plus)";
        }

        if (Detail.Length > 2000)
        {
            return "Détail de proposition trop long (2000 caractères au plus)";
        }

        return EstGainGB is < 0 or double.NaN ? "Gain estimé invalide" : null;
    }
}

/// <summary>Proposition en attente, telle que stockee et affichee.</summary>
public sealed record PendingProposal(Proposal Proposal, bool RequiresAdmin, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>Decision de l'utilisateur ; <see cref="Until"/> borne un rejet ou un report.</summary>
public sealed record DecisionRecord(string ProposalId, DecisionKind Kind, DateTimeOffset At, DateTimeOffset? Until);
