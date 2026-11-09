using FreeRes.Core;
using FreeRes.Core.Actions;

namespace FreeRes.App.Tests;

internal sealed class FixedElevation(bool admin) : IElevation
{
    public bool IsAdministrator => admin;
}

/// <summary>
/// Mode eleve (« --run-action ») : il n'execute qu'une action ADMIN de la liste fermee et n'ecrit son resultat
/// que dans le dossier impose. Dossier de donnees isole par FREERES_DATA_DIR (jamais le vrai profil).
/// Aucune action admin reelle n'est executee ici (refus avant execution).
/// </summary>
[Collection(nameof(ElevatedModeTests))]
public sealed class ElevatedModeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "freeres-elev-" + Guid.NewGuid().ToString("N"));
    private readonly string? _previous = Environment.GetEnvironmentVariable(AppPaths.DataDirOverrideVariable);

    public ElevatedModeTests() => Environment.SetEnvironmentVariable(AppPaths.DataDirOverrideVariable, _dir);

    [Fact]
    public void Analyse_stricte_des_arguments()
    {
        Assert.True(ElevatedMode.TryParse(["--run-action", "abc", "--result", "r.json"], out var enc, out var res));
        Assert.Equal("abc", enc);
        Assert.Equal("r.json", res);
        Assert.False(ElevatedMode.TryParse(["--run-action", "abc"], out _, out _));
        Assert.False(ElevatedMode.TryParse(["--background"], out _, out _));
        Assert.False(ElevatedMode.TryParse(["--result", "r.json", "--run-action", "abc"], out _, out _));
    }

    [Fact]
    public void Chemin_de_resultat_non_conforme_rien_n_est_ecrit()
    {
        var outside = Path.Combine(_dir, "ailleurs.json");
        Assert.Equal(2, ElevatedMode.Run(ElevatedActionProtocol.Encode(ActionRequest.Of("CleanSystemZones")), outside, new FixedElevation(true)));
        Assert.False(File.Exists(outside));
    }

    [Fact]
    public void Processus_non_eleve_resultat_en_echec()
    {
        var path = ElevatedActionProtocol.NewResultPath(AppPaths.Default());
        Assert.Equal(1, ElevatedMode.Run(ElevatedActionProtocol.Encode(ActionRequest.Of("CleanSystemZones")), path, new FixedElevation(false)));
        Assert.Equal("Processus non eleve", ElevatedActionProtocol.ReadResult(path)!.Message);
    }

    [Theory]
    [InlineData("EmptyRecycleBin", "Action non administrateur refusee")]   // moindre privilege
    [InlineData("RunScript", "inconnue")]
    public void Action_non_admin_ou_inconnue_refusee_en_mode_eleve(string type, string expected)
    {
        var path = ElevatedActionProtocol.NewResultPath(AppPaths.Default());
        Assert.Equal(1, ElevatedMode.Run(ElevatedActionProtocol.Encode(ActionRequest.Of(type)), path, new FixedElevation(true)));
        var r = ElevatedActionProtocol.ReadResult(path)!;
        Assert.False(r.Ok);
        Assert.Contains(expected, r.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Requete_illisible_signalee()
    {
        var path = ElevatedActionProtocol.NewResultPath(AppPaths.Default());
        Assert.Equal(1, ElevatedMode.Run("!!!", path, new FixedElevation(true)));
        Assert.Equal("Requete d'action illisible", ElevatedActionProtocol.ReadResult(path)!.Message);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AppPaths.DataDirOverrideVariable, _previous);
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
