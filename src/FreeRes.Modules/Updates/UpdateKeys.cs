namespace FreeRes.Modules.Updates;

/// <summary>
/// Cle PUBLIQUE de signature des mises a jour (ECDSA P-256, SubjectPublicKeyInfo en base64). La cle privee
/// correspondante n'est jamais dans le depot : poste de publication (protegee par DPAPI) et secret de la CI.
/// NE PAS CHANGER : les versions installees ne pourraient plus verifier les mises a jour.
/// </summary>
public static class UpdateKeys
{
    public const string PublicKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEVu1J/anN8GbjI7LpNgm6ALpKc5vM0nGeJGXSkORcy4d7JJ1e8VtMfaEUkQvghL7qOSeD9z4GAGUbiWtY01qW+Q==";
}
