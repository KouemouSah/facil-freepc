using FreeRes.Core;
using FreeRes.Core.Engine;
using FreeRes.Core.Storage;
using FreeRes.Host;
using FreeRes.Modules;
using Microsoft.Data.Sqlite;

namespace FreeRes.App;

internal static class Program
{
    /// <summary>
    /// Arguments :
    ///   --background : demarre dans la zone de notification sans ouvrir la fenetre (ouverture de session) ;
    ///   --quit       : demande a l'instance en cours de se fermer (desinstallation).
    /// </summary>
    [STAThread]
    private static int Main(string[] args)
    {
        if (ElevatedMode.TryParse(args, out var encodedAction, out var resultPath))
        {
            // Relance UAC pour UNE action administrateur : jamais d'interface ni d'instance unique.
            return ElevatedMode.Run(encodedAction, resultPath);
        }

        if (args.Contains("--quit", StringComparer.OrdinalIgnoreCase))
        {
            // Utilise par l'installeur avant remplacement/suppression des fichiers : on ATTEND la fin reelle.
            return SingleInstance.RequestQuitAndWait(InstanceNames.Default, TimeSpan.FromSeconds(15)) ? 0 : 1;
        }

        using var instance = SingleInstance.Acquire(InstanceNames.Default);
        if (!instance.IsOwner)
        {
            SingleInstance.Signal(InstanceNames.Default.ShowEvent);
            return 0;
        }

        ApplicationConfiguration.Initialize();
        var paths = AppPaths.Default();
        paths.EnsureCreated();

        FreeResRuntime runtime;
        try
        {
            runtime = FreeResRuntime.Create(paths, modules: ModulePack.Create, elevatedRunner: new UacElevatedRunner(paths));
        }
        catch (DatabaseVersionException ex)
        {
            ShowFatal(ex.Message);
            return 2;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException or InvalidOperationException)
        {
            ShowFatal($"Démarrage impossible : {ex.Message}\n\nDonnées : {paths.DataDirectory}");
            return 3;
        }

        using (runtime)
        {
            var options = new LocalWebHostOptions { Token = SessionToken.Create(), Icons = new ShellIconExtractor() };
            var host = LocalWebHost.Create(options, runtime);
            host.StartAsync().GetAwaiter().GetResult();
            try
            {
                using var context = new TrayContext(host, paths, runtime.Engine, openWindow: !args.Contains("--background", StringComparer.OrdinalIgnoreCase));
                Application.Run(context);
            }
            finally
            {
                host.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }

        return 0;
    }

    private static void ShowFatal(string message) =>
        MessageBox.Show(message, AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
}
