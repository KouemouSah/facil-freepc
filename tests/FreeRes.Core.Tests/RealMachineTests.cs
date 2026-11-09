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
    public void Aucune_dependance_a_WMI()
    {
        var refs = typeof(AppInfo).Assembly.GetReferencedAssemblies().Select(a => a.Name);
        Assert.DoesNotContain("System.Management", refs);
    }
}
