using System.Globalization;
using FreeRes.Core;
using FreeRes.Core.Decisions;
using FreeRes.Core.Engine;

namespace FreeRes.App;

/// <summary>Textes de la notification et de l'info-bulle de l'icone (logique pure, testee).</summary>
internal sealed record EscalationNotice(string Title, string Text, string TrayText, bool Critical)
{
    // Limite Windows de l'info-bulle d'une icone de notification (NotifyIcon.Text) : 127 caracteres.
    private const int TrayTextMax = 127;
    private const int BalloonTextMax = 200;

    public static EscalationNotice For(CycleReport report, Proposal? top)
    {
        ArgumentNullException.ThrowIfNull(report);
        var critical = top?.Severity == Severity.Crit;
        var count = report.PendingCount;
        var tray = count == 0
            ? $"{AppInfo.Name} : tout va bien"
            : string.Create(CultureInfo.InvariantCulture, $"{AppInfo.Name} : {count} décision{(count > 1 ? "s" : string.Empty)} en attente");
        var title = critical ? "Ressources critiques" : count > 1 ? $"{count} décisions en attente" : "Une décision vous attend";
        var text = top is null
            ? "Ouvrez Facil FreePC pour voir l'état de votre PC."
            : top.Title + (top.EstGainGB > 0 ? string.Create(CultureInfo.InvariantCulture, $" (~{top.EstGainGB:0.#} GB)") : string.Empty)
              + (count > 1 ? $" et {count - 1} autre{(count > 2 ? "s" : string.Empty)}" : string.Empty) + ". Cliquez pour décider.";
        return new EscalationNotice(title, Truncate(text, BalloonTextMax), Truncate(tray, TrayTextMax), critical);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
