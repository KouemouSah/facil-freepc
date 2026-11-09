using System.Text.RegularExpressions;

namespace FreeRes.Modules.Disk;

/// <summary>
/// Regle de nettoyage d'une zone sure. <see cref="IntervalHours"/> : frequence d'execution automatique.
/// Une regle « admin » n'est jamais automatique : elle est regroupee dans une proposition (UAC a l'approbation).
/// </summary>
public sealed record CleanRule(string Id, string Path, int OlderThanDays, string? DirPattern, bool RequiresAdmin, int IntervalHours, string Why);

/// <summary>Reglages du module disque ; <see cref="Default"/> = zones generiques valables sur tout Windows.</summary>
public sealed record DiskOptions(
    IReadOnlyList<string> AllowedRoots,
    IReadOnlyList<CleanRule> Rules,
    string NpmCachePath,
    double NpmCacheProposeGB = 1.0,
    double RecycleBinProposeGB = 2.0,
    double SystemZonesProposeGB = 0.5)
{
    public static DiskOptions Default { get; } = new(
        [
            "%TEMP%",
            @"%SystemRoot%\Temp",
            @"%LOCALAPPDATA%\CrashDumps",
            @"%ProgramData%\Microsoft\Windows\WER",
            @"%LOCALAPPDATA%\Microsoft\Windows\WER",
            @"%APPDATA%\Code\logs",
            @"%APPDATA%\Code\CachedExtensionVSIXs",
            @"%LOCALAPPDATA%\npm-cache\_cacache",
            @"%LOCALAPPDATA%\npm-cache\_logs",
            @"%SystemRoot%\SoftwareDistribution\Download",
            @"%SystemRoot%\Logs\CBS",
        ],
        [
            // Dossiers « xxxxxxxx.xxx » laisses par les installeurs (Visual Studio Installer : ~140 MB par verification,
            // jamais nettoyes — constate le 2026-11-08). Toutes les heures, au-dela d'un jour.
            new("installer-leak", "%TEMP%", 1, @"^[a-z0-9]{8}\.[a-z0-9]{3}$", false, 1, "Dossiers temporaires d'installeurs jamais nettoyes"),
            new("user-temp", "%TEMP%", 7, null, false, 24, "Fichiers temporaires de l'utilisateur"),
            new("crashdumps", @"%LOCALAPPDATA%\CrashDumps", 7, null, false, 24, "Dumps de plantage d'applications"),
            new("wer-user", @"%LOCALAPPDATA%\Microsoft\Windows\WER", 14, null, false, 24, "Rapports d'erreurs de l'utilisateur"),
            new("vscode-logs", @"%APPDATA%\Code\logs", 7, null, false, 24, "Journaux de VS Code"),
            new("vscode-vsix", @"%APPDATA%\Code\CachedExtensionVSIXs", 14, null, false, 24, "Paquets d'extensions VS Code deja installees"),
            new("npm-logs", @"%LOCALAPPDATA%\npm-cache\_logs", 7, null, false, 24, "Journaux npm"),
            new("windows-temp", @"%SystemRoot%\Temp", 7, null, true, 24, "Fichiers temporaires du systeme"),
            new("wer-system", @"%ProgramData%\Microsoft\Windows\WER", 14, null, true, 24, "Rapports d'erreurs Windows"),
            new("cbs-logs", @"%SystemRoot%\Logs\CBS", 30, null, true, 24, "Journaux de maintenance Windows"),
        ],
        @"%LOCALAPPDATA%\npm-cache\_cacache");

    /// <summary>Incoherences : regle hors liste blanche, identifiant duplique, motif invalide, duree hors bornes.</summary>
    public IReadOnlyList<string> Validate(SafeCleaner cleaner)
    {
        ArgumentNullException.ThrowIfNull(cleaner);
        var errors = new List<string>();
        foreach (var dup in Rules.GroupBy(r => r.Id, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            errors.Add($"Regle en double : {dup.Key}");
        }

        foreach (var r in Rules)
        {
            if (!cleaner.IsSafe(r.Path))
            {
                errors.Add($"Regle {r.Id} : chemin hors liste blanche ({SafeCleaner.Expand(r.Path)})");
            }

            if (r.OlderThanDays is < 1 or > 3650 || r.IntervalHours is < 1 or > 24 * 30)
            {
                errors.Add($"Regle {r.Id} : duree hors bornes");
            }

            if (r.DirPattern is not null)
            {
                try
                {
                    _ = new Regex(r.DirPattern, RegexOptions.None, TimeSpan.FromSeconds(1));
                }
                catch (ArgumentException)
                {
                    errors.Add($"Regle {r.Id} : motif invalide");
                }
            }
        }

        if (!cleaner.IsSafe(NpmCachePath))
        {
            errors.Add("Cache npm hors liste blanche");
        }

        return errors;
    }
}
