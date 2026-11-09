using System.Globalization;
using System.Text;
using System.Text.Json;
using FreeRes.Core.Actions;
using FreeRes.Core.Decisions;
using FreeRes.Core.Engine;
using FreeRes.Core.Logging;
using FreeRes.Core.Platform;
using FreeRes.Core.Storage;
using FreeRes.Modules.Disk;

namespace FreeRes.Modules.Docker;

/// <summary>
/// Docker Desktop (backend WSL 2) — diagnostic du 2026-11-09 :
///   * le disque de donnees ext4 est monte SANS « discard » (et seulement Docker demarre) : chaque suppression laisse
///     ses blocs alloues dans le VHDX, qui ne retrecit jamais seul. Un fstrim (Docker demarre) est indispensable
///     avant tout compactage ;
///   * plancher incompressible ~2 GB (journal ext4 + metadonnees) : 7,70 GB de VHDX pour 5,74 GB de donnees.
/// Donc :
///   * AUTOMATIQUE : arret de la VM docker-desktop restee allumee sans Docker Desktop (2 observations a 10 min
///     d'ecart), fstrim quotidien et mesure toutes les 6 h quand Docker tourne ;
///   * PROPOSITIONS : purge du cache de build, des images inutilisees (JAMAIS les volumes), compactage du VHDX
///     (Docker arrete) selon l'espace mort REEL (VHDX - donnees - plancher), repli sur la taille si jamais mesure.
/// Module inactif si Docker Desktop n'est pas installe.
/// </summary>
public sealed class DockerModule : IResourceModule, IActionObserver
{
    public const string DockerPrune = "DockerPrune";
    public const string DockerTrim = "DockerTrim";
    public const string WslTerminate = "WslTerminate";
    public const string CompactDockerVhdx = "CompactDockerVhdx";

    internal const string OrphanSinceKey = "wslOrphanSince";
    internal const string TrimmedAtKey = "trimmedAt";
    internal const string TrimFailedAtKey = "trimFailedAt";
    internal const string CompactedAtKey = "compactedAt";
    internal const string DiskUsageKey = "diskUsage";

    /// <summary>Niveaux de purge autorises (liste fermee). Aucun ne touche aux volumes.</summary>
    public static IReadOnlyList<string> PruneLevels { get; } = ["builder", "unused-images", "dangling"];

    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(30);
    private readonly DockerOptions _options;
    private readonly IDockerHost _host;
    private readonly IProcessRunner _processes;
    private readonly EventLog _log;
    private readonly TimeProvider _time;
    private readonly ModuleState? _state;

    public DockerModule(ModuleServices services, DockerOptions? options = null, IDockerHost? host = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        _options = options ?? DockerOptions.Default;
        var errors = _options.Validate();
        if (errors.Count > 0)
        {
            throw new InvalidOperationException("Configuration du module Docker invalide : " + string.Join(" ; ", errors));
        }

        _host = host ?? new WindowsDockerHost();
        _processes = services.Processes;
        _log = services.Log;
        _time = services.Time;
        _state = services.State is null ? null : new ModuleState(services.State, Name);
    }

    public string Name => "docker";

    /// <summary>Derniere mesure memorisee (null si jamais mesure ou etat illisible).</summary>
    public DockerDiskUsage? LastUsage
    {
        get
        {
            if (_state?.Get(DiskUsageKey) is not { } json)
            {
                return null;
            }

            try
            {
                return JsonSerializer.Deserialize<DockerDiskUsage>(json);
            }
            catch (JsonException)
            {
                return null;   // etat corrompu : sera remplace a la prochaine mesure
            }
        }
    }

    public async Task<ModuleEvaluation> EvaluateAsync(EngineContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!_host.IsInstalled)
        {
            return ModuleEvaluation.Empty;
        }

        var now = _time.GetLocalNow();
        var auto = new List<ActionRequest>();
        var proposals = new List<Proposal>();
        var severity = context.Snapshot.SystemDriveFreeGB < context.Thresholds.DiskFreeWarnGB ? Severity.Warn : Severity.Info;

        if (_host.IsDockerRunning())
        {
            _state?.SetDate(OrphanSinceKey, null);
            if (IsTrimDue(now))
            {
                auto.Add(ActionRequest.Of(DockerTrim));
            }

            var usage = LastUsage;
            if (usage is null || (now - usage.MeasuredAt).TotalHours >= _options.MeasureEveryHours)
            {
                usage = await MeasureAsync(now, cancellationToken).ConfigureAwait(false) ?? usage;
            }

            AddPruneProposals(usage, severity, proposals);
        }
        else
        {
            if (await IsOrphanVmConfirmedAsync(now, cancellationToken).ConfigureAwait(false))
            {
                auto.Add(new ActionRequest(WslTerminate, new Dictionary<string, string> { ["distro"] = _options.WslDistro }));
            }

            AddCompactProposal(severity, proposals);
        }

        return new ModuleEvaluation(proposals, auto);
    }

    public void OnActionCompleted(ActionRequest request, ActionResult result)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(result);
        if (result.DryRun || _state is null)
        {
            return;
        }

        var now = _time.GetLocalNow();
        switch (request.Type)
        {
            case DockerTrim when result.Ok:
                _state.SetDate(TrimmedAtKey, now);
                _state.SetDate(TrimFailedAtKey, null);
                break;
            case DockerTrim:
                _state.SetDate(TrimFailedAtKey, now);   // pas de trimmedAt : aucun compactage propose sur un trim rate
                break;
            case DockerPrune when result.Ok:
                // La place liberee n'est rendue au VHDX qu'apres un fstrim, et la mesure est perimee :
                // trim et re-mesure au prochain cycle (les propositions de purge disparaissent aussitot).
                _state.SetDate(TrimmedAtKey, null);
                _state.Set(DiskUsageKey, null);
                break;
            case CompactDockerVhdx when result.Ok:
                _state.SetDate(CompactedAtKey, now);
                break;
            default:
                break;
        }
    }

    public void RegisterActions(ActionCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        catalog.Register(new ActionDefinition(DockerPrune, RequiresAdmin: false, ValidatePrune, ExecutePruneAsync));
        catalog.Register(new ActionDefinition(DockerTrim, RequiresAdmin: false, DiskModule.NoParameters, (_, dryRun, ct) => ExecuteTrimAsync(dryRun, ct)));
        catalog.Register(new ActionDefinition(WslTerminate, RequiresAdmin: false, ValidateTerminate, ExecuteTerminateAsync));
        catalog.Register(new ActionDefinition(CompactDockerVhdx, RequiresAdmin: true, DiskModule.NoParameters, (_, dryRun, ct) => ExecuteCompactAsync(dryRun, ct)));
    }

    /// <summary>Script diskpart de compactage du VHDX (lecture seule : seuls les blocs trimes sont rendus a Windows).</summary>
    public static string DiskpartCompactScript(string vhdxPath) =>
        $"select vdisk file=\"{vhdxPath}\"\r\nattach vdisk readonly\r\ncompact vdisk\r\ndetach vdisk\r\n";

    /// <summary>Script de secours : detache le VHDX si le compactage a echoue apres l'attachement.</summary>
    public static string DiskpartDetachScript(string vhdxPath) =>
        $"select vdisk file=\"{vhdxPath}\"\r\ndetach vdisk\r\n";

    internal static string? ValidatePrune(IReadOnlyDictionary<string, string> p)
    {
        if (p.Count != 1 || !p.TryGetValue("level", out var level))
        {
            return "un seul parametre « level » attendu";
        }

        return PruneLevels.Contains(level, StringComparer.Ordinal)
            ? null
            : $"niveau non autorise : '{level}' (builder, unused-images ou dangling ; les volumes ne sont JAMAIS purges)";
    }

    private string? ValidateTerminate(IReadOnlyDictionary<string, string> p) =>
        p.Count == 1 && p.TryGetValue("distro", out var d) && string.Equals(d, _options.WslDistro, StringComparison.Ordinal)
            ? null
            : $"distribution non autorisee (seule '{_options.WslDistro}' peut etre arretee)";

    // ------------------------------------------------------------------ evaluation

    private bool IsTrimDue(DateTimeOffset now)
    {
        var trimmed = _state?.GetDate(TrimmedAtKey);
        if (trimmed is not null && (now - trimmed.Value).TotalHours < _options.TrimEveryHours)
        {
            return false;
        }

        // Un trim en echec n'est retente qu'apres un delai : pas un wsl.exe a chaque cycle.
        var failed = _state?.GetDate(TrimFailedAtKey);
        return failed is null || (now - failed.Value).TotalHours >= _options.TrimRetryHours;
    }

    /// <summary>
    /// VM docker-desktop allumee sans Docker Desktop : arretee a la 2e observation espacee d'au moins 10 min
    /// (evite de la tuer pendant un demarrage ou un arret de Docker).
    /// </summary>
    private async Task<bool> IsOrphanVmConfirmedAsync(DateTimeOffset now, CancellationToken ct)
    {
        var running = await RunningDistrosAsync(ct).ConfigureAwait(false);
        if (!running.Contains(_options.WslDistro, StringComparer.OrdinalIgnoreCase))
        {
            _state?.SetDate(OrphanSinceKey, null);
            return false;
        }

        var since = _state?.GetDate(OrphanSinceKey);
        if (since is null)
        {
            _state?.SetDate(OrphanSinceKey, now);
            return false;
        }

        if ((now - since.Value).TotalMinutes < _options.OrphanConfirmMinutes)
        {
            return false;
        }

        _state?.SetDate(OrphanSinceKey, null);
        return true;
    }

    private async Task<IReadOnlyList<string>> RunningDistrosAsync(CancellationToken ct)
    {
        if (_host.WslExe is not { } wsl)
        {
            return [];
        }

        // Sans redirection de console, wsl.exe ecrit en UTF-16.
        var r = await _processes.RunAsync(wsl, ["--list", "--running", "--quiet"], TimeSpan.FromSeconds(20), ct, Encoding.Unicode).ConfigureAwait(false);
        return r.TimedOut || r.ExitCode != 0 ? [] : DockerParsers.ParseWslRunning(r.StdOut);
    }

    /// <summary>Mesure Docker demarre : df -B1 dans la distribution + docker system df. Null si df echoue.</summary>
    private async Task<DockerDiskUsage?> MeasureAsync(DateTimeOffset now, CancellationToken ct)
    {
        long? used = null;
        if (_host.WslExe is { } wsl)
        {
            var r = await _processes.RunAsync(wsl, ["-d", _options.WslDistro, "-u", "root", "--", "df", "-B1", _options.DataMount], ReadTimeout, ct).ConfigureAwait(false);
            used = r.TimedOut || r.ExitCode != 0 ? null : DockerParsers.ParseDfUsedBytes(r.StdOut);
        }

        if (used is null)
        {
            _log.Write(LogLevel.Warn, Name, "Mesure du disque Docker impossible (wsl df)");
            return null;
        }

        var df = new DockerSystemDf(null, null, null, null, null, null, null);
        if (_host.DockerCli is { } cli)
        {
            var d = await _processes.RunAsync(cli, ["system", "df", "--format", "{{.Type}}|{{.Size}}|{{.Reclaimable}}"], ReadTimeout, ct).ConfigureAwait(false);
            if (!d.TimedOut && d.ExitCode == 0)
            {
                df = DockerParsers.ParseSystemDf(d.StdOut);
            }
        }

        var usage = new DockerDiskUsage(
            now,
            DockerParsers.ToGB(used.Value),
            VhdxGB(),
            df.ImagesGB,
            df.ImagesReclaimableGB,
            df.ContainersGB,
            df.VolumesGB,
            df.VolumesUnusedGB,
            df.BuildCacheGB,
            df.BuildCacheReclaimableGB);
        _state?.Set(DiskUsageKey, JsonSerializer.Serialize(usage));
        _log.Write(
            LogLevel.Info,
            Name,
            string.Create(CultureInfo.InvariantCulture, $"Disque Docker : VHDX {Show(usage.VhdxGB)} GB, donnees {usage.InsideUsedGB} GB (images {Show(usage.ImagesGB)}, conteneurs {Show(usage.ContainersGB)}, volumes {Show(usage.VolumesGB)}, cache build {Show(usage.BuildCacheGB)})"));
        return usage;
    }

    private void AddPruneProposals(DockerDiskUsage? usage, Severity severity, List<Proposal> proposals)
    {
        if (usage?.BuildCacheReclaimableGB is { } build && build >= _options.BuildCacheProposeGB)
        {
            proposals.Add(new Proposal(
                "docker-builder",
                string.Create(CultureInfo.InvariantCulture, $"Purger le cache de build Docker (> {_options.BuilderPruneUntilHours / 24} jours)"),
                "Les volumes ne sont JAMAIS touches. Le fstrim suivant rendra la place au disque Docker.",
                build,
                severity,
                new ActionRequest(DockerPrune, new Dictionary<string, string> { ["level"] = "builder" })));
        }

        if (usage?.ImagesReclaimableGB is { } images && images >= _options.UnusedImagesProposeGB)
        {
            proposals.Add(new Proposal(
                "docker-images",
                string.Create(CultureInfo.InvariantCulture, $"Supprimer les images Docker inutilisees depuis {_options.UnusedImagesUntilHours / 24} jours"),
                "Elles seront re-telechargees si un compose les reclame. Volumes intacts.",
                images,
                severity,
                new ActionRequest(DockerPrune, new Dictionary<string, string> { ["level"] = "unused-images" })));
        }
    }

    private void AddCompactProposal(Severity severity, List<Proposal> proposals)
    {
        if (VhdxGB() is not { } vhdx)
        {
            return;
        }

        var trimmed = _state?.GetDate(TrimmedAtKey);
        var compacted = _state?.GetDate(CompactedAtKey);
        if (compacted is not null && (trimmed is null || trimmed <= compacted))
        {
            return;   // deja compacte : rien de plus a recuperer avant un nouveau fstrim
        }

        var usage = LastUsage;
        if (usage is not null && trimmed is not null)
        {
            // Gain reel = VHDX - donnees - plancher ext4 ; n'a de sens qu'apres un fstrim reussi.
            var slack = DockerParsers.VhdxSlackGB(vhdx, usage.InsideUsedGB, _options.VhdxMetadataOverheadGB);
            if (slack >= _options.CompactMinGainGB)
            {
                proposals.Add(new Proposal(
                    "docker-vhdx",
                    string.Create(CultureInfo.InvariantCulture, $"Compacter le disque Docker ({vhdx} GB, ~{slack} GB d'espace mort)"),
                    string.Create(CultureInfo.InvariantCulture, $"Donnees reelles {usage.InsideUsedGB} GB + plancher ext4 ~{_options.VhdxMetadataOverheadGB} GB. Compactage diskpart, Docker arrete (WSL est arrete pendant l'operation). Demande l'autorisation administrateur."),
                    slack,
                    severity,
                    ActionRequest.Of(CompactDockerVhdx)));
            }
        }
        else if (vhdx >= _options.CompactFallbackVhdxGB)
        {
            // Jamais mesure (ou jamais trime) : repli sur la taille absolue, gain inconnu, estimation prudente.
            proposals.Add(new Proposal(
                "docker-vhdx",
                string.Create(CultureInfo.InvariantCulture, $"Compacter le disque Docker ({vhdx} GB, gain non mesure)"),
                "Aucune mesure interne encore : demarrez Docker une fois pour mesurer et trimer. Compactage diskpart, Docker arrete. Demande l'autorisation administrateur.",
                Math.Round(vhdx * 0.25, 2),
                severity,
                ActionRequest.Of(CompactDockerVhdx)));
        }
    }

    // ------------------------------------------------------------------ actions

    private async Task<ActionOutcome> ExecutePruneAsync(ActionRequest request, bool dryRun, CancellationToken ct)
    {
        string[] args = request.Parameters["level"] switch
        {
            "builder" => ["builder", "prune", "-f", "--filter", string.Create(CultureInfo.InvariantCulture, $"until={_options.BuilderPruneUntilHours}h")],
            "unused-images" => ["image", "prune", "-a", "-f", "--filter", string.Create(CultureInfo.InvariantCulture, $"until={_options.UnusedImagesUntilHours}h")],
            "dangling" => ["image", "prune", "-f"],
            var other => throw new ActionRejectedException($"Niveau DockerPrune non autorise : {other}"),   // deja refuse par la validation
        };

        if (_host.DockerCli is not { } cli)
        {
            return new ActionOutcome(false, "docker.exe introuvable");
        }

        if (!_host.IsDockerRunning())
        {
            return new ActionOutcome(false, "Docker Desktop ne tourne pas");
        }

        if (dryRun)
        {
            return new ActionOutcome(true, "simulation : docker " + string.Join(' ', args));
        }

        var r = await _processes.RunAsync(cli, args, TimeSpan.FromMinutes(15), ct).ConfigureAwait(false);
        var tail = string.Join(' ', r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).TakeLast(2));
        return r.TimedOut
            ? new ActionOutcome(false, "docker prune : delai de 15 min depasse")
            : new ActionOutcome(r.ExitCode == 0, r.ExitCode == 0 ? tail : $"docker prune a echoue (code {r.ExitCode}) {r.StdErr}".Trim());
    }

    /// <summary>fstrim : signale au VHDX les blocs liberes (montage sans « discard »). Sur, en ligne, sans perte.</summary>
    private async Task<ActionOutcome> ExecuteTrimAsync(bool dryRun, CancellationToken ct)
    {
        if (!_host.IsDockerRunning())
        {
            return new ActionOutcome(false, "Docker Desktop ne tourne pas (disque de donnees non monte)");
        }

        if (_host.WslExe is not { } wsl)
        {
            return new ActionOutcome(false, "wsl.exe introuvable");
        }

        if (dryRun)
        {
            return new ActionOutcome(true, $"simulation : fstrim {_options.DataMount}");
        }

        var r = await _processes.RunAsync(wsl, ["-d", _options.WslDistro, "-u", "root", "--", "fstrim", _options.DataMount], TimeSpan.FromMinutes(10), ct).ConfigureAwait(false);
        return r.TimedOut
            ? new ActionOutcome(false, "fstrim : delai de 10 min depasse")
            : new ActionOutcome(r.ExitCode == 0, $"fstrim {_options.DataMount} (code {r.ExitCode}) {r.StdErr}".Trim());
    }

    private async Task<ActionOutcome> ExecuteTerminateAsync(ActionRequest request, bool dryRun, CancellationToken ct)
    {
        var distro = request.Parameters["distro"];
        if (_host.IsDockerRunning())
        {
            // Docker a pu demarrer depuis l'evaluation : la VM n'est plus orpheline, l'arreter casserait Docker.
            return new ActionOutcome(false, $"Docker Desktop tourne : la VM {distro} n'est pas orpheline");
        }

        if (_host.WslExe is not { } wsl)
        {
            return new ActionOutcome(false, "wsl.exe introuvable");
        }

        if (dryRun)
        {
            return new ActionOutcome(true, $"simulation : wsl --terminate {distro}");
        }

        var r = await _processes.RunAsync(wsl, ["--terminate", distro], TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        return new ActionOutcome(r.ExitCode == 0 && !r.TimedOut, $"VM {distro} orpheline arretee (code {r.ExitCode})");
    }

    /// <summary>
    /// Compactage par diskpart (present sur tout Windows, contrairement a Optimize-VHD qui exige le module Hyper-V).
    /// Uniquement le VHDX DETECTE (jamais un chemin recu en parametre), Docker arrete, WSL arrete avant.
    /// </summary>
    private async Task<ActionOutcome> ExecuteCompactAsync(bool dryRun, CancellationToken ct)
    {
        if (_host.IsDockerRunning())
        {
            return new ActionOutcome(false, "Docker Desktop tourne : arretez-le avant le compactage");
        }

        if (_host.DetectVhdx() is not { } vhdx)
        {
            return new ActionOutcome(false, "Disque Docker (VHDX) introuvable");
        }

        if (!IsScriptSafe(vhdx))
        {
            return new ActionOutcome(false, $"Chemin du VHDX non pris en charge par diskpart (caracteres non ASCII ou guillemets) : {vhdx}");
        }

        var before = _host.FileBytes(vhdx) ?? 0;
        if (dryRun)
        {
            return new ActionOutcome(true, string.Create(CultureInfo.InvariantCulture, $"simulation : compactage diskpart de {vhdx} ({DockerParsers.ToGB(before)} GB)"));
        }

        if (_host.WslExe is { } wsl)
        {
            var shutdown = await _processes.RunAsync(wsl, ["--shutdown"], TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);
            if (shutdown.TimedOut || shutdown.ExitCode != 0)
            {
                return new ActionOutcome(false, $"wsl --shutdown a echoue (code {shutdown.ExitCode}) : VHDX peut-etre encore utilise, compactage annule");
            }

            if (_options.WslShutdownSettleSeconds > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(_options.WslShutdownSettleSeconds), ct).ConfigureAwait(false);
            }
        }

        if (_host.IsDockerRunning())
        {
            return new ActionOutcome(false, "Docker Desktop a redemarre pendant la preparation : compactage annule");
        }

        var diskpart = Path.Combine(Environment.SystemDirectory, "diskpart.exe");
        var compact = await RunDiskpartAsync(diskpart, DiskpartCompactScript(vhdx), TimeSpan.FromMinutes(60), ct).ConfigureAwait(false);
        if (compact.TimedOut || compact.ExitCode != 0)
        {
            // Ne jamais laisser le VHDX attache : Docker ne pourrait plus demarrer.
            var detach = await RunDiskpartAsync(diskpart, DiskpartDetachScript(vhdx), TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);
            return new ActionOutcome(false, compact.TimedOut
                ? $"diskpart : delai de 60 min depasse (detachement de secours : code {detach.ExitCode})"
                : $"diskpart a echoue (code {compact.ExitCode}, detachement de secours : code {detach.ExitCode})");
        }

        var after = _host.FileBytes(vhdx) ?? before;
        return new ActionOutcome(true, string.Create(CultureInfo.InvariantCulture, $"VHDX {DockerParsers.ToGB(before)} -> {DockerParsers.ToGB(after)} GB"));
    }

    /// <summary>
    /// Script transmis par l'ENTREE STANDARD, jamais par fichier : cette action s'execute en administrateur, et un
    /// fichier de script dans %TEMP% (modifiable par l'utilisateur) pourrait etre remplace entre son ecriture et
    /// sa lecture par diskpart (ex. « select disk 0 / clean » : effacement du disque avec les droits admin).
    /// </summary>
    private Task<ProcessResult> RunDiskpartAsync(string diskpart, string script, TimeSpan timeout, CancellationToken ct) =>
        _processes.RunAsync(diskpart, [], timeout, ct, standardInput: script);

    // Script ecrit en ASCII : un caractere non ASCII serait altere, un guillemet casserait la commande.
    private static bool IsScriptSafe(string path) => path.All(c => c is >= ' ' and <= '~' && c != '"');

    private double? VhdxGB() => _host.DetectVhdx() is { } path && _host.FileBytes(path) is { } bytes ? DockerParsers.ToGB(bytes) : null;

    private static string Show(double? gb) => gb is { } v ? v.ToString(CultureInfo.InvariantCulture) : "?";
}
