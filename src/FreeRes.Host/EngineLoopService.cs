using FreeRes.Core.Engine;
using FreeRes.Core.Logging;
using Microsoft.Extensions.Hosting;

namespace FreeRes.Host;

/// <summary>
/// Boucle de surveillance : un cycle au demarrage puis toutes les N minutes (settings.json).
/// Une erreur de cycle est journalisee ; la boucle ne s'arrete jamais d'elle-meme.
/// </summary>
public sealed class EngineLoopService(FreeResRuntime runtime) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(runtime.Engine.Settings.MonitorIntervalMinutes);
        using var timer = new PeriodicTimer(interval);
        do
        {
            try
            {
                await runtime.Engine.RunCycleAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // La surveillance doit survivre a toute erreur de cycle (journalisee).
            catch (Exception ex)
#pragma warning restore CA1031
            {
                runtime.Log.Write(LogLevel.Error, "monitor", $"Cycle en échec : {ex.Message}");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
