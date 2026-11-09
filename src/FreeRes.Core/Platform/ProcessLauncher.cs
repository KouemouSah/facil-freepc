using System.Diagnostics;

namespace FreeRes.Core.Platform;

/// <summary>
/// Lancement DETACHE d'une application (ex. relancer un navigateur) : on n'attend pas sa fin, contrairement a
/// <see cref="IProcessRunner"/>. Arguments separes (ArgumentList) : aucune injection par un parametre.
/// </summary>
public interface IProcessLauncher
{
    /// <summary>true si le processus a demarre.</summary>
    bool Launch(string fileName, IReadOnlyList<string> arguments);
}

public sealed class ProcessLauncher : IProcessLauncher
{
    public bool Launch(string fileName, IReadOnlyList<string> arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(arguments);
        var psi = new ProcessStartInfo(fileName) { UseShellExecute = false };
        foreach (var a in arguments)
        {
            psi.ArgumentList.Add(a);
        }

        try
        {
            using var p = Process.Start(psi);
            return p is not null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
