using FreeRes.Core.Metrics;

namespace FreeRes.Core.Policy;

/// <summary>Comment une decision parvient a l'utilisateur.</summary>
public enum EscalationMode
{
    /// <summary>Rien (pas de bruit).</summary>
    None,

    /// <summary>Notification Windows cliquable.</summary>
    Toast,

    /// <summary>La fenetre s'ouvre d'elle-meme (etat critique uniquement).</summary>
    Window,
}

/// <summary>
/// Politique validee le 2026-11-08 (version PowerShell), reprise a l'identique :
///   * fenetre qui s'ouvre seule UNIQUEMENT si etat critique + utilisateur present + delai ecoule ;
///   * notification pour toute nouvelle proposition, ou rappel apres le delai de notification ;
///   * sinon rien.
/// </summary>
public static class EscalationPolicy
{
    public static bool IsCritical(SystemSnapshot s, Thresholds t)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(t);
        return s.RamAvailGB < t.RamAvailCritGB || s.CommitPct >= t.CommitCritPct || s.SystemDriveFreeGB < t.DiskFreeCritGB;
    }

    public static EscalationMode Decide(
        SystemSnapshot snapshot,
        Thresholds thresholds,
        NotificationSettings notification,
        int newCount,
        int pendingCount,
        DateTimeOffset? lastWindowAt,
        DateTimeOffset? lastToastAt,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(notification);
        if (pendingCount <= 0)
        {
            return EscalationMode.None;
        }

        var userActive = snapshot.IdleMinutes < notification.UserActiveMaxIdleMinutes;
        var windowCooldownOver = lastWindowAt is null || (now - lastWindowAt.Value).TotalMinutes >= notification.WindowCooldownMinutes;
        if (IsCritical(snapshot, thresholds) && userActive && windowCooldownOver)
        {
            return EscalationMode.Window;
        }

        if (newCount > 0)
        {
            return EscalationMode.Toast;
        }

        var toastCooldownOver = lastToastAt is null || (now - lastToastAt.Value).TotalMinutes >= notification.ToastCooldownMinutes;
        return toastCooldownOver ? EscalationMode.Toast : EscalationMode.None;
    }
}
