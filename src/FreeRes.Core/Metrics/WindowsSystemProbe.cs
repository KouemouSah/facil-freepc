using System.Diagnostics;
using Microsoft.Win32;

namespace FreeRes.Core.Metrics;

/// <summary>Mesures reelles : API Windows, registre et liste des processus. Aucun WMI.</summary>
public sealed class WindowsSystemProbe(TimeProvider time) : ISystemProbe
{
    private const double GB = 1024d * 1024 * 1024;

    public WindowsSystemProbe()
        : this(TimeProvider.System)
    {
    }

    public SystemSnapshot Capture()
    {
        var mem = NativeMethods.ReadMemoryStatus();
        var commitLimit = (double)mem.TotalPageFile;
        var commitUsed = commitLimit - mem.AvailPageFile;
        var drive = SystemDriveInfo();

        return new SystemSnapshot(
            time.GetLocalNow(),
            Round(mem.TotalPhys / GB),
            Round(mem.AvailPhys / GB),
            (int)mem.MemoryLoad,
            Round(commitLimit / GB),
            Round(commitUsed / GB),
            Math.Round(100 * commitUsed / Math.Max(commitLimit, 1), 1),
            drive.Name.TrimEnd('\\'),
            Round(drive.TotalSize / GB),
            Round(drive.AvailableFreeSpace / GB),
            NativeMethods.IdleMinutes(),
            IsRebootPending(),
            TopProcesses(10));
    }

    /// <summary>Espace libre du disque systeme (GB), sans capture complete : sert a mesurer le gain d'une action.</summary>
    public static double SystemDriveFreeGB() => Round(SystemDriveInfo().AvailableFreeSpace / GB);

    internal static DriveInfo SystemDriveInfo()
    {
        var root = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
        return new DriveInfo(root);
    }

    /// <summary>Redemarrage exige par la maintenance Windows (bloque Windows Update, DISM, et retient de la RAM).</summary>
    internal static bool IsRebootPending()
    {
        using var cbs = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
        using var wu = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
        return cbs is not null || wu is not null;
    }

    /// <summary>Programmes les plus gourmands (working set cumule par nom de processus).</summary>
    internal static IReadOnlyList<ProcessUsage> TopProcesses(int count)
    {
        var totals = new Dictionary<string, (int Count, long Bytes)>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    var key = p.ProcessName;
                    totals.TryGetValue(key, out var t);
                    totals[key] = (t.Count + 1, t.Bytes + p.WorkingSet64);
                }
                catch (InvalidOperationException)
                {
                    // processus termine entre l'enumeration et la lecture : ignore
                }
            }
        }

        return totals
            .OrderByDescending(kv => kv.Value.Bytes)
            .Take(count)
            .Select(kv => new ProcessUsage(kv.Key, kv.Value.Count, Round(kv.Value.Bytes / GB)))
            .ToList();
    }

    private static double Round(double value) => Math.Round(value, 2);
}
