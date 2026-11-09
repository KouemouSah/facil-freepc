using System.Diagnostics;

namespace FreeRes.Modules.Docker;

/// <summary>
/// Ce que le module Docker observe de la machine (remplacable par un faux dans les tests).
/// Aucune methode ne lance de programme : les programmes externes passent par <c>IProcessRunner</c>.
/// </summary>
public interface IDockerHost
{
    /// <summary>Docker Desktop installe (CLI ou executable present).</summary>
    bool IsInstalled { get; }

    /// <summary>Chemin du CLI docker.exe, ou null s'il est absent.</summary>
    string? DockerCli { get; }

    /// <summary>Chemin de wsl.exe, ou null s'il est absent.</summary>
    string? WslExe { get; }

    /// <summary>Docker Desktop tourne (processus com.docker.backend ou Docker Desktop).</summary>
    bool IsDockerRunning();

    /// <summary>Chemin du disque de donnees detecte (emplacement actuel puis ancien), ou null.</summary>
    string? DetectVhdx();

    /// <summary>Taille du fichier en octets, ou null s'il n'existe pas.</summary>
    long? FileBytes(string path);
}

/// <summary>Implementation reelle (Docker Desktop, backend WSL 2).</summary>
public sealed class WindowsDockerHost : IDockerHost
{
    /// <summary>Emplacements connus du VHDX de donnees, du plus recent au plus ancien. Seuls chemins acceptes.</summary>
    public static IReadOnlyList<string> VhdxCandidates { get; } =
    [
        @"%LOCALAPPDATA%\Docker\wsl\disk\docker_data.vhdx",
        @"%LOCALAPPDATA%\Docker\wsl\data\ext4.vhdx",
    ];

    private static readonly string DockerDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Docker", "Docker");

    public bool IsInstalled => DockerCli is not null || File.Exists(Path.Combine(DockerDir, "Docker Desktop.exe"));

    public string? DockerCli => Existing(Path.Combine(DockerDir, "resources", "bin", "docker.exe"));

    public string? WslExe =>
        Existing(Path.Combine(Environment.SystemDirectory, "wsl.exe"))
        ?? Existing(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WSL", "wsl.exe"));

    public bool IsDockerRunning() => IsProcessRunning("com.docker.backend") || IsProcessRunning("Docker Desktop");

    public string? DetectVhdx() => VhdxCandidates.Select(Environment.ExpandEnvironmentVariables).FirstOrDefault(File.Exists);

    public long? FileBytes(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? info.Length : null;
    }

    private static string? Existing(string path) => File.Exists(path) ? path : null;

    private static bool IsProcessRunning(string name)
    {
        var processes = Process.GetProcessesByName(name);
        foreach (var p in processes)
        {
            p.Dispose();
        }

        return processes.Length > 0;
    }
}
