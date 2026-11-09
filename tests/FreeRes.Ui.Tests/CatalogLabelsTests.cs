using FreeRes.Core;
using FreeRes.Core.Actions;
using FreeRes.Core.Engine;
using FreeRes.Core.Metrics;
using FreeRes.Core.Platform;
using FreeRes.Host;
using FreeRes.Modules;

namespace FreeRes.Ui.Tests;

/// <summary>Garde-fou : chaque action du catalogue REEL de l'application a un intitule pour l'utilisateur.</summary>
public sealed class CatalogLabelsTests
{
    [Fact]
    public void Toutes_les_actions_du_catalogue_reel_ont_un_intitule()
    {
        var dir = Path.Combine(Path.GetTempPath(), "freeres-labels-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppPaths(dir);
            var services = new ModuleServices(paths, new Core.Logging.EventLog(Path.Combine(dir, "logs"), TimeProvider.System), TimeProvider.System, new StaticProbe(), new ProcessRunner(), null);
            var catalog = new ActionCatalog();
            ModulePack.Create(services).RegisterActions(catalog);

            Assert.True(catalog.Types.Count >= 20, $"catalogue inattendu : {catalog.Types.Count} actions");
            var missing = catalog.Types.Where(t => !ActionLabels.IsKnown(t)).ToList();
            Assert.True(missing.Count == 0, "actions sans intitule : " + string.Join(", ", missing));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
