using FreeRes.Core.Machine;

namespace FreeRes.Core.Policy;

/// <summary>Seuils d'alerte (« Warn ») et critiques (« Crit ») appliques par le moteur.</summary>
public sealed record Thresholds(
    double RamAvailWarnGB,
    double RamAvailCritGB,
    double CommitWarnPct,
    double CommitCritPct,
    double DiskFreeWarnGB,
    double DiskFreeCritGB);

/// <summary>Seuils explicitement imposes par l'utilisateur (settings.json) ; null = valeur calculee.</summary>
public sealed record ThresholdOverrides(
    double? RamAvailWarnGB = null,
    double? RamAvailCritGB = null,
    double? CommitWarnPct = null,
    double? CommitCritPct = null,
    double? DiskFreeWarnGB = null,
    double? DiskFreeCritGB = null);

/// <summary>
/// Seuils RELATIFS a la machine. Proportions calibrees sur la machine de reference (15,7 GB de RAM,
/// disque de 235 GB) pour retrouver exactement les seuils valides de la version PowerShell
/// (RAM 3,0 / 1,5 GB ; disque 30 / 12 GB), bornees pour rester raisonnables sur 4 GB comme sur 128 GB.
/// </summary>
public static class ThresholdCalculator
{
    public const double CommitWarnPct = 85;
    public const double CommitCritPct = 95;

    public static Thresholds FromProfile(MachineProfile profile, ThresholdOverrides? overrides = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var o = overrides ?? new ThresholdOverrides();
        var ram = profile.RamTotalGB;
        var disk = profile.SystemDriveTotalGB;

        return new Thresholds(
            o.RamAvailWarnGB ?? Math.Round(Math.Clamp(ram * 0.19, 1.5, 6.0), 1),
            o.RamAvailCritGB ?? Math.Round(Math.Clamp(ram * 0.095, 0.75, 3.0), 1),
            o.CommitWarnPct ?? CommitWarnPct,
            o.CommitCritPct ?? CommitCritPct,
            o.DiskFreeWarnGB ?? Math.Round(Math.Clamp(disk * 0.128, 10, 40)),
            o.DiskFreeCritGB ?? Math.Round(Math.Clamp(disk * 0.051, 5, 15)));
    }

    /// <summary>Incoherences (un seuil critique doit etre plus severe que le seuil d'alerte, etc.).</summary>
    public static IReadOnlyList<string> Validate(Thresholds t)
    {
        ArgumentNullException.ThrowIfNull(t);
        var errors = new List<string>();
        if (t.RamAvailCritGB <= 0 || t.RamAvailWarnGB <= 0)
        {
            errors.Add("Les seuils de RAM disponible doivent etre positifs.");
        }

        if (t.RamAvailCritGB >= t.RamAvailWarnGB)
        {
            errors.Add("RAM : le seuil critique doit etre inferieur au seuil d'alerte.");
        }

        if (t.CommitWarnPct is <= 0 or > 100 || t.CommitCritPct is <= 0 or > 100)
        {
            errors.Add("Commit : les seuils doivent etre compris entre 0 et 100 %.");
        }

        if (t.CommitCritPct <= t.CommitWarnPct)
        {
            errors.Add("Commit : le seuil critique doit etre superieur au seuil d'alerte.");
        }

        if (t.DiskFreeCritGB <= 0 || t.DiskFreeWarnGB <= 0)
        {
            errors.Add("Les seuils d'espace disque doivent etre positifs.");
        }

        if (t.DiskFreeCritGB >= t.DiskFreeWarnGB)
        {
            errors.Add("Disque : le seuil critique doit etre inferieur au seuil d'alerte.");
        }

        return errors;
    }
}
