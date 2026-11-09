using System.Security.Cryptography;
using System.Text;

namespace FreeRes.Host;

/// <summary>Generation et comparaison du jeton de session.</summary>
public static class SessionToken
{
    /// <summary>256 bits aleatoires, encodes en base64url (utilisable tel quel dans un cookie).</summary>
    public static string Create()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>Comparaison en temps constant : pas de fuite du jeton par mesure du temps de reponse.</summary>
    public static bool Matches(string? candidate, string expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (candidate is null)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(candidate), Encoding.UTF8.GetBytes(expected));
    }
}
