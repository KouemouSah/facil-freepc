namespace FreeRes.Core.Metrics;

/// <summary>Consommation agregee d'un programme (tous ses processus).</summary>
public sealed record ProcessUsage(string Name, int Count, double WorkingSetGB);

/// <summary>
/// Etat instantane de la machine. Le « commit » (RAM + fichier d'echange engages) est l'indicateur cle :
/// c'est son epuisement, et non la RAM « utilisee », qui fige Windows (diagnostic du 2026-11-08).
/// </summary>
public sealed record SystemSnapshot(
    DateTimeOffset Timestamp,
    double RamTotalGB,
    double RamAvailGB,
    int MemoryLoadPct,
    double CommitLimitGB,
    double CommitUsedGB,
    double CommitPct,
    string SystemDrive,
    double SystemDriveTotalGB,
    double SystemDriveFreeGB,
    double IdleMinutes,
    bool RebootPending,
    IReadOnlyList<ProcessUsage> TopProcesses);

/// <summary>Source des mesures (remplacable par un faux dans les tests).</summary>
public interface ISystemProbe
{
    SystemSnapshot Capture();
}
