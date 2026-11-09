namespace FreeRes.Modules.Apps;

/// <summary>
/// Etat active/desactive d'un element de demarrage, tel que le lit et l'ecrit le Gestionnaire des taches :
/// valeur REG_BINARY de 12 octets, nommee comme la valeur Run (ou le fichier du dossier Demarrage), sous
/// « Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\{Run|Run32|StartupFolder} » de HKCU ou HKLM.
///
/// Semantique (verifiee sur la machine de developpement, coherente avec les outils publics qui manipulent cette cle) :
///   * octets 0-3 : drapeaux ; seul le BIT 0 de l'octet 0 compte : PAIR = active, IMPAIR = desactive.
///     Valeurs observees : 02 et 06 (active, 06 pose par Windows pour SecurityHealth), 04 (active, raccourci du
///     dossier commun), 03 (desactive par le Gestionnaire des taches), 01 (desactive, pose par une application).
///   * octets 4-11 : FILETIME UTC de la desactivation (zero quand l'element est active) ;
///   * valeur absente ou illisible (moins d'un octet) = ACTIVE : la cle Run seule suffit a lancer le programme.
/// Le Gestionnaire des taches ecrit 03 00 00 00 + FILETIME pour desactiver et 02 + 11 zeros pour activer :
/// on reproduit exactement ces deux formes. L'entree Run n'est JAMAIS supprimee (action reversible).
/// </summary>
internal static class StartupApproved
{
    public const string RootPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    public static bool IsEnabled(object? value) =>
        value is not byte[] { Length: > 0 } bytes || (bytes[0] & 1) == 0;

    public static byte[] Encode(bool enabled, DateTimeOffset now)
    {
        var data = new byte[12];
        if (enabled)
        {
            data[0] = 0x02;
            return data;
        }

        data[0] = 0x03;
        BitConverter.TryWriteBytes(data.AsSpan(4), now.UtcDateTime.ToFileTimeUtc());   // petit-boutiste sur x86/x64/ARM64
        return data;
    }
}
