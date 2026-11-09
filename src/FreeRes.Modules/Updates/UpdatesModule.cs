using FreeRes.Core;
using FreeRes.Core.Actions;
using FreeRes.Core.Decisions;
using FreeRes.Core.Engine;
using FreeRes.Core.Platform;
using FreeRes.Core.Storage;

namespace FreeRes.Modules.Updates;

/// <summary>Etat affiche : version installee, verification hebdomadaire, derniere verification, version disponible.</summary>
public sealed record UpdateStatus(string CurrentVersion, bool WeeklyCheck, DateTimeOffset? CheckedAt, string? Available, string? Notes, string? LastError);

/// <summary>
/// Mises a jour signees :
///   * verification une fois par semaine si elle est activee (updates.weeklyCheck), ou a la demande (bouton) ;
///   * seule une version STRICTEMENT superieure, au manifeste signe par Facil, devient une decision ;
///   * « Appliquer » : telechargement dans le dossier de donnees (jamais %TEMP%), empreinte verifiee, fichier garde
///     ouvert en lecture seule (aucun remplacement possible) jusqu'au lancement de l'installeur, qui ferme FreePC,
///     installe pour l'utilisateur (aucun droit administrateur) et le relance.
/// </summary>
public sealed class UpdatesModule : IResourceModule, IStatusSource, IUpdateCheck
{
    public const string InstallUpdate = "InstallUpdate";
    public static readonly TimeSpan CheckInterval = TimeSpan.FromDays(7);
    private const string CheckedAtKey = "checkedAt";

    private readonly IUpdateSource _source;
    private readonly string _publicKey;
    private readonly string _currentVersion;
    private readonly bool _weeklyCheck;
    private readonly IProcessLauncher _launcher;
    private readonly TimeProvider _time;
    private readonly AppPaths _paths;
    private readonly ModuleState? _state;
    private readonly Lock _sync = new();
    private UpdateManifest? _available;
    private string? _lastError;

    public UpdatesModule(ModuleServices services, IUpdateSource? source = null, string? publicKey = null, string? currentVersion = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        _source = source ?? new HttpUpdateSource();
        _publicKey = publicKey ?? UpdateKeys.PublicKey;
        _currentVersion = currentVersion ?? AppInfo.Version;
        _weeklyCheck = services.Settings?.Updates.WeeklyCheck ?? true;
        _launcher = services.EffectiveLauncher;
        _time = services.Time;
        _paths = services.Paths;
        _state = services.State is null ? null : new ModuleState(services.State, Name);
    }

    public string Name => "updates";

    public object? CurrentStatus => new UpdateStatus(_currentVersion, _weeklyCheck, _state?.GetDate(CheckedAtKey), _available?.Version, _available?.Notes, _lastError);

    public async Task<ModuleEvaluation> EvaluateAsync(EngineContext context, CancellationToken cancellationToken)
    {
        var last = _state?.GetDate(CheckedAtKey);
        if (_weeklyCheck && (last is null || _time.GetUtcNow() - last >= CheckInterval))
        {
            await CheckAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_available is not { } m)
        {
            return ModuleEvaluation.Empty;
        }

        var notes = m.Notes.Length > 280 ? m.Notes[..279] + "…" : m.Notes;
        return new ModuleEvaluation(
            [
                new Proposal(
                    "update-" + m.Version.Replace('.', '-'),
                    $"Mettre à jour Facil FreePC vers la version {m.Version}",
                    $"Version installée : {_currentVersion}. {notes} FreePC se ferme, s'installe puis redémarre ; vos réglages et votre historique sont conservés.",
                    0,
                    Severity.Info,
                    new ActionRequest(InstallUpdate, new Dictionary<string, string> { ["version"] = m.Version })),
            ],
            []);
    }

    /// <summary>Verification demandee par l'utilisateur (meme si la verification hebdomadaire est desactivee).</summary>
    public async Task<string> CheckNowAsync(CancellationToken cancellationToken)
    {
        await CheckAsync(cancellationToken).ConfigureAwait(false);
        return _lastError is { } error
            ? error
            : _available is { } m ? $"Version {m.Version} disponible : une décision a été ajoutée." : $"Facil FreePC est à jour (version {_currentVersion}).";
    }

    private async Task CheckAsync(CancellationToken cancellationToken)
    {
        // Deux verifications simultanees (bouton + cycle) ne font qu'une lecture de plus ; le resultat est publie
        // d'un bloc sous verrou.
        UpdateManifest? available = null;
        string? error = null;
        var fetched = await _source.FetchManifestAsync(cancellationToken).ConfigureAwait(false);
        _state?.SetDate(CheckedAtKey, _time.GetUtcNow());
        if (fetched is not { } f)
        {
            error = "Vérification impossible : pas de connexion, ou aucune version publiée.";
        }
        else
        {
            try
            {
                var manifest = UpdateVerifier.Verify(f.Manifest, f.Signature, _publicKey);
                available = manifest.ParsedVersion > Version.Parse(_currentVersion) ? manifest : null;
            }
            catch (UpdateRejectedException ex)
            {
                error = "Mise à jour refusée : " + ex.Message;
            }
        }

        lock (_sync)
        {
            // Hors ligne : la version deja verifiee reste proposee ; refus : plus rien n'est propose.
            if (fetched is not null)
            {
                _available = available;
            }

            _lastError = error;
        }
    }

    public void RegisterActions(ActionCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        catalog.Register(new ActionDefinition(
            InstallUpdate,
            RequiresAdmin: false,
            p => p.TryGetValue("version", out var v) && _available is { } m && string.Equals(v, m.Version, StringComparison.Ordinal)
                ? null
                : "aucune mise à jour vérifiée pour cette version",
            (request, dryRun, ct) => InstallAsync(dryRun, ct)));
    }

    private async Task<ActionOutcome> InstallAsync(bool dryRun, CancellationToken cancellationToken)
    {
        if (_available is not { } m)
        {
            return new ActionOutcome(false, "aucune mise à jour vérifiée", Changed: false);
        }

        if (dryRun)
        {
            return new ActionOutcome(true, $"simulation : installation de la version {m.Version}", Changed: false);
        }

        var url = UpdateVerifier.CheckInstallerUrl(m.Url);
        var dir = Path.Combine(_paths.DataDirectory, "updates");
        Directory.CreateDirectory(dir);
        foreach (var old in Directory.EnumerateFiles(dir, "*.exe"))
        {
            File.Delete(old);   // anciens installeurs : seul le dernier telechargement sert
        }

        var installer = Path.Combine(dir, $"FacilFreePC-Setup-{m.Version}.exe");
        try
        {
            await _source.DownloadAsync(url, installer, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            return new ActionOutcome(false, "téléchargement impossible : " + ex.Message, Changed: false);
        }

        // Fichier ouvert en lecture seule et partage en lecture : personne ne peut le remplacer entre la
        // verification de l'empreinte et le lancement de l'installeur.
        using (var locked = new FileStream(installer, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try
            {
                UpdateVerifier.CheckSha256(locked, m.Sha256);
            }
            catch (UpdateRejectedException ex)
            {
                locked.Dispose();
                File.Delete(installer);
                return new ActionOutcome(false, ex.Message, Changed: false);
            }

            // /RELAUNCH=1 : l'installeur relance FreePC (en arriere-plan) une fois la mise a jour terminee.
            if (!_launcher.Launch(installer, ["/SILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/RELAUNCH=1"]))
            {
                return new ActionOutcome(false, "l'installeur n'a pas pu être lancé", Changed: false);
            }
        }

        return new ActionOutcome(true, $"Installation de la version {m.Version} lancée : FreePC se ferme puis redémarre.");
    }
}
