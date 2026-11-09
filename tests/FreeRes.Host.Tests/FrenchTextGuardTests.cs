using System.Text.RegularExpressions;

namespace FreeRes.Host.Tests;

/// <summary>
/// Garde-fou : aucun texte visible (litteral de chaine contenant une phrase) ne reprend une graphie sans accent.
/// Les commentaires et le SQL ne sont pas concernes. Liste limitee aux mots SANS ambiguite.
/// </summary>
public sealed partial class FrenchTextGuardTests
{
    private static readonly string[] Banned =
    [
        "memoire", "systeme", "demarrage", "redemarrage", "redemarrer", "fenetre", "fenetres", "deja", "etat", "etre",
        "securite", "donnees", "parametres", "reglages", "decision", "decisions", "resultat", "echec", "echecs", "delai",
        "defaut", "element", "elements", "modele", "modeles", "dechargement", "telechargement", "verifiable", "integre",
        "necessite", "apres", "acces", "arriere", "taches", "duree", "operation", "detail", "details", "meme",
    ];

    private static readonly string[] SourceDirs = ["src", "tools"];

    [Fact]
    public void Aucun_texte_visible_sans_accent()
    {
        var root = RepoRoot();
        var files = SourceDirs
            .SelectMany(d => Directory.EnumerateFiles(Path.Combine(root, d), "*.*", SearchOption.AllDirectories))
            .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".js", StringComparison.Ordinal) || f.EndsWith(".html", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();
        Assert.True(files.Count > 50, $"sources introuvables sous {root}");

        var faults = new List<string>();
        foreach (var file in files)
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith('*'))
                {
                    continue;
                }

                foreach (Match lit in Literal().Matches(lines[i]))
                {
                    var text = Hole().Replace(lit.Groups[1].Value, " ");
                    if (!text.Trim().Contains(' ', StringComparison.Ordinal) || Sql().IsMatch(text))
                    {
                        continue;
                    }

                    foreach (Match w in Word().Matches(text))
                    {
                        if (Banned.Contains(w.Value.ToLowerInvariant()))
                        {
                            faults.Add($"{Path.GetRelativePath(root, file)}:{i + 1} « {w.Value} »");
                        }
                    }
                }
            }
        }

        Assert.True(faults.Count == 0, "textes sans accent :\n" + string.Join('\n', faults));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FreeRes.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("FreeRes.sln introuvable");
    }

    [GeneratedRegex("\"((?:[^\"\\\\\\n]|\\\\.)*)\"")]
    private static partial Regex Literal();

    [GeneratedRegex(@"\{[^}]*\}")]
    private static partial Regex Hole();

    [GeneratedRegex(@"(?<![\p{L}])\p{L}+(?![\p{L}])")]
    private static partial Regex Word();

    [GeneratedRegex(@"\b(SELECT|INSERT|DELETE|UPDATE|CREATE)\b")]
    private static partial Regex Sql();
}
