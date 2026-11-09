using System.Diagnostics;
using System.Text;

namespace FreeRes.Core.Platform;

/// <summary>Resultat d'un programme externe.</summary>
public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr, bool TimedOut);

/// <summary>Lancement de programmes externes (dism, shutdown, docker, wsl...).</summary>
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken, Encoding? outputEncoding = null);
}

/// <summary>
/// Implementation reelle. Arguments passes un par un (ArgumentList) : aucune concatenation de ligne de
/// commande, donc aucune injection possible par un parametre. Processus tue si le delai est depasse.
/// </summary>
public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken, Encoding? outputEncoding = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(arguments);
        var psi = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        if (outputEncoding is not null)
        {
            psi.StandardOutputEncoding = outputEncoding;
            psi.StandardErrorEncoding = outputEncoding;
        }

        foreach (var a in arguments)
        {
            psi.ArgumentList.Add(a);
        }

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return new ProcessResult(-2, string.Empty, ex.Message, false);
        }

        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // deja termine
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new ProcessResult(-1, string.Empty, $"Delai depasse ({timeout})", true);
        }

        return new ProcessResult(process.ExitCode, Clean(await stdout.ConfigureAwait(false)), Clean(await stderr.ConfigureAwait(false)), false);
    }

    // wsl.exe ecrit en UTF-16 avec des octets nuls quand l'encodage n'est pas precise.
    private static string Clean(string s) => s.Replace("\0", string.Empty, StringComparison.Ordinal);
}
