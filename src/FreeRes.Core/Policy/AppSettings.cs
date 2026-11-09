using System.Text.Json;
using System.Text.Json.Serialization;

namespace FreeRes.Core.Policy;

/// <summary>Reglages de notification (politique validee le 2026-11-08 dans la version PowerShell).</summary>
public sealed record NotificationSettings(
    int WindowCooldownMinutes = 60,
    int ToastCooldownMinutes = 120,
    int UserActiveMaxIdleMinutes = 10);

/// <summary>Durees de conservation des donnees locales.</summary>
public sealed record RetentionSettings(int HistoryDays = 180, int LogDays = 60, int DecisionsDays = 30);

/// <summary>Mises a jour : verification hebdomadaire signee (lecture seule sur GitHub), desactivable.</summary>
public sealed record UpdateSettings(bool WeeklyCheck = true);

/// <summary>
/// Reglages utilisateur (%LOCALAPPDATA%\FacilFreePC\settings.json). Tout est facultatif : sans fichier,
/// les valeurs par defaut et les seuils calcules pour la machine s'appliquent.
/// </summary>
public sealed record AppSettings
{
    public int Version { get; init; } = 1;

    public int MonitorIntervalMinutes { get; init; } = 5;

    public double SnoozeHours { get; init; } = 24;

    public double RejectHours { get; init; } = 24 * 7;

    public ThresholdOverrides Thresholds { get; init; } = new();

    public NotificationSettings Notification { get; init; } = new();

    public RetentionSettings Retention { get; init; } = new();

    public UpdateSettings Updates { get; init; } = new();

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (Version != 1)
        {
            errors.Add($"Version de réglages non prise en charge : {Version}.");
        }

        if (MonitorIntervalMinutes is < 1 or > 1440)
        {
            errors.Add("L'intervalle de surveillance doit être compris entre 1 et 1440 minutes.");
        }

        if (SnoozeHours <= 0 || RejectHours <= 0)
        {
            errors.Add("Les durées de report et de rejet doivent être positives.");
        }

        if (Notification.WindowCooldownMinutes <= 0 || Notification.ToastCooldownMinutes <= 0 || Notification.UserActiveMaxIdleMinutes <= 0)
        {
            errors.Add("Les délais de notification doivent être positifs.");
        }

        if (Retention.HistoryDays <= 0 || Retention.LogDays <= 0 || Retention.DecisionsDays <= 0)
        {
            errors.Add("Les durées de conservation doivent être positives.");
        }

        return errors;
    }
}

/// <summary>Resultat du chargement : reglages appliques + erreurs a montrer a l'utilisateur.</summary>
public sealed record SettingsLoadResult(AppSettings Settings, IReadOnlyList<string> Errors);

public static class SettingsStore
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>
    /// Fichier absent = defauts. Fichier illisible ou invalide = defauts ET erreur signalee : l'application
    /// ne doit ni planter ni appliquer des valeurs incoherentes.
    /// </summary>
    public static SettingsLoadResult Load(string path)
    {
        if (!File.Exists(path))
        {
            return new SettingsLoadResult(new AppSettings(), []);
        }

        AppSettings? settings;
        try
        {
            settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json);
        }
        catch (JsonException ex)
        {
            return new SettingsLoadResult(new AppSettings(), [$"settings.json illisible ({ex.Message}) : valeurs par défaut appliquées."]);
        }

        if (settings is null)
        {
            return new SettingsLoadResult(new AppSettings(), ["settings.json vide : valeurs par défaut appliquées."]);
        }

        var errors = settings.Validate();
        return errors.Count == 0
            ? new SettingsLoadResult(settings, [])
            : new SettingsLoadResult(new AppSettings(), errors.Select(e => e + " Valeurs par défaut appliquées.").ToList());
    }
}
