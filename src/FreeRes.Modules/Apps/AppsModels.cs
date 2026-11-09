using System.ServiceProcess;

namespace FreeRes.Modules.Apps;

/// <summary>Portee d'une installation ou d'un element de demarrage : toute la machine (HKLM) ou l'utilisateur (HKCU).</summary>
public enum AppScope
{
    Machine,
    User,
}

/// <summary>Origine d'un element de demarrage.</summary>
public enum StartupKind
{
    /// <summary>Valeur d'une cle Run (HKCU, HKLM ou HKLM\WOW6432Node).</summary>
    Run,

    /// <summary>Fichier d'un dossier Demarrage (utilisateur ou commun).</summary>
    StartupFolder,

    /// <summary>Service tiers en demarrage automatique.</summary>
    Service,

    /// <summary>Tache planifiee declenchee a l'ouverture de session (lecture seule).</summary>
    ScheduledTask,
}

/// <summary>Origine d'une application de l'inventaire.</summary>
public enum AppSource
{
    /// <summary>Cle Uninstall du registre.</summary>
    Registry,

    /// <summary>Application deduite d'un element de demarrage dont l'executable n'appartient a aucune application installee.</summary>
    StartupItem,
}

/// <summary>
/// Application de l'inventaire. <see cref="Folder"/> : dossier servant au rattachement des processus (null si
/// inconnu ou trop large, ex. « C:\Program Files ») ; <see cref="Protection"/> : raison du refus inconditionnel, ou null.
/// </summary>
public sealed record InstalledApp(
    string Id,
    string Name,
    string? Publisher,
    string? Version,
    string? InstallLocation,
    long? EstimatedSizeKB,
    DateOnly? InstallDate,
    AppScope Scope,
    AppSource Source,
    string? Folder,
    string? Protection);

/// <summary>
/// Element lance au demarrage. <see cref="Id"/> : empreinte stable de son emplacement et de son nom (format des
/// identifiants de proposition). <see cref="ApprovedSubKey"/> : sous-cle de « Explorer\StartupApproved » qui porte son
/// etat (Run, Run32, StartupFolder), null pour les services et les taches. Pour un dossier Demarrage, <see cref="Name"/>
/// est le nom du fichier (c'est aussi le nom de la valeur StartupApproved).
/// </summary>
public sealed record StartupItem(
    string Id,
    StartupKind Kind,
    AppScope Scope,
    string Name,
    string Location,
    string? Command,
    string? ExecutablePath,
    bool Enabled,
    string? ApprovedSubKey,
    string? AppId,
    string? Protection,
    string? ServiceName = null,
    ServiceControllerStatus? ServiceStatus = null);

/// <summary>Consommation d'une application (processus de la session de l'utilisateur).</summary>
public sealed record AppUsage(string AppId, string Name, int ProcessCount, int WindowedProcessCount, long WorkingSetBytes)
{
    /// <summary>« Arriere-plan » : au moins un processus et AUCUN avec une fenetre principale.</summary>
    public bool Background => ProcessCount > 0 && WindowedProcessCount == 0;

    public double WorkingSetGB => Math.Round(WorkingSetBytes / 1073741824d, 2);
}

/// <summary>Consommation d'un service (processus de la session 0 dont l'image est celle du service).</summary>
public sealed record ServiceUsage(string ServiceName, int ProcessCount, long WorkingSetBytes);

/// <summary>Dernier inventaire du module Applications (lu par l'interface).</summary>
public sealed record AppsInventory(
    DateTimeOffset At,
    IReadOnlyList<InstalledApp> Apps,
    IReadOnlyList<StartupItem> StartupItems,
    IReadOnlyList<AppUsage> Usage,
    IReadOnlyList<ServiceUsage> Services,
    IReadOnlyList<string> Warnings);

/// <summary>Processus observe (image lue par QueryFullProcessImageName, null si inaccessible).</summary>
public sealed record ProcessSample(int Pid, string Name, string? ImagePath, long WorkingSetBytes, int SessionId, bool HasWindow);

/// <summary>Fichier d'un dossier Demarrage ; <see cref="Target"/> null si la cible du raccourci n'a pas pu etre resolue.</summary>
public sealed record StartupFolderEntry(AppScope Scope, string Folder, string FileName, string? Target, string? Arguments);

/// <summary>Tache planifiee declenchee a l'ouverture de session.</summary>
public sealed record ScheduledTaskEntry(string Path, string Name, bool Enabled, string? Executable, string? Arguments);

/// <summary>Reglages du module.</summary>
public sealed record AppsOptions(
    double BackgroundProposeMB = 200,
    double ServiceProposeMB = 200,
    double InventoryRefreshMinutes = 30,
    double ScheduledTasksTimeoutSeconds = 20)
{
    public static AppsOptions Default { get; } = new();

    /// <summary>Incoherences de configuration (liste vide si les reglages sont acceptables).</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (BackgroundProposeMB is <= 0 or double.NaN || ServiceProposeMB is <= 0 or double.NaN)
        {
            errors.Add("Les seuils de proposition (MB) doivent etre positifs.");
        }

        if (InventoryRefreshMinutes is < 1 or > 1440 or double.NaN)
        {
            errors.Add("Le rafraichissement de l'inventaire doit etre compris entre 1 et 1440 min.");
        }

        if (ScheduledTasksTimeoutSeconds is < 1 or > 300 or double.NaN)
        {
            errors.Add("Le delai de lecture des taches planifiees doit etre compris entre 1 et 300 s.");
        }

        return errors;
    }
}
