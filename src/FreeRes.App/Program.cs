using FreeRes.Core;
using FreeRes.Host;

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

        var options = new LocalWebHostOptions { Token = SessionToken.Create() };
        var host = LocalWebHost.Create(options);
        host.StartAsync().GetAwaiter().GetResult();
        try
        {
            using var context = new TrayContext(host, paths, openWindow: !args.Contains("--background", StringComparer.OrdinalIgnoreCase));
            Application.Run(context);
        }
        finally
        {
            host.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        return 0;
    }
}
