using System.Text.RegularExpressions;

namespace FreeRes.Modules.Tests;

/// <summary>
/// Garde-fous de conception verifies sur le CODE SOURCE des modules (leurs actions peuvent s'executer en
/// administrateur) : un manquement doit faire echouer la construction, pas attendre une revue.
/// </summary>
public sealed partial class ArchitectureGuardTests
{
    private static readonly string ModulesSource = FindModulesSource();

    private static string FindModulesSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FreeRes.sln")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("FreeRes.sln introuvable"), "src", "FreeRes.Modules");
    }

    private static IEnumerable<(string File, int Line, string Text)> Matches(Regex pattern) =>
        Directory.EnumerateFiles(ModulesSource, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(f => File.ReadLines(f).Select((t, i) => (File: Path.GetFileName(f), Line: i + 1, Text: t)))
            .Where(l => !l.Text.TrimStart().StartsWith("//", StringComparison.Ordinal) && !l.Text.TrimStart().StartsWith("///", StringComparison.Ordinal))
            .Where(l => pattern.IsMatch(l.Text));

    [GeneratedRegex(@"GetTempPath|GetTempFileName")]
    private static partial Regex TempPath();

    [GeneratedRegex(@"Process\.Start\(|new\s+Process\s*[({]")]
    private static partial Regex DirectProcess();

    [GeneratedRegex(@"System\.Management|ManagementObject|Win32_")]
    private static partial Regex Wmi();

    /// <summary>
    /// Un acces a %TEMP% n'est admis qu'en LECTURE et doit le declarer sur la ligne meme
    /// (« garde-fou-temp: lecture seule » + justification), pour rester visible a toute relecture.
    /// </summary>
    [Fact]
    public void Aucun_fichier_temporaire_un_script_ecrit_dans_TEMP_peut_etre_substitue_avant_execution_admin() =>
        Assert.DoesNotContain(Matches(TempPath()), l => !l.Text.Contains("garde-fou-temp: lecture seule", StringComparison.Ordinal));

    [Fact]
    public void Aucun_lancement_direct_de_processus_tout_passe_par_IProcessRunner() =>
        Assert.Empty(Matches(DirectProcess()));

    [Fact]
    public void Aucun_WMI() => Assert.Empty(Matches(Wmi()));
}
