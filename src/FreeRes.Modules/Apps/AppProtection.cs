using System.Text.RegularExpressions;

namespace FreeRes.Modules.Apps;

/// <summary>
/// Refus INCONDITIONNEL : aucune proposition ni action sur Facil FreePC lui-meme, les logiciels de securite, les
/// pilotes d'entree/audio/graphiques et les composants Windows. Listes GENERIQUES (editeurs et produits du marche),
/// confrontees au nom, a l'editeur et aux chemins ; le dossier Windows et celui de Facil FreePC sont lus sur la
/// machine (<see cref="AppsEnvironment"/>). Les mots courts (ESET, AVG, Elan...) sont bornes pour eviter
/// les faux positifs (« RESET », « Elance »).
/// </summary>
internal sealed partial class AppProtection(AppsEnvironment environment)
{
    public const string SelfReason = "Facil FreePC lui-meme";
    public const string WindowsReason = "composant Windows";
    public const string SecurityPrefix = "logiciel de securite";
    public const string DriverPrefix = "pilote d'entree, audio ou graphique";

    // Bornes : ni lettre ni chiffre de part et d'autre.
    private const string B = "(?<![A-Za-z0-9])";
    private const string E = "(?![A-Za-z0-9])";

    [GeneratedRegex(B + "(Facil ?FreePC|FreeRes)" + E, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SelfPattern();

    [GeneratedRegex(
        B + "(Windows Defender|Microsoft Defender|Windows Security|SecurityHealth[A-Za-z]*|MsMpEng|WinDefend|MsSense|"
        + "Kaspersky|ESET|Bitdefender|Norton[A-Za-z]*|Gen Digital|Symantec|McAfee|Avast|AVG|Sophos|Trend ?Micro|"
        + "Malwarebytes|CrowdStrike|SentinelOne|Sentinel ?Labs|SentinelAgent|Panda Security|Panda Dome|F-Secure|WithSecure|"
        + "G ?DATA|Webroot|Avira|Trellix|Cylance|Carbon ?Black|Cortex XDR|Emsisoft|Comodo|ZoneAlarm|FortiClient|Fortinet|"
        + "Quick Heal|K7 ?(Computing|Security|TotalSecurity))" + E,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecurityPattern();

    [GeneratedRegex(
        B + "(Realtek|Synaptics|Elan|ELAN Microelectronics|ETDCtrl|NVIDIA|GeForce|Advanced Micro Devices|Radeon|"
        + @"AMD\s+(Software|Adrenalin|Display|Graphics|Chipset)|Intel[^\\\r\n]{0,20}?(Graphics|Arc|Iris|Display Audio|Smart Sound)|"
        + "igfx[A-Za-z]*|Waves ?(Audio|MaxxAudio)|MaxxAudio|Dolby|Conexant|Nahimic|Cirrus Logic|Alps ?Alpine)" + E,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DriverPattern();

    public AppsEnvironment Environment { get; } = environment;

    /// <summary>Raison du refus, ou null si l'element peut faire l'objet d'une proposition.</summary>
    public string? Check(string? name, string? publisher, params string?[] paths)
    {
        var texts = new[] { name, publisher }.Concat(paths).Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t!).ToList();

        if (texts.Any(t => SelfPattern().IsMatch(t)) || paths.Any(p => Environment.SelfPaths.Any(s => AppPathRules.IsUnder(p, s))))
        {
            return SelfReason;
        }

        foreach (var t in texts)
        {
            if (SecurityPattern().Match(t) is { Success: true } m)
            {
                return $"{SecurityPrefix} ({m.Value})";
            }
        }

        foreach (var t in texts)
        {
            if (DriverPattern().Match(t) is { Success: true } m)
            {
                return $"{DriverPrefix} ({m.Value})";
            }
        }

        return paths.Any(p => IsWindowsPath(p)) ? WindowsReason : null;
    }

    public bool IsWindowsPath(string? path) => AppPathRules.IsUnder(path, Environment.WindowsDirectory);

    /// <summary>Dossier trop large pour representer une seule application (racine, Program Files, profil...).</summary>
    public bool IsBroadFolder(string? folder)
    {
        var f = AppPathRules.Normalize(folder);
        if (f is null)
        {
            return true;
        }

        // Tout dossier SOUS Windows (pas seulement C:\Windows lui-meme) : constate sur machine reelle, un pilote
        // declare « InstallLocation = C:\WINDOWS\system32 » et se voyait attribuer 67 processus Windows.
        return string.Equals(Path.GetPathRoot(f)?.TrimEnd('\\'), f.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
            || Environment.BroadFolders.Any(b => AppPathRules.SamePath(b, f))
            || IsWindowsPath(f);
    }
}
