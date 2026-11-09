using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

namespace FreeRes.Modules.Browsers;

/// <summary>Acces systeme du module (remplace par un faux dans les tests : aucun vrai navigateur n'est touche).</summary>
public interface IBrowserPlatform
{
    bool FileExists(string path);

    /// <summary>Lecture partagee (le navigateur garde ses fichiers ouverts) ; null si absent ou illisible.</summary>
    string? ReadAllText(string path);

    DateTime? LastWriteUtc(string path);

    IReadOnlyList<string> Directories(string path);

    /// <summary>Processus dont le nom d'image est dans <paramref name="exeNames"/>.</summary>
    IReadOnlyList<BrowserProcess> Processes(IReadOnlyCollection<string> exeNames);

    /// <summary>Fermeture PROPRE d'une instance Chromium (Restart Manager, sans forcer). Code Win32 (0 = demandee).</summary>
    int CloseChromium(int mainPid, DateTimeOffset startTime);

    /// <summary>Ctrl+Maj+Q a Firefox, UNIQUEMENT si sa fenetre est au premier plan ; false si aucune touche envoyee.</summary>
    bool SendFirefoxQuit(int mainPid);

    /// <summary>true si le .xpi porte une signature Mozilla (META-INF/mozilla.rsa ou cose.sig).</summary>
    bool IsSignedXpi(string path);
}

/// <summary>Implementation Windows (aucun WMI : Toolhelp, handles, Restart Manager, SendInput).</summary>
public sealed partial class WindowsBrowserPlatform : IBrowserPlatform
{
    public bool FileExists(string path) => File.Exists(path);

    public string? ReadAllText(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public DateTime? LastWriteUtc(string path) => File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;

    public IReadOnlyList<string> Directories(string path)
    {
        try
        {
            return Directory.Exists(path) ? Directory.GetDirectories(path) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public IReadOnlyList<BrowserProcess> Processes(IReadOnlyCollection<string> exeNames)
    {
        var parents = ParentMap();
        var result = new List<BrowserProcess>();
        foreach (var name in exeNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(name)))
            {
                using (p)
                {
                    DateTimeOffset? start = null;
                    long ws = 0;
                    try
                    {
                        ws = p.WorkingSet64;
                        start = p.StartTime;
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                        // processus termine ou inaccessible : mesures partielles
                    }

                    result.Add(new BrowserProcess(p.Id, parents.GetValueOrDefault(p.Id), ImagePath(p.Id), ws, start));
                }
            }
        }

        return result;
    }

    public int CloseChromium(int mainPid, DateTimeOffset startTime)
    {
        int r;
        uint session;
        unsafe
        {
            var key = stackalloc char[64];   // CCH_RM_SESSION_KEY + 1
            r = RmStartSession(out session, 0, key);
        }

        if (r != 0)
        {
            return r;
        }

        try
        {
            var ft = startTime.ToFileTime();
            var proc = new RmUniqueProcess { Pid = mainPid, StartLow = unchecked((uint)ft), StartHigh = unchecked((uint)(ft >> 32)) };
            r = RmRegisterResources(session, 0, IntPtr.Zero, 1, ref proc, 0, IntPtr.Zero);
            // flags 0 : JAMAIS RmForceShutdown ; une application qui refuse fait echouer l'operation.
            return r != 0 ? r : RmShutdown(session, 0, IntPtr.Zero);
        }
        finally
        {
            _ = RmEndSession(session);
        }
    }

    public bool SendFirefoxQuit(int mainPid)
    {
        var window = MainWindow(mainPid, "MozillaWindowClass");
        if (window == IntPtr.Zero)
        {
            return false;
        }

        if (IsIconic(window))
        {
            _ = ShowWindow(window, 9);   // SW_RESTORE seulement si reduite (pas de redimensionnement)
        }

        for (var i = 0; i < 3 && GetForegroundWindow() != window; i++)
        {
            Key(0x12, false);   // ALT : leve le verrou de premier plan de Windows
            Key(0x12, true);
            _ = SetForegroundWindow(window);
            Thread.Sleep(400);
        }

        if (GetForegroundWindow() != window)
        {
            return false;   // jamais de touche envoyee a une autre fenetre
        }

        Key(0x11, false);   // Ctrl
        Key(0x10, false);   // Maj
        Key(0x51, false);   // Q
        Key(0x51, true);
        Key(0x10, true);
        Key(0x11, true);
        return true;
    }

    public bool IsSignedXpi(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            return zip.Entries.Any(e => e.FullName.Equals("META-INF/mozilla.rsa", StringComparison.OrdinalIgnoreCase)
                || e.FullName.Equals("META-INF/cose.sig", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ natif

    private static Dictionary<int, int> ParentMap()
    {
        var map = new Dictionary<int, int>();
        var snap = CreateToolhelp32Snapshot(0x2, 0);   // TH32CS_SNAPPROCESS
        if (snap == new IntPtr(-1))
        {
            return map;
        }

        try
        {
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (Process32FirstW(snap, ref entry))
            {
                do
                {
                    map[(int)entry.ProcessId] = (int)entry.ParentProcessId;
                }
                while (Process32NextW(snap, ref entry));
            }
        }
        finally
        {
            _ = CloseHandle(snap);
        }

        return map;
    }

    private static string? ImagePath(int pid)
    {
        var h = OpenProcess(0x1000, false, pid);   // PROCESS_QUERY_LIMITED_INFORMATION
        if (h == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var buffer = new char[32768];
            var size = buffer.Length;
            unsafe
            {
                fixed (char* p = buffer)
                {
                    return QueryFullProcessImageNameW(h, 0, p, ref size) ? new string(buffer, 0, size) : null;
                }
            }
        }
        finally
        {
            _ = CloseHandle(h);
        }
    }

    private static IntPtr MainWindow(int pid, string windowClass)
    {
        var found = IntPtr.Zero;
        var cls = new char[256];
        _ = EnumWindows(
            (h, param) =>
            {
                _ = GetWindowThreadProcessId(h, out var owner);
                if (owner != pid || !IsWindowVisible(h) || GetWindow(h, 4) != IntPtr.Zero)
                {
                    return true;
                }

                int len;
                unsafe
                {
                    fixed (char* p = cls)
                    {
                        len = GetClassNameW(h, p, cls.Length);
                    }
                }

                if (new string(cls, 0, Math.Max(0, len)) == windowClass)
                {
                    found = h;
                    return false;
                }

                return true;
            },
            IntPtr.Zero);
        return found;
    }

    private static void Key(ushort vk, bool up)
    {
        var input = new Input { Type = 1, Vk = vk, Flags = up ? 0x2u : 0u };
        _ = SendInput(1, ref input, Marshal.SizeOf<Input>());
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExeFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RmUniqueProcess
    {
        public int Pid;
        public uint StartLow;
        public uint StartHigh;
    }

    // INPUT (x64) : type sur 4 octets, union alignee sur 8, taille 40 (la plus grande variante, MOUSEINPUT, fait 32).
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct Input
    {
        [FieldOffset(0)]
        public uint Type;
        [FieldOffset(8)]
        public ushort Vk;
        [FieldOffset(10)]
        public ushort Scan;
        [FieldOffset(12)]
        public uint Flags;
        [FieldOffset(16)]
        public uint Time;
        [FieldOffset(24)]
        public IntPtr ExtraInfo;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr param);

#pragma warning disable SYSLIB1054 // ProcessEntry32 (chaine ByValTStr) et le delegue de rappel exigent le marshaling classique.
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr param);
#pragma warning restore SYSLIB1054

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryFullProcessImageNameW(IntPtr process, uint flags, char* buffer, ref int size);

    [LibraryImport("rstrtmgr.dll")]
    private static unsafe partial int RmStartSession(out uint session, int flags, char* key);

    [LibraryImport("rstrtmgr.dll")]
    private static partial int RmRegisterResources(uint session, uint files, IntPtr fileNames, uint processes, ref RmUniqueProcess process, uint services, IntPtr serviceNames);

    [LibraryImport("rstrtmgr.dll")]
    private static partial int RmShutdown(uint session, uint flags, IntPtr callback);

    [LibraryImport("rstrtmgr.dll")]
    private static partial int RmEndSession(uint session);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(IntPtr hwnd, out int pid);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetWindow(IntPtr hwnd, uint cmd);

    [LibraryImport("user32.dll")]
    private static unsafe partial int GetClassNameW(IntPtr hwnd, char* name, int max);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(IntPtr hwnd, int cmd);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr hwnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint SendInput(uint count, ref Input input, int size);
}
