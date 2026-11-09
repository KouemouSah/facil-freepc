using System.Drawing;

namespace FreeRes.App.Tests;

/// <summary>Extraction reelle sur des fichiers de Windows (aucun faux : c'est l'appel au Shell qui est verifie).</summary>
public sealed class ShellIconExtractorTests
{
    [Fact]
    public void Icone_d_un_executable_Windows_en_png_64_px_avec_transparence()
    {
        var png = new ShellIconExtractor().ExtractPng(Path.Combine(Environment.SystemDirectory, "notepad.exe"), 64);

        Assert.NotNull(png);
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], png[..4]);
        using var image = new Bitmap(new MemoryStream(png));
        Assert.Equal(new Size(64, 64), image.Size);
        Assert.Equal(0, image.GetPixel(0, 0).A);   // coin transparent : pas de fond noir
    }

    [Fact]
    public void Fichier_absent_ou_sans_icone_null()
    {
        var extractor = new ShellIconExtractor();
        Assert.Null(extractor.ExtractPng(Path.Combine(Path.GetTempPath(), "absent-" + Guid.NewGuid().ToString("N") + ".exe"), 64));

        var empty = Path.Combine(Path.GetTempPath(), "vide-" + Guid.NewGuid().ToString("N") + ".exe");
        File.WriteAllBytes(empty, [0]);
        try
        {
            Assert.Null(extractor.ExtractPng(empty, 64));
        }
        finally
        {
            File.Delete(empty);
        }
    }
}
