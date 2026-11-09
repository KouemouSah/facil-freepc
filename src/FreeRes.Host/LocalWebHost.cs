using System.Net;
using FreeRes.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;

namespace FreeRes.Host;

/// <summary>Serveur de l'interface : Kestrel sur 127.0.0.1 uniquement, API + fichiers statiques embarques.</summary>
public sealed class LocalWebHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private LocalWebHost(WebApplication app, LocalWebHostOptions options)
    {
        _app = app;
        Options = options;
    }

    public LocalWebHostOptions Options { get; }

    /// <summary>Adresse reelle apres demarrage (port attribue par le systeme si Port = 0).</summary>
    public Uri BaseAddress { get; private set; } = new($"http://{LocalWebHostOptions.LoopbackHost}/");

    /// <summary>Construit l'application. <paramref name="configure"/> sert aux tests (serveur de test en memoire).</summary>
    public static LocalWebHost Create(LocalWebHostOptions options, Action<WebApplicationBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, options.Port));
        builder.Services.AddSingleton(options);
        configure?.Invoke(builder);

        var app = builder.Build();
        app.UseMiddleware<LocalSecurityMiddleware>();

        var files = new ManifestEmbeddedFileProvider(typeof(LocalWebHost).Assembly, "wwwroot");
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
        app.UseStaticFiles(new StaticFileOptions { FileProvider = files });

        app.MapGet("/api/health", (LocalWebHostOptions o) => new HealthResponse(
            AppInfo.Name,
            AppInfo.Version,
            Environment.MachineName,
            o.StartedAt));

        return new LocalWebHost(app, options);
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _app.StartAsync(cancellationToken).ConfigureAwait(false);
        var addresses = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses;
        var first = addresses?.FirstOrDefault();
        if (first is not null)
        {
            // Kestrel annonce « http://127.0.0.1:PORT » : on normalise en URI de base avec « / » final.
            BaseAddress = new Uri(first.TrimEnd('/') + "/");
        }
    }

    /// <summary>Acces au serveur pour les tests (TestServer).</summary>
    internal IServiceProvider Services => _app.Services;

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>Reponse de /api/health.</summary>
public sealed record HealthResponse(string Name, string Version, string Machine, DateTimeOffset StartedAt);
