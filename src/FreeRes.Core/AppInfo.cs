using System.Reflection;

namespace FreeRes.Core;

/// <summary>
/// Identite de l'application, lue depuis les attributs de l'assembly (source unique : Directory.Build.props).
/// </summary>
public static class AppInfo
{
    /// <summary>Identifiant technique sans espace : dossiers, objets de synchronisation, cle d'installation.</summary>
    public const string Id = "FacilFreePC";

    private static readonly Assembly Self = typeof(AppInfo).Assembly;

    /// <summary>Nom affiche (« Facil FreePC »).</summary>
    public static string Name { get; } = Self.GetCustomAttribute<AssemblyProductAttribute>()?.Product ?? Id;

    public static string Company { get; } = Self.GetCustomAttribute<AssemblyCompanyAttribute>()?.Company ?? string.Empty;

    public static string Copyright { get; } = Self.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright ?? string.Empty;

    public static string Version { get; } = ReadVersion();

    private static string ReadVersion()
    {
        var info = Self.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(info))
        {
            return Self.GetName().Version?.ToString(3) ?? "0.0.0";
        }

        // Le SDK ajoute « +<commit> » ; on ne garde que la version semantique.
        var plus = info.IndexOf('+', StringComparison.Ordinal);
        return plus >= 0 ? info[..plus] : info;
    }
}
