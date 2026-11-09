using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FreeRes.Core.Actions;
using FreeRes.Core.Engine;
using FreeRes.Core.Platform;
using FreeRes.Core.Policy;
using FreeRes.Modules.Updates;

namespace FreeRes.Modules.Tests;

/// <summary>Source simulee : manifeste et signature fournis par le test, installeur ecrit sur disque.</summary>
internal sealed class FakeUpdateSource : IUpdateSource
{
    public (byte[] Manifest, string Signature)? Published { get; set; }

    public byte[] Installer { get; set; } = [1, 2, 3];

    public int Fetches { get; private set; }

    public List<Uri> Downloads { get; } = [];

    public Task<(byte[] Manifest, string Signature)?> FetchManifestAsync(CancellationToken cancellationToken)
    {
        Fetches++;
        return Task.FromResult(Published);
    }

    public Task DownloadAsync(Uri url, string destination, CancellationToken cancellationToken)
    {
        Downloads.Add(url);
        File.WriteAllBytes(destination, Installer);
        return Task.CompletedTask;
    }
}

/// <summary>Lanceur qui tente de REMPLACER l'installeur au moment du lancement (doit echouer : fichier verrouille).</summary>
internal sealed class SwappingLauncher : IProcessLauncher
{
    public List<(string File, IReadOnlyList<string> Args)> Launches { get; } = [];

    public bool SwapBlocked { get; private set; }

    public bool Launch(string fileName, IReadOnlyList<string> arguments)
    {
        Launches.Add((fileName, arguments));
        try
        {
            File.WriteAllBytes(fileName, [6, 6, 6]);
        }
        catch (IOException)
        {
            SwapBlocked = true;
        }

        return true;
    }
}

public sealed class UpdatesModuleTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeTime _time = new(Fixtures.Start);
    private readonly FakeUpdateSource _source = new();
    private readonly SwappingLauncher _launcher = new();
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly string _publicKey;

    public UpdatesModuleTests() => _publicKey = Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo());

    public void Dispose()
    {
        _key.Dispose();
        _dir.Dispose();
    }

    private static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private byte[] Json(string version, string url = "https://github.com/KouemouSah/facil-freepc/releases/download/v9/FacilFreePC-Setup.exe", string? sha = null) =>
        JsonSerializer.SerializeToUtf8Bytes(new UpdateManifest(version, url, sha ?? Sha(_source.Installer), "Corrections."), UpdateVerifier.JsonOptions);

    private string Sign(byte[] data, ECDsa? key = null) =>
        Convert.ToBase64String((key ?? _key).SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

    private void Publish(string version, string? url = null, string? sha = null)
    {
        var json = url is null ? Json(version, sha: sha) : Json(version, url, sha);
        _source.Published = (json, Sign(json));
    }

    private UpdatesModule Module(bool weekly = true, string current = "0.6.0") =>
        new(Fixtures.Services(_dir, _time) with { Launcher = _launcher, Settings = new AppSettings { Updates = new UpdateSettings(weekly) } }, _source, _publicKey, current);

    private static ActionCatalog Catalog(UpdatesModule m)
    {
        var c = new ActionCatalog();
        m.RegisterActions(c);
        return c;
    }

    private static ActionRequest Install(string version) => new(UpdatesModule.InstallUpdate, new Dictionary<string, string> { ["version"] = version });

    [Fact]
    public async Task Version_superieure_signee_devient_une_decision_jamais_appliquee_seule()
    {
        Publish("0.7.0");
        var m = Module();

        var eval = await m.EvaluateAsync(Fixtures.Context(_time), default);

        var p = Assert.Single(eval.Proposals);
        Assert.Equal("update-0-7-0", p.Id);
        Assert.Contains("0.7.0", p.Title, StringComparison.Ordinal);
        Assert.Contains("Corrections.", p.Detail, StringComparison.Ordinal);
        Assert.Empty(eval.AutoActions);
        Assert.Equal("0.7.0", ((UpdateStatus)m.CurrentStatus!).Available);
    }

    [Theory]
    [InlineData("0.6.0")]
    [InlineData("0.5.9")]
    public async Task Version_egale_ou_inferieure_rien_a_proposer(string published)
    {
        Publish(published);
        var m = Module();
        Assert.Empty((await m.EvaluateAsync(Fixtures.Context(_time), default)).Proposals);
        Assert.Equal("Facil FreePC est à jour (version 0.6.0).", await m.CheckNowAsync(default));
    }

    [Fact]
    public async Task Manifeste_modifie_apres_signature_ou_autre_cle_refuse()
    {
        var json = Json("0.7.0");
        var signature = Sign(json);
        json[^2] ^= 0x01;   // un octet change (dans les notes)
        _source.Published = (json, signature);
        var m = Module();
        Assert.Empty((await m.EvaluateAsync(Fixtures.Context(_time), default)).Proposals);
        Assert.Contains("signature invalide", ((UpdateStatus)m.CurrentStatus!).LastError, StringComparison.Ordinal);

        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var good = Json("0.7.0");
        _source.Published = (good, Sign(good, other));
        Assert.StartsWith("Mise à jour refusée : signature invalide", await m.CheckNowAsync(default), StringComparison.Ordinal);
        Assert.Throws<ActionRejectedException>(() => Catalog(m).Resolve(Install("0.7.0")));
    }

    [Theory]
    [InlineData("http://github.com/x/FacilFreePC-Setup.exe")]          // pas HTTPS
    [InlineData("https://evil.example/FacilFreePC-Setup.exe")]          // hote non autorise
    [InlineData("https://github.com.evil.example/Setup.exe")]
    [InlineData("https://user@github.com/x/Setup.exe")]                  // identifiants
    [InlineData("https://github.com:8443/x/Setup.exe")]                  // port
    [InlineData("https://github.com/x/Setup.msi")]                       // pas un .exe
    public async Task Adresse_d_installeur_non_autorisee_refusee_meme_signee(string url)
    {
        Publish("0.7.0", url);
        var m = Module();
        Assert.Empty((await m.EvaluateAsync(Fixtures.Context(_time), default)).Proposals);
        Assert.Contains("non autorisee", ((UpdateStatus)m.CurrentStatus!).LastError, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1.0", "version invalide")]
    [InlineData("1.0.0-beta", "version invalide")]
    public async Task Version_mal_formee_refusee(string version, string reason)
    {
        Publish(version);
        var m = Module();
        await m.EvaluateAsync(Fixtures.Context(_time), default);
        Assert.Contains(reason, ((UpdateStatus)m.CurrentStatus!).LastError, StringComparison.Ordinal);
    }

    [Fact]
    public void Signature_et_empreinte_mal_formees_refusees()
    {
        var json = Json("0.7.0", sha: "ABC");
        Assert.Throws<UpdateRejectedException>(() => UpdateVerifier.Verify(json, Sign(json), _publicKey));
        Assert.Throws<UpdateRejectedException>(() => UpdateVerifier.Verify(Json("0.7.0"), "pas-du-base64!", _publicKey));
        Assert.Throws<UpdateRejectedException>(() => UpdateVerifier.Verify(new byte[UpdateVerifier.MaxManifestBytes + 1], "AA==", _publicKey));
    }

    [Fact]
    public async Task Verification_hebdomadaire_une_fois_par_semaine_et_desactivable()
    {
        Publish("0.7.0");
        var m = Module();
        await m.EvaluateAsync(Fixtures.Context(_time), default);
        _time.Advance(TimeSpan.FromDays(6));
        await m.EvaluateAsync(Fixtures.Context(_time), default);
        Assert.Equal(1, _source.Fetches);
        _time.Advance(TimeSpan.FromDays(1));
        await m.EvaluateAsync(Fixtures.Context(_time), default);
        Assert.Equal(2, _source.Fetches);

        var off = Module(weekly: false);
        var before = _source.Fetches;
        _time.Advance(TimeSpan.FromDays(30));
        Assert.Empty((await off.EvaluateAsync(Fixtures.Context(_time), default)).Proposals);
        Assert.Equal(before, _source.Fetches);   // desactivee : aucune requete
        Assert.Contains("0.7.0", await off.CheckNowAsync(default), StringComparison.Ordinal);   // le bouton verifie quand meme
        Assert.Equal(before + 1, _source.Fetches);
    }

    [Fact]
    public async Task Hors_ligne_la_version_verifiee_reste_proposee()
    {
        Publish("0.7.0");
        var m = Module();
        await m.CheckNowAsync(default);
        _source.Published = null;
        Assert.StartsWith("Vérification impossible", await m.CheckNowAsync(default), StringComparison.Ordinal);
        Assert.Single((await m.EvaluateAsync(Fixtures.Context(_time), default)).Proposals);
    }

    [Fact]
    public async Task Installation_empreinte_verifiee_fichier_verrouille_jusqu_au_lancement()
    {
        Publish("0.7.0");
        var m = Module();
        await m.CheckNowAsync(default);

        var r = await Catalog(m).Resolve(Install("0.7.0")).Execute(Install("0.7.0"), false, default);

        Assert.True(r.Ok, r.Message);
        var (file, args) = Assert.Single(_launcher.Launches);
        Assert.Equal(Path.Combine(_dir.Sub("data"), "updates", "FacilFreePC-Setup-0.7.0.exe"), file);
        Assert.Equal(["/SILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/RELAUNCH=1"], args);
        Assert.True(_launcher.SwapBlocked, "l'installeur a pu etre remplace entre la verification et le lancement");
        Assert.Equal(_source.Installer, File.ReadAllBytes(file));
    }

    [Fact]
    public async Task Installeur_different_de_l_empreinte_signee_refuse_et_supprime()
    {
        Publish("0.7.0", sha: Sha([9, 9, 9]));
        var m = Module();
        await m.CheckNowAsync(default);

        var r = await Catalog(m).Resolve(Install("0.7.0")).Execute(Install("0.7.0"), false, default);

        Assert.False(r.Ok);
        Assert.Contains("empreinte", r.Message, StringComparison.Ordinal);
        Assert.Empty(_launcher.Launches);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_dir.Sub("data"), "updates")));
    }

    [Fact]
    public async Task Version_demandee_differente_de_la_version_verifiee_refusee_et_simulation_sans_telechargement()
    {
        Publish("0.7.0");
        var m = Module();
        await m.CheckNowAsync(default);
        var catalog = Catalog(m);

        Assert.Throws<ActionRejectedException>(() => catalog.Resolve(Install("0.8.0")));
        var dry = await catalog.Resolve(Install("0.7.0")).Execute(Install("0.7.0"), true, default);
        Assert.True(dry.Ok);
        Assert.Empty(_source.Downloads);
        Assert.Empty(_launcher.Launches);
    }

    [Fact]
    public void Cle_publique_de_l_application_valide()
    {
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(UpdateKeys.PublicKey), out _);
        Assert.Equal(256, key.KeySize);
        Assert.Equal(Encoding.ASCII.GetBytes("https://github.com/KouemouSah/facil-freepc/releases/latest/download/latest.json"), Encoding.ASCII.GetBytes(HttpUpdateSource.ManifestUrl));
    }
}
