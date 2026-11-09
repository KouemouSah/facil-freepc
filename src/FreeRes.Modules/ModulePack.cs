using FreeRes.Core.Actions;
using FreeRes.Core.Engine;
using FreeRes.Modules.Ai;
using FreeRes.Modules.Apps;
using FreeRes.Modules.Browsers;
using FreeRes.Modules.Disk;
using FreeRes.Modules.Docker;
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
        var docker = new DockerModule(services);
        var apps = new AppsModule(services);
        var browsers = new BrowsersModule(services);
        var ai = new AiModule(services);

        void Register(ActionCatalog catalog)
        {
            disk.RegisterActions(catalog);
            windows.RegisterActions(catalog);
            docker.RegisterActions(catalog);
            apps.RegisterActions(catalog);
            browsers.RegisterActions(catalog);
            ai.RegisterActions(catalog);
        }

        return new ModuleSetup([disk, windows, docker, apps, browsers, ai], Register);
    }
}
