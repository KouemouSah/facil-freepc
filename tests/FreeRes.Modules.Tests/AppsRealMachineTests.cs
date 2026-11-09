using System.Diagnostics;
using System.Text.RegularExpressions;
using FreeRes.Core.Actions;
using FreeRes.Modules.Apps;
using Xunit.Abstractions;

namespace FreeRes.Modules.Tests;

/// <summary>
/// Inventaire sur la VRAIE machine, en LECTURE SEULE : aucune ecriture au registre, aucun processus arrete,
/// aucune action executee.
/// </summary>
public sealed partial class AppsRealMachineTests(ITestOutputHelper output)
{
    [GeneratedRegex("^[a-z0-9-]{3,60}$")]
    private static partial Regex IdPattern();

    [Fact]
    public void Inventaire_reel_sans_WMI_coherent()
    {
        var sw = Stopwatch.StartNew();
        var stat = new AppsScanner(AppsPlatform.Windows(), new AppProtection(AppsEnvironment.Current())).Scan(true, TimeSpan.FromSeconds(20));
        var processes = new WindowsProcessControl();
        var snapshot = processes.Snapshot();
        var (usage, services) = ProcessAttribution.Measure(stat.Apps, stat.Items, snapshot, processes.CurrentSessionId);
        sw.Stop();

        output.WriteLine($"{stat.Apps.Count} applications, {stat.Items.Count} elements de demarrage, {snapshot.Count} processus, {usage.Count} applications actives, {services.Count} services mesures en {sw.Elapsed}");
        foreach (var i in stat.Items)
        {
            output.WriteLine($"  {i.Kind,-14} {(i.Enabled ? "on " : "off")} {i.Scope,-7} {i.Name} -> {i.ExecutablePath} [{i.Protection ?? "-"}]");
        }

        foreach (var w in stat.Warnings)
        {
            output.WriteLine("  avertissement : " + w);
        }

        Assert.NotEmpty(stat.Apps);
        Assert.All(stat.Apps.Select(a => a.Id).Concat(stat.Items.Select(i => i.Id)), id => Assert.Matches(IdPattern(), id));
        Assert.Equal(stat.Items.Count, stat.Items.Select(i => i.Id).Distinct().Count());
        Assert.Contains(snapshot, p => p.Pid == Environment.ProcessId && p.ImagePath is not null);   // QueryFullProcessImageName fonctionne
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(60), $"inventaire en {sw.Elapsed}");
    }

    [Fact]
    public void Module_enregistre_ses_six_actions_et_aucune_dependance_a_WMI()
    {
        using var dir = new TempDir();
        var catalog = new ActionCatalog();
        ModulePack.Create(Fixtures.Services(dir, TimeProvider.System)).RegisterActions(catalog);

        Assert.Superset(
            new HashSet<string> { AppsModule.DisableStartupItem, AppsModule.EnableStartupItem, AppsModule.DisableMachineStartupItem, AppsModule.EnableMachineStartupItem, AppsModule.StopAppProcesses, AppsModule.SetServiceManual },
            catalog.Types.ToHashSet());
        Assert.DoesNotContain("System.Management", typeof(AppsModule).Assembly.GetReferencedAssemblies().Select(a => a.Name));
    }
}
