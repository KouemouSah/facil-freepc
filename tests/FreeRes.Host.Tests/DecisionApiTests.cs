using System.Net;
using System.Text.Json;
using FreeRes.Core;
using FreeRes.Core.Actions;
using FreeRes.Core.Decisions;
using FreeRes.Core.Engine;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace FreeRes.Host.Tests;

/// <summary>Module de test : trois propositions, une action automatique, un etat expose.</summary>
internal sealed class DecisionModule : IResourceModule, IStatusSource
{
    public string Name => "essai";

    public object? CurrentStatus => new { navigateurs = new[] { "Firefox <script>" } };

    public Task<ModuleEvaluation> EvaluateAsync(EngineContext context, CancellationToken cancellationToken) =>
        Task.FromResult(new ModuleEvaluation(
            [
                new Proposal("essai-appliquer", "Liberer le cache", "Detail", 1.5, Severity.Warn, ActionRequest.Of("TestCount")),
                new Proposal("essai-reporter", "Reporter ceci", "Detail", 0, Severity.Info, ActionRequest.Of("TestCount")),
                new Proposal("essai-ignorer", "Ignorer ceci", "Detail", 0, Severity.Info, ActionRequest.Of("TestCount")),
            ],
            [ActionRequest.Of("TestAuto"), ActionRequest.Of("TestAutoNoop")]));
}

/// <summary>Routes de decision, d'analyse, d'etat des modules et d'historique (serveur reel en memoire).</summary>
public sealed class DecisionApiTests : IAsyncLifetime
{
    private const string Token = "jeton-decisions-0123456789";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "freeres-host-" + Guid.NewGuid().ToString("N"));
    private int _executions;
    private FreeResRuntime _runtime = null!;
    private LocalWebHost _host = null!;
    private TestServer _server = null!;

    public async Task InitializeAsync()
    {
        _runtime = FreeResRuntime.Create(
            new AppPaths(_dir),
            new StaticProbe(),
            _ => new ModuleSetup(
                [new DecisionModule()],
                c =>
                {
                    c.Register(new ActionDefinition("TestCount", false, _ => null, (_, _, _) =>
                    {
                        Interlocked.Increment(ref _executions);
                        return Task.FromResult(new ActionOutcome(true, "cache libere"));
                    }));
                    c.Register(new ActionDefinition("TestAuto", false, _ => null, (_, _, _) => Task.FromResult(new ActionOutcome(true, "machine virtuelle arrêtée"))));
                    // Action automatique sans effet : journalisee, mais absente de « Fait automatiquement ».
                    c.Register(new ActionDefinition("TestAutoNoop", false, _ => null, (_, _, _) => Task.FromResult(new ActionOutcome(true, "zone déjà propre", Changed: false))));
                }));
        _host = LocalWebHost.Create(new LocalWebHostOptions { Token = Token }, _runtime, b => b.WebHost.UseTestServer());
        await _host.StartAsync();
        _server = (TestServer)_host.Services.GetRequiredService<IServer>();
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

    private async Task<HttpResponseMessage> Send(HttpMethod method, string path, bool cookie = true, bool sameOrigin = true)
    {
        using var client = _server.CreateClient();
        using var req = new HttpRequestMessage(method, new Uri("http://127.0.0.1" + path));
        if (cookie)
        {
            req.Headers.Add("Cookie", $"{LocalWebHostOptions.TokenCookieName}={Token}");
        }

        if (sameOrigin)
        {
            req.Headers.Add("Origin", "http://127.0.0.1");
            req.Headers.Add("Sec-Fetch-Site", "same-origin");
        }

        return await client.SendAsync(req);
    }

    private async Task<JsonElement> Json(HttpMethod method, string path)
    {
        using var res = await Send(method, path);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private async Task<string[]> PendingIds() =>
        [.. (await Json(HttpMethod.Get, "/api/state")).GetProperty("pending").EnumerateArray().Select(p => p.GetProperty("id").GetString()!)];

    [Fact]
    public async Task Appliquer_execute_l_action_et_retire_la_decision()
    {
        var r = await Json(HttpMethod.Post, "/api/proposals/essai-appliquer/approve");

        Assert.True(r.GetProperty("ok").GetBoolean());
        Assert.Equal("cache libere", r.GetProperty("message").GetString());
        Assert.False(r.GetProperty("stillPending").GetBoolean());
        Assert.Equal(1, _executions);
        Assert.DoesNotContain("essai-appliquer", await PendingIds());

        using var again = await Send(HttpMethod.Post, "/api/proposals/essai-appliquer/approve");
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        Assert.Equal(1, _executions);
    }

    [Fact]
    public async Task Double_clic_simultane_une_seule_execution()
    {
        var a = Send(HttpMethod.Post, "/api/proposals/essai-appliquer/approve");
        var b = Send(HttpMethod.Post, "/api/proposals/essai-appliquer/approve");
        using var ra = await a;
        using var rb = await b;

        Assert.Equal(1, _executions);
        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.NotFound], new[] { ra.StatusCode, rb.StatusCode }.Order());   // 200 < 404
    }

    [Fact]
    public async Task Plus_tard_et_ignorer_retirent_sans_executer()
    {
        using var snooze = await Send(HttpMethod.Post, "/api/proposals/essai-reporter/snooze");
        using var reject = await Send(HttpMethod.Post, "/api/proposals/essai-ignorer/reject");

        Assert.Equal(HttpStatusCode.NoContent, snooze.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, reject.StatusCode);
        Assert.Equal(["essai-appliquer"], await PendingIds());
        Assert.Equal(0, _executions);

        // Un nouveau cycle respecte les choix : elles ne reviennent pas.
        await Json(HttpMethod.Post, "/api/cycle");
        Assert.Equal(["essai-appliquer"], await PendingIds());
    }

    [Theory]
    [InlineData("/api/proposals/inconnue/approve", HttpStatusCode.NotFound)]
    [InlineData("/api/proposals/inconnue/snooze", HttpStatusCode.NotFound)]
    [InlineData("/api/proposals/Majuscules/approve", HttpStatusCode.BadRequest)]
    [InlineData("/api/proposals/a%2F..%2Fb/reject", HttpStatusCode.BadRequest)]
    [InlineData("/api/proposals/ab/approve", HttpStatusCode.BadRequest)]
    public async Task Identifiant_inconnu_ou_invalide_refuse(string path, HttpStatusCode expected)
    {
        using var res = await Send(HttpMethod.Post, path);
        Assert.Equal(expected, res.StatusCode);
        Assert.Equal(0, _executions);
    }

    [Theory]
    [InlineData("/api/proposals/essai-appliquer/approve")]
    [InlineData("/api/proposals/essai-reporter/snooze")]
    [InlineData("/api/cycle")]
    public async Task Decisions_protegees_contre_les_requetes_inter_sites_et_sans_session(string path)
    {
        using var noOrigin = await Send(HttpMethod.Post, path, sameOrigin: false);
        using var noCookie = await Send(HttpMethod.Post, path, cookie: false);

        Assert.Equal(HttpStatusCode.Forbidden, noOrigin.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, noCookie.StatusCode);
        Assert.Equal(0, _executions);
        Assert.Equal(3, (await PendingIds()).Length);
    }

    [Fact]
    public async Task Decision_par_GET_impossible()
    {
        using var res = await Send(HttpMethod.Get, "/api/proposals/essai-appliquer/approve");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, res.StatusCode);
        Assert.Equal(0, _executions);
    }

    [Fact]
    public async Task Etat_contient_les_actions_automatiques_et_les_reglages_de_decision()
    {
        var s = await Json(HttpMethod.Get, "/api/state");

        var auto = Assert.Single(s.GetProperty("recentAuto").EnumerateArray());
        Assert.Equal("action", auto.GetProperty("kind").GetString());
        Assert.True(auto.GetProperty("auto").GetBoolean());
        Assert.Contains("machine virtuelle arrêtée", auto.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(24, s.GetProperty("snoozeHours").GetDouble());
        Assert.Equal(168, s.GetProperty("rejectHours").GetDouble());
        Assert.Equal(_dir, s.GetProperty("dataDirectory").GetString());
        Assert.Equal(["essai"], s.GetProperty("modules").EnumerateArray().Select(m => m.GetString()));
        Assert.False(s.GetProperty("decisionInProgress").GetBoolean());
    }

    [Fact]
    public async Task Etat_d_un_module_et_module_inconnu()
    {
        var m = await Json(HttpMethod.Get, "/api/modules/essai");
        Assert.Equal("essai", m.GetProperty("name").GetString());
        Assert.Equal("Firefox <script>", m.GetProperty("status").GetProperty("navigateurs")[0].GetString());

        using var unknown = await Send(HttpMethod.Get, "/api/modules/inconnu");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task Historique_actions_decisions_et_mesures()
    {
        await Json(HttpMethod.Post, "/api/proposals/essai-appliquer/approve");
        using (await Send(HttpMethod.Post, "/api/proposals/essai-ignorer/reject"))
        {
        }

        var h = await Json(HttpMethod.Get, "/api/history");
        var events = h.GetProperty("events").EnumerateArray().ToList();

        // Le plus recent d'abord.
        Assert.Equal("decision", events[0].GetProperty("kind").GetString());
        Assert.Equal("Ignorée : Ignorer ceci", events[0].GetProperty("message").GetString());
        Assert.Contains(events, e => e.GetProperty("kind").GetString() == "action" && !e.GetProperty("auto").GetBoolean());
        Assert.Contains(events, e => e.GetProperty("kind").GetString() == "proposal");
        Assert.DoesNotContain(events, e => e.GetProperty("message").GetString()!.StartsWith("RAM ", StringComparison.Ordinal));   // lignes de cycle exclues
        Assert.NotEmpty(h.GetProperty("metrics").EnumerateArray());
    }

    [Fact]
    public async Task Analyser_maintenant_lance_un_cycle()
    {
        var before = _runtime.Engine.LastReport!.At;
        var c = await Json(HttpMethod.Post, "/api/cycle");
        Assert.Equal(3, c.GetProperty("pendingCount").GetInt32());
        Assert.True(_runtime.Engine.LastReport!.At >= before);
        Assert.Empty(c.GetProperty("errors").EnumerateArray());
    }
}
