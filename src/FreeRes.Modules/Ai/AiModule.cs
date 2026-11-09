using System.Globalization;
using FreeRes.Core.Actions;
using FreeRes.Core.Decisions;
using FreeRes.Core.Engine;
using FreeRes.Core.Platform;
using FreeRes.Modules.Disk;

namespace FreeRes.Modules.Ai;

/// <summary>
/// Application d'IA locale connue. Detection par CHEMIN (jamais par le seul nom : Â« LMS.exe Â» est aussi un
/// service Intel, constate le 2026-11-09). <see cref="ActionsVerified"/> : seules les applications dont l'interface
/// de dechargement a ete verifiee ont des actions (Ollama : API documentee) ; les autres sont mesurees uniquement.
/// </summary>
public sealed record AiAppDefinition(string Id, string Name, IReadOnlyList<string> InstallDirs, IReadOnlyList<string> ModelDirs, bool ActionsVerified);

/// <summary>Etat d'une application d'IA (affiche par l'interface).</summary>
public sealed record AiAppStatus(
    string Id,
    string Name,
    bool Installed,
    int ProcessCount,
    long WorkingSetBytes,
    string? ModelsDir,
    long ModelsDiskBytes,
    IReadOnlyList<OllamaLoadedModel> LoadedModels,
    int InstalledModelCount,
    string Note)
{
    public double WorkingSetGB => Math.Round(WorkingSetBytes / 1073741824d, 2);

    public double ModelsDiskGB => Math.Round(ModelsDiskBytes / 1073741824d, 2);

    /// <summary>Executable principal (source de l'icone), ou null. Non expose.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? IconImage { get; init; }
}

/// <summary>Acces systeme du module (remplace par un faux dans les tests).</summary>
public interface IAiPlatform
{
    bool DirectoryExists(string path);

    long DirectorySize(string path);

    /// <summary>Somme du working set des processus dont l'image est sous <paramref name="folder"/>, et leur nombre.</summary>
    (int Count, long Bytes) ProcessesUnder(string folder);

    /// <summary>Executables situes directement dans <paramref name="folder"/> (noms de fichier).</summary>
    IReadOnlyList<string> ExecutablesIn(string folder);
}

public sealed class WindowsAiPlatform : IAiPlatform
{
    public bool DirectoryExists(string path) => Directory.Exists(path);

    public IReadOnlyList<string> ExecutablesIn(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder, "*.exe", SearchOption.TopDirectoryOnly).Select(Path.GetFileName).OfType<string>().ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public long DirectorySize(string path) => SafeCleaner.FolderSize(path).Bytes;

    public (int Count, long Bytes) ProcessesUnder(string folder)
    {
        var prefix = folder.TrimEnd('\\') + "\\";
        var count = 0;
        long bytes = 0;
        foreach (var p in System.Diagnostics.Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    // Image lue par QueryFullProcessImageName (droits limites, pas de chargement des modules).
                    var path = Apps.WindowsProcessControl.ImagePath(p.Id);
                    if (path is not null && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        count++;
                        bytes += p.WorkingSet64;
                    }
                }
                catch (InvalidOperationException)
                {
                    // processus termine entre l'enumeration et la lecture
                }
            }
        }

        return (count, bytes);
    }
}

/// <summary>
/// Applications d'IA locales :
///   * Ollama (natif ou dans Docker, via son API en boucle locale) : modeles charges, memoire, memoire graphique ;
///     sous tension memoire -> proposition de DECHARGER un modele (il se rechargera à la prochaine demande) ;
///   * LM Studio, GPT4All, Jan : detectes et mesures ; aucune action tant qu'elle n'est pas verifiee en reel ;
///   * disque presque plein : proposition d'OUVRIR le dossier des modeles. JAMAIS de suppression de modele.
/// </summary>
public sealed class AiModule : IResourceModule, IStatusSource, IIconSource
{
    public const string UnloadOllamaModel = "UnloadOllamaModel";
    public const string OpenModelsFolder = "OpenModelsFolder";
    private static readonly TimeSpan SizeCacheTtl = TimeSpan.FromHours(6);

    private readonly IAiPlatform _platform;
    private readonly IOllamaClient _ollama;
    private readonly IProcessLauncher _launcher;
    private readonly TimeProvider _time;
    private readonly IReadOnlyList<AiAppDefinition> _apps;
    private readonly Dictionary<string, (DateTimeOffset At, long Bytes)> _sizeCache = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<AiAppStatus> _last = [];

    public AiModule(ModuleServices services, IAiPlatform? platform = null, IOllamaClient? ollama = null, IReadOnlyList<AiAppDefinition>? apps = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        _platform = platform ?? new WindowsAiPlatform();
        _ollama = ollama ?? new HttpOllamaClient(Environment.GetEnvironmentVariable("OLLAMA_HOST"));
        _launcher = services.EffectiveLauncher;
        _time = services.Time;
        _apps = apps ?? DefaultApps();
    }

    public string Name => "ai";

    public IReadOnlyList<AiAppStatus> LastStatus => Volatile.Read(ref _last);

    public object? CurrentStatus => LastStatus;

    public string? IconPath(string id) => LastStatus.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.Ordinal))?.IconImage;

    /// <summary>
    /// Executable principal d'un dossier d'installation : jamais un desinstalleur ni un programme de mise a jour ;
    /// celui dont le nom contient celui de l'application (lettres et chiffres) d'abord, sinon le premier par ordre alphabetique.
    /// </summary>
    internal static string? MainExecutable(string appName, IReadOnlyList<string> exeNames)
    {
        static string Key(string s) => new([.. s.ToLowerInvariant().Where(char.IsLetterOrDigit)]);
        var candidates = exeNames
            .Where(n => !n.StartsWith("unins", StringComparison.OrdinalIgnoreCase)
                     && !n.StartsWith("uninstall", StringComparison.OrdinalIgnoreCase)
                     && !n.Contains("update", StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var key = Key(appName);
        return candidates.FirstOrDefault(n => Key(Path.GetFileNameWithoutExtension(n)).Contains(key, StringComparison.Ordinal))
            ?? candidates.FirstOrDefault();
    }

    /// <summary>Emplacements par defaut. Ollama : OLLAMA_MODELS sinon %USERPROFILE%\.ollama\models.</summary>
    public static IReadOnlyList<AiAppDefinition> DefaultApps()
    {
        var ollamaModels = Environment.GetEnvironmentVariable("OLLAMA_MODELS");
        return
        [
            new("ollama", "Ollama", [@"%LOCALAPPDATA%\Programs\Ollama"],
                [string.IsNullOrWhiteSpace(ollamaModels) ? @"%USERPROFILE%\.ollama\models" : ollamaModels], ActionsVerified: true),
            new("lmstudio", "LM Studio", [@"%LOCALAPPDATA%\Programs\LM Studio", @"%ProgramFiles%\LM Studio"],
                [@"%USERPROFILE%\.lmstudio\models", @"%USERPROFILE%\.cache\lm-studio\models"], ActionsVerified: false),
            new("gpt4all", "GPT4All", [@"%USERPROFILE%\gpt4all", @"%ProgramFiles%\gpt4all"],
                [@"%LOCALAPPDATA%\nomic.ai\GPT4All"], ActionsVerified: false),
            new("jan", "Jan", [@"%LOCALAPPDATA%\Programs\jan", @"%LOCALAPPDATA%\Programs\Jan"],
                [@"%APPDATA%\Jan\data\models", @"%USERPROFILE%\jan\models"], ActionsVerified: false),
        ];
    }

    public async Task<ModuleEvaluation> EvaluateAsync(EngineContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var statuses = new List<AiAppStatus>();
        var loaded = await _ollama.LoadedAsync(cancellationToken).ConfigureAwait(false);
        var installedModels = loaded is null ? null : await _ollama.InstalledAsync(cancellationToken).ConfigureAwait(false);

        foreach (var app in _apps)
        {
            var installDir = app.InstallDirs.Select(Environment.ExpandEnvironmentVariables).FirstOrDefault(_platform.DirectoryExists);
            var modelsDir = app.ModelDirs.Select(Environment.ExpandEnvironmentVariables).FirstOrDefault(_platform.DirectoryExists);
            var isOllama = app.Id == "ollama";
            var serverUp = isOllama && loaded is not null;   // Ollama peut tourner dans Docker sans etre installe
            if (installDir is null && modelsDir is null && !serverUp)
            {
                continue;
            }

            var (count, bytes) = installDir is null ? (0, 0L) : _platform.ProcessesUnder(installDir);
            var disk = modelsDir is null ? 0 : CachedSize(modelsDir);
            var note = isOllama
                ? serverUp ? $"Serveur actif ({_ollama.Endpoint})" : "Serveur non démarré"
                : "Mesure seulement : déchargement non vérifié pour cette application";
            statuses.Add(new AiAppStatus(app.Id, app.Name, installDir is not null, count, bytes, modelsDir, disk,
                isOllama ? loaded ?? [] : [], isOllama ? installedModels?.Count ?? 0 : 0, note)
            {
                IconImage = installDir is not null && MainExecutable(app.Name, _platform.ExecutablesIn(installDir)) is { } exe
                    ? Path.Combine(installDir, exe)
                    : null,
            });
        }

        Volatile.Write(ref _last, statuses);
        return new ModuleEvaluation(Proposals(context, statuses), []);
    }

    private static List<Proposal> Proposals(EngineContext context, List<AiAppStatus> statuses)
    {
        var s = context.Snapshot;
        var t = context.Thresholds;
        var pressure = s.RamAvailGB < t.RamAvailWarnGB || s.CommitPct >= t.CommitWarnPct;
        var critical = s.RamAvailGB < t.RamAvailCritGB || s.CommitPct >= t.CommitCritPct;
        var diskLow = s.SystemDriveFreeGB < t.DiskFreeWarnGB;
        var proposals = new List<Proposal>();

        foreach (var st in statuses)
        {
            if (pressure)
            {
                foreach (var m in st.LoadedModels.Where(m => m.SizeBytes > 0))
                {
                    var gb = Math.Round(m.SizeBytes / 1073741824d, 2);
                    var vram = Math.Round(m.VramBytes / 1073741824d, 2);
                    proposals.Add(new Proposal(
                        "ai-ollama-unload-" + AppIdFragment(m.Name),
                        string.Create(CultureInfo.InvariantCulture, $"Décharger le modèle Ollama Â« {m.Name} Â» ({gb} GB)"),
                        string.Create(CultureInfo.InvariantCulture, $"{gb} GB chargés dont {vram} GB en mémoire graphique. Le modèle n'est pas supprimé : il se rechargera à la prochaine demande (quelques secondes)."),
                        gb,
                        critical ? Severity.Crit : Severity.Warn,
                        new ActionRequest(UnloadOllamaModel, new Dictionary<string, string> { ["model"] = m.Name })));
                }
            }

            if (diskLow && st.ModelsDir is not null && st.ModelsDiskGB >= 2)
            {
                proposals.Add(new Proposal(
                    $"ai-{st.Id}-models-disk",
                    string.Create(CultureInfo.InvariantCulture, $"Les modèles {st.Name} occupent {st.ModelsDiskGB} GB"),
                    "Le disque système est presque plein. FreePC ne supprime JAMAIS de modèle : le dossier s'ouvre pour que vous décidiez (ou déplaciez les modèles).",
                    0,
                    Severity.Info,
                    new ActionRequest(OpenModelsFolder, new Dictionary<string, string> { ["app"] = st.Id })));
            }
        }

        return proposals;
    }

    /// <summary>Fragment d'identifiant sur (le nom d'un modele contient Â« : Â», Â« / Â», majuscules...).</summary>
    internal static string AppIdFragment(string modelName)
    {
        var chars = modelName.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray();
        var text = new string(chars).Trim('-');
        while (text.Contains("--", StringComparison.Ordinal))
        {
            text = text.Replace("--", "-", StringComparison.Ordinal);
        }

        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(modelName)))[..6].ToLowerInvariant();
        text = text.Length > 30 ? text[..30].TrimEnd('-') : text;
        return $"{(text.Length == 0 ? "modele" : text)}-{hash}";
    }

    public void RegisterActions(ActionCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        catalog.Register(new ActionDefinition(UnloadOllamaModel, RequiresAdmin: false, ValidateModel, UnloadAsync));
        catalog.Register(new ActionDefinition(OpenModelsFolder, RequiresAdmin: false, ValidateApp, (req, dryRun, _) => Task.FromResult(OpenFolder(req, dryRun))));
    }

    private static string? ValidateModel(IReadOnlyDictionary<string, string> p) =>
        p.Count == 1 && p.TryGetValue("model", out var m) && m.Length is > 0 and <= 200 && !m.Any(char.IsControl)
            ? null
            : "un seul paramètre Â« model Â» (nom de modèle) attendu";

    private string? ValidateApp(IReadOnlyDictionary<string, string> p) =>
        p.Count == 1 && p.TryGetValue("app", out var id) && _apps.Any(a => a.Id == id) ? null : "application d'IA inconnue";

    private async Task<ActionOutcome> UnloadAsync(ActionRequest req, bool dryRun, CancellationToken ct)
    {
        var model = req.Parameters["model"];
        // L'action ne vise qu'un modele REELLEMENT charge maintenant (pas un nom arbitraire).
        var loaded = await _ollama.LoadedAsync(ct).ConfigureAwait(false);
        if (loaded is null)
        {
            return new ActionOutcome(false, "Serveur Ollama injoignable");
        }

        var target = loaded.FirstOrDefault(m => m.Name == model);
        if (target is null)
        {
            return new ActionOutcome(true, $"Â« {model} Â» n'est déjà plus chargé");
        }

        if (dryRun)
        {
            return new ActionOutcome(true, $"simulation : déchargement de Â« {model} Â»");
        }

        if (!await _ollama.UnloadAsync(model, ct).ConfigureAwait(false))
        {
            return new ActionOutcome(false, $"Ollama a refusé le déchargement de Â« {model} Â»");
        }

        var after = await _ollama.LoadedAsync(ct).ConfigureAwait(false);
        var still = after?.Any(m => m.Name == model) ?? false;
        return still
            ? new ActionOutcome(false, $"Â« {model} Â» est toujours chargé (déchargement en cours ou modèle en cours d'utilisation)")
            : new ActionOutcome(true, string.Create(CultureInfo.InvariantCulture, $"Modèle Â« {model} Â» déchargé ({Math.Round(target.SizeBytes / 1073741824d, 2)} GB libérés)"));
    }

    private ActionOutcome OpenFolder(ActionRequest req, bool dryRun)
    {
        var app = _apps.First(a => a.Id == req.Parameters["app"]);
        var dir = app.ModelDirs.Select(Environment.ExpandEnvironmentVariables).FirstOrDefault(_platform.DirectoryExists);
        if (dir is null)
        {
            return new ActionOutcome(false, $"Dossier des modèles {app.Name} introuvable");
        }

        if (dryRun)
        {
            return new ActionOutcome(true, $"simulation : ouverture de {dir}");
        }

        return _launcher.Launch(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), [dir])
            ? new ActionOutcome(true, $"Dossier ouvert : {dir}")
            : new ActionOutcome(false, "Impossible d'ouvrir l'explorateur");
    }

    private long CachedSize(string dir)
    {
        var now = _time.GetLocalNow();
        if (_sizeCache.TryGetValue(dir, out var c) && now - c.At < SizeCacheTtl)
        {
            return c.Bytes;
        }

        var bytes = _platform.DirectorySize(dir);
        _sizeCache[dir] = (now, bytes);
        return bytes;
    }
}
