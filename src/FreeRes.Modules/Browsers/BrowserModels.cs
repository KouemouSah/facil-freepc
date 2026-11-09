namespace FreeRes.Modules.Browsers;

public enum BrowserEngine
{
    Chromium,
    Gecko,
}

/// <summary>
/// Navigateur connu. <see cref="RestartValidated"/> : le redemarrage propre (fermeture sans forcer + restauration
/// de toutes les fenetres) a ete VERIFIE en reel pour ce navigateur ; sinon il est refuse.
/// Verifications du 2026-11-09 (profils jetables) : Chrome et Edge = Restart Manager sur le processus principal +
/// relance --restore-last-session (un seul profil actif) ; Firefox = Ctrl+Maj+Q + restauration de session.
/// Brave, Opera, Vivaldi : non vérifiés.
/// </summary>
public sealed record BrowserDefinition(
    string Id,
    string Name,
    BrowserEngine Engine,
    string ExeName,
    IReadOnlyList<string> ExeCandidates,
    string? UserDataDir,
    bool RestartValidated,
    string? MemorySaverSettingsUrl,
    string? StartupSettingsUrl,
    string? FirefoxDefaultProfileSuffix = null);

/// <summary>Processus d'un navigateur (image lue par handle, parent par instantane Toolhelp).</summary>
public sealed record BrowserProcess(int Pid, int ParentPid, string? ImagePath, long WorkingSetBytes, DateTimeOffset? StartTime);

/// <summary>Etat d'un navigateur installe, tel que l'interface l'affichera.</summary>
public sealed record BrowserStatus(
    string Id,
    string Name,
    string ExePath,
    bool Running,
    int ProcessCount,
    int MainProcessCount,
    long WorkingSetBytes,
    double? UptimeHours,
    bool? MemorySaverEnabled,
    string MemorySaverNote,
    int ActiveProfiles,
    string? ActiveProfile,
    bool RestartPossible,
    string RestartNote,
    bool? ExtensionInstalled)
{
    public double WorkingSetGB => Math.Round(WorkingSetBytes / 1073741824d, 2);
}

/// <summary>
/// Reglages du module. Seuil de redemarrage RELATIF a la RAM (35 % ; ~5,5 GB sur 16 GB, proche des 6 GB valides
/// dans la version PowerShell), jamais sous <see cref="RestartMinGB"/>.
/// </summary>
public sealed record BrowsersOptions(
    double RestartMinGB = 2.0,
    double RestartRamShare = 0.35,
    double RestartMinUptimeHours = 12,
    int QuitTimeoutSeconds = 60)
{
    public static BrowsersOptions Default { get; } = new();

    public double RestartThresholdGB(double ramTotalGB) => Math.Round(Math.Max(RestartMinGB, ramTotalGB * RestartRamShare), 1);
}
