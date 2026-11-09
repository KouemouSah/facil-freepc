using System.ComponentModel;
using System.Diagnostics;
using FreeRes.Core;
using FreeRes.Core.Actions;
using FreeRes.Core.Engine;

namespace FreeRes.App;

/// <summary>
/// Execute une action administrateur approuvee : relance de FacilFreePC.exe avec « runas » (demande UAC),
/// attente de la fin, lecture du resultat. Refus de l'UAC = resultat « Elevation refusee » (rien n'est tente).
/// </summary>
internal sealed class UacElevatedRunner(AppPaths paths, TimeSpan timeout) : IElevatedRunner
{
    private const int ErrorCancelled = 1223;   // ERROR_CANCELLED : l'utilisateur a refuse la demande UAC

    public UacElevatedRunner(AppPaths paths)
        : this(paths, TimeSpan.FromHours(2))
    {
    }

    public async Task<ActionResult> RunAsync(ActionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Chemin de l'exécutable introuvable");
        var resultPath = ElevatedActionProtocol.NewResultPath(paths);
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = true,
            Verb = "runas",
            // base64url (A-Z a-z 0-9 - _) et chemin sans guillemet : aucun echappement possible.
            Arguments = $"{ElevatedActionProtocol.RunActionArgument} {ElevatedActionProtocol.Encode(request)} {ElevatedActionProtocol.ResultArgument} \"{resultPath}\"",
        };

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return new ActionResult(request.Type, false, $"{MonitorEngine.ElevationCancelledPrefix} par l'utilisateur", 0, false);
        }

        if (process is null)
        {
            return new ActionResult(request.Type, false, "Relance élevée impossible", 0, false);
        }

        using (process)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new ActionResult(request.Type, false, $"Action élevée toujours en cours après {timeout}", 0, false);
            }

            var result = ElevatedActionProtocol.ReadResult(resultPath);
            TryDelete(resultPath);
            return result ?? new ActionResult(request.Type, false, $"Résultat de l'action élevée introuvable (code {process.ExitCode})", 0, false);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // nettoye au prochain passage de la retention
        }
    }
}
