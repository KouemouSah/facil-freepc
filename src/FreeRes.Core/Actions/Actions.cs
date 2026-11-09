using System.Text.RegularExpressions;

namespace FreeRes.Core.Actions;

/// <summary>
/// Action demandee : un TYPE d'une liste fermee + des parametres texte valides par le type.
/// Jamais de code ni de commande libre : une proposition stockee ne peut pas servir a executer autre chose
/// (garantie reprise de la version PowerShell).
/// </summary>
public sealed record ActionRequest(string Type, IReadOnlyDictionary<string, string> Parameters)
{
    /// <summary>
    /// Action sans parametre. Fabrique plutot que 2e constructeur : un record a constructeur unique reste
    /// relisible par System.Text.Json (les propositions sont stockees en JSON dans la base).
    /// </summary>
    public static ActionRequest Of(string type) => new(type, new Dictionary<string, string>());
}

/// <summary>Ce qu'un module rend apres execution.</summary>
/// <summary>
/// Resultat d'une action. <see cref="Changed"/> : false quand l'action n'avait rien a faire (ex. zone deja propre) ;
/// elle reste journalisee mais n'apparait pas dans « Fait automatiquement ».
/// </summary>
public sealed record ActionOutcome(bool Ok, string Message, bool Changed = true);

/// <summary>Resultat complet, journalise et affiche.</summary>
public sealed record ActionResult(string Type, bool Ok, string Message, double DiskDeltaGB, bool DryRun);

/// <summary>
/// Definition d'une action, enregistree par un module au demarrage.
/// <see cref="ValidateParameters"/> renvoie un message d'erreur, ou null si les parametres sont acceptables.
/// </summary>
public sealed record ActionDefinition(
    string Type,
    bool RequiresAdmin,
    Func<IReadOnlyDictionary<string, string>, string?> ValidateParameters,
    Func<ActionRequest, bool, CancellationToken, Task<ActionOutcome>> Execute);

/// <summary>Action refusee AVANT execution (type inconnu, parametres invalides, droits insuffisants).</summary>
public sealed class ActionRejectedException(string message) : Exception(message);

/// <summary>Liste fermee des actions possibles : seules celles enregistrees par le code existent.</summary>
public sealed partial class ActionCatalog
{
    private readonly Dictionary<string, ActionDefinition> _definitions = new(StringComparer.Ordinal);

    [GeneratedRegex("^[A-Z][A-Za-z0-9]{2,48}$")]
    private static partial Regex TypePattern();

    public void Register(ActionDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!TypePattern().IsMatch(definition.Type))
        {
            throw new ArgumentException($"Type d'action invalide : '{definition.Type}'", nameof(definition));
        }

        if (!_definitions.TryAdd(definition.Type, definition))
        {
            throw new InvalidOperationException($"Type d'action déjà enregistré : {definition.Type}");
        }
    }

    public bool IsKnown(string type) => _definitions.ContainsKey(type);

    public IReadOnlyCollection<string> Types => _definitions.Keys;

    /// <summary>Definition + validation des parametres ; leve <see cref="ActionRejectedException"/> sinon.</summary>
    public ActionDefinition Resolve(ActionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_definitions.TryGetValue(request.Type, out var def))
        {
            throw new ActionRejectedException($"Action inconnue ou non autorisée : '{request.Type}'");
        }

        var error = def.ValidateParameters(request.Parameters);
        if (error is not null)
        {
            throw new ActionRejectedException($"{request.Type} : {error}");
        }

        return def;
    }
}
