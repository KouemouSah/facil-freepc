using System.Text.RegularExpressions;

namespace FreeRes.Modules.Disk;

/// <summary>Bilan d'un nettoyage (ou de sa simulation).</summary>
public sealed record CleanResult(string Path, long FreedBytes, int Files, int Dirs, int Skipped, bool DryRun, IReadOnlyList<string> MatchedDirs)
{
    public double FreedGB => Math.Round(FreedBytes / (1024d * 1024 * 1024), 2);
}

/// <summary>
/// Suppression bornee, reprise de Remove-FreeResOlderThan (version PowerShell eprouvee) :
///   * uniquement sous une racine de la liste blanche, jamais une racine de lecteur ;
///   * ne suit JAMAIS une jonction ou un lien (protege les donnees hors perimetre) ;
///   * un fichier n'est supprime que si sa creation ET sa modification depassent le seuil (un fichier extrait
///     recemment d'une archive garde une vieille date de modification) ;
///   * fichiers verrouilles ignores ; mode « dirPattern » : dossiers de 1er niveau correspondant au motif.
/// </summary>
public sealed class SafeCleaner(IReadOnlyList<string> allowedRoots, TimeProvider time)
{
    private readonly string[] _roots = allowedRoots.Select(Normalize).Where(r => r.Length > 3).ToArray();

    public static string Expand(string path) => Environment.ExpandEnvironmentVariables(path ?? string.Empty);

    private static string Normalize(string path)
    {
        try
        {
            return Path.GetFullPath(Expand(path)).TrimEnd('\\');
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Empty;
        }
    }

    /// <summary>true si le chemin est une racine autorisee ou se trouve dessous (jamais une racine de lecteur).</summary>
    public bool IsSafe(string path)
    {
        var full = Normalize(path);
        if (full.Length <= 3)
        {
            return false;   // « C: », « C:\ » ou chemin invalide
        }

        return _roots.Any(r => string.Equals(full, r, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase));
    }

    public CleanResult RemoveOlderThan(string path, int olderThanDays, string? dirPattern = null, IReadOnlySet<string>? excludeDirs = null, bool dryRun = false)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(olderThanDays, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(olderThanDays, 3650);
        var root = Normalize(path);
        if (!IsSafe(root))
        {
            throw new InvalidOperationException($"Chemin hors liste blanche, refuse : {Expand(path)}");
        }

        var matched = new List<string>();
        long freed = 0;
        int files = 0, dirs = 0, skipped = 0;
        if (!Directory.Exists(root))
        {
            return new CleanResult(root, 0, 0, 0, 0, dryRun, matched);
        }

        var cutoff = time.GetLocalNow().LocalDateTime.AddDays(-olderThanDays);
        var exclude = excludeDirs ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rootInfo = new DirectoryInfo(root);

        if (!string.IsNullOrEmpty(dirPattern))
        {
            var rx = new Regex(dirPattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
            foreach (var d in SafeEnumerate(rootInfo.EnumerateDirectories))
            {
                if (IsReparse(d) || !rx.IsMatch(d.Name) || Newest(d) >= cutoff || exclude.Contains(d.FullName))
                {
                    continue;
                }

                var size = FolderSize(d.FullName);
                matched.Add(d.FullName);
                if (dryRun)
                {
                    freed += size.Bytes;
                    files += size.Files;
                    dirs++;
                    continue;
                }

                try
                {
                    DeleteTree(d);
                    freed += size.Bytes;
                    files += size.Files;
                    dirs++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    freed += size.Bytes - FolderSize(d.FullName).Bytes;   // suppression partielle
                    skipped++;
                }
            }

            return new CleanResult(root, freed, files, dirs, skipped, dryRun, matched);
        }

        var visitedDirs = new List<DirectoryInfo>();
        var stack = new Stack<DirectoryInfo>();
        stack.Push(rootInfo);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            FileSystemInfo[] items;
            try
            {
                items = current.GetFileSystemInfos();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skipped++;
                continue;
            }

            foreach (var item in items)
            {
                if (IsReparse(item))
                {
                    continue;   // jonction / lien : jamais suivi ni supprime
                }

                if (item is DirectoryInfo dir)
                {
                    if (!exclude.Contains(dir.FullName))
                    {
                        stack.Push(dir);
                        visitedDirs.Add(dir);
                    }

                    continue;
                }

                var file = (FileInfo)item;
                if (Newest(file) >= cutoff)
                {
                    continue;
                }

                if (dryRun)
                {
                    freed += file.Length;
                    files++;
                    continue;
                }

                try
                {
                    var length = file.Length;
                    if (file.IsReadOnly)
                    {
                        file.IsReadOnly = false;
                    }

                    file.Delete();
                    freed += length;
                    files++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    skipped++;   // fichier verrouille par un programme en cours
                }
            }
        }

        if (!dryRun)
        {
            // dossiers devenus vides, du plus profond au moins profond ; un dossier recent est conserve
            foreach (var d in visitedDirs.OrderByDescending(x => x.FullName.Length))
            {
                try
                {
                    d.Refresh();
                    if (!d.Exists || (d.LastWriteTime >= cutoff && d.CreationTime >= cutoff) || d.EnumerateFileSystemInfos().Any())
                    {
                        continue;
                    }

                    d.Delete();
                    dirs++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // dossier utilise : conserve
                }
            }
        }

        return new CleanResult(root, freed, files, dirs, skipped, dryRun, matched);
    }

    /// <summary>Taille d'un dossier sans suivre les jonctions (ni boucle ni double compte).</summary>
    public static (long Bytes, int Files) FolderSize(string path)
    {
        long total = 0;
        var count = 0;
        if (!Directory.Exists(path))
        {
            return (0, 0);
        }

        var stack = new Stack<DirectoryInfo>();
        stack.Push(new DirectoryInfo(path));
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            FileSystemInfo[] items;
            try
            {
                items = d.GetFileSystemInfos();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var i in items)
            {
                if (IsReparse(i))
                {
                    continue;
                }

                if (i is DirectoryInfo sub)
                {
                    stack.Push(sub);
                }
                else
                {
                    total += ((FileInfo)i).Length;
                    count++;
                }
            }
        }

        return (total, count);
    }

    private static bool IsReparse(FileSystemInfo info) => (info.Attributes & FileAttributes.ReparsePoint) != 0;

    private static DateTime Newest(FileSystemInfo info) => info.CreationTime > info.LastWriteTime ? info.CreationTime : info.LastWriteTime;

    private static List<DirectoryInfo> SafeEnumerate(Func<IEnumerable<DirectoryInfo>> source)
    {
        try
        {
            return source().ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Suppression recursive qui supprime une jonction comme un lien, sans jamais entrer dedans.</summary>
    private static void DeleteTree(DirectoryInfo dir)
    {
        foreach (var item in dir.GetFileSystemInfos())
        {
            if (item is DirectoryInfo sub)
            {
                if (IsReparse(sub))
                {
                    sub.Delete();   // retire le lien seul, la cible reste intacte
                }
                else
                {
                    DeleteTree(sub);
                }
            }
            else
            {
                var f = (FileInfo)item;
                if (f.IsReadOnly)
                {
                    f.IsReadOnly = false;
                }

                f.Delete();
            }
        }

        dir.Delete();
    }
}
