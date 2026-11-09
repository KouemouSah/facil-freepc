using FreeRes.Core;
using FreeRes.Core.Actions;
using FreeRes.Core.Engine;
using FreeRes.Core.Logging;
using FreeRes.Core.Metrics;
using FreeRes.Core.Platform;
using FreeRes.Modules;

namespace FreeRes.App;

/// <summary>
/// Relance elevee : « FacilFreePC.exe --run-action &lt;requete&gt; --result &lt;fichier&gt; ».
/// Execute UNE action administrateur de la liste fermee puis s'arrete : pas de fenetre, pas d'instance unique,
/// pas d'acces a la base (les modules memorisent leurs dates dans le processus utilisateur).
/// Limite connue : avec une elevation par un AUTRE compte administrateur, %LOCALAPPDATA% differe ; l'action
/// s'execute mais son resultat n'est pas relu (signale comme tel dans l'interface).
/// </summary>
internal static class ElevatedMode
{
    public static bool TryParse(string[] args, out string encoded, out string resultPath)
    {
        encoded = string.Empty;
        resultPath = string.Empty;
        if (args.Length != 4 || args[0] != ElevatedActionProtocol.RunActionArgument || args[2] != ElevatedActionProtocol.ResultArgument)
        {
            return false;
        }

        encoded = args[1];
        resultPath = args[3];
        return true;
    }

    public static int Run(string encoded, string resultPath, IElevation? elevation = null)
    {
        var paths = AppPaths.Default();
        if (!ElevatedActionProtocol.IsValidResultPath(paths, resultPath))
        {
            return 2;   // chemin de resultat non conforme : on n'ecrit rien nulle part
        }

        elevation ??= new WindowsElevation();
        var log = new EventLog(paths.LogDirectory, TimeProvider.System);
        ActionResult result;
        var request = ElevatedActionProtocol.Decode(encoded);
        if (request is null)
        {
            result = new ActionResult("?", false, "Requête d'action illisible", 0, false);
        }
        else if (!elevation.IsAdministrator)
        {
            result = new ActionResult(request.Type, false, "Processus non élevé", 0, false);
        }
        else
        {
            result = Execute(request, paths, log, elevation);
        }

        ElevatedActionProtocol.WriteResult(resultPath, result);
        return result.Ok ? 0 : 1;
    }

    private static ActionResult Execute(ActionRequest request, AppPaths paths, EventLog log, IElevation elevation)
    {
        var catalog = new ActionCatalog();
        var services = new ModuleServices(paths, log, TimeProvider.System, new WindowsSystemProbe(), new ProcessRunner(), State: null);
        ModulePack.Create(services).RegisterActions(catalog);
        try
        {
            if (!catalog.Resolve(request).RequiresAdmin)
            {
                // Moindre privilege : une action utilisateur ne s'execute jamais avec des droits eleves.
                return new ActionResult(request.Type, false, "Action non administrateur refusée en mode élevé", 0, false);
            }

            var dispatcher = new ActionDispatcher(catalog, elevation, WindowsSystemProbe.SystemDriveFreeGB, log);
            return dispatcher.ExecuteAsync(request, dryRun: false, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (ActionRejectedException ex)
        {
            return new ActionResult(request.Type, false, ex.Message, 0, false);
        }
    }
}
