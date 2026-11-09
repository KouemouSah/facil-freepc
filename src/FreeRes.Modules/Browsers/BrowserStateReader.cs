using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FreeRes.Modules.Browsers;

/// <summary>
/// Lecture de l'etat des navigateurs a partir de LEURS fichiers (lecture seule). Seules des cles VERIFIEES sont
/// utilisees (2026-11-09) :
///   * Chromium « Local State » : performance_tuning.high_efficiency_mode.state (0 = economiseur de memoire
///     desactive, 2 = active ; absent = valeur par defaut du navigateur) et profile.last_active_profiles ;
///   * Firefox : profil actif = session de secours (recovery.jsonlz4) la plus recente ; prefs.js/user.js :
///     browser.startup.page (3 = restaurer la session) ; extensions.json : extension compagnon active.
/// </summary>
public sealed partial class BrowserStateReader(IBrowserPlatform platform)
{
    [GeneratedRegex(@"^\s*user_pref\(""browser\.startup\.page"",\s*(\d+)\);", RegexOptions.Multiline)]
    private static partial Regex StartupPage();

    /// <summary>Etat de l'economiseur de memoire Chromium : true/false si explicite, null si absent ou illisible.</summary>
    public bool? ChromiumMemorySaver(string userDataDir)
    {
        using var doc = Json(Path.Combine(userDataDir, "Local State"));
        if (doc is null || !TryGet(doc.RootElement, out var state, "performance_tuning", "high_efficiency_mode", "state") || state.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        return state.GetInt32() != 0;
    }

    /// <summary>Profils Chromium actifs au moment de la lecture (Local State) ; vide si inconnu.</summary>
    public IReadOnlyList<string> ChromiumActiveProfiles(string userDataDir)
    {
        using var doc = Json(Path.Combine(userDataDir, "Local State"));
        if (doc is null || !TryGet(doc.RootElement, out var list, "profile", "last_active_profiles") || list.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return list.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList();
    }

    /// <summary>Profil Firefox actif : celui dont la session de secours a ete ecrite le plus recemment.</summary>
    public string? FirefoxActiveProfile(string profilesRoot) =>
        platform.Directories(profilesRoot)
            .Select(d => (Dir: d, At: platform.LastWriteUtc(Path.Combine(d, "sessionstore-backups", "recovery.jsonlz4"))))
            .Where(x => x.At is not null)
            .OrderByDescending(x => x.At)
            .Select(x => x.Dir)
            .FirstOrDefault();

    /// <summary>browser.startup.page = 3 (« ouvrir les fenetres et onglets precedents ») ; user.js prime sur prefs.js.</summary>
    public bool FirefoxRestoresSession(string profileDir)
    {
        int? value = null;
        foreach (var file in new[] { "prefs.js", "user.js" })
        {
            var text = platform.ReadAllText(Path.Combine(profileDir, file));
            if (text is null)
            {
                continue;
            }

            foreach (Match m in StartupPage().Matches(text))
            {
                value = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            }
        }

        return value == 3;
    }

    /// <summary>
    /// true si <paramref name="profileDir"/> est le profil PAR DEFAUT de l'edition (installs.ini « Default= ») ET
    /// porte le suffixe de cette edition : la relancer sans argument equivaut alors au lancement habituel de
    /// l'utilisateur (constate le 2026-11-09 : une relance « -profile chemin » fait ecrire a Firefox des erreurs de
    /// rattachement au raccourci de la barre des taches). Sinon, relance explicite avec -profile.
    /// </summary>
    public bool FirefoxIsEditionDefaultProfile(string profilesRoot, string profileDir, string editionSuffix)
    {
        var folder = Path.GetFileName(profileDir.TrimEnd('\\'));
        if (string.IsNullOrEmpty(editionSuffix) || !folder.EndsWith(editionSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var installs = platform.ReadAllText(Path.Combine(Path.GetDirectoryName(profilesRoot.TrimEnd('\\'))!, "installs.ini"));
        if (installs is null)
        {
            return false;
        }

        var expected = "Profiles/" + folder;
        return installs.Split('\n')
            .Select(l => l.Trim())
            .Any(l => l.StartsWith("Default=", StringComparison.OrdinalIgnoreCase)
                && string.Equals(l["Default=".Length..].Replace('\\', '/'), expected, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Extension compagnon presente et active dans le profil (extensions.json).</summary>
    public bool FirefoxExtensionActive(string profileDir)
    {
        using var doc = Json(Path.Combine(profileDir, "extensions.json"));
        if (doc is null || !doc.RootElement.TryGetProperty("addons", out var addons) || addons.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        return addons.EnumerateArray().Any(a =>
            a.TryGetProperty("id", out var id) && id.GetString() == BrowserCatalog.FirefoxExtensionId
            && a.TryGetProperty("active", out var active) && active.ValueKind == JsonValueKind.True);
    }

    private JsonDocument? Json(string path)
    {
        var text = platform.ReadAllText(path);
        if (text is null)
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            return null;   // fichier en cours d'ecriture : etat inconnu au lieu d'une erreur
        }
    }

    private static bool TryGet(JsonElement root, out JsonElement value, params string[] path)
    {
        value = root;
        foreach (var p in path)
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(p, out value))
            {
                return false;
            }
        }

        return true;
    }
}
