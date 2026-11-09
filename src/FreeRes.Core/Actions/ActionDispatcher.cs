using System.Security.Principal;
using FreeRes.Core.Logging;

namespace FreeRes.Core.Actions;

/// <summary>Droits du processus courant (remplacable dans les tests).</summary>
public interface IElevation
{
    bool IsAdministrator { get; }
}

public sealed class WindowsElevation : IElevation
{
    public bool IsAdministrator
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }
}

/// <summary>
/// Execute une action de la liste fermee : validation, controle des droits, mesure du gain disque,
/// journalisation. Une erreur du module donne un resultat en echec, jamais un plantage du moteur.
/// </summary>
public sealed class ActionDispatcher(ActionCatalog catalog, IElevation elevation, Func<double> systemDriveFreeGB, EventLog log)
{
    public async Task<ActionResult> ExecuteAsync(ActionRequest request, bool dryRun, CancellationToken cancellationToken)
    {
        var def = catalog.Resolve(request);   // leve ActionRejectedException : refus avant toute execution
        if (def.RequiresAdmin && !dryRun && !elevation.IsAdministrator)
        {
            // L'execution elevee (relance « runas » pour UNE action) est apportee par le premier module qui en a besoin (phase 2).
            throw new ActionRejectedException($"{request.Type} necessite des droits administrateur");
        }

        var before = systemDriveFreeGB();
        ActionOutcome outcome;
        try
        {
            outcome = await def.Execute(request, dryRun, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // Un module defaillant ne doit jamais arreter la surveillance : l'echec est rendu et journalise.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            outcome = new ActionOutcome(false, $"Erreur : {ex.Message}");
        }

        var delta = Math.Round(systemDriveFreeGB() - before, 2);
        var result = new ActionResult(request.Type, outcome.Ok, outcome.Message, delta, dryRun);
        log.Write(
            outcome.Ok ? LogLevel.Action : LogLevel.Error,
            "action",
            $"{request.Type} : {outcome.Message}{(dryRun ? " (simulation)" : string.Empty)}",
            new Dictionary<string, object?> { ["diskDeltaGB"] = delta, ["dryRun"] = dryRun });
        return result;
    }
}
