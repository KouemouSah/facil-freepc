using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FreeRes.Modules.Updates;

// Signature des mises a jour de Facil FreePC.
//   new-key                 cree la cle (refuse si elle existe), l'enregistre protegee par DPAPI (compte Windows
//                           courant) et affiche la cle PUBLIQUE a placer dans UpdateKeys.cs
//   export-ci               ecrit la cle privee (PKCS#8 base64) sur la sortie standard, A REDIRIGER vers
//                           « gh secret set UPDATE_SIGNING_KEY » (jamais vers l'ecran). Depuis Windows PowerShell 5.1,
//                           passer par cmd (cmd /c "FreeRes.UpdateSign.exe export-ci | gh secret set UPDATE_SIGNING_KEY") :
//                           le tube PowerShell ajoute un BOM au secret, meme avec $OutputEncoding en ASCII (constate).
//   manifest <version> <url> <sha256> <notes> <sortie.json>
//   sign <latest.json>      ecrit latest.json.sig puis la reverifie avec la cle publique compilee dans l'application
// Cle privee : variable UPDATE_SIGNING_KEY (CI) sinon fichier protege %APPDATA%\FacilFreePC-signing\update-key.dpapi.
var keyFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FacilFreePC-signing", "update-key.dpapi");

try
{
    switch (args.FirstOrDefault())
    {
        case "new-key":
            if (File.Exists(keyFile))
            {
                throw new InvalidOperationException($"une clé existe déjà ({keyFile}) : la remplacer rendrait les versions installées incapables de vérifier les mises à jour");
            }

            using (var key = ECDsa.Create(ECCurve.NamedCurves.nistP256))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(keyFile)!);
                File.WriteAllBytes(keyFile, ProtectedData.Protect(key.ExportPkcs8PrivateKey(), null, DataProtectionScope.CurrentUser));
                Console.WriteLine("Clé créée : " + keyFile);
                Console.WriteLine("Clé publique (UpdateKeys.PublicKey) : " + Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
            }

            return 0;

        case "export-ci":
            if (!Console.IsOutputRedirected)
            {
                throw new InvalidOperationException("sortie non redirigée : la clé privée ne s'affiche jamais à l'écran (utiliser « | gh secret set UPDATE_SIGNING_KEY »)");
            }

            Console.Out.Write(Convert.ToBase64String(LoadPrivateKey(keyFile)));
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
                key.ImportPkcs8PrivateKey(LoadPrivateKey(keyFile), out _);
                var signature = Convert.ToBase64String(key.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
                File.WriteAllText(args[1] + ".sig", signature, Encoding.ASCII);
                var verified = UpdateVerifier.Verify(bytes, signature, UpdateKeys.PublicKey);   // la cle correspond a celle de l'application
                Console.WriteLine($"Signé et vérifié : version {verified.Version} -> {args[1]}.sig");
            }

            return 0;

        default:
            Console.Error.WriteLine("usage : new-key | export-ci | manifest <version> <url> <sha256> <notes> <sortie> | sign <latest.json>");
            return 2;
    }
}
catch (Exception ex) when (ex is InvalidOperationException or IOException or CryptographicException or UpdateRejectedException or FormatException)
{
    Console.Error.WriteLine("ERREUR : " + ex.Message);
    return 1;
}

static byte[] LoadPrivateKey(string keyFile)
{
    if (Environment.GetEnvironmentVariable("UPDATE_SIGNING_KEY") is { Length: > 0 } ci)
    {
        // Windows PowerShell 5.1 prefixe d'un BOM UTF-8 (U+FEFF) toute chaine envoyee par « | » a un programme natif :
        // un secret cree par « export-ci | gh secret set » depuis ce shell le contient. Il n'appartient pas a la cle.
        try
        {
            return Convert.FromBase64String(ci.Trim().TrimStart('﻿').Trim());
        }
        catch (FormatException)
        {
            throw new InvalidOperationException("UPDATE_SIGNING_KEY n'est pas une clé PKCS#8 en base64 valide : recréer le secret avec « export-ci »");
        }
    }

    if (!File.Exists(keyFile))
    {
        throw new InvalidOperationException("clé de signature absente (UPDATE_SIGNING_KEY ou " + keyFile + ")");
    }

    return ProtectedData.Unprotect(File.ReadAllBytes(keyFile), null, DataProtectionScope.CurrentUser);
}
