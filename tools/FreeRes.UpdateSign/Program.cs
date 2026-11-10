using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FreeRes.Modules.Updates;
using FreeRes.UpdateSign;

// Signature des mises a jour de Facil FreePC.
//   new-key                 cree la cle (refuse si elle existe), l'enregistre protegee par DPAPI (compte Windows
//                           courant) et affiche la cle PUBLIQUE a placer dans UpdateKeys.cs
//   backup <fichier.pem>    SAUVEGARDE chiffree par phrase secrete (lisible sur tout poste), relue et verifiee,
//                           emplacement memorise. A placer hors du poste (ex. Google Drive) ; phrase gardee ailleurs.
//   verify-backup [fichier] dechiffre la sauvegarde et verifie qu'elle correspond a la cle de l'application
//   restore <fichier.pem>   recree la cle de travail de ce poste depuis la sauvegarde (nouveau PC)
//   backup-status           sans phrase secrete : la sauvegarde est-elle connue et presente ? (code 0 / 3)
//   export-ci               ecrit la cle privee (PKCS#8 base64) sur la sortie standard, A REDIRIGER vers
//                           « gh secret set UPDATE_SIGNING_KEY » (jamais vers l'ecran). Depuis Windows PowerShell 5.1,
//                           passer par cmd (cmd /c "FreeRes.UpdateSign.exe export-ci | gh secret set UPDATE_SIGNING_KEY") :
//                           le tube PowerShell ajoute un BOM au secret, meme avec $OutputEncoding en ASCII (constate).
//   manifest <version> <url> <sha256> <notes> <sortie.json>
//   sign <latest.json>      ecrit latest.json.sig puis la reverifie avec la cle publique compilee dans l'application
// Lecture de la cle : UPDATE_SIGNING_KEY (CI), sinon %APPDATA%\FacilFreePC-signing\update-key.dpapi, sinon la
// sauvegarde (UPDATE_SIGNING_KEY_BACKUP ou emplacement memorise ; phrase : UPDATE_SIGNING_KEY_PASSPHRASE ou saisie
// masquee), qui restaure alors automatiquement la cle de travail du poste.
var store = new SigningKeyStore(
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FacilFreePC-signing"),
    UpdateKeys.PublicKey);
var backupEnv = Environment.GetEnvironmentVariable(SigningKeyStore.BackupEnv);

try
{
    switch (args.FirstOrDefault())
    {
        case "new-key":
            if (File.Exists(store.DpapiFile))
            {
                throw new InvalidOperationException($"une clé existe déjà ({store.DpapiFile}) : la remplacer rendrait les versions installées incapables de vérifier les mises à jour");
            }

            using (var key = ECDsa.Create(ECCurve.NamedCurves.nistP256))
            {
                Directory.CreateDirectory(store.Directory);
                File.WriteAllBytes(store.DpapiFile, ProtectedData.Protect(key.ExportPkcs8PrivateKey(), null, DataProtectionScope.CurrentUser));
                Console.WriteLine("Clé créée : " + store.DpapiFile);
                Console.WriteLine("Clé publique (UpdateKeys.PublicKey) : " + Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
                Console.WriteLine("IMPORTANT : sauvegarder maintenant avec « backup <fichier.pem> ».");
            }

            return 0;

        case "backup" when args.Length == 2:
        {
            var pkcs8 = LoadKey();
            var secret = ReadPassphrase(confirm: true);
            try
            {
                store.WriteBackup(pkcs8, args[1], secret);
            }
            finally
            {
                Array.Clear(secret);
                CryptographicOperations.ZeroMemory(pkcs8);
            }

            Console.WriteLine("Sauvegarde chiffrée écrite, relue et vérifiée : " + Path.GetFullPath(args[1]));
            Console.WriteLine("Conservez la phrase secrète AILLEURS que ce fichier (gestionnaire de mots de passe, papier).");
            return 0;
        }

        case "verify-backup" when args.Length is 1 or 2:
        {
            var path = args.Length == 2 ? Path.GetFullPath(args[1]) : store.ResolveBackupPath(backupEnv)
                ?? throw new InvalidOperationException("aucune sauvegarde connue : préciser le fichier");
            var secret = ReadPassphrase(confirm: false);
            try
            {
                CryptographicOperations.ZeroMemory(store.ImportEncrypted(File.ReadAllText(path, Encoding.ASCII), secret));
            }
            finally
            {
                Array.Clear(secret);
            }

            Console.WriteLine("Sauvegarde valide : déchiffrée et conforme à la clé publique de l'application (" + path + ")");
            return 0;
        }

        case "restore" when args.Length == 2:
        {
            var path = Path.GetFullPath(args[1]);
            var secret = ReadPassphrase(confirm: false);
            try
            {
                var pkcs8 = store.ImportEncrypted(File.ReadAllText(path, Encoding.ASCII), secret);
                store.SaveDpapi(pkcs8);
                CryptographicOperations.ZeroMemory(pkcs8);
            }
            finally
            {
                Array.Clear(secret);
            }

            store.RecordBackupLocation(path);
            Console.WriteLine("Clé restaurée sur ce poste : " + store.DpapiFile);
            return 0;
        }

        case "backup-status":
        {
            var (ok, message) = store.BackupStatus(backupEnv);
            Console.WriteLine(message);
            return ok ? 0 : 3;
        }

        case "export-ci":
            if (!Console.IsOutputRedirected)
            {
                throw new InvalidOperationException("sortie non redirigée : la clé privée ne s'affiche jamais à l'écran (utiliser « | gh secret set UPDATE_SIGNING_KEY »)");
            }

            Console.Out.Write(Convert.ToBase64String(LoadKey()));
            return 0;

        case "manifest" when args.Length == 6:
            UpdateVerifier.CheckInstallerUrl(args[2]);
            var manifest = new UpdateManifest(args[1], args[2], args[3].ToLowerInvariant(), args[4]);
            File.WriteAllBytes(args[5], JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions(UpdateVerifier.JsonOptions) { WriteIndented = true }));
            Console.WriteLine("Manifeste : " + args[5]);
            return 0;

        case "sign" when args.Length == 2:
            var bytes = File.ReadAllBytes(args[1]);
            using (var key = ECDsa.Create())
            {
                key.ImportPkcs8PrivateKey(LoadKey(), out _);
                var signature = Convert.ToBase64String(key.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
                File.WriteAllText(args[1] + ".sig", signature, Encoding.ASCII);
                var verified = UpdateVerifier.Verify(bytes, signature, UpdateKeys.PublicKey);   // la cle correspond a celle de l'application
                Console.WriteLine($"Signé et vérifié : version {verified.Version} -> {args[1]}.sig");
            }

            return 0;

        default:
            Console.Error.WriteLine("usage : new-key | backup <fichier.pem> | verify-backup [fichier] | restore <fichier.pem> | backup-status"
                + " | export-ci | manifest <version> <url> <sha256> <notes> <sortie> | sign <latest.json>");
            return 2;
    }
}
catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or CryptographicException or UpdateRejectedException or FormatException)
{
    Console.Error.WriteLine("ERREUR : " + ex.Message);
    return 1;
}

byte[] LoadKey() => store.Load(
    Environment.GetEnvironmentVariable(SigningKeyStore.KeyEnv),
    backupEnv,
    () => ReadPassphrase(confirm: false),
    message => Console.Error.WriteLine(message));

// Phrase secrete : variable UPDATE_SIGNING_KEY_PASSPHRASE (automatisation), sinon saisie MASQUEE au clavier.
// Jamais en argument de ligne de commande (visible dans la liste des processus et l'historique).
static char[] ReadPassphrase(bool confirm)
{
    if (Environment.GetEnvironmentVariable(SigningKeyStore.PassphraseEnv) is { Length: > 0 } fromEnv)
    {
        return fromEnv.ToCharArray();
    }

    if (Console.IsInputRedirected)
    {
        throw new InvalidOperationException($"phrase secrète requise : saisie au clavier (terminal interactif) ou variable {SigningKeyStore.PassphraseEnv}");
    }

    var first = Prompt("Phrase secrète de la sauvegarde : ");
    if (!confirm)
    {
        return first;
    }

    SigningKeyStore.CheckPassphrase(first);
    var second = Prompt("Confirmer la phrase secrète : ");
    var same = first.AsSpan().SequenceEqual(second);
    Array.Clear(second);
    if (!same)
    {
        Array.Clear(first);
        throw new InvalidOperationException("les deux saisies diffèrent");
    }

    return first;
}

static char[] Prompt(string label)
{
    Console.Error.Write(label);
    var buffer = new List<char>();
    while (true)
    {
        var k = Console.ReadKey(intercept: true);
        if (k.Key == ConsoleKey.Enter)
        {
            break;
        }

        if (k.Key == ConsoleKey.Backspace)
        {
            if (buffer.Count > 0)
            {
                buffer.RemoveAt(buffer.Count - 1);
            }

            continue;
        }

        if (!char.IsControl(k.KeyChar))
        {
            buffer.Add(k.KeyChar);
        }
    }

    Console.Error.WriteLine();
    var result = buffer.ToArray();
    buffer.Clear();
    return result;
}
