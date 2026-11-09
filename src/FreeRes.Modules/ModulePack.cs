using FreeRes.Core.Actions;
using FreeRes.Core.Engine;
using FreeRes.Modules.Disk;
using FreeRes.Modules.Windows;

namespace FreeRes.Modules;

/// <summary>
/// Assemblage de tous les modules et de leurs actions. Utilise par l'application ET par sa relance elevee
/// (qui n'a besoin que du catalogue d'actions pour executer UNE action administrateur).
/// </summary>
public static class ModulePack
{
    public static ModuleSetup Create(ModuleServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var disk = new DiskModule(services);
        var windows = new WindowsMaintenanceModule(services, disk.Cleaner);

        void Register(ActionCatalog catalog)
        {
            disk.RegisterActions(catalog);
            windows.RegisterActions(catalog);
        }

        return new ModuleSetup([disk, windows], Register);
    }
}
