using System.Diagnostics;
using System.Text;

namespace FreeRes.Core.Platform;

/// <summary>Resultat d'un programme externe.</summary>
public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr, bool TimedOut);

/// <summary>Lancement de programmes externes (dism, shutdown, docker, wsl...).</summary>
public interface IProcessRunner
{
    /// <param name="standardInput">
    /// Texte envoye sur l'entree standard puis fermee. A PREFERER a un fichier de script pour un programme lance
    /// en administrateur : un fichier dans un dossier modifiable par l'utilisateur pourrait etre remplace entre
    /// son ecriture et sa lecture (ex. script diskpart transforme en « clean » du disque).
    /// </param>
    Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken, Encoding? outputEncoding = null, string? standardInput = null);
}

/// <summary>
/// Implementation reelle. Arguments passes un par un (ArgumentList) : aucune concatenation de ligne de
/// commande, donc aucune injection possible par un parametre. Processus tue si le delai est dépassé.
/// </summary>
public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken, Encoding? outputEncoding = null, string? standardInput = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(arguments);
        var psi = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInput is not null,
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

        if (standardInput is not null)
        {
            await process.StandardInput.WriteAsync(standardInput.AsMemory(), cancellationToken).ConfigureAwait(false);
            process.StandardInput.Close();   // fin de flux : le programme sait qu'il n'y a plus de commande
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
            return new ProcessResult(-1, string.Empty, $"Délai dépassé ({timeout})", true);
        }

        return new ProcessResult(process.ExitCode, Clean(await stdout.ConfigureAwait(false)), Clean(await stderr.ConfigureAwait(false)), false);
    }

    // wsl.exe ecrit en UTF-16 avec des octets nuls quand l'encodage n'est pas precise.
    private static string Clean(string s) => s.Replace("\0", string.Empty, StringComparison.Ordinal);
}
