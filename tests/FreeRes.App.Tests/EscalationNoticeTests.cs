using FreeRes.Core.Actions;
using FreeRes.Core.Decisions;
using FreeRes.Core.Engine;
using FreeRes.Core.Metrics;
using FreeRes.Core.Policy;

namespace FreeRes.App.Tests;

public sealed class EscalationNoticeTests
{
    private static CycleReport Report(int pending) => new(
        DateTimeOffset.Now,
        new SystemSnapshot(DateTimeOffset.Now, 16, 6, 60, 24, 12, 50, "C:", 235, 40, 1, false, []),
        0,
        pending,
        0,
        EscalationMode.Toast,
        [],
        []);

    private static Proposal P(string title, Severity severity, double gain = 0) => new("essai-a", title, "detail", gain, severity, ActionRequest.Of("X"));

    [Fact]
    public void Aucune_decision_info_bulle_rassurante()
    {
        var n = EscalationNotice.For(Report(0), null);
        Assert.Equal("Facil FreePC : tout va bien", n.TrayText);
        Assert.False(n.Critical);
    }

    [Fact]
    public void Une_decision_titre_et_gain()
    {
        var n = EscalationNotice.For(Report(1), P("Redemarrer Firefox", Severity.Warn, 5.5));
        Assert.Equal("Une decision vous attend", n.Title);
        Assert.Equal("Redemarrer Firefox (~5.5 GB). Cliquez pour decider.", n.Text);
        Assert.Equal("Facil FreePC : 1 decision en attente", n.TrayText);
    }

    [Fact]
    public void Plusieurs_decisions_dont_une_critique()
    {
        var n = EscalationNotice.For(Report(3), P("Decharger le modele", Severity.Crit));
        Assert.True(n.Critical);
        Assert.Equal("Ressources critiques", n.Title);
        Assert.Equal("Decharger le modele et 2 autres. Cliquez pour decider.", n.Text);
        Assert.Equal("Facil FreePC : 3 decisions en attente", n.TrayText);
    }

    [Fact]
    public void Textes_tronques_aux_limites_de_Windows()
    {
        var n = EscalationNotice.For(Report(2), P(new string('x', 400), Severity.Info));
        Assert.True(n.Text.Length <= 200);
        Assert.EndsWith("…", n.Text, StringComparison.Ordinal);
        Assert.True(n.TrayText.Length <= 127);
    }
}
