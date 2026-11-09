using System.Net;
using System.Text.Json;
using FreeRes.Core;
using FreeRes.Core.Engine;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace FreeRes.Host.Tests;

internal sealed class UpdateCheckModule : IResourceModule, IUpdateCheck
{
    public int Checks { get; private set; }

    public string Name => "updates";

    public Task<string> CheckNowAsync(CancellationToken cancellationToken)
    {
        Checks++;
        return Task.FromResult("Facil FreePC est à jour (version 0.6.0).");
    }

    public Task<ModuleEvaluation> EvaluateAsync(EngineContext context, CancellationToken cancellationToken) => Task.FromResult(ModuleEvaluation.Empty);
}

/// <summary>Route POST /api/updates/check : verification a la demande, protegee comme les decisions.</summary>
public sealed class UpdateApiTests : IAsyncLifetime
{
    private const string Token = "jeton-mises-a-jour-0123456789";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "freeres-upd-" + Guid.NewGuid().ToString("N"));
    private readonly UpdateCheckModule _module = new();
    private FreeResRuntime _runtime = null!;
    private LocalWebHost _host = null!;
    private TestServer _server = null!;

    public async Task InitializeAsync()
    {
        _runtime = FreeResRuntime.Create(new AppPaths(_dir), new StaticProbe(), _ => new ModuleSetup([_module], _ => { }));
        _host = LocalWebHost.Create(new LocalWebHostOptions { Token = Token }, _runtime, b => b.WebHost.UseTestServer());
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

    private async Task<HttpResponseMessage> Post(bool sameOrigin)
    {
        using var client = _server.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri("http://127.0.0.1/api/updates/check"));
        req.Headers.Add("Cookie", $"{LocalWebHostOptions.TokenCookieName}={Token}");
        req.Headers.Add("Origin", sameOrigin ? "http://127.0.0.1" : "https://site-malveillant.example");
        req.Headers.Add("Sec-Fetch-Site", sameOrigin ? "same-origin" : "cross-site");
        return await client.SendAsync(req);
    }

    [Fact]
    public async Task Verification_a_la_demande_renvoie_le_message()
    {
        using var res = await Post(sameOrigin: true);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("Facil FreePC est à jour (version 0.6.0).", body.GetProperty("message").GetString());
        Assert.Equal(1, _module.Checks);
    }

    [Fact]
    public async Task Une_page_tierce_ne_peut_pas_declencher_de_verification()
    {
        using var res = await Post(sameOrigin: false);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Equal(0, _module.Checks);
    }
}
