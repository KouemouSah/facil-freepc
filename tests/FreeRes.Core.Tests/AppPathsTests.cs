namespace FreeRes.Core.Tests;

public sealed class AppPathsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "freeres-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Dossiers_derives_du_dossier_de_donnees()
    {
        var p = new AppPaths(_root);
        Assert.Equal(Path.GetFullPath(_root), p.DataDirectory);
        Assert.Equal(Path.Combine(p.DataDirectory, "logs"), p.LogDirectory);
        Assert.Equal(Path.Combine(p.DataDirectory, "WebView2"), p.WebViewDirectory);
    }

    [Fact]
    public void EnsureCreated_cree_les_dossiers_et_est_idempotent()
    {
        var p = new AppPaths(_root);
        p.EnsureCreated();
        p.EnsureCreated();
        Assert.True(Directory.Exists(p.LogDirectory));
        Assert.True(Directory.Exists(p.WebViewDirectory));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Refuse_un_dossier_vide(string dir) => Assert.Throws<ArgumentException>(() => new AppPaths(dir));

    [Fact]
    public void Defaut_sous_LocalAppData_sauf_surcharge()
    {
        var previous = Environment.GetEnvironmentVariable(AppPaths.DataDirOverrideVariable);
        try
        {
            Environment.SetEnvironmentVariable(AppPaths.DataDirOverrideVariable, null);
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            Assert.Equal(Path.Combine(local, "FacilFreePC"), AppPaths.Default().DataDirectory);

            Environment.SetEnvironmentVariable(AppPaths.DataDirOverrideVariable, _root);
            Assert.Equal(Path.GetFullPath(_root), AppPaths.Default().DataDirectory);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppPaths.DataDirOverrideVariable, previous);
        }
    }

    [Fact]
    public void Version_semantique_sans_suffixe_de_commit()
    {
        Assert.Matches(@"^\d+\.\d+\.\d+$", AppInfo.Version);
        Assert.Equal("Facil FreePC", AppInfo.Name);
        Assert.Equal("Emac SAH", AppInfo.Company);
        Assert.Equal("Copyright (c) 2026 EMAC SAH AI Solutions Architect", AppInfo.Copyright);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
