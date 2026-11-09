using System.Globalization;
using System.Text.RegularExpressions;

namespace FreeRes.Modules.Docker;

/// <summary>
/// Occupation du disque de donnees Docker, mesuree Docker demarre et memorisee dans l'etat du module.
/// <see cref="InsideUsedGB"/> : vue Linux (df) ; les autres valeurs viennent de « docker system df » (null si indisponible).
/// </summary>
public sealed record DockerDiskUsage(
    DateTimeOffset MeasuredAt,
    double InsideUsedGB,
    double? VhdxGB,
    double? ImagesGB = null,
    double? ImagesReclaimableGB = null,
    double? ContainersGB = null,
    double? VolumesGB = null,
    double? VolumesUnusedGB = null,
    double? BuildCacheGB = null,
    double? BuildCacheReclaimableGB = null);

/// <summary>Repartition rendue par « docker system df » (en GB, tels qu'affiches par Docker).</summary>
public sealed record DockerSystemDf(
    double? ImagesGB,
    double? ImagesReclaimableGB,
    double? ContainersGB,
    double? VolumesGB,
    double? VolumesUnusedGB,
    double? BuildCacheGB,
    double? BuildCacheReclaimableGB);

/// <summary>Lecture des sorties de df, docker system df et wsl --list (portage exact de la version PowerShell).</summary>
public static partial class DockerParsers
{
    private const double BytesPerGB = 1024d * 1024 * 1024;

    [GeneratedRegex(@"([\d\.,]+)\s*([kKMGT]?B)")]
    private static partial Regex SizePattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>Sortie de « df -B1 &lt;montage&gt; » -> octets utilises (colonne 3 de la derniere ligne), null si illisible.</summary>
    public static long? ParseDfUsedBytes(string? text)
    {
        var lines = (text ?? string.Empty).Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (lines.Count < 2)
        {
            return null;
        }

        var fields = Whitespace().Split(lines[^1]);
        if (fields.Length < 4)
        {
            return null;
        }

        return long.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var used) ? used : null;
    }

    /// <summary>
    /// « 1.234GB » / « 512MB » / « 0B » / « 1.2GB (35%) » -> octets. Multiples de 1024 comme la version PowerShell :
    /// la valeur convertie en GB reste ainsi egale au nombre affiche par Docker, sur lequel les seuils ont ete regles.
    /// </summary>
    public static double ParseSizeBytes(string? text)
    {
        var m = SizePattern().Match(text ?? string.Empty);
        if (!m.Success || !double.TryParse(m.Groups[1].Value.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var n))
        {
            return 0;
        }

        return m.Groups[2].Value.ToUpperInvariant() switch
        {
            "KB" => n * 1024,
            "MB" => n * 1024 * 1024,
            "GB" => n * BytesPerGB,
            "TB" => n * BytesPerGB * 1024,
            _ => n,
        };
    }

    /// <summary>Sortie de « docker system df --format "{{.Type}}|{{.Size}}|{{.Reclaimable}}" ».</summary>
    public static DockerSystemDf ParseSystemDf(string? text)
    {
        double? images = null, imagesRec = null, containers = null, volumes = null, volumesUnused = null, build = null, buildRec = null;
        foreach (var line in (text ?? string.Empty).Split('\n'))
        {
            var parts = line.Trim().Split('|');
            if (parts.Length != 3)
            {
                continue;
            }

            var size = ToGB(ParseSizeBytes(parts[1]));
            var reclaimable = ToGB(ParseSizeBytes(parts[2]));
            switch (parts[0].Trim())
            {
                case "Images":
                    (images, imagesRec) = (size, reclaimable);
                    break;
                case "Containers":
                    containers = size;
                    break;
                case "Local Volumes":
                    (volumes, volumesUnused) = (size, reclaimable);
                    break;
                case "Build Cache":
                    (build, buildRec) = (size, reclaimable);
                    break;
                default:
                    break;
            }
        }

        return new DockerSystemDf(images, imagesRec, containers, volumes, volumesUnused, build, buildRec);
    }

    /// <summary>Sortie de « wsl --list --running --quiet » -> noms des distributions (lignes sans espace).</summary>
    public static IReadOnlyList<string> ParseWslRunning(string? text) =>
        (text ?? string.Empty).Replace("\0", string.Empty, StringComparison.Ordinal)
            .Split('\n')
            .Select(l => l.Trim().TrimStart('﻿'))
            .Where(l => l.Length > 0 && !l.Any(char.IsWhiteSpace))
            .ToList();

    /// <summary>Espace recuperable par compactage = VHDX - donnees reelles - plancher ext4. Jamais negatif.</summary>
    public static double VhdxSlackGB(double vhdxGB, double insideUsedGB, double overheadGB) =>
        Math.Round(Math.Max(0d, vhdxGB - insideUsedGB - overheadGB), 2);

    public static double ToGB(double bytes) => Math.Round(bytes / BytesPerGB, 2);
}
