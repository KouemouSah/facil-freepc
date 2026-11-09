using FreeRes.Modules.Disk;

namespace FreeRes.Modules.Tests;

/// <summary>Parite Pester « Securite des chemins et nettoyage » (8 cas) + suppression de dossier contenant une jonction.</summary>
public sealed class SafeCleanerTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeTime _time = new(DateTimeOffset.Now);
    private readonly SafeCleaner _cleaner;

    public SafeCleanerTests() => _cleaner = new SafeCleaner([_dir.Sub("zone")], _time);

    [Fact]
    public void Accepte_la_racine_autorisee_et_ses_sous_dossiers()
    {
        Assert.True(_cleaner.IsSafe(_dir.Sub("zone")));
        Assert.True(_cleaner.IsSafe(_dir.Sub(@"zone\a\b")));
    }

    [Theory]
    [InlineData(@"C:\")]
    [InlineData("C:")]
    [InlineData(@"C:\Windows\System32")]
    [InlineData(@"C:\Users")]
    public void Refuse_chemins_systeme_et_racines_de_lecteur(string path) => Assert.False(_cleaner.IsSafe(path));

    [Fact]
    public void Refuse_la_traversee_et_un_prefixe_trompeur()
    {
        Assert.False(_cleaner.IsSafe(_dir.Sub(@"zone\..\autre")));
        Assert.False(_cleaner.IsSafe(_dir.Sub("zone-voisine")));   // « zone » est un prefixe, pas un parent
    }

    [Fact]
    public void Refuse_un_nettoyage_hors_liste_blanche() =>
        Assert.Throws<InvalidOperationException>(() => _cleaner.RemoveOlderThan(_dir.Sub("ailleurs"), 7));

    [Fact]
    public void Supprime_les_vieux_fichiers_garde_les_recents_et_simule_sans_rien_toucher()
    {
        var old = Fixtures.AgedFile(_dir.Sub(@"zone\vieux.tmp"), 30, now: _time.Now);
        var recent = Fixtures.AgedFile(_dir.Sub(@"zone\recent.tmp"), 1, now: _time.Now);

        var sim = _cleaner.RemoveOlderThan(_dir.Sub("zone"), 7, dryRun: true);
        Assert.Equal(1, sim.Files);
        Assert.Equal(4096, sim.FreedBytes);
        Assert.True(File.Exists(old));

        var real = _cleaner.RemoveOlderThan(_dir.Sub("zone"), 7);
        Assert.Equal(1, real.Files);
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
    }

    [Fact]
    public void Fichier_extrait_recemment_avec_une_vieille_date_de_modification_conserve()
    {
        var f = Fixtures.AgedFile(_dir.Sub(@"zone\extrait.dat"), 60, now: _time.Now);
        File.SetCreationTime(f, _time.Now.LocalDateTime);   // cree aujourd'hui (extraction), modifie il y a 60 jours
        _cleaner.RemoveOlderThan(_dir.Sub("zone"), 7);
        Assert.True(File.Exists(f));
    }

    [Fact]
    public void Mode_motif_ne_supprime_que_les_dossiers_vieux_ET_conformes()
    {
        foreach (var name in new[] { "abcd1234.tmp", "efgh5678.xyz", "pas-conforme" })
        {
            Directory.CreateDirectory(_dir.Sub($@"zone\{name}"));
            Fixtures.AgedFile(_dir.Sub($@"zone\{name}\f.bin"), 5, now: _time.Now);
        }

        Fixtures.Age(_dir.Sub(@"zone\abcd1234.tmp"), 5, _time.Now);
        Fixtures.Age(_dir.Sub(@"zone\pas-conforme"), 5, _time.Now);
        // efgh5678.xyz reste recent (date du dossier = maintenant)

        var r = _cleaner.RemoveOlderThan(_dir.Sub("zone"), 1, @"^[a-z0-9]{8}\.[a-z0-9]{3}$");
        Assert.Equal(1, r.Dirs);
        Assert.False(Directory.Exists(_dir.Sub(@"zone\abcd1234.tmp")));
        Assert.True(Directory.Exists(_dir.Sub(@"zone\efgh5678.xyz")));
        Assert.True(Directory.Exists(_dir.Sub(@"zone\pas-conforme")));
    }

    [Fact]
    public void Simulation_un_dossier_deja_attribue_n_est_pas_recompte()
    {
        Directory.CreateDirectory(_dir.Sub(@"zone\abcd1234.tmp"));
        Fixtures.AgedFile(_dir.Sub(@"zone\abcd1234.tmp\f.bin"), 5, now: _time.Now);
        Fixtures.Age(_dir.Sub(@"zone\abcd1234.tmp"), 5, _time.Now);
        var first = _cleaner.RemoveOlderThan(_dir.Sub("zone"), 1, @"^[a-z0-9]{8}\.[a-z0-9]{3}$", dryRun: true);
        var second = _cleaner.RemoveOlderThan(_dir.Sub("zone"), 1, dryRun: true, excludeDirs: first.MatchedDirs.ToHashSet(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(1, first.Files);
        Assert.Equal(0, second.Files);
    }

    [Fact]
    public void Ne_suit_jamais_une_jonction_les_donnees_hors_perimetre_sont_protegees()
    {
        var precieux = Fixtures.AgedFile(_dir.Sub(@"precieux\donnee.db"), 90, now: _time.Now);
        Directory.CreateDirectory(_dir.Sub("zone"));
        Fixtures.Junction(_dir.Sub(@"zone\lien"), _dir.Sub("precieux"));

        _cleaner.RemoveOlderThan(_dir.Sub("zone"), 7);
        Assert.True(File.Exists(precieux));
    }

    [Fact]
    public void Dossier_supprime_par_motif_contenant_une_jonction_la_cible_reste_intacte()
    {
        var precieux = Fixtures.AgedFile(_dir.Sub(@"precieux\donnee.db"), 90, now: _time.Now);
        Directory.CreateDirectory(_dir.Sub(@"zone\abcd1234.tmp"));
        Fixtures.Junction(_dir.Sub(@"zone\abcd1234.tmp\lien"), _dir.Sub("precieux"));
        Fixtures.Age(_dir.Sub(@"zone\abcd1234.tmp"), 5, _time.Now);

        var r = _cleaner.RemoveOlderThan(_dir.Sub("zone"), 1, @"^[a-z0-9]{8}\.[a-z0-9]{3}$");
        Assert.Equal(1, r.Dirs);
        Assert.False(Directory.Exists(_dir.Sub(@"zone\abcd1234.tmp")));
        Assert.True(File.Exists(precieux));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3651)]
    public void Duree_hors_bornes_refusee(int days) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => _cleaner.RemoveOlderThan(_dir.Sub("zone"), days));

    public void Dispose() => _dir.Dispose();
}
