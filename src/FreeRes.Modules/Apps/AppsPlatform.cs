using System.ServiceProcess;
using Microsoft.Win32;

namespace FreeRes.Modules.Apps;

/// <summary>
/// Acces au registre (remplacable par un faux : les tests n'ecrivent JAMAIS dans le vrai registre).
/// Chemins relatifs a la ruche, sans « HKLM\ ». Une cle absente ou inaccessible donne une liste vide / null.
/// </summary>
public interface IRegistryView
{
    IReadOnlyList<string> SubKeyNames(RegistryHive hive, RegistryView view, string path);

    IReadOnlyList<string> ValueNames(RegistryHive hive, RegistryView view, string path);

    /// <summary>Valeur (REG_EXPAND_SZ developpe), ou null.</summary>
    object? GetValue(RegistryHive hive, RegistryView view, string path, string name);

    /// <summary>Ecrit une valeur REG_BINARY (cree la cle si besoin).</summary>
    void SetBinary(RegistryHive hive, RegistryView view, string path, string name, byte[] data);
}

/// <summary>Processus de la machine (remplacable : les tests ne tuent AUCUN vrai processus).</summary>
public interface IProcessControl
{
    int CurrentSessionId { get; }

    int CurrentProcessId { get; }

    IReadOnlyList<ProcessSample> Snapshot();

    /// <summary>
    /// Termine le processus <paramref name="pid"/> SI son image est toujours <paramref name="expectedImagePath"/>
    /// (protection contre la reutilisation d'un PID). Renvoie null en cas de succes, sinon la raison de l'echec.
    /// </summary>
    string? Kill(int pid, string expectedImagePath);
}

/// <summary>Etat des services (lecture seule ; le changement de type de demarrage passe par sc.exe).</summary>
public interface IServiceControl
{
    ServiceControllerStatus? Status(string serviceName);
}

/// <summary>Contenu des dossiers Demarrage (utilisateur et commun).</summary>
public interface IStartupFolderSource
{
    IReadOnlyList<StartupFolderEntry> List();
}

/// <summary>Taches planifiees a l'ouverture de session, hors \Microsoft\ (lecture seule).</summary>
public interface IScheduledTaskSource
{
    IReadOnlyList<ScheduledTaskEntry> List(TimeSpan timeout);
}

/// <summary>Ensemble des acces systeme du module Applications.</summary>
public sealed record AppsPlatform(
    IRegistryView Registry,
    IProcessControl Processes,
    IServiceControl Services,
    IStartupFolderSource StartupFolders,
    IScheduledTaskSource ScheduledTasks,
    Func<string, string?> CompanyOf)
{
    public static AppsPlatform Windows() => new(
        new WindowsRegistryView(),
        new WindowsProcessControl(),
        new WindowsServiceControl(),
        new WindowsStartupFolders(),
        new ComScheduledTasks(),
        FileCompany);

    /// <summary>Editeur lu dans les informations de version de l'executable (aucun WMI).</summary>
    internal static string? FileCompany(string path)
    {
        try
        {
            return File.Exists(path) ? System.Diagnostics.FileVersionInfo.GetVersionInfo(path).CompanyName?.Trim() is { Length: > 0 } c ? c : null : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>
/// Dossiers de reference de la machine COURANTE, lus a l'execution (rien de code en dur) :
/// Windows (composants proteges), dossier de Facil FreePC (protege) et dossiers trop larges pour servir de
/// dossier d'application (racines de disque, Program Files, profil, AppData...).
/// </summary>
public sealed record AppsEnvironment(string WindowsDirectory, IReadOnlyList<string> SelfPaths, IReadOnlyList<string> BroadFolders)
{
    public static AppsEnvironment Current()
    {
        static string F(Environment.SpecialFolder f) => Environment.GetFolderPath(f, Environment.SpecialFolderOption.DoNotVerify);
        var windows = F(Environment.SpecialFolder.Windows);
        var profile = F(Environment.SpecialFolder.UserProfile);
        var local = F(Environment.SpecialFolder.LocalApplicationData);
        var broad = new List<string>
        {
            windows,
            F(Environment.SpecialFolder.ProgramFiles),
            F(Environment.SpecialFolder.ProgramFilesX86),
            F(Environment.SpecialFolder.CommonProgramFiles),
            F(Environment.SpecialFolder.CommonProgramFilesX86),
            F(Environment.SpecialFolder.CommonApplicationData),
            profile,
            local,
            F(Environment.SpecialFolder.ApplicationData),
            F(Environment.SpecialFolder.Desktop),
            F(Environment.SpecialFolder.MyDocuments),
            F(Environment.SpecialFolder.Startup),
            F(Environment.SpecialFolder.CommonStartup),
            Path.GetTempPath(),   // garde-fou-temp: lecture seule (dossier trop large pour rattacher une appli ; rien n'y est ecrit)
        };
        if (local.Length > 0)
        {
            broad.Add(Path.Combine(local, "Programs"));
            broad.Add(Path.Combine(local, "Temp"));
        }

        if (Path.GetDirectoryName(profile) is { Length: > 0 } users)
        {
            broad.Add(users);   // C:\Users
        }

        var self = new List<string> { AppContext.BaseDirectory };
        if (Environment.ProcessPath is { } exe)
        {
            self.Add(exe);
        }

        return new AppsEnvironment(windows, self, broad.Where(b => b.Length > 0).ToList());
    }
}
