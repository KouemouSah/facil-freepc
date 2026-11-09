using System.Net;
using FreeRes.Core;
using FreeRes.Core.Actions;
using FreeRes.Core.Engine;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace FreeRes.Host.Tests;

/// <summary>Module de test qui associe des identifiants a des chemins d'executables.</summary>
internal sealed class IconModule(IReadOnlyDictionary<string, string> paths) : IResourceModule, IIconSource
{
    public string Name => "outils";

    public string? IconPath(string id) => paths.GetValueOrDefault(id);

    public Task<ModuleEvaluation> EvaluateAsync(EngineContext context, CancellationToken cancellationToken) =>
        Task.FromResult(new ModuleEvaluation([], []));
}

internal sealed class FakeIconExtractor : IIconExtractor
{
    public static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public List<string> Calls { get; } = [];

    public byte[]? ExtractPng(string path, int size)
    {
        lock (Calls)
        {
            Calls.Add(path);
        }

        return path.EndsWith("sans-icone.exe", StringComparison.Ordinal) ? null : Png;
    }
}

public sealed class IconPathPolicyTests
{
    private static DriveType Fixed(string _) => DriveType.Fixed;

    [Theory]
    [InlineData(@"C:\Program Files\App\app.exe", @"C:\Program Files\App\app.exe")]
    [InlineData(@"""C:\App\app.exe""", @"C:\App\app.exe")]
    [InlineData(@"\\?\C:\App\app.exe", @"C:\App\app.exe")]
    [InlineData(@"\\serveur\partage\app.exe", null)]           // UNC : identifiants envoyes au serveur
    [InlineData(@"\\?\UNC\serveur\partage\app.exe", null)]
    [InlineData(@"C:\App\..\Windows\app.exe", null)]
    [InlineData(@"app.exe", null)]
    [InlineData(@"C:\App\script.bat", null)]
    [InlineData(@"C:\App\icone.dll", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Seuls_les_executables_locaux_absolus_sont_lus(string? path, string? expected) =>
        Assert.Equal(expected, IconPathPolicy.Normalize(path, Fixed));

    [Theory]
    [InlineData(DriveType.Network)]
    [InlineData(DriveType.Removable)]
    [InlineData(DriveType.CDRom)]
    [InlineData(DriveType.NoRootDirectory)]
    public void Lecteur_reseau_ou_amovible_refuse(DriveType type) =>
        Assert.Null(IconPathPolicy.Normalize(@"Z:\App\app.exe", _ => type));

    [Fact]
    public void Lecteur_systeme_reel_accepte() =>
        Assert.NotNull(IconPathPolicy.Normalize(Path.Combine(Environment.SystemDirectory, "notepad.exe")));
}

/// <summary>Route /api/icons (serveur reel en memoire).</summary>
public sealed class IconApiTests : IAsyncLifetime
{
    private const string Token = "jeton-icones-0123456789";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "freeres-icons-" + Guid.NewGuid().ToString("N"));
    private readonly FakeIconExtractor _extractor = new();
    private FreeResRuntime _runtime = null!;
    private LocalWebHost _host = null!;
    private TestServer _server = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        var app = Path.Combine(_dir, "app.exe");
        var none = Path.Combine(_dir, "sans-icone.exe");
        File.WriteAllBytes(app, [1]);
        File.WriteAllBytes(none, [1]);
        var paths = new Dictionary<string, string>
        {
            ["app"] = app,
            ["sans-icone"] = none,
            ["absent"] = Path.Combine(_dir, "absent.exe"),
            ["reseau"] = @"\\serveur\partage\app.exe",
        };
        _runtime = FreeResRuntime.Create(new AppPaths(Path.Combine(_dir, "data")), new StaticProbe(), _ => new ModuleSetup([new IconModule(paths)], _ => { }));
        _host = LocalWebHost.Create(new LocalWebHostOptions { Token = Token, Icons = _extractor }, _runtime, b => b.WebHost.UseTestServer());
        await _host.StartAsync();
        _server = (TestServer)_host.Services.GetRequiredService<IServer>();
    }

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        _runtime.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private async Task<HttpResponseMessage> Get(string path, bool cookie = true)
    {
        using var client = _server.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, new Uri("http://127.0.0.1" + path));
        if (cookie)
        {
            req.Headers.Add("Cookie", $"{LocalWebHostOptions.TokenCookieName}={Token}");
        }

        return await client.SendAsync(req);
    }

    [Fact]
    public async Task Icone_servie_en_png_puis_lue_depuis_le_cache()
    {
        using var first = await Get("/api/icons/outils/app");
        using var second = await Get("/api/icons/outils/app");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("image/png", first.Content.Headers.ContentType?.MediaType);
        Assert.Equal(FakeIconExtractor.Png, await first.Content.ReadAsByteArrayAsync());
        Assert.Contains("private", first.Headers.CacheControl?.ToString() ?? "", StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Single(_extractor.Calls);
    }

    [Theory]
    [InlineData("/api/icons/outils/inconnu")]
    [InlineData("/api/icons/autre/app")]
    [InlineData("/api/icons/outils/sans-icone")]
    [InlineData("/api/icons/outils/absent")]
    public async Task Sans_icone_204_sans_erreur(string path)
    {
        using var res = await Get(path);
        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
    }

    [Fact]
    public async Task Chemin_reseau_jamais_lu()
    {
        using var res = await Get("/api/icons/outils/reseau");
        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
        Assert.DoesNotContain(_extractor.Calls, c => c.StartsWith(@"\\", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("/api/icons/outils/APP")]
    [InlineData("/api/icons/Outils/app")]
    [InlineData("/api/icons/outils/a%2E%2E%5Cb")]
    public async Task Identifiant_invalide_400(string path)
    {
        using var res = await Get(path);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Sans_jeton_401()
    {
        using var res = await Get("/api/icons/outils/app", cookie: false);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.Empty(_extractor.Calls);
    }
}
