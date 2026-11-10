using System.Security.Cryptography;
using System.Text;

namespace FreeRes.UpdateSign.Tests;

/// <summary>
/// Cle de signature : sauvegarde chiffree, restauration, lecture automatique. Chaque test cree SA cle et SON dossier
/// temporaire : la vraie cle du poste de publication n'est jamais touchee.
/// </summary>
public sealed class SigningKeyStoreTests : IDisposable
{
    private const string Phrase = "cheval correct pile agrafe 2026";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "freeres-sign-tests-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _pkcs8;
    private readonly SigningKeyStore _store;

    public SigningKeyStoreTests()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        _pkcs8 = key.ExportPkcs8PrivateKey();
        _store = new SigningKeyStore(Path.Combine(_root, "poste"), Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string Backup => Path.Combine(_root, "drive", "cle.pem");

    private static char[] NoPrompt() => throw new InvalidOperationException("phrase secrète demandée alors qu'elle ne devait pas l'être");

    [Fact]
    public void Secret_CI_avec_BOM_et_fin_de_ligne_est_lu()
    {
        var value = "﻿" + Convert.ToBase64String(_pkcs8) + "\r\n";
        Assert.Equal(_pkcs8, SigningKeyStore.ParseCiKey(value));
    }

    [Fact]
    public void Secret_CI_invalide_donne_un_message_explicite()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => SigningKeyStore.ParseCiKey("pas#une*cle"));
        Assert.Contains("export-ci", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Sauvegarde_puis_dechiffrement_redonnent_la_meme_cle()
    {
        _store.WriteBackup(_pkcs8, Backup, Phrase);
        Assert.Equal(_pkcs8, _store.ImportEncrypted(File.ReadAllText(Backup), Phrase));
    }

    [Fact]
    public void La_sauvegarde_est_chiffree_et_ne_contient_pas_la_cle_en_clair()
    {
        _store.WriteBackup(_pkcs8, Backup, Phrase);
        var text = File.ReadAllText(Backup);
        Assert.StartsWith("-----BEGIN ENCRYPTED PRIVATE KEY-----", text, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(_pkcs8)[..40], text, StringComparison.Ordinal);
    }

    [Fact]
    public void Mauvaise_phrase_secrete_est_refusee_clairement()
    {
        _store.WriteBackup(_pkcs8, Backup, Phrase);
        var ex = Assert.Throws<InvalidOperationException>(() => _store.ImportEncrypted(File.ReadAllText(Backup), "une autre phrase de seize+"));
        Assert.Contains("phrase secrète incorrecte", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Phrase_secrete_trop_courte_est_refusee_et_rien_n_est_ecrit()
    {
        Assert.Throws<InvalidOperationException>(() => _store.WriteBackup(_pkcs8, Backup, "court"));
        Assert.False(File.Exists(Backup));
    }

    [Fact]
    public void Une_sauvegarde_existante_n_est_jamais_ecrasee()
    {
        _store.WriteBackup(_pkcs8, Backup, Phrase);
        var before = File.ReadAllBytes(Backup);
        Assert.Throws<InvalidOperationException>(() => _store.WriteBackup(_pkcs8, Backup, Phrase));
        Assert.Equal(before, File.ReadAllBytes(Backup));
    }

    [Fact]
    public void Une_cle_etrangere_a_l_application_est_refusee()
    {
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var foreign = other.ExportPkcs8PrivateKey();
        Assert.Throws<InvalidOperationException>(() => _store.WriteBackup(foreign, Backup, Phrase));
        Assert.False(Directory.Exists(Path.GetDirectoryName(Backup)));                // refus AVANT toute ecriture

        // Sauvegarde valide d'une AUTRE cle : dechiffrable, mais refusee car elle ne signerait rien d'acceptable.
        Directory.CreateDirectory(Path.GetDirectoryName(Backup)!);
        File.WriteAllText(Backup + ".autre", SigningKeyStore.ExportEncrypted(foreign, Phrase));
        var ex = Assert.Throws<InvalidOperationException>(() => _store.ImportEncrypted(File.ReadAllText(Backup + ".autre"), Phrase));
        Assert.Contains("ne correspond PAS", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Un_fichier_qui_n_est_pas_une_sauvegarde_est_refuse()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => _store.ImportEncrypted("bonjour", Phrase));
        Assert.Contains("ENCRYPTED PRIVATE KEY", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Chargement_le_secret_CI_est_prioritaire()
    {
        Assert.Equal(_pkcs8, _store.Load(Convert.ToBase64String(_pkcs8), null, NoPrompt, _ => { }));
    }

    [Fact]
    public void Chargement_la_cle_du_poste_est_utilisee_sans_phrase_secrete()
    {
        _store.SaveDpapi(_pkcs8);
        Assert.Equal(_pkcs8, _store.Load(null, null, NoPrompt, _ => { }));
    }

    [Fact]
    public void Chargement_depuis_la_sauvegarde_restaure_la_cle_du_poste()
    {
        _store.WriteBackup(_pkcs8, Backup, Phrase);   // memorise l'emplacement
        var prompts = 0;
        var logs = new List<string>();

        Assert.Equal(_pkcs8, _store.Load(null, null, () => { prompts++; return Phrase.ToCharArray(); }, logs.Add));
        Assert.True(File.Exists(_store.DpapiFile));
        Assert.Contains(logs, l => l.Contains("restaurée", StringComparison.Ordinal));

        // Signature suivante : cle du poste, la phrase n'est plus demandee.
        Assert.Equal(_pkcs8, _store.Load(null, null, NoPrompt, _ => { }));
        Assert.Equal(1, prompts);
    }

    [Fact]
    public void Chargement_mauvaise_phrase_ne_cree_pas_de_cle_sur_le_poste()
    {
        _store.WriteBackup(_pkcs8, Backup, Phrase);
        Assert.Throws<InvalidOperationException>(() => _store.Load(null, null, () => "une autre phrase de seize+".ToCharArray(), _ => { }));
        Assert.False(File.Exists(_store.DpapiFile));
    }

    [Fact]
    public void Chargement_la_variable_de_sauvegarde_prime_sur_l_emplacement_memorise()
    {
        var other = new SigningKeyStore(Path.Combine(_root, "autre-poste"), Convert.ToBase64String(PublicOf(_pkcs8)));
        _store.WriteBackup(_pkcs8, Backup, Phrase);
        other.RecordBackupLocation(Path.Combine(_root, "inexistant.pem"));
        Assert.Equal(_pkcs8, other.Load(null, Backup, () => Phrase.ToCharArray(), _ => { }));
    }

    [Fact]
    public void Chargement_sans_aucune_source_indique_comment_faire()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => _store.Load(null, null, NoPrompt, _ => { }));
        Assert.Contains(SigningKeyStore.BackupEnv, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void La_cle_du_poste_n_est_jamais_ecrasee()
    {
        _store.SaveDpapi(_pkcs8);
        Assert.Throws<InvalidOperationException>(() => _store.SaveDpapi(_pkcs8));
    }

    [Fact]
    public void Etat_de_la_sauvegarde_sans_phrase_secrete()
    {
        Assert.False(_store.BackupStatus(null).Ok);                                   // jamais sauvegardee

        _store.RecordBackupLocation(Backup);
        Assert.Contains("introuvable", _store.BackupStatus(null).Message, StringComparison.Ordinal);

        Directory.CreateDirectory(Path.GetDirectoryName(Backup)!);
        File.WriteAllText(Backup, "texte quelconque", Encoding.ASCII);
        Assert.False(_store.BackupStatus(null).Ok);                                   // pas une cle chiffree

        File.Delete(Backup);
        _store.WriteBackup(_pkcs8, Backup, Phrase);
        Assert.True(_store.BackupStatus(null).Ok);
    }

    private static byte[] PublicOf(byte[] pkcs8)
    {
        using var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(pkcs8, out _);
        return key.ExportSubjectPublicKeyInfo();
    }
}
