namespace FreeRes.Core;

/// <summary>
/// Emplacements des donnees. Tout est par utilisateur, sous %LOCALAPPDATA%\FacilFreePC :
/// aucune ecriture dans Program Files ni dans HKLM (installation sans droits administrateur).
/// </summary>
public sealed class AppPaths
{
    /// <summary>Variable d'environnement permettant aux tests d'isoler le dossier de donnees.</summary>
    public const string DataDirOverrideVariable = "FREERES_DATA_DIR";

    public AppPaths(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        DataDirectory = Path.GetFullPath(dataDirectory);
    }

    public string DataDirectory { get; }

    public string LogDirectory => Path.Combine(DataDirectory, "logs");

    /// <summary>Profil WebView2 dedie : cookies et cache isoles de tout navigateur de l'utilisateur.</summary>
    public string WebViewDirectory => Path.Combine(DataDirectory, "WebView2");

    /// <summary>Dossier par defaut, ou celui impose par <see cref="DataDirOverrideVariable"/>.</summary>
    public static AppPaths Default()
    {
        var overrideDir = Environment.GetEnvironmentVariable(DataDirOverrideVariable);
        if (!string.IsNullOrWhiteSpace(overrideDir))
        {
            return new AppPaths(overrideDir);
        }

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new AppPaths(Path.Combine(local, AppInfo.Id));
    }

    /// <summary>Cree les dossiers necessaires (idempotent).</summary>
    public void EnsureCreated()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(WebViewDirectory);
    }
}
