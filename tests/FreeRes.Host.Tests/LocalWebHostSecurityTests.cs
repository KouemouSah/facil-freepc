using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace FreeRes.Host.Tests;

/// <summary>
/// Barrieres de securite de l'interface locale, verifiees par de vraies requetes HTTP sur un serveur de test.
/// Chaque test attend un code precis : un test ne doit pas pouvoir reussir pour une autre raison.
/// </summary>
public sealed class LocalWebHostSecurityTests : IAsyncLifetime
{
    private const string Token = "jeton-de-test-0123456789";
    private LocalWebHost _host = null!;
    private TestServer _server = null!;

    public async Task InitializeAsync()
    {
        _host = LocalWebHost.Create(new LocalWebHostOptions { Token = Token }, b => b.WebHost.UseTestServer());
        await _host.StartAsync();
        _server = (TestServer)_host.Services.GetRequiredService<IServer>();
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private static HttpRequestMessage Request(HttpMethod method, string path, string? cookie = Token, string host = "127.0.0.1")
    {
        var req = new HttpRequestMessage(method, new Uri($"http://{host}{path}"));
        if (cookie is not null)
        {
            req.Headers.Add("Cookie", $"{LocalWebHostOptions.TokenCookieName}={cookie}");
        }

        return req;
    }

    private async Task<HttpResponseMessage> Send(HttpRequestMessage req)
    {
        using var client = _server.CreateClient();
        return await client.SendAsync(req);
    }

    [Fact]
    public async Task Health_avec_jeton_valide_repond_identite()
    {
        using var res = await Send(Request(HttpMethod.Get, "/api/health"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var h = await res.Content.ReadFromJsonAsync<HealthResponse>();
        Assert.NotNull(h);
        Assert.Equal("Facil FreePC", h.Name);
        Assert.Equal(Environment.MachineName, h.Machine);
        Assert.Matches(@"^\d+\.\d+\.\d+", h.Version);
    }

    [Fact]
    public async Task Sans_cookie_refuse_401()
    {
        using var res = await Send(Request(HttpMethod.Get, "/api/health", cookie: null));
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Mauvais_jeton_refuse_401()
    {
        using var res = await Send(Request(HttpMethod.Get, "/api/health", cookie: "faux"));
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Page_d_accueil_protegee_par_le_jeton()
    {
        using var res = await Send(Request(HttpMethod.Get, "/", cookie: null));
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Theory]
    [InlineData("attaquant.example")]
    [InlineData("localhost")]
    [InlineData("127.0.0.2")]
    public async Task Hote_etranger_refuse_meme_avec_jeton_dns_rebinding(string host)
    {
        using var res = await Send(Request(HttpMethod.Get, "/api/health", host: host));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Post_depuis_une_autre_origine_refuse_403()
    {
        var req = Request(HttpMethod.Post, "/api/health");
        req.Headers.Add("Origin", "https://site-malveillant.example");
        using var res = await Send(req);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Post_sans_origine_refuse_403()
    {
        using var res = await Send(Request(HttpMethod.Post, "/api/health"));
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Post_inter_sites_refuse_meme_avec_origine_falsifiee()
    {
        var req = Request(HttpMethod.Post, "/api/health");
        req.Headers.Add("Origin", "http://127.0.0.1");
        req.Headers.Add("Sec-Fetch-Site", "cross-site");
        using var res = await Send(req);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Post_de_meme_origine_franchit_la_securite()
    {
        // /api/health n'accepte que GET : 405 prouve que la requete a passe les 3 barrieres.
        var req = Request(HttpMethod.Post, "/api/health");
        req.Headers.Add("Origin", "http://127.0.0.1");
        req.Headers.Add("Sec-Fetch-Site", "same-origin");
        using var res = await Send(req);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, res.StatusCode);
    }

    [Fact]
    public async Task En_tetes_de_securite_presents()
    {
        using var res = await Send(Request(HttpMethod.Get, "/api/health"));
        Assert.Contains("default-src 'self'", res.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
        Assert.Equal("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", res.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("no-referrer", res.Headers.GetValues("Referrer-Policy").Single());
    }

    [Fact]
    public async Task Interface_embarquee_servie()
    {
        using var res = await Send(Request(HttpMethod.Get, "/"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("text/html", res.Content.Headers.ContentType?.MediaType);
        Assert.Contains("<title>Facil FreePC</title>", await res.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var js = await Send(Request(HttpMethod.Get, "/app.js"));
        Assert.Equal(HttpStatusCode.OK, js.StatusCode);
        using var logo = await Send(Request(HttpMethod.Get, "/logo.png"));
        Assert.Equal(HttpStatusCode.OK, logo.StatusCode);
        Assert.Equal("image/png", logo.Content.Headers.ContentType?.MediaType);
    }
}
