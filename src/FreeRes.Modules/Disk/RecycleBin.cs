using System.Runtime.InteropServices;

namespace FreeRes.Modules.Disk;

/// <summary>Corbeille de l'utilisateur (remplacable dans les tests).</summary>
public interface IRecycleBin
{
    long SizeBytes();

    void Empty();
}

/// <summary>API Shell : taille et vidage de la corbeille de TOUS les lecteurs de l'utilisateur, sans confirmation.</summary>
public sealed partial class ShellRecycleBin : IRecycleBin
{
    private const uint NoConfirmation = 0x1;
    private const uint NoProgressUi = 0x2;
    private const uint NoSound = 0x4;
    private const int EmptyBin = unchecked((int)0x8000FFFF);   // E_UNEXPECTED rendu quand la corbeille est deja vide

    [StructLayout(LayoutKind.Sequential)]
    private struct ShQueryRbInfo
    {
        public int Size;
        public long SizeBytes;
        public long NumItems;
    }

    [LibraryImport("shell32.dll", EntryPoint = "SHQueryRecycleBinW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHQueryRecycleBin(string? rootPath, ref ShQueryRbInfo info);

    [LibraryImport("shell32.dll", EntryPoint = "SHEmptyRecycleBinW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHEmptyRecycleBin(IntPtr hwnd, string? rootPath, uint flags);

    public long SizeBytes()
    {
        var info = new ShQueryRbInfo { Size = Marshal.SizeOf<ShQueryRbInfo>() };
        return SHQueryRecycleBin(null, ref info) == 0 ? info.SizeBytes : 0;
    }

    public void Empty()
    {
        var hr = SHEmptyRecycleBin(IntPtr.Zero, null, NoConfirmation | NoProgressUi | NoSound);
        if (hr != 0 && hr != EmptyBin)
        {
            Marshal.ThrowExceptionForHR(hr);
        }
    }
}
