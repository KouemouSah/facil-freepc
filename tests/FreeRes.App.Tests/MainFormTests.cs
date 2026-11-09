using System.Drawing;

namespace FreeRes.App.Tests;

public sealed class MainFormTests
{
    [Fact]
    public void Grand_ecran_a_100_pourcent_taille_confortable()
    {
        Assert.Equal(new Size(1280, 800), MainForm.FitToScreen(new Rectangle(0, 0, 2560, 1400), 1.0));
    }

    [Fact]
    public void Ecran_4K_a_200_pourcent_taille_logique_preservee()
    {
        Assert.Equal(new Size(2560, 1600), MainForm.FitToScreen(new Rectangle(0, 0, 3840, 2100), 2.0));
    }

    [Fact]
    public void Portable_a_150_pourcent_plafonne_a_la_zone_utile()
    {
        // Cas reel de la machine de developpement : 1920x1008 px utiles a 150 %.
        var area = new Rectangle(0, 0, 1920, 1008);
        var size = MainForm.FitToScreen(area, 1.5);
        Assert.Equal(new Size(1766, 927), size);
        Assert.True(size.Width <= area.Width && size.Height <= area.Height);
    }

    [Fact]
    public void Petit_ecran_ne_deborde_jamais()
    {
        var area = new Rectangle(0, 0, 1024, 600);
        var size = MainForm.FitToScreen(area, 1.25);
        Assert.True(size.Width <= area.Width && size.Height <= area.Height, $"{size} deborde de {area.Size}");
    }
}
