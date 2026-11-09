using System.ServiceProcess;
using FreeRes.Core.Engine;
using FreeRes.Core.Machine;
using FreeRes.Core.Metrics;
using FreeRes.Core.Policy;
using FreeRes.Modules.Apps;
using Microsoft.Win32;

namespace FreeRes.Modules.Tests;

/// <summary>Registre EN MEMOIRE : aucun test n'ecrit dans le vrai registre.</summary>
internal sealed class FakeRegistry : IRegistryView
{
    private readonly Dictionary<string, Dictionary<string, object>> _keys = new(StringComparer.OrdinalIgnoreCase);

    public List<(RegistryHive Hive, RegistryView View, string Path, string Name, byte[] Data)> Writes { get; } = [];

    public void Set(RegistryHive hive, RegistryView view, string path, string name, object value)
    {
        var k = Key(hive, view, path);
        if (!_keys.TryGetValue(k, out var values))
        {
            _keys[k] = values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        }

        values[name] = value;
    }

    public object? Peek(RegistryHive hive, RegistryView view, string path, string name) => GetValue(hive, view, path, name);

    public IReadOnlyList<string> SubKeyNames(RegistryHive hive, RegistryView view, string path)
    {
        var prefix = Key(hive, view, path) + "\\";
        return _keys.Keys
            .Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(k => k[prefix.Length..].Split('\\')[0])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<string> ValueNames(RegistryHive hive, RegistryView view, string path) =>
        _keys.TryGetValue(Key(hive, view, path), out var v) ? v.Keys.ToList() : [];

    public object? GetValue(RegistryHive hive, RegistryView view, string path, string name) =>
        _keys.TryGetValue(Key(hive, view, path), out var v) && v.TryGetValue(name, out var o) ? o : null;

    public void SetBinary(RegistryHive hive, RegistryView view, string path, string name, byte[] data)
    {
        Writes.Add((hive, view, path, name, data));
        Set(hive, view, path, name, data);
    }

    private static string Key(RegistryHive hive, RegistryView view, string path) => $"{hive}|{view}|{path}";
}

/// <summary>Processus SIMULES : Kill enregistre l'appel, aucun vrai processus n'est touche.</summary>
internal sealed class FakeProcesses : IProcessControl
{
    public List<ProcessSample> Processes { get; } = [];

    public List<int> Killed { get; } = [];

    public int CurrentSessionId => 1;

    public int CurrentProcessId => 4242;

    public IReadOnlyList<ProcessSample> Snapshot() => Processes.ToList();

    public string? Kill(int pid, string expectedImagePath)
    {
        Killed.Add(pid);
        return null;
    }
}

internal sealed class FakeServices : IServiceControl
{
    public Dictionary<string, ServiceControllerStatus> Statuses { get; } = new(StringComparer.OrdinalIgnoreCase);

    public ServiceControllerStatus? Status(string serviceName) => Statuses.TryGetValue(serviceName, out var s) ? s : null;
}

internal sealed class FakeFolders : IStartupFolderSource
{
    public List<StartupFolderEntry> Entries { get; } = [];

    public IReadOnlyList<StartupFolderEntry> List() => Entries;
}

internal sealed class FakeTasks : IScheduledTaskSource
{
    public List<ScheduledTaskEntry> Entries { get; } = [];

    public Exception? Failure { get; set; }

    public IReadOnlyList<ScheduledTaskEntry> List(TimeSpan timeout) => Failure is null ? Entries : throw Failure;
}

/// <summary>Machine simulee pour le module Applications : registre, processus, services, dossiers, taches.</summary>
internal sealed class AppsWorld
{
    public const long MB = 1024 * 1024;
    public const string Uninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    public const string Run = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string Run32 = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    public const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";
    public const string Services = @"SYSTEM\CurrentControlSet\Services";

    private int _nextPid = 100;

    public FakeRegistry Registry { get; } = new();

    public FakeProcesses Processes { get; } = new();

    public FakeServices ServiceStates { get; } = new();

    public FakeFolders Folders { get; } = new();

    public FakeTasks Tasks { get; } = new();

    public Dictionary<string, string> Companies { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Machine fictive generique : rien de propre a la machine de developpement.</summary>
    public static AppsEnvironment Environment { get; } = new(
        @"C:\Windows",
        [@"C:\Program Files\Facil FreePC"],
        [@"C:\Windows", @"C:\Program Files", @"C:\Program Files (x86)", @"C:\ProgramData", @"C:\Users", @"C:\Users\u", @"C:\Users\u\AppData\Local", @"C:\Users\u\AppData\Roaming"]);

    public AppsPlatform Platform => new(Registry, Processes, ServiceStates, Folders, Tasks, path => Companies.TryGetValue(path, out var c) ? c : null);

    public void App(string key, string? name, string? publisher = null, string? location = null, RegistryHive hive = RegistryHive.LocalMachine, RegistryView view = RegistryView.Registry64, Action<string>? extra = null)
    {
        var path = Uninstall + "\\" + key;
        Registry.Set(hive, view, path, "UninstallString", @"MsiExec.exe /X{" + key + "}");
        if (name is not null)
        {
            Registry.Set(hive, view, path, "DisplayName", name);
        }

        if (publisher is not null)
        {
            Registry.Set(hive, view, path, "Publisher", publisher);
        }

        if (location is not null)
        {
            Registry.Set(hive, view, path, "InstallLocation", location);
        }

        extra?.Invoke(path);
    }

    public void RunEntry(string name, string command, RegistryHive hive = RegistryHive.CurrentUser, string path = Run) =>
        Registry.Set(hive, RegistryView.Registry64, path, name, command);

    public void ApprovedValue(RegistryHive hive, string sub, string name, params byte[] firstBytes)
    {
        var data = new byte[12];
        firstBytes.CopyTo(data, 0);
        Registry.Set(hive, RegistryView.Registry64, Approved + "\\" + sub, name, data);
    }

    public void Service(string name, string imagePath, int start = 2, int type = 0x10, string? display = null)
    {
        var path = Services + "\\" + name;
        Registry.Set(RegistryHive.LocalMachine, RegistryView.Registry64, path, "Start", start);
        Registry.Set(RegistryHive.LocalMachine, RegistryView.Registry64, path, "Type", type);
        Registry.Set(RegistryHive.LocalMachine, RegistryView.Registry64, path, "ImagePath", imagePath);
        if (display is not null)
        {
            Registry.Set(RegistryHive.LocalMachine, RegistryView.Registry64, path, "DisplayName", display);
        }
    }

    public ProcessSample Process(string image, long mb, bool window = false, int session = 1)
    {
        var p = new ProcessSample(_nextPid++, Path.GetFileNameWithoutExtension(image), image, mb * MB, session, window);
        Processes.Processes.Add(p);
        return p;
    }

    public AppsModule Module(ModuleServices services, AppsOptions? options = null) => new(services, options, Platform, Environment);

    /// <summary>Contexte de cycle : RAM disponible et memoire engagee parametrables (seuils d'alerte 3 GB / 85 %).</summary>
    public static EngineContext Context(TimeProvider time, double ramAvailGB = 6, double commitPct = 50)
    {
        var snapshot = new SystemSnapshot(time.GetLocalNow(), 15.69, ramAvailGB, 60, 23.69, 12, commitPct, "C:", 235.48, 40, 1, false, []);
        var profile = new MachineProfile("PC", "Fabricant", "Modele", "Windows 11", "25H2", 26200, 15.69, 12, "C:", 235.48);
        return new EngineContext(snapshot, profile, new Thresholds(3.0, 1.5, 85, 95, 30, 12), new AppSettings());
    }
}
