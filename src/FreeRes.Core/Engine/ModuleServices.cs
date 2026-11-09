using FreeRes.Core.Actions;
using FreeRes.Core.Logging;
using FreeRes.Core.Metrics;
using FreeRes.Core.Platform;
using FreeRes.Core.Storage;

namespace FreeRes.Core.Engine;

/// <summary>
/// Services offerts aux modules. <see cref="State"/> est null dans le processus ELEVE : celui-ci n'execute
/// qu'une action et ne touche pas a la base (un module enregistre ses dates via <see cref="IActionObserver"/>).
/// </summary>
public sealed record ModuleServices(
    AppPaths Paths,
    EventLog Log,
    TimeProvider Time,
    ISystemProbe Probe,
    IProcessRunner Processes,
    IStateStore? State,
    IProcessLauncher? Launcher = null)
{
    /// <summary>Lanceur detache (relance d'un navigateur...) ; implementation reelle par defaut.</summary>
    public IProcessLauncher EffectiveLauncher => Launcher ?? new ProcessLauncher();
}

/// <summary>Modules a surveiller + enregistrement de leurs actions dans la liste fermee.</summary>
public sealed record ModuleSetup(IReadOnlyList<IResourceModule> Modules, Action<ActionCatalog> RegisterActions)
{
    public static ModuleSetup None { get; } = new([], _ => { });
}

/// <summary>Un module qui veut savoir qu'une de ses actions a abouti (ex. memoriser la date du dernier DISM).</summary>
public interface IActionObserver
{
    void OnActionCompleted(ActionRequest request, ActionResult result);
}
