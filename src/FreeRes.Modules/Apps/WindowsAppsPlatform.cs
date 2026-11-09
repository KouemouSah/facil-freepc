using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security;
using System.ServiceProcess;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace FreeRes.Modules.Apps;

/// <summary>Registre reel (Microsoft.Win32.Registry, aucun WMI).</summary>
internal sealed class WindowsRegistryView : IRegistryView
{
    public IReadOnlyList<string> SubKeyNames(RegistryHive hive, RegistryView view, string path) =>
        Read(hive, view, path, k => (IReadOnlyList<string>)k.GetSubKeyNames()) ?? [];

    public IReadOnlyList<string> ValueNames(RegistryHive hive, RegistryView view, string path) =>
        Read(hive, view, path, k => (IReadOnlyList<string>)k.GetValueNames()) ?? [];

    public object? GetValue(RegistryHive hive, RegistryView view, string path, string name) =>
        Read<object>(hive, view, path, k => k.GetValue(name));

    public void SetBinary(RegistryHive hive, RegistryView view, string path, string name, byte[] data)
    {
        using var root = RegistryKey.OpenBaseKey(hive, view);
        using var key = root.CreateSubKey(path, writable: true);
        key.SetValue(name, data, RegistryValueKind.Binary);
    }

    private static T? Read<T>(RegistryHive hive, RegistryView view, string path, Func<RegistryKey, T?> read)
        where T : class
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var key = root.OpenSubKey(path, writable: false);
            return key is null ? null : read(key);
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;   // cle protegee ou supprimee pendant la lecture
        }
    }
}

/// <summary>
/// Processus reels. Image lue par QueryFullProcessImageName (PROCESS_QUERY_LIMITED_INFORMATION), jamais par
/// Process.MainModule qui echoue sur les processus eleves. Fenetre principale : meme critere que
/// Process.MainWindowHandle (fenetre de premier niveau visible et sans proprietaire), calcule en UN seul
/// parcours EnumWindows au lieu d'un parcours par processus.
/// </summary>
internal sealed partial class WindowsProcessControl : IProcessControl
{
    private const uint ProcessTerminate = 0x0001;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint GwOwner = 4;

    public int CurrentSessionId { get; } = CurrentSession();

    public int CurrentProcessId { get; } = Environment.ProcessId;

    public IReadOnlyList<ProcessSample> Snapshot()
    {
        var windowed = WindowedProcessIds();
        var list = new List<ProcessSample>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    list.Add(new ProcessSample(p.Id, p.ProcessName, ImagePath(p.Id), p.WorkingSet64, p.SessionId, windowed.Contains(p.Id)));
                }
                catch (InvalidOperationException)
                {
                    // processus termine entre l'enumeration et la lecture
                }
            }
        }

        return list;
    }

    public string? Kill(int pid, string expectedImagePath)
    {
        // Le handle ouvert fige l'objet processus : le PID ne peut plus etre reattribue entre controle et arret.
        using var handle = OpenProcess(ProcessTerminate | ProcessQueryLimitedInformation, false, (uint)pid);
        if (handle.IsInvalid)
        {
            return $"accès refusé ou processus terminé (erreur {Marshal.GetLastPInvokeError()})";
        }

        var image = QueryImage(handle);
        if (image is null || !string.Equals(image, expectedImagePath, StringComparison.OrdinalIgnoreCase))
        {
            return "l'image du processus a changé : arrêt annulé";
        }

        return TerminateProcess(handle, 1) ? null : $"TerminateProcess a échoué (erreur {Marshal.GetLastPInvokeError()})";
    }

    private static int CurrentSession()
    {
        using var self = Process.GetCurrentProcess();
        return self.SessionId;
    }

    internal static string? ImagePath(int pid)
    {
        using var handle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);
        return handle.IsInvalid ? null : QueryImage(handle);
    }

    private static unsafe string? QueryImage(SafeProcessHandle handle)
    {
        var buffer = stackalloc char[1024];
        uint size = 1024;
        return QueryFullProcessImageName(handle, 0, buffer, ref size) ? new string(buffer, 0, (int)size) : null;
    }

    private static unsafe HashSet<int> WindowedProcessIds()
    {
        var set = new HashSet<int>();
        var gc = GCHandle.Alloc(set);
        try
        {
            EnumWindows(&OnWindow, GCHandle.ToIntPtr(gc));
        }
        finally
        {
            gc.Free();
        }

        return set;
    }

    [UnmanagedCallersOnly]
    private static int OnWindow(nint hwnd, nint state)
    {
        if (IsWindowVisible(hwnd) && GetWindow(hwnd, GwOwner) == 0 && GetWindowThreadProcessId(hwnd, out var pid) != 0)
        {
            ((HashSet<int>)GCHandle.FromIntPtr(state).Target!).Add((int)pid);
        }

        return 1;   // continuer l'enumeration
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, char* buffer, ref uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateProcess(SafeProcessHandle process, uint exitCode);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool EnumWindows(delegate* unmanaged<nint, nint, int> callback, nint state);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial nint GetWindow(nint hwnd, uint command);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint hwnd, out uint processId);
}

/// <summary>Etat des services via ServiceController.</summary>
internal sealed class WindowsServiceControl : IServiceControl
{
    public ServiceControllerStatus? Status(string serviceName)
    {
        try
        {
            using var sc = new ServiceController(serviceName);
            return sc.Status;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;   // service supprime ou inaccessible
        }
    }
}

/// <summary>
/// Dossiers Demarrage. Cible des raccourcis lue par l'objet COM ShellLink (IShellLinkW, aucun WMI) ; si elle ne
/// peut pas etre lue (raccourci « annonce » MSI/AppX, fichier corrompu), seule l'entree du raccourci est gardee.
/// </summary>
internal sealed class WindowsStartupFolders : IStartupFolderSource
{
    public IReadOnlyList<StartupFolderEntry> List()
    {
        var list = new List<StartupFolderEntry>();
        Add(list, AppScope.User, Environment.GetFolderPath(Environment.SpecialFolder.Startup));
        Add(list, AppScope.Machine, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup));
        return list;
    }

    private static void Add(List<StartupFolderEntry> list, AppScope scope, string folder)
    {
        if (folder.Length == 0 || !Directory.Exists(folder))
        {
            return;
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                var name = Path.GetFileName(file);
                if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string? target = file;
                string? args = null;
                if (name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    (target, args) = ShellLinkReader.Read(file);
                }

                list.Add(new StartupFolderEntry(scope, folder, name, target, args));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // dossier inaccessible : rien a lister
        }
    }
}

/// <summary>Lecture de la cible d'un raccourci .lnk par IShellLinkW / IPersistFile.</summary>
internal static class ShellLinkReader
{
    private const int MaxPath = 1024;

    public static (string? Target, string? Arguments) Read(string lnkPath)
    {
        object? link = null;
        var buffer = Marshal.AllocHGlobal(MaxPath * sizeof(char));
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"), throwOnError: false);
            if (type is null)
            {
                return (null, null);
            }

            link = Activator.CreateInstance(type);
            if (link is not IShellLinkW shell || link is not IPersistFile file)
            {
                return (null, null);
            }

            file.Load(lnkPath, 0);   // STGM_READ
            shell.GetPath(buffer, MaxPath, 0, 0);
            var target = Marshal.PtrToStringUni(buffer);
            shell.GetArguments(buffer, MaxPath);
            var args = Marshal.PtrToStringUni(buffer);
            return (string.IsNullOrWhiteSpace(target) ? null : target, string.IsNullOrWhiteSpace(args) ? null : args);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or UnauthorizedAccessException or IOException or ArgumentException)
        {
            return (null, null);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            if (link is not null && Marshal.IsComObject(link))
            {
                Marshal.FinalReleaseComObject(link);
            }
        }
    }

    /// <summary>Debut de la table virtuelle de IShellLinkW (ordre impose par shobjidl.h), jusqu'a GetArguments.</summary>
    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    internal interface IShellLinkW
    {
        void GetPath(nint file, int maxChars, nint findData, uint flags);

        void GetIDList(out nint idList);

        void SetIDList(nint idList);

        void GetDescription(nint name, int maxChars);

        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);

        void GetWorkingDirectory(nint dir, int maxChars);

        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);

        void GetArguments(nint args, int maxChars);
    }
}

/// <summary>
/// Taches planifiees via l'objet COM « Schedule.Service », en LECTURE SEULE (Connect, GetFolder, GetTasks).
/// Execute sur un thread dedie avec delai : si le service de taches ne repond pas, l'inventaire continue sans
/// les taches (signale dans ses avertissements) au lieu de bloquer la surveillance.
/// </summary>
internal sealed class ComScheduledTasks : IScheduledTaskSource
{
    private const int TaskEnumHidden = 1;
    private const int TaskTriggerLogon = 9;
    private const int TaskActionExec = 0;

    public IReadOnlyList<ScheduledTaskEntry> List(TimeSpan timeout)
    {
        List<ScheduledTaskEntry>? result = null;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = Read();
            }
#pragma warning disable CA1031 // Toute erreur COM est rendue a l'appelant (avertissement d'inventaire), jamais un plantage.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                error = ex;
            }
        })
        { IsBackground = true, Name = "FacilFreePC.ScheduledTasks" };
        thread.Start();
        if (!thread.Join(timeout))
        {
            throw new System.TimeoutException($"Planificateur de tâches sans réponse après {timeout.TotalSeconds} s");
        }

        if (error is not null)
        {
            throw new InvalidOperationException("Lecture des tâches planifiées impossible : " + error.Message, error);
        }

        return result ?? [];
    }

    private static List<ScheduledTaskEntry> Read()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!;
        dynamic service = Activator.CreateInstance(type)!;
        try
        {
            service.Connect();
            var list = new List<ScheduledTaskEntry>();
            Walk(service.GetFolder("\\"), list);
            return list;
        }
        finally
        {
            Marshal.FinalReleaseComObject((object)service);
        }
    }

    private static void Walk(dynamic folder, List<ScheduledTaskEntry> list)
    {
        string path = folder.Path;
        if (path.Equals("\\Microsoft", StringComparison.OrdinalIgnoreCase) || path.StartsWith("\\Microsoft\\", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        foreach (var task in folder.GetTasks(TaskEnumHidden))
        {
            try
            {
                if (ToEntry(task) is ScheduledTaskEntry entry)
                {
                    list.Add(entry);
                }
            }
            catch (COMException)
            {
                // tache illisible (droits) : ignoree
            }
        }

        foreach (var sub in folder.GetFolders(0))
        {
            Walk(sub, list);
        }
    }

    private static ScheduledTaskEntry? ToEntry(dynamic task)
    {
        var definition = task.Definition;
        var atLogon = false;
        foreach (var trigger in definition.Triggers)
        {
            if ((int)trigger.Type == TaskTriggerLogon)
            {
                atLogon = true;
            }
        }

        if (!atLogon)
        {
            return null;
        }

        string? exe = null;
        string? args = null;
        foreach (var action in definition.Actions)
        {
            if (exe is null && (int)action.Type == TaskActionExec)
            {
                exe = (string?)action.Path;
                args = (string?)action.Arguments;
            }
        }

        return new ScheduledTaskEntry((string)task.Path, (string)task.Name, (bool)task.Enabled, exe, args);
    }
}
