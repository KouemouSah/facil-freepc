using System.Security.Cryptography;
using System.Text;

namespace FreeRes.UpdateSign;

/// <summary>
/// Stockage de la cle privee de signature des mises a jour.
/// <list type="bullet">
/// <item>Copie de travail : fichier protege par DPAPI (compte Windows courant). Liee a CE poste : ce n'est PAS une
/// sauvegarde (illisible sur un autre PC ou apres reinstallation).</item>
/// <item>Sauvegarde : PKCS#8 CHIFFRE au format PEM (PBES2 : AES-256-CBC, PBKDF2-SHA256, 600 000 iterations) protege par
/// une phrase secrete. Lisible sur n'importe quel poste ; peut donc vivre dans un dossier synchronise (Google Drive).
/// La phrase secrete ne doit jamais etre stockee au meme endroit.</item>
/// </list>
/// Toute cle chargee est controlee contre la cle PUBLIQUE compilee dans l'application : une sauvegarde d'une autre
/// cle est refusee au lieu de produire des mises a jour que les versions installees rejetteraient.
/// </summary>
public sealed class SigningKeyStore
{
    public const string KeyEnv = "UPDATE_SIGNING_KEY";
    public const string BackupEnv = "UPDATE_SIGNING_KEY_BACKUP";
    public const string PassphraseEnv = "UPDATE_SIGNING_KEY_PASSPHRASE";
    public const int MinPassphraseLength = 16;
    private const int Iterations = 600_000;
    private const string PemLabel = "ENCRYPTED PRIVATE KEY";

    private readonly string _expectedPublicKey;

    public SigningKeyStore(string directory, string expectedPublicKey)
    {
        Directory = directory;
        _expectedPublicKey = expectedPublicKey;
    }

    public string Directory { get; }
    public string DpapiFile => Path.Combine(Directory, "update-key.dpapi");
    public string BackupLocationFile => Path.Combine(Directory, "backup-location.txt");

    /// <summary>Secret CI (base64). Windows PowerShell 5.1 prefixe d'un BOM toute chaine envoyee par « | » a un
    /// programme natif : il n'appartient pas a la cle et est retire.</summary>
    public static byte[] ParseCiKey(string value)
    {
        try
        {
            return Convert.FromBase64String(value.Trim().TrimStart('﻿').Trim());
        }
        catch (FormatException)
        {
            throw new InvalidOperationException($"{KeyEnv} n'est pas une clé PKCS#8 en base64 valide : recréer le secret avec « export-ci »");
        }
    }

    public static void CheckPassphrase(ReadOnlySpan<char> passphrase)
    {
        if (passphrase.Trim().Length < MinPassphraseLength)
        {
            throw new InvalidOperationException($"phrase secrète trop courte : {MinPassphraseLength} caractères minimum (une phrase de plusieurs mots convient)");
        }
    }

    /// <summary>Verifie que la cle privee correspond a la cle publique de l'application.</summary>
    public void EnsureMatchesPublicKey(byte[] pkcs8)
    {
        using var key = ECDsa.Create();
        try
        {
            key.ImportPkcs8PrivateKey(pkcs8, out _);
        }
        catch (CryptographicException)
        {
            throw new InvalidOperationException("la clé n'est pas une clé privée ECDSA PKCS#8 valide");
        }

        if (Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()) != _expectedPublicKey)
        {
            throw new InvalidOperationException("cette clé ne correspond PAS à la clé publique de l'application : les versions installées refuseraient ses mises à jour");
        }
    }

    public static string ExportEncrypted(byte[] pkcs8, ReadOnlySpan<char> passphrase)
    {
        CheckPassphrase(passphrase);
        using var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(pkcs8, out _);
        var encrypted = key.ExportEncryptedPkcs8PrivateKey(passphrase, new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, Iterations));
        return new string(PemEncoding.Write(PemLabel, encrypted)) + "\n";
    }

    /// <summary>Dechiffre une sauvegarde et verifie qu'il s'agit bien de la cle de l'application.</summary>
    public byte[] ImportEncrypted(string pem, ReadOnlySpan<char> passphrase)
    {
        if (!pem.Contains("-----BEGIN " + PemLabel + "-----", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("ce fichier n'est pas une sauvegarde chiffrée de la clé (en-tête PEM « " + PemLabel + " » absent)");
        }

        using var key = ECDsa.Create();
        try
        {
            key.ImportFromEncryptedPem(pem, passphrase);
        }
        catch (CryptographicException)
        {
            throw new InvalidOperationException("phrase secrète incorrecte (ou sauvegarde endommagée)");
        }

        var pkcs8 = key.ExportPkcs8PrivateKey();
        EnsureMatchesPublicKey(pkcs8);
        return pkcs8;
    }

    /// <summary>Ecrit la sauvegarde chiffree, la relit et la dechiffre pour prouver qu'elle est restaurable, puis
    /// memorise son emplacement (lecture automatique ulterieure). N'ecrase jamais une sauvegarde existante.</summary>
    public void WriteBackup(byte[] pkcs8, string path, ReadOnlySpan<char> passphrase)
    {
        EnsureMatchesPublicKey(pkcs8);
        var full = Path.GetFullPath(path);
        if (File.Exists(full))
        {
            throw new InvalidOperationException($"une sauvegarde existe déjà ({full}) : choisir un autre fichier ou la vérifier avec « verify-backup »");
        }

        var dir = Path.GetDirectoryName(full)!;
        System.IO.Directory.CreateDirectory(dir);
        var pem = ExportEncrypted(pkcs8, passphrase);
        using (var stream = new FileStream(full, FileMode.CreateNew, FileAccess.Write))
        {
            stream.Write(Encoding.ASCII.GetBytes(pem));
        }

        var roundTrip = ImportEncrypted(File.ReadAllText(full, Encoding.ASCII), passphrase);
        var same = CryptographicOperations.FixedTimeEquals(roundTrip, pkcs8);
        CryptographicOperations.ZeroMemory(roundTrip);
        if (!same)
        {
            File.Delete(full);
            throw new InvalidOperationException("la sauvegarde relue ne correspond pas à la clé : fichier supprimé");
        }

        RecordBackupLocation(full);
    }

    public void RecordBackupLocation(string path)
    {
        System.IO.Directory.CreateDirectory(Directory);
        File.WriteAllText(BackupLocationFile, Path.GetFullPath(path), new UTF8Encoding(false));
    }

    /// <summary>Emplacement de la sauvegarde : variable UPDATE_SIGNING_KEY_BACKUP, sinon emplacement memorise.</summary>
    public string? ResolveBackupPath(string? backupEnv)
    {
        if (!string.IsNullOrWhiteSpace(backupEnv))
        {
            return Path.GetFullPath(backupEnv.Trim());
        }

        return File.Exists(BackupLocationFile) ? File.ReadAllText(BackupLocationFile).Trim() is { Length: > 0 } p ? p : null : null;
    }

    public byte[] LoadDpapi() => ProtectedData.Unprotect(File.ReadAllBytes(DpapiFile), null, DataProtectionScope.CurrentUser);

    /// <summary>Cree la copie de travail DPAPI de ce poste. N'ecrase jamais une cle existante.</summary>
    public void SaveDpapi(byte[] pkcs8)
    {
        EnsureMatchesPublicKey(pkcs8);
        if (File.Exists(DpapiFile))
        {
            throw new InvalidOperationException($"une clé existe déjà sur ce poste ({DpapiFile})");
        }

        System.IO.Directory.CreateDirectory(Directory);
        using var stream = new FileStream(DpapiFile, FileMode.CreateNew, FileAccess.Write);
        stream.Write(ProtectedData.Protect(pkcs8, null, DataProtectionScope.CurrentUser));
    }

    /// <summary>
    /// Charge la cle, dans l'ordre : secret CI, copie DPAPI du poste, sauvegarde chiffree (phrase secrete demandee a
    /// <paramref name="passphrase"/>). Lue depuis la sauvegarde, la cle recree la copie DPAPI : les signatures
    /// suivantes ne redemandent plus la phrase.
    /// </summary>
    public byte[] Load(string? ciKey, string? backupEnv, Func<char[]> passphrase, Action<string> log)
    {
        if (!string.IsNullOrEmpty(ciKey))
        {
            return ParseCiKey(ciKey);
        }

        if (File.Exists(DpapiFile))
        {
            return LoadDpapi();
        }

        var backup = ResolveBackupPath(backupEnv);
        if (backup is null || !File.Exists(backup))
        {
            throw new InvalidOperationException($"clé de signature absente ({KeyEnv}, {DpapiFile}"
                + (backup is null ? ", aucune sauvegarde connue : " + BackupEnv + ")" : ", sauvegarde introuvable : " + backup + ")"));
        }

        log("Clé absente de ce poste : lecture de la sauvegarde " + backup);
        var secret = passphrase();
        try
        {
            var pkcs8 = ImportEncrypted(File.ReadAllText(backup, Encoding.ASCII), secret);
            SaveDpapi(pkcs8);
            RecordBackupLocation(backup);
            log("Clé restaurée sur ce poste (" + DpapiFile + ")");
            return pkcs8;
        }
        finally
        {
            Array.Clear(secret);
        }
    }

    /// <summary>Etat de la sauvegarde sans phrase secrete (rappel dans build.ps1) : connue, presente, au bon format.</summary>
    public (bool Ok, string Message) BackupStatus(string? backupEnv)
    {
        var backup = ResolveBackupPath(backupEnv);
        if (backup is null)
        {
            return (false, "clé NON sauvegardée : lancer « backup <fichier> » (perte du poste = plus aucune mise à jour publiable)");
        }

        if (!File.Exists(backup))
        {
            return (false, "sauvegarde introuvable : " + backup + " (disque de synchronisation déconnecté ?)");
        }

        var text = File.ReadAllText(backup, Encoding.ASCII);
        return text.Contains("-----BEGIN " + PemLabel + "-----", StringComparison.Ordinal)
            ? (true, "sauvegarde présente : " + backup + " (déchiffrement contrôlé par « verify-backup »)")
            : (false, "le fichier de sauvegarde n'est pas une clé chiffrée : " + backup);
    }
}
