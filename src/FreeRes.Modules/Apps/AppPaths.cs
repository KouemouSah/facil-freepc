using System.Security.Cryptography;
using System.Text;

namespace FreeRes.Modules.Apps;

/// <summary>Chemins : normalisation, appartenance a un dossier, executable d'une ligne de commande, identifiants stables.</summary>
internal static class AppPathRules
{
    /// <summary>
    /// Chemin absolu normalise (separateurs Windows, sans separateur final), ou null s'il n'est pas absolu ou invalide.
    /// </summary>
    public static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var p = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')).Replace('/', '\\');
        if (p.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            p = p[4..];
        }

        if (!Path.IsPathFullyQualified(p))
        {
            return null;
        }

        try
        {
            p = Path.GetFullPath(p);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        return p.Length > 3 ? p.TrimEnd('\\') : p.TrimEnd('\\') + "\\";
    }

    /// <summary>
    /// Vrai si <paramref name="path"/> est <paramref name="folder"/> ou se trouve dessous. Comparaison par segment :
    /// « C:\App » ne capture PAS « C:\AppData ».
    /// </summary>
    public static bool IsUnder(string? path, string? folder)
    {
        var p = Normalize(path);
        var f = Normalize(folder);
        if (p is null || f is null)
        {
            return false;
        }

        if (p.Equals(f, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var prefix = f.EndsWith('\\') ? f : f + "\\";
        return p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary><see cref="IsUnder"/> sur des chemins DEJA normalises (boucles de rattachement des processus).</summary>
    public static bool IsUnderNormalized(string path, string folder) =>
        path.Equals(folder, StringComparison.OrdinalIgnoreCase)
        || (path.Length > folder.Length && path.StartsWith(folder, StringComparison.OrdinalIgnoreCase) && (folder.EndsWith('\\') || path[folder.Length] == '\\'));

    public static bool SamePath(string? a, string? b) =>
        Normalize(a) is { } x && Normalize(b) is { } y && x.Equals(y, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Executable d'une ligne de commande : chemin entre guillemets, sinon jusqu'au premier « .exe » suivi d'un
    /// espace (chemins non cites contenant des espaces), sinon premier mot. Null si le resultat n'est pas absolu.
    /// </summary>
    public static string? ExtractExecutable(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        var c = Environment.ExpandEnvironmentVariables(command.Trim());
        string exe;
        if (c.StartsWith('"'))
        {
            var end = c.IndexOf('"', 1);
            exe = end > 1 ? c[1..end] : c.Trim('"');
        }
        else
        {
            var idx = c.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            while (idx >= 0 && idx + 4 < c.Length && !char.IsWhiteSpace(c[idx + 4]))
            {
                idx = c.IndexOf(".exe", idx + 4, StringComparison.OrdinalIgnoreCase);
            }

            exe = idx >= 0 ? c[..(idx + 4)] : c.Split(' ', 2)[0];
        }

        return Normalize(exe);
    }

    /// <summary>
    /// ImagePath d'un service : formes « \SystemRoot\... », « system32\... » (relatif a Windows), « \??\C:\... »,
    /// guillemets et arguments.
    /// </summary>
    public static string? ServiceExecutable(string? imagePath, string windowsDirectory)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            return null;
        }

        var p = imagePath.Trim();
        if (p.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
        {
            p = Path.Combine(windowsDirectory, p[12..]);
        }
        else if (p.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            p = p[4..];
        }
        else if (!p.StartsWith('"') && !p.Contains(':', StringComparison.Ordinal) && !p.StartsWith('%') && !p.StartsWith('\\'))
        {
            p = Path.Combine(windowsDirectory, p);   // « system32\svchost.exe -k ... »
        }

        return ExtractExecutable(p);
    }

    /// <summary>
    /// Identifiant stable : prefixe + 16 hexadecimaux (64 bits de SHA-256 de la cle, insensible a la casse).
    /// Respecte le format des identifiants de proposition « ^[a-z0-9-]{3,60}$ ».
    /// </summary>
    public static string StableId(string prefix, string key)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key.ToUpperInvariant()));
        return prefix + "-" + Convert.ToHexStringLower(hash, 0, 8);
    }
}
