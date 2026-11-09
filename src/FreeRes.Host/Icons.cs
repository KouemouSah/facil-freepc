using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using FreeRes.Core.Engine;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FreeRes.Host;

/// <summary>Extraction de l'icone d'un executable en PNG carre (implementation Windows fournie par l'application).</summary>
public interface IIconExtractor
{
    /// <summary>PNG de <paramref name="size"/> pixels, ou null si le fichier n'a pas d'icone lisible.</summary>
    byte[]? ExtractPng(string path, int size);
}

/// <summary>
/// Chemins dont l'icone peut etre lue. Le chemin vient TOUJOURS de l'etat d'un module (registre, processus), jamais de
/// la requete ; il reste a filtrer ce qu'un logiciel a pu inscrire dans le registre :
///   * chemin absolu sur un lecteur local fixe : un chemin reseau (\\serveur\..., lecteur reseau mappe) ferait
///     contacter un serveur distant avec les identifiants Windows de l'utilisateur ;
///   * executable (.exe) uniquement, chemin deja normalise (pas de « .. »).
/// </summary>
public static partial class IconPathPolicy
{
    [GeneratedRegex(@"^[A-Za-z]:\\")]
    private static partial Regex DrivePath();

    public static string? Normalize(string? path, Func<string, DriveType>? driveType = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var p = path.Trim().Trim('"');
        if (p.StartsWith(@"\\?\", StringComparison.Ordinal) && DrivePath().IsMatch(p[4..]))
        {
            p = p[4..];
        }

        if (!DrivePath().IsMatch(p) || p.Contains("..", StringComparison.Ordinal) || p.IndexOfAny(Path.GetInvalidPathChars()) >= 0
            || !p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var type = (driveType ?? DefaultDriveType)(p[..3]);
        return type == DriveType.Fixed ? p : null;
    }

    private static DriveType DefaultDriveType(string root)
    {
        try
        {
            return new DriveInfo(root).DriveType;
        }
        catch (ArgumentException)
        {
            return DriveType.Unknown;
        }
    }
}

/// <summary>
/// Route GET /api/icons/{module}/{id} : icone de l'executable que le MODULE associe a l'element (cache memoire borne,
/// absences comprises). 204 = pas d'icone (reponse normale, sans erreur dans la console) : l'interface garde son icone monochrome.
/// </summary>
public static partial class IconApi
{
    public const int Size = 64;
    private const int MaxEntries = 512;

    [GeneratedRegex("^[a-z]{2,20}$")]
    private static partial Regex ModulePattern();

    [GeneratedRegex("^[a-z0-9-]{1,80}$")]
    private static partial Regex IdPattern();

    public static void Map(IEndpointRouteBuilder app, FreeResRuntime? runtime, IIconExtractor? extractor, Func<string, DriveType>? driveType = null)
    {
        var cache = new ConcurrentDictionary<string, byte[]?>(StringComparer.OrdinalIgnoreCase);
        app.MapGet("/api/icons/{module}/{id}", (string module, string id, HttpResponse response) =>
        {
            if (!ModulePattern().IsMatch(module) || !IdPattern().IsMatch(id))
            {
                return Results.BadRequest("identifiant invalide");
            }

            var source = runtime?.Engine.IconSources.FirstOrDefault(s => string.Equals(s.Name, module, StringComparison.Ordinal));
            if (extractor is null || source is null || IconPathPolicy.Normalize(source.IconPath(id), driveType) is not { } path)
            {
                return Results.NoContent();
            }

            if (!cache.TryGetValue(path, out var png))
            {
                png = File.Exists(path) ? extractor.ExtractPng(path, Size) : null;
                if (cache.Count >= MaxEntries)
                {
                    cache.Clear();
                }

                cache[path] = png;
            }

            if (png is null)
            {
                return Results.NoContent();
            }

            response.Headers.CacheControl = "private, max-age=3600";
            return Results.Bytes(png, "image/png");
        });
    }
}
