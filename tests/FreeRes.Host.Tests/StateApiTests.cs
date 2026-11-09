using System.Net;
using System.Text.Json;
using FreeRes.Core;
using FreeRes.Core.Actions;
using FreeRes.Core.Decisions;
using FreeRes.Core.Engine;
using FreeRes.Core.Metrics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace FreeRes.Host.Tests;

/// <summary>Mesures fixes pour l'API (machine de reference).</summary>
internal sealed class StaticProbe : ISystemProbe
{
    public double RamAvailGB { get; set; } = 6.0;

    public SystemSnapshot Capture() => new(
        DateTimeOffset.Now, 15.69, RamAvailGB, 60, 23.69, 11.8, 49.8, "C:", 235.48, 43.9, 1, false,
        [new ProcessUsage("firefox", 30, 4.2)]);
}

internal sealed class OneProposalModule : IResourceModule
{
    public string Name => "test";

    public Task<ModuleEvaluation> EvaluateAsync(EngineContext context, CancellationToken cancellationToken) =>
        Task.FromResult(new ModuleEvaluation(
            [new Proposal("test-cache", "Vider le cache de test", "Détail <b>non interprete</b>", 1.25, Severity.Warn, ActionRequest.Of("TestNoop"))],
            []));
}

public sealed class StateApiTests : IAsyncLifetime
{
    private const string Token = "jeton-etat-0123456789";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "freeres-host-" + Guid.NewGuid().ToString("N"));
    private readonly StaticProbe _probe = new();
    private FreeResRuntime _runtime = null!;
    private LocalWebHost _host = null!;
    private TestServer _server = null!;

    public async Task InitializeAsync()
    {
        _runtime = FreeResRuntime.Create(
            new AppPaths(_dir),
            _probe,
            _ => new ModuleSetup(
                [new OneProposalModule()],
                c => c.Register(new ActionDefinition("TestNoop", false, _ => null, (_, _, _) => Task.FromResult(new ActionOutcome(true, "ok"))))));
        _host = LocalWebHost.Create(new LocalWebHostOptions { Token = Token }, _runtime, b => b.WebHost.UseTestServer());
        await _host.StartAsync();
        _server = (TestServer)_host.Services.GetRequiredService<IServer>();

        // La boucle de surveillance lance un premier cycle au demarrage : on l'attend (5 s max).
        for (var i = 0; i < 50 && _runtime.Engine.LastReport is null; i++)
        {
            await Task.Delay(100);
        }
    }

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        _runtime.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private async Task<HttpResponseMessage> Get(string path, bool withCookie = true)
    {
        using var client = _server.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, new Uri("http://127.0.0.1" + path));
        if (withCookie)
        {
            req.Headers.Add("Cookie", $"{LocalWebHostOptions.TokenCookieName}={Token}");
        }

        return await client.SendAsync(req);
    }

    [Fact]
    public async Task Etat_complet_du_moteur_apres_le_premier_cycle()
    {
        Assert.NotNull(_runtime.Engine.LastReport);
        using var res = await Get("/api/state");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        Assert.Equal(3.0, root.GetProperty("thresholds").GetProperty("ramAvailWarnGB").GetDouble());
        Assert.Equal(15.69, root.GetProperty("profile").GetProperty("ramTotalGB").GetDouble());
        Assert.False(root.GetProperty("critical").GetBoolean());
        Assert.Equal("firefox", root.GetProperty("snapshot").GetProperty("topProcesses")[0].GetProperty("name").GetString());

        var pending = root.GetProperty("pending")[0];
        Assert.Equal("test-cache", pending.GetProperty("id").GetString());
        Assert.Equal("Warn", pending.GetProperty("severity").GetString());          // enum en texte, pas en nombre
        Assert.Equal("Détail <b>non interprete</b>", pending.GetProperty("detail").GetString());
        Assert.Equal("Toast", root.GetProperty("lastCycle").GetProperty("escalation").GetString());
    }

    [Fact]
    public async Task Etat_critique_signale()
    {
        _probe.RamAvailGB = 0.5;
        using var res = await Get("/api/state");
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("critical").GetBoolean());
    }

    [Fact]
    public async Task Etat_protege_par_le_jeton()
    {
        using var res = await Get("/api/state", withCookie: false);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }
}

public sealed class StateApiWithoutEngineTests
{
    [Fact]
    public async Task Sans_moteur_repond_503()
    {
        await using var host = LocalWebHost.Create(new LocalWebHostOptions { Token = "t-0123456789" }, configure: b => b.WebHost.UseTestServer());
        await host.StartAsync();
        var server = (TestServer)host.Services.GetRequiredService<IServer>();
        using var client = server.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, new Uri("http://127.0.0.1/api/state"));
        req.Headers.Add("Cookie", $"{LocalWebHostOptions.TokenCookieName}=t-0123456789");
        using var res = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
    }
}
