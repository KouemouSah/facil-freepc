using System.Text.RegularExpressions;

namespace FreeRes.Modules.Docker;

/// <summary>
/// Reglages du module Docker/WSL (valeurs validees dans la version PowerShell, config.json « docker »).
/// <see cref="WslDistro"/> et <see cref="DataMount"/> sont passes en arguments a wsl.exe : liste blanche stricte.
/// </summary>
public sealed partial record DockerOptions(
    string WslDistro = "docker-desktop",
    string DataMount = "/mnt/docker-desktop-disk",
    double MeasureEveryHours = 6,
    double TrimEveryHours = 24,
    double TrimRetryHours = 1,
    double VhdxMetadataOverheadGB = 2.0,
    double CompactMinGainGB = 1.5,
    double CompactFallbackVhdxGB = 10,
    double BuildCacheProposeGB = 1.0,
    double UnusedImagesProposeGB = 2.0,
    int BuilderPruneUntilHours = 168,
    int UnusedImagesUntilHours = 720,
    double OrphanConfirmMinutes = 10,
    int WslShutdownSettleSeconds = 5)
{
    public static DockerOptions Default { get; } = new();

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex DistroPattern();

    [GeneratedRegex("^/mnt/[A-Za-z0-9._-]+(/[A-Za-z0-9._-]+)*$")]
    private static partial Regex MountPattern();

    /// <summary>Incoherences de configuration (meme controles que Test-FreeResConfig de la version PowerShell).</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (!DistroPattern().IsMatch(WslDistro))
        {
            errors.Add("WslDistro invalide");
        }

        if (!MountPattern().IsMatch(DataMount))
        {
            errors.Add("DataMount invalide (attendu /mnt/...)");
        }

        var positives = new (string Name, double Value)[]
        {
            (nameof(MeasureEveryHours), MeasureEveryHours),
            (nameof(TrimEveryHours), TrimEveryHours),
            (nameof(TrimRetryHours), TrimRetryHours),
            (nameof(CompactMinGainGB), CompactMinGainGB),
            (nameof(CompactFallbackVhdxGB), CompactFallbackVhdxGB),
            (nameof(BuildCacheProposeGB), BuildCacheProposeGB),
            (nameof(UnusedImagesProposeGB), UnusedImagesProposeGB),
            (nameof(BuilderPruneUntilHours), BuilderPruneUntilHours),
            (nameof(UnusedImagesUntilHours), UnusedImagesUntilHours),
            (nameof(OrphanConfirmMinutes), OrphanConfirmMinutes),
        };
        foreach (var (name, value) in positives)
        {
            if (!(value > 0))
            {
                errors.Add($"{name} doit etre > 0");
            }
        }

        if (!(VhdxMetadataOverheadGB >= 0))
        {
            errors.Add("VhdxMetadataOverheadGB doit etre >= 0");
        }

        if (WslShutdownSettleSeconds is < 0 or > 60)
        {
            errors.Add("WslShutdownSettleSeconds doit etre entre 0 et 60");
        }

        return errors;
    }
}
