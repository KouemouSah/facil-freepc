using FreeRes.Core.Machine;
using FreeRes.Core.Metrics;
using FreeRes.Core.Policy;

namespace FreeRes.Core.Tests;

/// <summary>
/// Parite Pester : « Configuration » (seuil critique >= alerte refuse, config reelle valide) et
/// « Politique d'escalade » (6 cas). Nouveau : seuils RELATIFS a la machine.
/// </summary>
public sealed class ThresholdTests
{
    private static MachineProfile Machine(double ramGB, double diskGB) =>
        new("PC", "Dell", "Latitude", "Windows 11 Pro", "25H2", 26200, ramGB, 12, "C:", diskGB);

    [Fact]
    public void Machine_de_reference_retrouve_les_seuils_valides_de_la_version_PowerShell()
    {
        var t = ThresholdCalculator.FromProfile(Machine(15.69, 235.48));
        Assert.Equal(new Thresholds(3.0, 1.5, 85, 95, 30, 12), t);
    }

    [Fact]
    public void Petite_machine_seuils_bornes_par_le_bas()
    {
        var t = ThresholdCalculator.FromProfile(Machine(4, 64));
        Assert.Equal(new Thresholds(1.5, 0.8, 85, 95, 10, 5), t);
    }

    [Fact]
    public void Grosse_machine_seuils_bornes_par_le_haut()
    {
        var t = ThresholdCalculator.FromProfile(Machine(128, 2000));
        Assert.Equal(new Thresholds(6.0, 3.0, 85, 95, 40, 15), t);
    }

    [Fact]
    public void Seuils_calcules_toujours_coherents_de_2_a_256_GB()
    {
        for (var ram = 2.0; ram <= 256; ram *= 1.37)
        {
            for (var disk = 32.0; disk <= 8192; disk *= 1.9)
            {
                Assert.Empty(ThresholdCalculator.Validate(ThresholdCalculator.FromProfile(Machine(ram, disk))));
            }
        }
    }

    [Fact]
    public void Surcharges_utilisateur_appliquees()
    {
        var t = ThresholdCalculator.FromProfile(Machine(15.69, 235.48), new ThresholdOverrides(RamAvailWarnGB: 4, DiskFreeCritGB: 8));
        Assert.Equal(4, t.RamAvailWarnGB);
        Assert.Equal(8, t.DiskFreeCritGB);
        Assert.Equal(1.5, t.RamAvailCritGB);
    }

    [Theory]
    [InlineData(3.0, 5.0, 85, 95, 30, 12, "RAM : le seuil critique")]
    [InlineData(3.0, 1.5, 95, 85, 30, 12, "Commit : le seuil critique")]
    [InlineData(3.0, 1.5, 85, 120, 30, 12, "compris entre 0 et 100")]
    [InlineData(3.0, 1.5, 85, 95, 10, 12, "Disque : le seuil critique")]
    public void Seuils_incoherents_refuses_avec_message_precis(double rw, double rc, double cw, double cc, double dw, double dc, string expected)
    {
        var errors = ThresholdCalculator.Validate(new Thresholds(rw, rc, cw, cc, dw, dc));
        Assert.Contains(errors, e => e.Contains(expected, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Windows 10 Pro", 26200, "Windows 11 Pro")]
    [InlineData("Windows 10 Enterprise", 22000, "Windows 11 Enterprise")]
    [InlineData("Windows 10 Pro", 19045, "Windows 10 Pro")]
    [InlineData(null, 26200, "Windows")]
    public void Nom_du_systeme_corrige_pour_Windows_11(string? productName, int build, string expected) =>
        Assert.Equal(expected, MachineProfileReader.OsName(productName, build));
}

public sealed class SettingsStoreTests : IDisposable
{
    private readonly TempDir _dir = new();

    [Fact]
    public void Sans_fichier_valeurs_par_defaut_sans_erreur()
    {
        var r = SettingsStore.Load(_dir.File("absent.json"));
        Assert.Empty(r.Errors);
        Assert.Equal(5, r.Settings.MonitorIntervalMinutes);
        Assert.Equal(new NotificationSettings(60, 120, 10), r.Settings.Notification);
    }

    [Fact]
    public void Fichier_valide_charge_avec_ses_surcharges()
    {
        File.WriteAllText(_dir.File("s.json"), """
            // commentaire accepte
            { "monitorIntervalMinutes": 15, "thresholds": { "ramAvailWarnGB": 4 }, "notification": { "toastCooldownMinutes": 30 }, }
            """);
        var r = SettingsStore.Load(_dir.File("s.json"));
        Assert.Empty(r.Errors);
        Assert.Equal(15, r.Settings.MonitorIntervalMinutes);
        Assert.Equal(4, r.Settings.Thresholds.RamAvailWarnGB);
        Assert.Equal(30, r.Settings.Notification.ToastCooldownMinutes);
    }

    [Theory]
    [InlineData("{ pas du json", "illisible")]
    [InlineData("""{ "monitorIntervalMinutes": 0 }""", "intervalle")]
    [InlineData("""{ "cleInconnue": 1 }""", "illisible")]
    [InlineData("""{ "retention": { "historyDays": -1 } }""", "conservation")]
    [InlineData("""{ "version": 2 }""", "Version")]
    public void Fichier_invalide_defauts_appliques_et_erreur_signalee(string json, string expected)
    {
        File.WriteAllText(_dir.File("s.json"), json);
        var r = SettingsStore.Load(_dir.File("s.json"));
        Assert.Contains(r.Errors, e => e.Contains(expected, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(new AppSettings().MonitorIntervalMinutes, r.Settings.MonitorIntervalMinutes);
    }

    public void Dispose() => _dir.Dispose();
}

public sealed class EscalationPolicyTests
{
    private static readonly Thresholds T = new(3.0, 1.5, 85, 95, 30, 12);
    private static readonly NotificationSettings N = new();
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    private static SystemSnapshot Snap(double ram, double commit, double idle = 1, double disk = 50) =>
        new(Now, 15.69, ram, 60, 23.69, 10, commit, "C:", 235, disk, idle, false, []);

    private static readonly SystemSnapshot Ok = Snap(6, 60);
    private static readonly SystemSnapshot Crit = Snap(1.0, 96);

    [Fact]
    public void Rien_si_aucune_proposition() =>
        Assert.Equal(EscalationMode.None, EscalationPolicy.Decide(Crit, T, N, 0, 0, null, null, Now));

    [Fact]
    public void Fenetre_si_critique_utilisateur_present_et_delai_ecoule() =>
        Assert.Equal(EscalationMode.Window, EscalationPolicy.Decide(Crit, T, N, 1, 1, null, null, Now));

    [Fact]
    public void Notification_et_pas_fenetre_si_critique_mais_utilisateur_absent() =>
        Assert.Equal(EscalationMode.Toast, EscalationPolicy.Decide(Snap(1.0, 96, idle: 30), T, N, 1, 1, null, null, Now));

    [Fact]
    public void Pas_de_deuxieme_fenetre_pendant_le_delai() =>
        Assert.Equal(EscalationMode.None, EscalationPolicy.Decide(Crit, T, N, 0, 1, Now.AddMinutes(-10), Now.AddMinutes(-10), Now));

    [Fact]
    public void Notification_pour_une_nouvelle_proposition_non_critique() =>
        Assert.Equal(EscalationMode.Toast, EscalationPolicy.Decide(Ok, T, N, 1, 1, null, null, Now));

    [Fact]
    public void Rappel_apres_le_delai_de_notification() =>
        Assert.Equal(EscalationMode.Toast, EscalationPolicy.Decide(Ok, T, N, 0, 2, null, Now.AddMinutes(-200), Now));

    [Fact]
    public void Pas_de_rappel_avant_le_delai_de_notification() =>
        Assert.Equal(EscalationMode.None, EscalationPolicy.Decide(Ok, T, N, 0, 2, null, Now.AddMinutes(-30), Now));

    [Fact]
    public void Disque_sous_le_seuil_critique_est_critique() =>
        Assert.True(EscalationPolicy.IsCritical(Snap(6, 60, disk: 11), T));
}
