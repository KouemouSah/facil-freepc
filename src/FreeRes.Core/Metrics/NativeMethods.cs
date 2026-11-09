using System.Runtime.InteropServices;

namespace FreeRes.Core.Metrics;

/// <summary>
/// Appels Windows directs. AUCUN WMI/CIM : sur la machine de reference, WMI s'est bloque pendant une
/// maintenance Windows Update (2026-11-08) et aurait fige la surveillance.
/// </summary>
internal static partial class NativeMethods
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;   // = limite de commit (RAM + fichier d'echange)
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetLastInputInfo(ref LastInputInfo info);

    internal static MemoryStatusEx ReadMemoryStatus()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status))
        {
            throw new InvalidOperationException($"GlobalMemoryStatusEx a échoué (erreur {Marshal.GetLastPInvokeError()})");
        }

        return status;
    }

    /// <summary>Minutes depuis la derniere saisie clavier/souris de l'utilisateur.</summary>
    internal static double IdleMinutes()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info))
        {
            return 0;
        }

        // Compteurs 32 bits : la soustraction non signee gere le rebouclage (~49,7 jours).
        var idleMs = unchecked((uint)Environment.TickCount - info.Time);
        return Math.Round(idleMs / 60000.0, 1);
    }
}
