using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FreeRes.Modules.Updates;

/// <summary>Version publiee : installeur, empreinte, notes. Lu seulement apres verification de sa signature.</summary>
public sealed record UpdateManifest(string Version, string Url, string Sha256, string Notes)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public Version ParsedVersion => System.Version.Parse(Version);
}

/// <summary>Refus d'une mise a jour (signature, contenu, hote, empreinte...) : jamais installee.</summary>
public sealed class UpdateRejectedException(string message) : Exception(message);

/// <summary>
/// Verification d'un manifeste de mise a jour. Sans certificat de signature de code, c'est la seule protection
/// contre un canal de publication compromis : le manifeste (version, adresse ET empreinte de l'installeur) est
/// signe avec la cle privee de Facil (ECDSA P-256, SHA-256, signature IEEE P1363 de 64 octets en base64) ; la cle
/// publique est compilee dans l'application. L'installeur telecharge doit ensuite avoir l'empreinte signee.
/// </summary>
public static partial class UpdateVerifier
{
    public const int MaxManifestBytes = 16 * 1024;

    /// <summary>Hotes autorises pour l'installeur (HTTPS uniquement) : depot GitHub de Facil FreePC et site Emac SAH.</summary>
    public static IReadOnlySet<string> AllowedHosts { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "github.com",
        "emacsah.com",
        "www.emacsah.com",
    };

    [GeneratedRegex(@"^\d{1,4}\.\d{1,4}\.\d{1,4}$")]
    private static partial Regex VersionPattern();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256Pattern();

    public static UpdateManifest Verify(byte[] manifest, string signatureBase64, string publicKeySpkiBase64)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(signatureBase64);
        ArgumentNullException.ThrowIfNull(publicKeySpkiBase64);
        if (manifest.Length is 0 or > MaxManifestBytes)
        {
            throw new UpdateRejectedException("manifeste vide ou trop grand");
        }

        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(signatureBase64.Trim());
        }
        catch (FormatException)
        {
            throw new UpdateRejectedException("signature illisible");
        }

        using (var key = ECDsa.Create())
        {
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeySpkiBase64), out _);
            if (signature.Length != 64 || !key.VerifyData(manifest, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            {
                throw new UpdateRejectedException("signature invalide : manifeste non publie par Facil");
            }
        }

        UpdateManifest? m;
        try
        {
            m = JsonSerializer.Deserialize<UpdateManifest>(manifest, JsonOptions);
        }
        catch (JsonException)
        {
            throw new UpdateRejectedException("manifeste illisible");
        }

        if (m is null || m.Version is null || m.Url is null || m.Sha256 is null || m.Notes is null)
        {
            throw new UpdateRejectedException("manifeste incomplet");
        }

        if (!VersionPattern().IsMatch(m.Version))
        {
            throw new UpdateRejectedException("version invalide");
        }

        if (!Sha256Pattern().IsMatch(m.Sha256))
        {
            throw new UpdateRejectedException("empreinte SHA-256 invalide");
        }

        CheckInstallerUrl(m.Url);
        return m;
    }

    /// <summary>HTTPS, hote autorise, fichier .exe, sans identifiants ni port exotique.</summary>
    public static Uri CheckInstallerUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !uri.IsDefaultPort
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !AllowedHosts.Contains(uri.Host)
            || !uri.AbsolutePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new UpdateRejectedException("adresse de l'installeur non autorisee : " + url);
        }

        return uri;
    }

    /// <summary>Empreinte du fichier telecharge, comparee a celle du manifeste signe.</summary>
    public static void CheckSha256(Stream installer, string expectedHex)
    {
        ArgumentNullException.ThrowIfNull(installer);
        var actual = Convert.ToHexStringLower(SHA256.HashData(installer));
        if (!string.Equals(actual, expectedHex, StringComparison.Ordinal))
        {
            throw new UpdateRejectedException("empreinte de l'installeur differente de celle publiee : fichier refuse");
        }
    }

    public static JsonSerializerOptions JsonOptions { get; } = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
}
