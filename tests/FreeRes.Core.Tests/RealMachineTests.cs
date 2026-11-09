using System.Diagnostics;
using FreeRes.Core.Machine;
using FreeRes.Core.Metrics;

namespace FreeRes.Core.Tests;

/// <summary>
/// Parite Pester : « Mesures sans WMI » (valeurs coherentes en moins de 10 s) et « n'appelle ni WMI ni CIM ».
/// Executes sur la vraie machine.
/// </summary>
public sealed class RealMachineTests
{
    [Fact]
    public void Mesures_reelles_coherentes_en_moins_de_10_s()
    {
        var sw = Stopwatch.StartNew();
        var s = new WindowsSystemProbe().Capture();
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"capture en {sw.Elapsed}");
        Assert.True(s.RamTotalGB > 1);
        Assert.InRange(s.RamAvailGB, 0.01, s.RamTotalGB);
        Assert.InRange(s.CommitPct, 0, 100);
        Assert.True(s.CommitLimitGB >= s.RamTotalGB, "la limite de commit inclut la RAM");
        Assert.True(s.SystemDriveFreeGB > 0 && s.SystemDriveFreeGB <= s.SystemDriveTotalGB);
        Assert.NotEmpty(s.TopProcesses);
        Assert.True(s.TopProcesses.Zip(s.TopProcesses.Skip(1)).All(p => p.First.WorkingSetGB >= p.Second.WorkingSetGB), "tri decroissant");
        Assert.True(s.IdleMinutes >= 0);
    }

    [Fact]
    public void Profil_machine_lu_sans_WMI()
    {
        var p = MachineProfileReader.Read(new WindowsSystemProbe());
        Assert.Equal(Environment.MachineName, p.MachineName);
        Assert.StartsWith("Windows", p.OsName, StringComparison.Ordinal);
        Assert.True(p.OsBuild >= 17763);
        Assert.Equal(Environment.ProcessorCount, p.LogicalProcessors);
    }

    [Fact]
    public async Task Lanceur_transmet_l_entree_standard_sans_fichier_intermediaire()
    {
        // findstr recopie les lignes recues sur son entree standard : preuve que le texte passe par le flux.
        var r = await new Platform.ProcessRunner().RunAsync(
            Path.Combine(Environment.SystemDirectory, "findstr.exe"), ["."], TimeSpan.FromSeconds(20), default, standardInput: "commande-1\r\ncommande-2\r\n");
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("commande-1", r.StdOut, StringComparison.Ordinal);
        Assert.Contains("commande-2", r.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Lanceur_arguments_separes_sans_interpretation_par_un_shell()
    {
        // Un parametre contenant « & » n'est jamais interprete : findstr le recherche litteralement.
        var r = await new Platform.ProcessRunner().RunAsync(
            Path.Combine(Environment.SystemDirectory, "findstr.exe"), ["/L", "a&b"], TimeSpan.FromSeconds(20), default, standardInput: "x a&b y\r\nautre\r\n");
        Assert.Equal("x a&b y", r.StdOut.Trim());
    }

    [Fact]
    public void Aucune_dependance_a_WMI()
    {
        var refs = typeof(AppInfo).Assembly.GetReferencedAssemblies().Select(a => a.Name);
        Assert.DoesNotContain("System.Management", refs);
    }
}
