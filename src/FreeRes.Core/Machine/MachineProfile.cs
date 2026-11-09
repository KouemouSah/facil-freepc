using FreeRes.Core.Metrics;
using Microsoft.Win32;

namespace FreeRes.Core.Machine;

/// <summary>
/// Description de la machine, lue au demarrage (registre + API, sans WMI). Sert a calculer des seuils
/// adaptes a CHAQUE PC : un produit distribue ne peut pas reprendre les valeurs codees pour un seul portable.
/// L'inventaire des logiciels installes fait partie du module Applications (phase 2).
/// </summary>
public sealed record MachineProfile(
    string MachineName,
    string Manufacturer,
    string Model,
    string OsName,
    string OsDisplayVersion,
    int OsBuild,
    double RamTotalGB,
    int LogicalProcessors,
    string SystemDrive,
    double SystemDriveTotalGB);

public static class MachineProfileReader
{
    public static MachineProfile Read(ISystemProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        var snapshot = probe.Capture();

        using var bios = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
        using var nt = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        var build = int.TryParse(nt?.GetValue("CurrentBuildNumber") as string, out var b) ? b : Environment.OSVersion.Version.Build;

        return new MachineProfile(
            Environment.MachineName,
            Clean(bios?.GetValue("SystemManufacturer") as string),
            Clean(bios?.GetValue("SystemProductName") as string),
            OsName(nt?.GetValue("ProductName") as string, build),
            Clean(nt?.GetValue("DisplayVersion") as string),
            build,
            snapshot.RamTotalGB,
            Environment.ProcessorCount,
            snapshot.SystemDrive,
            snapshot.SystemDriveTotalGB);
    }

    /// <summary>
    /// Le registre de Windows 11 indique encore « Windows 10 » dans ProductName : on corrige a partir du
    /// numero de build (Windows 11 = build 22000 et plus).
    /// </summary>
    public static string OsName(string? productName, int build)
    {
        var name = Clean(productName);
        if (name.Length == 0)
        {
            name = "Windows";
        }

        return build >= 22000 && name.Contains("Windows 10", StringComparison.OrdinalIgnoreCase)
            ? name.Replace("Windows 10", "Windows 11", StringComparison.OrdinalIgnoreCase)
            : name;
    }

    private static string Clean(string? value) => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
}
