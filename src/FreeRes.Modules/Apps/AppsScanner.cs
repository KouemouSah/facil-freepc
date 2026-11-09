using System.Globalization;
using Microsoft.Win32;

namespace FreeRes.Modules.Apps;

/// <summary>Partie « statique » de l'inventaire (registre, dossiers, taches), mise en cache entre deux cycles.</summary>
internal sealed record StaticInventory(IReadOnlyList<InstalledApp> Apps, IReadOnlyList<StartupItem> Items, IReadOnlyList<string> Warnings);

/// <summary>Definition d'un service lue dans le registre.</summary>
internal sealed record ServiceEntry(string Name, string DisplayName, string? ImagePath, string? Executable, int Start, int Type);

/// <summary>
/// Lecture de l'inventaire : applications installees (cles Uninstall), elements de demarrage (Run, dossiers
/// Demarrage, services tiers automatiques, taches a l'ouverture de session). Uniquement registre, fichiers et
/// COM du planificateur en lecture : AUCUN WMI (ni Win32_Product, qui relance une reparation de chaque MSI).
/// </summary>
internal sealed class AppsScanner(AppsPlatform platform, AppProtection protection)
{
    internal const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    internal const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string Run32Path = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    internal const string ServicesPath = @"SYSTEM\CurrentControlSet\Services";
    internal const int ServiceAutoStart = 2;
    internal const int ServiceDemandStart = 3;
    internal const int ServiceWin32Mask = 0x30;   // SERVICE_WIN32_OWN_PROCESS | SERVICE_WIN32_SHARE_PROCESS

    private static readonly (RegistryHive Hive, RegistryView View, AppScope Scope, string Label)[] UninstallRoots =
    [
        (RegistryHive.LocalMachine, RegistryView.Registry64, AppScope.Machine, "HKLM64"),
        (RegistryHive.LocalMachine, RegistryView.Registry32, AppScope.Machine, "HKLM32"),
        (RegistryHive.CurrentUser, RegistryView.Registry64, AppScope.User, "HKCU"),
    ];

    private static readonly (RegistryHive Hive, string Path, string ApprovedSubKey, AppScope Scope, string Label)[] RunKeys =
    [
        (RegistryHive.CurrentUser, RunPath, "Run", AppScope.User, @"HKCU\" + RunPath),
        (RegistryHive.LocalMachine, RunPath, "Run", AppScope.Machine, @"HKLM\" + RunPath),
        (RegistryHive.LocalMachine, Run32Path, "Run32", AppScope.Machine, @"HKLM\" + Run32Path),
    ];

    public AppProtection Protection => protection;

    /// <summary>Inventaire complet ; <paramref name="includeServicesAndTasks"/> false pour la validation d'une action.</summary>
    public StaticInventory Scan(bool includeServicesAndTasks, TimeSpan tasksTimeout)
    {
        var warnings = new List<string>();
        var apps = InstalledApps().ToList();
        var items = new List<StartupItem>();
        foreach (var raw in RunItems().Concat(FolderItems()))
        {
            items.Add(Link(raw, apps, createApp: true));
        }

        if (includeServicesAndTasks)
        {
            items.AddRange(ServiceItems().Select(i => Link(i, apps, createApp: false)));
            try
            {
                items.AddRange(TaskItems(platform.ScheduledTasks.List(tasksTimeout)).Select(i => Link(i, apps, createApp: false)));
            }
            catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
            {
                warnings.Add("Tâches planifiées non lues : " + ex.Message);
            }
        }

        return new StaticInventory(apps, items, warnings);
    }

    /// <summary>
    /// Cles Uninstall de HKLM (vues 64 et 32 bits) et HKCU. Exclues : SystemComponent=1, ParentKeyName (mises a
    /// jour et correctifs rattaches a un produit), DisplayName absent.
    /// </summary>
    public IEnumerable<InstalledApp> InstalledApps()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (hive, view, scope, label) in UninstallRoots)
        {
            foreach (var key in platform.Registry.SubKeyNames(hive, view, UninstallPath))
            {
                var path = UninstallPath + "\\" + key;
                string? Str(string name) => platform.Registry.GetValue(hive, view, path, name) is string s && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;
                var name = Str("DisplayName");
                if (name is null || AsInt(platform.Registry.GetValue(hive, view, path, "SystemComponent")) == 1 || Str("ParentKeyName") is not null)
                {
                    continue;
                }

                var id = AppPathRules.StableId("app", $"{label}|{key}");
                if (!seen.Add(id))
                {
                    continue;
                }

                var publisher = Str("Publisher");
                var location = Str("InstallLocation");
                var folder = protection.IsBroadFolder(location) ? null : AppPathRules.Normalize(location);
                yield return new InstalledApp(
                    id,
                    name,
                    publisher,
                    Str("DisplayVersion"),
                    location,
                    AsInt(platform.Registry.GetValue(hive, view, path, "EstimatedSize")) is int kb and > 0 ? kb : null,
                    ParseDate(Str("InstallDate")),
                    scope,
                    AppSource.Registry,
                    folder,
                    protection.Check(name, publisher, location));
            }
        }
    }

    /// <summary>Valeurs des cles Run : HKCU, HKLM (64 bits) et HKLM\WOW6432Node (32 bits, etat dans « Run32 »).</summary>
    public IEnumerable<StartupItem> RunItems()
    {
        foreach (var (hive, path, sub, scope, label) in RunKeys)
        {
            foreach (var name in platform.Registry.ValueNames(hive, RegistryView.Registry64, path))
            {
                if (name.Length == 0 || platform.Registry.GetValue(hive, RegistryView.Registry64, path, name) is not string { Length: > 0 } command)
                {
                    continue;
                }

                var approved = platform.Registry.GetValue(hive, RegistryView.Registry64, StartupApproved.RootPath + "\\" + sub, name);
                yield return new StartupItem(
                    AppPathRules.StableId("su", $"RUN|{label}|{name}"),
                    StartupKind.Run,
                    scope,
                    name,
                    label,
                    command,
                    AppPathRules.ExtractExecutable(command),
                    StartupApproved.IsEnabled(approved),
                    sub,
                    null,
                    null);
            }
        }
    }

    /// <summary>Fichiers des dossiers Demarrage ; etat dans « StartupFolder » de HKCU (utilisateur) ou HKLM (commun).</summary>
    public IEnumerable<StartupItem> FolderItems()
    {
        foreach (var e in platform.StartupFolders.List())
        {
            var hive = e.Scope == AppScope.User ? RegistryHive.CurrentUser : RegistryHive.LocalMachine;
            var approved = platform.Registry.GetValue(hive, RegistryView.Registry64, StartupApproved.RootPath + @"\StartupFolder", e.FileName);
            var shortcut = Path.Combine(e.Folder, e.FileName);
            yield return new StartupItem(
                AppPathRules.StableId("su", $"FOLDER|{e.Folder}|{e.FileName}"),
                StartupKind.StartupFolder,
                e.Scope,
                e.FileName,
                e.Folder,
                e.Target is null ? shortcut : $"{e.Target} {e.Arguments}".Trim(),
                AppPathRules.Normalize(e.Target),   // cible non resolue : seul le raccourci est connu (pas d'executable)
                StartupApproved.IsEnabled(approved),
                "StartupFolder",
                null,
                null);
        }
    }

    /// <summary>Lecture d'un service (null s'il n'existe pas).</summary>
    public ServiceEntry? ReadService(string name)
    {
        var path = ServicesPath + "\\" + name;
        var start = AsInt(platform.Registry.GetValue(RegistryHive.LocalMachine, RegistryView.Registry64, path, "Start"));
        var type = AsInt(platform.Registry.GetValue(RegistryHive.LocalMachine, RegistryView.Registry64, path, "Type"));
        if (start is null || type is null)
        {
            return null;
        }

        var image = platform.Registry.GetValue(RegistryHive.LocalMachine, RegistryView.Registry64, path, "ImagePath") as string;
        var display = platform.Registry.GetValue(RegistryHive.LocalMachine, RegistryView.Registry64, path, "DisplayName") as string;
        return new ServiceEntry(
            name,
            string.IsNullOrWhiteSpace(display) || display.StartsWith('@') ? name : display.Trim(),   // « @dll,-id » : ressource, non resolue sans charger la DLL
            image,
            AppPathRules.ServiceExecutable(image, protection.Environment.WindowsDirectory),
            start.Value,
            type.Value);
    }

    /// <summary>Raison pour laquelle un service n'est pas un service tiers modifiable, ou null.</summary>
    public string? ServiceRefusal(ServiceEntry s)
    {
        if ((s.Type & ServiceWin32Mask) == 0)
        {
            return "pilote ou service non Win32";
        }

        if (s.Executable is null)
        {
            return "binaire du service introuvable";
        }

        if (protection.IsWindowsPath(s.Executable))
        {
            return "service Windows (binaire sous le dossier Windows)";
        }

        return protection.Check($"{s.DisplayName} ({s.Name})", platform.CompanyOf(s.Executable), s.Executable) is { } p ? "service protégé : " + p : null;
    }

    /// <summary>Services Win32 en demarrage automatique dont le binaire est HORS du dossier Windows.</summary>
    public IEnumerable<StartupItem> ServiceItems()
    {
        foreach (var name in platform.Registry.SubKeyNames(RegistryHive.LocalMachine, RegistryView.Registry64, ServicesPath))
        {
            if (ReadService(name) is not { Start: ServiceAutoStart } s || (s.Type & ServiceWin32Mask) == 0 || s.Executable is null || protection.IsWindowsPath(s.Executable))
            {
                continue;
            }

            yield return new StartupItem(
                AppPathRules.StableId("svc", "SERVICE|" + name),
                StartupKind.Service,
                AppScope.Machine,
                s.DisplayName,
                @"HKLM\" + ServicesPath + "\\" + name,
                s.ImagePath,
                s.Executable,
                true,
                null,
                null,
                ServiceRefusal(s),
                name);
        }
    }

    public static IEnumerable<StartupItem> TaskItems(IEnumerable<ScheduledTaskEntry> tasks) =>
        tasks.Select(t => new StartupItem(
            AppPathRules.StableId("task", "TASK|" + t.Path),
            StartupKind.ScheduledTask,
            AppScope.Machine,
            t.Name,
            t.Path,
            t.Executable is null ? null : $"{t.Executable} {t.Arguments}".Trim(),
            AppPathRules.Normalize(t.Executable),
            t.Enabled,
            null,
            null,
            null));

    /// <summary>
    /// Rattache un element a l'application installee dont le dossier contient son executable (dossier le plus long).
    /// A defaut, pour Run et dossier Demarrage (<paramref name="createApp"/>), une application est deduite du dossier
    /// de l'executable, s'il n'est pas trop large. Protection : celle de l'element, sinon celle de l'application.
    /// </summary>
    private StartupItem Link(StartupItem item, List<InstalledApp> apps, bool createApp)
    {
        var exe = item.ExecutablePath;
        var app = exe is null ? null : apps
            .Where(a => a.Folder is not null && AppPathRules.IsUnderNormalized(exe, a.Folder))
            .MaxBy(a => a.Folder!.Length);

        if (app is null && createApp && exe is not null && Path.GetDirectoryName(exe) is { } dir && !protection.IsBroadFolder(dir))
        {
            var folder = AppPathRules.Normalize(dir)!;
            var id = AppPathRules.StableId("app", "STARTUP|" + folder);
            app = apps.FirstOrDefault(a => a.Id == id);
            if (app is null)
            {
                var company = platform.CompanyOf(exe);
                app = new InstalledApp(id, item.Kind == StartupKind.StartupFolder ? Path.GetFileNameWithoutExtension(item.Name) : item.Name, company, null, null, null, null, item.Scope, AppSource.StartupItem, folder, protection.Check(item.Name, company, exe));
                apps.Add(app);
            }
        }

        var reason = item.Protection
            ?? protection.Check(item.Name, item.Kind == StartupKind.Service ? null : exe is null ? null : platform.CompanyOf(exe), exe, item.Kind == StartupKind.StartupFolder ? Path.Combine(item.Location, item.Name) : null)
            ?? app?.Protection;
        return item with { AppId = app?.Id, Protection = reason };
    }

    private static int? AsInt(object? value) => value switch
    {
        int i => i,
        long l when l is >= int.MinValue and <= int.MaxValue => (int)l,
        string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) => i,
        _ => null,
    };

    private static DateOnly? ParseDate(string? value) =>
        DateOnly.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
}
