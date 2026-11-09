using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using FreeRes.Host;

namespace FreeRes.App;

/// <summary>
/// Icone d'un executable via le Shell (SHDefExtractIcon : lecture des ressources du fichier, aucun code execute),
/// a la taille demandee (nette sur les ecrans a haute densite), convertie en PNG avec sa transparence.
/// </summary>
internal sealed partial class ShellIconExtractor : IIconExtractor
{
    public byte[]? ExtractPng(string path, int size)
    {
        // LOWORD = grande icone (taille voulue), HIWORD = petite icone (16 px, liberee aussitot).
        var sizes = (uint)size | (16u << 16);
        if (SHDefExtractIcon(path, 0, 0, out var large, out var small, sizes) != 0 || large == IntPtr.Zero)
        {
            Destroy(large);
            Destroy(small);
            return null;
        }

        try
        {
            using var icon = Icon.FromHandle(large);
            using var bitmap = icon.ToBitmap();
            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Png);
            return stream.ToArray();
        }
        catch (Exception ex) when (ex is ArgumentException or ExternalException)
        {
            return null;
        }
        finally
        {
            Destroy(large);
            Destroy(small);
        }
    }

    private static void Destroy(IntPtr handle)
    {
        if (handle != IntPtr.Zero)
        {
            DestroyIcon(handle);
        }
    }

    [LibraryImport("shell32.dll", EntryPoint = "SHDefExtractIconW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHDefExtractIcon(string pszIconFile, int iIndex, uint uFlags, out IntPtr phiconLarge, out IntPtr phiconSmall, uint nIconSize);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(IntPtr hIcon);
}
