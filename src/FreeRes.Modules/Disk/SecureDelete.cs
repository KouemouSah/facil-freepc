using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FreeRes.Modules.Disk;

/// <summary>
/// Suppression resistante aux attaques par jonction/lien (TOCTOU). Risque traite : en mode ELEVE, le nettoyage
/// des zones systeme (%SystemRoot%\Temp, WER...) parcourt des dossiers ou un utilisateur standard peut creer des
/// entrees ; s'il remplace un dossier verifie par une jonction vers C:\Windows\System32 entre la verification et
/// la suppression, une suppression par CHEMIN suivrait la jonction (elevation de privileges).
/// Parade : ouvrir l'element SANS suivre les liens, lire son chemin REEL via le handle, verifier qu'il reste
/// sous la racine (elle-meme resolue), puis supprimer PAR CE HANDLE. Aucune relecture du chemin entre controle
/// et suppression.
/// </summary>
internal static partial class SecureDelete
{
    private const uint Delete = 0x00010000;
    private const uint FileReadAttributes = 0x80;
    private const uint ShareAll = 0x7;
    private const uint OpenExisting = 3;
    private const uint FlagOpenReparsePoint = 0x00200000;
    private const uint FlagBackupSemantics = 0x02000000;   // obligatoire pour ouvrir un dossier
    private const int FileBasicInfo = 0;
    private const int FileDispositionInfo = 4;
    private const int FileDispositionInfoEx = 21;
    private const uint DispositionDelete = 0x1;
    private const uint DispositionPosixSemantics = 0x2;
    private const uint DispositionIgnoreReadonly = 0x10;
    private const uint AttributeReparsePoint = 0x400;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileBasicInformation
    {
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public uint FileAttributes;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    private static unsafe partial uint GetFinalPathNameByHandle(SafeFileHandle handle, char* buffer, uint size, uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, out FileBasicInformation info, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetFileInformationByHandle(SafeFileHandle handle, int infoClass, ref uint info, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetFileInformationByHandle(SafeFileHandle handle, int infoClass, ref byte info, uint size);

    /// <summary>Chemin reel d'un dossier existant (sans suivre de lien) ; null s'il est absent ou est lui-meme un lien.</summary>
    public static string? RealDirectoryPath(string path)
    {
        using var h = Open(path);
        if (h.IsInvalid || IsReparse(h))
        {
            return null;
        }

        return FinalPath(h);
    }

    public enum Result
    {
        Deleted,
        OutsideRoot,
        Reparse,
        Failed,
    }

    /// <summary>
    /// Supprime le fichier ou le dossier VIDE <paramref name="path"/> si, et seulement si, son chemin reel (resolu
    /// par handle) est strictement sous <paramref name="realRoot"/>. Un lien n'est supprime que si
    /// <paramref name="allowReparseLink"/> : c'est alors l'objet lien lui-meme (ouvert sans le suivre), jamais sa cible.
    /// </summary>
    public static Result TryDeleteWithinRoot(string path, string realRoot, bool allowReparseLink = false)
    {
        using var h = Open(path);
        if (h.IsInvalid)
        {
            return Result.Failed;
        }

        if (IsReparse(h) && !allowReparseLink)
        {
            return Result.Reparse;
        }

        var final = FinalPath(h);
        if (final is null || !final.StartsWith(realRoot.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
        {
            return Result.OutsideRoot;
        }

        // Suppression par le handle deja verifie (semantique POSIX + ignore lecture seule ; repli classique sur FAT).
        uint flags = DispositionDelete | DispositionPosixSemantics | DispositionIgnoreReadonly;
        if (SetFileInformationByHandle(h, FileDispositionInfoEx, ref flags, sizeof(uint)))
        {
            return Result.Deleted;
        }

        byte delete = 1;
        return SetFileInformationByHandle(h, FileDispositionInfo, ref delete, 1) ? Result.Deleted : Result.Failed;
    }

    private static SafeFileHandle Open(string path) =>
        CreateFile(@"\\?\" + Path.GetFullPath(path), Delete | FileReadAttributes, ShareAll, IntPtr.Zero, OpenExisting, FlagOpenReparsePoint | FlagBackupSemantics, IntPtr.Zero);

    private static bool IsReparse(SafeFileHandle h) =>
        !GetFileInformationByHandleEx(h, FileBasicInfo, out var info, (uint)Marshal.SizeOf<FileBasicInformation>())
        || (info.FileAttributes & AttributeReparsePoint) != 0;   // doute = traite comme un lien (jamais supprime)

    private static string? FinalPath(SafeFileHandle h)
    {
        const int Capacity = 32768;   // longueur maximale d'un chemin NT : pas de troncature possible
        var buffer = new char[Capacity];
        uint len;
        unsafe
        {
            fixed (char* p0 = buffer)
            {
                len = GetFinalPathNameByHandle(h, p0, Capacity, 0);
            }
        }

        if (len == 0 || len >= Capacity)
        {
            return null;
        }

        var p = new string(buffer, 0, (int)len);
        if (p.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
        {
            return @"\\" + p[8..];
        }

        return p.StartsWith(@"\\?\", StringComparison.Ordinal) ? p[4..] : p;
    }
}
