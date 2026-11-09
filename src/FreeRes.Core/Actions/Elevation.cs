using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FreeRes.Core.Actions;

/// <summary>Execute UNE action administrateur dans un processus eleve (UAC), action par action.</summary>
public interface IElevatedRunner
{
    Task<ActionResult> RunAsync(ActionRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Protocole entre l'application (droits utilisateur) et sa relance elevee :
///   FacilFreePC.exe --run-action &lt;requete base64url&gt; --result &lt;fichier&gt;
/// La requete voyage sur la LIGNE DE COMMANDE (visible dans le detail de la fenetre UAC, non modifiable apres
/// consentement) plutot que dans un fichier qu'un autre programme pourrait alterer entre-temps.
/// Le processus eleve n'accepte que des actions de la liste fermee marquees « admin » ; le resultat est ecrit
/// uniquement dans le dossier dedie de l'application, sous un nom impose.
/// </summary>
public static partial class ElevatedActionProtocol
{
    public const string RunActionArgument = "--run-action";
    public const string ResultArgument = "--result";
    public const int MaxEncodedLength = 8192;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [GeneratedRegex("^[0-9a-f]{32}\\.json$")]
    private static partial Regex ResultFileName();

    public static string ElevationDirectory(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return Path.Combine(paths.DataDirectory, "elevation");
    }

    public static string Encode(ActionRequest request)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(request, Json);
        var encoded = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return encoded.Length <= MaxEncodedLength ? encoded : throw new ArgumentException("Requête d'action trop longue", nameof(request));
    }

    /// <summary>Decode une requete ; null si absente, trop longue ou mal formee (jamais d'exception sur une entree hostile).</summary>
    public static ActionRequest? Decode(string? encoded)
    {
        if (string.IsNullOrEmpty(encoded) || encoded.Length > MaxEncodedLength)
        {
            return null;
        }

        try
        {
            var b64 = encoded.Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + ((4 - (b64.Length % 4)) % 4), '=');
            var request = JsonSerializer.Deserialize<ActionRequest>(Encoding.UTF8.GetString(Convert.FromBase64String(b64)), Json);
            return request is { Type: not null, Parameters: not null } ? request : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Nouveau fichier de resultat (nom aleatoire impose) dans le dossier d'elevation.</summary>
    public static string NewResultPath(AppPaths paths)
    {
        var dir = ElevationDirectory(paths);
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, Guid.NewGuid().ToString("N") + ".json");
    }

    /// <summary>Le fichier de resultat doit etre DIRECTEMENT dans le dossier d'elevation et porter un nom impose.</summary>
    public static bool IsValidResultPath(AppPaths paths, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        var dir = Path.GetFullPath(ElevationDirectory(paths));
        return string.Equals(Path.GetDirectoryName(full), dir, StringComparison.OrdinalIgnoreCase)
            && ResultFileName().IsMatch(Path.GetFileName(full));
    }

    public static void WriteResult(string path, ActionResult result) => File.WriteAllText(path, JsonSerializer.Serialize(result, Json));

    public static ActionResult? ReadResult(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<ActionResult>(File.ReadAllText(path), Json) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
