using System.Text.RegularExpressions;
using FreeRes.Core.Actions;
using FreeRes.Core.Decisions;
using FreeRes.Core.Platform;
using FreeRes.Modules.Apps;
using Microsoft.Win32;

namespace FreeRes.Modules.Tests;

/// <summary>
/// Module Applications sur une machine SIMULEE : registre en memoire, processus fictifs (aucun n'est tue),
/// sc.exe jamais lance. Chaque test verifie une raison precise.
/// </summary>
public sealed partial class AppsModuleTests : IDisposable
{
    private const long MB = AppsWorld.MB;
    private readonly TempDir _dir = new();
    private readonly FakeTime _time = new(Fixtures.Start);
    private readonly FakeProcessRunner _runner = new();
    private readonly AppsWorld _w = new();

    [GeneratedRegex("^[a-z0-9-]{3,60}$")]
    private static partial Regex ProposalIdPattern();

    private AppsModule Module(AppsOptions? options = null) => _w.Module(Fixtures.Services(_dir, _time, _runner), options);

    private ActionCatalog Catalog(AppsModule? module = null)
    {
        var catalog = new ActionCatalog();
        (module ?? Module()).RegisterActions(catalog);
        return catalog;
    }

    private static ActionRequest Req(string type, string key, string value) => new(type, new Dictionary<string, string> { [key] = value });

    private StaticInventory Scan() => new AppsScanner(_w.Platform, new AppProtection(AppsWorld.Environment)).Scan(true, TimeSpan.FromSeconds(1));

    /// <summary>Application « Chat » installee dans C:\Program Files\Chat, lancee par HKCU\Run, 300 MB sans fenetre.</summary>
    private StartupItem HeavyBackgroundChat(long mb = 300, bool window = false)
    {
        _w.App("Chat", "Chat", "Chat Inc.", @"C:\Program Files\Chat");
        _w.RunEntry("Chat", "\"C:\\Program Files\\Chat\\chat.exe\" --background");
        _w.Process(@"C:\Program Files\Chat\chat.exe", mb - 50, window);
        _w.Process(@"C:\Program Files\Chat\helper\chat-helper.exe", 50);
        return Scan().Items.Single(i => i.Name == "Chat");
    }

    // ---------------------------------------------------------------- inventaire

    [Fact]
    public void Inventaire_exclut_SystemComponent_ParentKeyName_et_entrees_sans_nom()
    {
        _w.App("Good", "Bonne Appli", "Editeur", @"C:\Program Files\Bonne", extra: p =>
        {
            _w.Registry.Set(RegistryHive.LocalMachine, RegistryView.Registry64, p, "DisplayVersion", "1.2.3");
            _w.Registry.Set(RegistryHive.LocalMachine, RegistryView.Registry64, p, "EstimatedSize", 2048);
            _w.Registry.Set(RegistryHive.LocalMachine, RegistryView.Registry64, p, "InstallDate", "20260115");
        });
        _w.App("Sys", "Composant", extra: p => _w.Registry.Set(RegistryHive.LocalMachine, RegistryView.Registry64, p, "SystemComponent", 1));
        _w.App("KB123", "Mise a jour de Bonne Appli", extra: p => _w.Registry.Set(RegistryHive.LocalMachine, RegistryView.Registry64, p, "ParentKeyName", "Good"));
        _w.App("NoName", null, "Editeur");
        _w.App("Old32", "Appli 32 bits", view: RegistryView.Registry32);
        _w.App("Mine", "Appli utilisateur", hive: RegistryHive.CurrentUser);

        var apps = Scan().Apps;

        Assert.Equal(["Appli 32 bits", "Appli utilisateur", "Bonne Appli"], apps.Select(a => a.Name).Order());
        var good = apps.Single(a => a.Name == "Bonne Appli");
        Assert.Equal(("Editeur", "1.2.3", 2048L, new DateOnly(2026, 1, 15), AppScope.Machine), (good.Publisher, good.Version, good.EstimatedSizeKB!.Value, good.InstallDate!.Value, good.Scope));
        Assert.Equal(@"C:\Program Files\Bonne", good.Folder);
        Assert.Equal(AppScope.User, apps.Single(a => a.Name == "Appli utilisateur").Scope);
    }

    [Fact]
    public void InstallLocation_trop_large_ne_sert_pas_de_dossier_d_application()
    {
        _w.App("Wide", "Appli large", location: @"C:\Program Files\");
        _w.App("Root", "Appli racine", location: @"C:\");
        Assert.All(Scan().Apps, a => Assert.Null(a.Folder));
    }

    // ---------------------------------------------------------------- StartupApproved

    [Theory]
    [InlineData(new byte[] { 0x02, 0, 0, 0 }, true)]
    [InlineData(new byte[] { 0x06, 0, 0, 0 }, true)]
    [InlineData(new byte[] { 0x04, 0, 0, 0 }, true)]
    [InlineData(new byte[] { 0x03, 0, 0, 0 }, false)]
    [InlineData(new byte[] { 0x01, 0, 0, 0 }, false)]
    [InlineData(new byte[] { 0x07, 0, 0, 0 }, false)]
    [InlineData(new byte[0], true)]
    public void Octet_0_de_StartupApproved_pair_active_impair_desactive(byte[] value, bool enabled) =>
        Assert.Equal(enabled, StartupApproved.IsEnabled(value));

    [Fact]
    public void StartupApproved_absent_ou_non_binaire_vaut_active() =>
        Assert.True(StartupApproved.IsEnabled(null) && StartupApproved.IsEnabled("03"));

    [Fact]
    public void Encodage_identique_au_Gestionnaire_des_taches()
    {
        var off = StartupApproved.Encode(false, Fixtures.Start);
        Assert.Equal(12, off.Length);
        Assert.Equal(new byte[] { 0x03, 0, 0, 0 }, off[..4]);
        Assert.Equal(Fixtures.Start.UtcDateTime.ToFileTimeUtc(), BitConverter.ToInt64(off, 4));
        Assert.Equal(new byte[] { 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, StartupApproved.Encode(true, Fixtures.Start));
    }

    [Fact]
    public void Etat_lu_dans_la_bonne_sous_cle_Run_Run32_et_StartupFolder()
    {
        _w.RunEntry("UserOff", @"C:\Tools\a.exe");
        _w.ApprovedValue(RegistryHive.CurrentUser, "Run", "UserOff", 0x03);
        _w.RunEntry("Wow", @"C:\Tools\b.exe", RegistryHive.LocalMachine, AppsWorld.Run32);
        _w.ApprovedValue(RegistryHive.LocalMachine, "Run", "Wow", 0x03);    // mauvaise sous-cle : ignoree
        _w.ApprovedValue(RegistryHive.LocalMachine, "Run32", "Wow", 0x02);
        _w.Folders.Entries.Add(new StartupFolderEntry(AppScope.User, @"C:\Users\u\Startup", "Note.lnk", @"C:\Tools\note.exe", null));
        _w.ApprovedValue(RegistryHive.CurrentUser, "StartupFolder", "Note.lnk", 0x03);
        _w.RunEntry("NoState", @"C:\Tools\c.exe");

        var items = Scan().Items.ToDictionary(i => i.Name);

        Assert.False(items["UserOff"].Enabled);
        Assert.True(items["Wow"].Enabled);
        Assert.Equal(("Run32", AppScope.Machine), (items["Wow"].ApprovedSubKey, items["Wow"].Scope));
        Assert.False(items["Note.lnk"].Enabled);
        Assert.True(items["NoState"].Enabled);
    }

    [Fact]
    public void Raccourci_non_resolu_garde_le_chemin_du_raccourci_sans_executable()
    {
        _w.Folders.Entries.Add(new StartupFolderEntry(AppScope.User, @"C:\Users\u\Startup", "Annonce.lnk", null, null));
        var item = Assert.Single(Scan().Items);
        Assert.Equal(@"C:\Users\u\Startup\Annonce.lnk", item.Command);
        Assert.Null(item.ExecutablePath);
        Assert.Null(item.AppId);
    }

    // ---------------------------------------------------------------- identifiants

    [Fact]
    public void Identifiants_stables_au_format_des_propositions_et_distincts_par_emplacement()
    {
        _w.RunEntry("Sync", @"C:\Sync\sync.exe");
        _w.RunEntry("Sync", @"C:\Sync\sync.exe", RegistryHive.LocalMachine);
        _w.RunEntry("Sync", @"C:\Sync\sync.exe", RegistryHive.LocalMachine, AppsWorld.Run32);
        _w.Folders.Entries.Add(new StartupFolderEntry(AppScope.User, @"C:\Users\u\Startup", "Sync.lnk", @"C:\Sync\sync.exe", null));
        _w.Service("SyncSvc", @"C:\Sync\svc.exe");
        _w.Tasks.Entries.Add(new ScheduledTaskEntry(@"\Sync\Logon", "Logon", true, @"C:\Sync\sync.exe", null));

        var first = Scan().Items.Select(i => i.Id).ToList();
        var second = Scan().Items.Select(i => i.Id).ToList();

        Assert.Equal(6, first.Distinct().Count());
        Assert.Equal(first, second);
        Assert.All(first, id => Assert.Matches(ProposalIdPattern(), id));
        Assert.All(first, id => Assert.Matches(ProposalIdPattern(), "apps-disable-" + id));
        Assert.Equal(AppPathRules.StableId("su", "RUN|X|Name"), AppPathRules.StableId("su", "run|x|NAME"));   // noms de registre insensibles a la casse
    }

    // ---------------------------------------------------------------- rattachement

    [Fact]
    public void Rattachement_par_prefixe_de_dossier_sans_capturer_un_dossier_voisin()
    {
        _w.App("App", "App", location: @"C:\App");
        _w.Process(@"C:\App\app.exe", 100);
        _w.Process(@"C:\AppData\other.exe", 900);
        _w.Process(@"C:\App2\x.exe", 900);
        var stat = Scan();

        var (usage, _) = ProcessAttribution.Measure(stat.Apps, stat.Items, _w.Processes.Snapshot(), 1);

        var u = Assert.Single(usage);
        Assert.Equal((1, 100 * MB), (u.ProcessCount, u.WorkingSetBytes));
        Assert.True(AppPathRules.IsUnder(@"C:\App\sub\x.exe", @"C:\App"));
        Assert.False(AppPathRules.IsUnder(@"C:\AppData\x.exe", @"C:\App"));
    }

    [Fact]
    public void Dossier_le_plus_long_l_emporte_et_executable_de_demarrage_rattache_exactement()
    {
        _w.App("Suite", "Suite", location: @"C:\Suite");
        _w.App("Tool", "Outil", location: @"C:\Suite\Tool");
        _w.RunEntry("Agent", @"C:\Agents\agent.exe -s");    // pas d'application installee : application deduite
        _w.Process(@"C:\Suite\Tool\tool.exe", 10);
        _w.Process(@"C:\Suite\main.exe", 20);
        _w.Process(@"C:\Agents\agent.exe", 30);
        var stat = Scan();

        var usage = ProcessAttribution.Measure(stat.Apps, stat.Items, _w.Processes.Snapshot(), 1).Apps.ToDictionary(u => u.Name, u => u.WorkingSetBytes);

        Assert.Equal(10 * MB, usage["Outil"]);
        Assert.Equal(20 * MB, usage["Suite"]);
        Assert.Equal(30 * MB, usage["Agent"]);
        Assert.Equal(AppSource.StartupItem, stat.Apps.Single(a => a.Name == "Agent").Source);
    }

    [Fact]
    public void Arriere_plan_seulement_si_aucun_processus_n_a_de_fenetre_dans_la_session()
    {
        _w.App("Bg", "Bg", location: @"C:\Bg");
        _w.App("Ui", "Ui", location: @"C:\Ui");
        _w.Process(@"C:\Bg\a.exe", 10);
        _w.Process(@"C:\Bg\b.exe", 10);
        _w.Process(@"C:\Bg\c.exe", 500, window: true, session: 0);   // autre session : ignore
        _w.Process(@"C:\Ui\a.exe", 10);
        _w.Process(@"C:\Ui\b.exe", 10, window: true);
        var stat = Scan();

        var usage = ProcessAttribution.Measure(stat.Apps, stat.Items, _w.Processes.Snapshot(), 1).Apps.ToDictionary(u => u.Name);

        Assert.True(usage["Bg"].Background);
        Assert.Equal(20 * MB, usage["Bg"].WorkingSetBytes);
        Assert.False(usage["Ui"].Background);
        Assert.Equal(1, usage["Ui"].WindowedProcessCount);
    }

    [Fact]
    public void Memoire_d_un_service_mesuree_en_session_0_sur_son_binaire()
    {
        _w.Service("Indexer", "\"C:\\Program Files\\Indexer\\idx.exe\" -service");
        _w.Process(@"C:\Program Files\Indexer\idx.exe", 400, session: 0);
        _w.Process(@"C:\Program Files\Indexer\idx.exe", 50, session: 1);   // meme binaire lance par l'utilisateur : pas le service
        var stat = Scan();

        var services = ProcessAttribution.Measure(stat.Apps, stat.Items, _w.Processes.Snapshot(), 1).Services;

        Assert.Equal(400 * MB, Assert.Single(services).WorkingSetBytes);
    }

    // ---------------------------------------------------------------- protections

    [Theory]
    [InlineData("Outil", "Editeur", @"C:\Windows\System32\tool.exe", AppProtection.WindowsReason)]
    [InlineData("Facil FreePC", "Emac SAH", @"C:\Ailleurs\x.exe", AppProtection.SelfReason)]
    [InlineData("Lanceur", "Editeur", @"C:\Program Files\Facil FreePC\FacilFreePC.exe", AppProtection.SelfReason)]
    [InlineData("Windows Security notification icon", "Microsoft Corporation", @"C:\Windows\System32\SecurityHealthSystray.exe", AppProtection.SecurityPrefix)]
    [InlineData("Agent", "Microsoft Corporation", @"C:\ProgramData\Microsoft\Windows Defender\Platform\MsMpEng.exe", AppProtection.SecurityPrefix)]
    [InlineData("Total Security", "Bitdefender SRL", @"C:\Program Files\Bitdefender\bd.exe", AppProtection.SecurityPrefix)]
    [InlineData("ESET Security", "ESET, spol. s r.o.", @"C:\Program Files\ESET\egui.exe", AppProtection.SecurityPrefix)]
    [InlineData("Kaspersky Standard", "AO Kaspersky Lab", @"C:\Program Files (x86)\Kaspersky Lab\avp.exe", AppProtection.SecurityPrefix)]
    [InlineData("Norton 360", "Gen Digital Inc.", @"C:\Program Files\Norton\n.exe", AppProtection.SecurityPrefix)]
    [InlineData("McAfee", "McAfee, LLC", @"C:\Program Files\McAfee\m.exe", AppProtection.SecurityPrefix)]
    [InlineData("AVG AntiVirus", "AVG Technologies", @"C:\Program Files\AVG\a.exe", AppProtection.SecurityPrefix)]
    [InlineData("Avast", "Avast Software", @"C:\Program Files\Avast Software\a.exe", AppProtection.SecurityPrefix)]
    [InlineData("Sophos Endpoint", "Sophos Limited", @"C:\Program Files\Sophos\s.exe", AppProtection.SecurityPrefix)]
    [InlineData("Apex One", "Trend Micro Inc.", @"C:\Program Files\Trend\t.exe", AppProtection.SecurityPrefix)]
    [InlineData("Malwarebytes", "Malwarebytes", @"C:\Program Files\Malwarebytes\mb.exe", AppProtection.SecurityPrefix)]
    [InlineData("Falcon Sensor", "CrowdStrike, Inc.", @"C:\Program Files\CrowdStrike\c.exe", AppProtection.SecurityPrefix)]
    [InlineData("Sentinel Agent", "SentinelOne", @"C:\Program Files\SentinelOne\s.exe", AppProtection.SecurityPrefix)]
    [InlineData("Panda Dome", "Panda Security", @"C:\Program Files\Panda\p.exe", AppProtection.SecurityPrefix)]
    [InlineData("F-Secure Elements", "F-Secure Corporation", @"C:\Program Files\F-Secure\f.exe", AppProtection.SecurityPrefix)]
    [InlineData("G DATA Total Security", "G DATA CyberDefense AG", @"C:\Program Files\G DATA\g.exe", AppProtection.SecurityPrefix)]
    [InlineData("Webroot SecureAnywhere", "Webroot", @"C:\Program Files\Webroot\w.exe", AppProtection.SecurityPrefix)]
    [InlineData("Realtek Audio Console", "Realtek Semiconductor Corp.", @"C:\Program Files\Realtek\Audio\r.exe", AppProtection.DriverPrefix)]
    [InlineData("Synaptics Pointing Device Driver", "Synaptics Incorporated", @"C:\Program Files\Synaptics\s.exe", AppProtection.DriverPrefix)]
    [InlineData("ELAN Smart-Pad", "ELAN Microelectronics Corp.", @"C:\Program Files\Elantech\e.exe", AppProtection.DriverPrefix)]
    [InlineData("Intel® Arc™ Control", "Intel Corporation", @"C:\Program Files\Intel\Arc\a.exe", AppProtection.DriverPrefix)]
    [InlineData("Intel® Graphics Software", "Intel Corporation", @"C:\Program Files\Intel\Intel Graphics Software\i.exe", AppProtection.DriverPrefix)]
    [InlineData("NVIDIA App", "NVIDIA Corporation", @"C:\Program Files\NVIDIA Corporation\n.exe", AppProtection.DriverPrefix)]
    [InlineData("AMD Software: Adrenalin Edition", "Advanced Micro Devices, Inc.", @"C:\Program Files\AMD\CNext\r.exe", AppProtection.DriverPrefix)]
    public void Chaque_categorie_protegee_est_refusee(string name, string publisher, string path, string expectedReasonStart)
    {
        var reason = new AppProtection(AppsWorld.Environment).Check(name, publisher, path);
        Assert.NotNull(reason);
        Assert.StartsWith(expectedReasonStart, reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Intel Driver & Support Assistant", "Intel", @"C:\Program Files (x86)\Intel\Driver and Support Assistant\DSATray.exe")]
    [InlineData("Reset Tool", "Elance Studio", @"C:\Program Files\ResetTool\reset.exe")]
    [InlineData("Docker Desktop", "Docker Inc.", @"C:\Program Files\Docker\Docker\Docker Desktop.exe")]
    [InlineData("Savegame Manager", "Savage Inc.", @"C:\Program Files\SAVGame\s.exe")]
    public void Logiciel_ordinaire_non_protege_sans_faux_positif_sur_les_mots_courts(string name, string publisher, string path) =>
        Assert.Null(new AppProtection(AppsWorld.Environment).Check(name, publisher, path));

    [Fact]
    public async Task Element_protege_jamais_propose_et_action_refusee_avec_sa_raison()
    {
        _w.App("Av", "Bitdefender Total Security", "Bitdefender", @"C:\Program Files\Bitdefender");
        _w.RunEntry("bdagent", @"C:\Program Files\Bitdefender\bdagent.exe");
        _w.Process(@"C:\Program Files\Bitdefender\bdagent.exe", 900);
        var item = Scan().Items.Single();

        var evaluation = await Module().EvaluateAsync(AppsWorld.Context(_time, ramAvailGB: 1), default);

        Assert.Empty(evaluation.Proposals);
        var ex = Assert.Throws<ActionRejectedException>(() => Catalog().Resolve(Req(AppsModule.DisableStartupItem, "id", item.Id)));
        Assert.Contains("element protege : " + AppProtection.SecurityPrefix, ex.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- propositions

    [Fact]
    public async Task Application_d_arriere_plan_de_200_MB_proposee_en_Info_sans_arret_hors_pression()
    {
        var item = HeavyBackgroundChat(mb: 300);

        var p = Assert.Single((await Module().EvaluateAsync(AppsWorld.Context(_time), default)).Proposals);

        Assert.Equal("apps-disable-" + item.Id, p.Id);
        Assert.Null(p.Validate());
        Assert.Equal(Severity.Info, p.Severity);
        Assert.Equal(Math.Round(300 * MB / 1073741824d, 2), p.EstGainGB);
        Assert.Equal(AppsModule.DisableStartupItem, p.Action.Type);
        Assert.Equal(item.Id, p.Action.Parameters["id"]);
        Assert.StartsWith("Desactiver le demarrage automatique de Chat", p.Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sous_le_seuil_de_200_MB_rien_n_est_propose()
    {
        HeavyBackgroundChat(mb: 199);
        Assert.Empty((await Module().EvaluateAsync(AppsWorld.Context(_time, ramAvailGB: 1), default)).Proposals);
    }

    [Fact]
    public async Task Seuil_configurable()
    {
        HeavyBackgroundChat(mb: 150);
        Assert.Single((await Module(new AppsOptions(BackgroundProposeMB: 100)).EvaluateAsync(AppsWorld.Context(_time), default)).Proposals);
    }

    [Theory]
    [InlineData(0, 200, 30, 20, "seuils de proposition")]
    [InlineData(200, -1, 30, 20, "seuils de proposition")]
    [InlineData(200, 200, 0, 20, "rafraichissement")]
    [InlineData(200, 200, 30, 0, "taches planifiees")]
    public void Configuration_incoherente_refusee_au_demarrage(double bgMB, double svcMB, double refreshMin, double tasksSec, string reason)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Module(new AppsOptions(bgMB, svcMB, refreshMin, tasksSec)));
        Assert.Contains(reason, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Application_avec_une_fenetre_jamais_proposee()
    {
        HeavyBackgroundChat(mb: 900, window: true);
        Assert.Empty((await Module().EvaluateAsync(AppsWorld.Context(_time, ramAvailGB: 1), default)).Proposals);
    }

    [Fact]
    public async Task Element_deja_desactive_jamais_propose()
    {
        HeavyBackgroundChat(mb: 900);
        _w.ApprovedValue(RegistryHive.CurrentUser, "Run", "Chat", 0x03);
        Assert.Empty((await Module().EvaluateAsync(AppsWorld.Context(_time), default)).Proposals);
    }

    [Theory]
    [InlineData(2.9, 50)]    // RAM disponible sous le seuil d'alerte (3 GB)
    [InlineData(6, 86)]      // memoire engagee au-dessus du seuil d'alerte (85 %)
    public async Task Sous_pression_memoire_severite_Warn_et_arret_des_processus_propose(double ramAvailGB, double commitPct)
    {
        var item = HeavyBackgroundChat(mb: 300);
        var appId = item.AppId!;

        var proposals = (await Module().EvaluateAsync(AppsWorld.Context(_time, ramAvailGB, commitPct), default)).Proposals.ToDictionary(p => p.Id);

        Assert.Equal(2, proposals.Count);
        Assert.Equal(Severity.Warn, proposals["apps-disable-" + item.Id].Severity);
        var stop = proposals["apps-stop-" + appId];
        Assert.Equal((AppsModule.StopAppProcesses, appId), (stop.Action.Type, stop.Action.Parameters["appId"]));
        Assert.Equal(Math.Round(300 * MB / 1073741824d, 2), stop.EstGainGB);
        Assert.Null(stop.Validate());
    }

    [Fact]
    public async Task Pile_au_seuil_pas_de_pression()
    {
        HeavyBackgroundChat(mb: 300);
        var p = Assert.Single((await Module().EvaluateAsync(AppsWorld.Context(_time, ramAvailGB: 3.0, commitPct: 85), default)).Proposals);
        Assert.Equal(Severity.Info, p.Severity);
    }

    [Fact]
    public async Task Element_commun_propose_avec_l_action_administrateur()
    {
        _w.App("Sync", "Sync", "Sync Co", @"C:\Program Files\Sync");
        _w.RunEntry("Sync", @"C:\Program Files\Sync\sync.exe", RegistryHive.LocalMachine, AppsWorld.Run32);
        _w.Process(@"C:\Program Files\Sync\sync.exe", 250);

        var p = Assert.Single((await Module().EvaluateAsync(AppsWorld.Context(_time), default)).Proposals);

        Assert.Equal(AppsModule.DisableMachineStartupItem, p.Action.Type);
        Assert.True(Catalog().Resolve(p.Action).RequiresAdmin);
    }

    [Fact]
    public async Task Service_tiers_gourmand_propose_en_manuel_service_Windows_jamais()
    {
        _w.Service("Indexer", "\"C:\\Program Files\\Indexer\\idx.exe\"", display: "Indexer Service");
        _w.Service("WinSvc", @"C:\Windows\System32\winsvc.exe");
        _w.Service("SharedSvc", @"%SystemRoot%\System32\svchost.exe -k netsvcs", type: 0x20);
        _w.Service("Small", @"C:\Program Files\Small\s.exe");
        _w.Process(@"C:\Program Files\Indexer\idx.exe", 250, session: 0);
        _w.Process(@"C:\Windows\System32\winsvc.exe", 900, session: 0);
        _w.Process(@"C:\Program Files\Small\s.exe", 199, session: 0);

        var p = Assert.Single((await Module().EvaluateAsync(AppsWorld.Context(_time), default)).Proposals);

        Assert.Equal(AppsModule.SetServiceManual, p.Action.Type);
        Assert.Equal("Indexer", p.Action.Parameters["service"]);
        Assert.Contains("Indexer Service", p.Title, StringComparison.Ordinal);
        Assert.Null(p.Validate());
        Assert.DoesNotContain(Module().Refresh().Inventory.StartupItems, i => i.ServiceName is "WinSvc" or "SharedSvc");
    }

    [Fact]
    public async Task Inventaire_expose_apres_evaluation_et_taches_illisibles_signalees_sans_bloquer()
    {
        HeavyBackgroundChat();
        _w.Tasks.Failure = new TimeoutException("Planificateur sans reponse");
        var m = Module();
        Assert.Null(m.LastInventory);

        await m.EvaluateAsync(AppsWorld.Context(_time), default);

        var inv = m.LastInventory!;
        Assert.Contains(inv.Apps, a => a.Name == "Chat");
        Assert.Contains(inv.StartupItems, i => i.Name == "Chat");
        Assert.Contains(inv.Usage, u => u.Name == "Chat" && u.Background);
        Assert.Contains("Planificateur sans reponse", Assert.Single(inv.Warnings), StringComparison.Ordinal);
    }

    [Fact]
    public void Taches_a_l_ouverture_de_session_listees_en_lecture_seule()
    {
        _w.Tasks.Entries.Add(new ScheduledTaskEntry(@"\Vendor\Updater", "Updater", false, @"C:\Program Files\Vendor\up.exe", "/silent"));
        var item = Assert.Single(Scan().Items);
        Assert.Equal((StartupKind.ScheduledTask, false, (string?)null), (item.Kind, item.Enabled, item.ApprovedSubKey));
        Assert.Throws<ActionRejectedException>(() => Catalog().Resolve(Req(AppsModule.DisableStartupItem, "id", item.Id)));
    }

    // ---------------------------------------------------------------- actions : validation

    [Theory]
    [InlineData("id", "su-123")]
    [InlineData("id", "SU-0123456789ABCDEF")]
    [InlineData("id", "su-0123456789abcdef; reg delete")]
    [InlineData("id", "task-0123456789abcdef")]
    [InlineData("name", "su-0123456789abcdef")]
    public void Demarrage_parametre_mal_forme_refuse(string key, string value)
    {
        var ex = Assert.Throws<ActionRejectedException>(() => Catalog().Resolve(Req(AppsModule.DisableStartupItem, key, value)));
        Assert.Contains("id d'element de demarrage invalide", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Demarrage_parametre_supplementaire_refuse()
    {
        var item = HeavyBackgroundChat();
        var req = new ActionRequest(AppsModule.DisableStartupItem, new Dictionary<string, string> { ["id"] = item.Id, ["x"] = "1" });
        Assert.Contains("invalide", Assert.Throws<ActionRejectedException>(() => Catalog().Resolve(req)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Demarrage_id_inconnu_refuse()
    {
        var ex = Assert.Throws<ActionRejectedException>(() => Catalog().Resolve(Req(AppsModule.EnableStartupItem, "id", "su-0123456789abcdef")));
        Assert.Contains("introuvable", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Portee_et_type_d_action_doivent_correspondre()
    {
        var user = HeavyBackgroundChat();
        _w.RunEntry("Machine", @"C:\Tools\m.exe", RegistryHive.LocalMachine);
        var machine = Scan().Items.Single(i => i.Name == "Machine");
        var catalog = Catalog();

        Assert.Contains("administrateur requise", Assert.Throws<ActionRejectedException>(() => catalog.Resolve(Req(AppsModule.DisableStartupItem, "id", machine.Id))).Message, StringComparison.Ordinal);
        Assert.Contains("action utilisateur requise", Assert.Throws<ActionRejectedException>(() => catalog.Resolve(Req(AppsModule.DisableMachineStartupItem, "id", user.Id))).Message, StringComparison.Ordinal);
        Assert.False(catalog.Resolve(Req(AppsModule.DisableStartupItem, "id", user.Id)).RequiresAdmin);
        Assert.False(catalog.Resolve(Req(AppsModule.EnableStartupItem, "id", user.Id)).RequiresAdmin);
        Assert.True(catalog.Resolve(Req(AppsModule.DisableMachineStartupItem, "id", machine.Id)).RequiresAdmin);
        Assert.True(catalog.Resolve(Req(AppsModule.EnableMachineStartupItem, "id", machine.Id)).RequiresAdmin);
    }

    [Theory]
    [InlineData("appId", "app-xyz", "appId invalide")]
    [InlineData("appId", "app-0123456789abcdef", "application introuvable")]
    [InlineData("id", "app-0123456789abcdef", "appId invalide")]
    public void Arret_parametre_invalide_ou_inconnu_refuse(string key, string value, string reason) =>
        Assert.Contains(reason, Assert.Throws<ActionRejectedException>(() => Catalog().Resolve(Req(AppsModule.StopAppProcesses, key, value))).Message, StringComparison.Ordinal);

    [Fact]
    public void Arret_refuse_pour_une_application_protegee_ou_sans_dossier_precis()
    {
        _w.App("Av", "Kaspersky", "AO Kaspersky Lab", @"C:\Program Files\Kaspersky Lab");
        _w.App("Wide", "Appli large", "Editeur", @"C:\Program Files");
        var apps = Scan().Apps.ToDictionary(a => a.Name, a => a.Id);
        var catalog = Catalog();

        Assert.Contains("application protegee : " + AppProtection.SecurityPrefix, Assert.Throws<ActionRejectedException>(() => catalog.Resolve(Req(AppsModule.StopAppProcesses, "appId", apps["Kaspersky"]))).Message, StringComparison.Ordinal);
        Assert.Contains("dossier de l'application inconnu ou trop large", Assert.Throws<ActionRejectedException>(() => catalog.Resolve(Req(AppsModule.StopAppProcesses, "appId", apps["Appli large"]))).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("service", "Bad\\Name", "nom de service invalide")]
    [InlineData("service", "svc; sc delete x", "nom de service invalide")]
    [InlineData("name", "Indexer", "nom de service invalide")]
    [InlineData("service", "Inconnu", "service introuvable")]
    [InlineData("service", "WinSvc", "service Windows")]
    [InlineData("service", "Driver", "pilote ou service non Win32")]
    [InlineData("service", "AvSvc", "service protege : " + AppProtection.SecurityPrefix)]
    [InlineData("service", "Manual", "pas en demarrage automatique")]
    public void Service_manuel_refuse_avec_la_raison_precise(string key, string value, string reason)
    {
        _w.Service("Indexer", @"C:\Program Files\Indexer\idx.exe");
        _w.Service("WinSvc", @"\SystemRoot\System32\winsvc.exe");
        _w.Service("Driver", @"\SystemRoot\System32\drivers\d.sys", type: 1);
        _w.Service("AvSvc", @"C:\Program Files\ESET\ekrn.exe");
        _w.Service("Manual", @"C:\Program Files\M\m.exe", start: 3);
        var ex = Assert.Throws<ActionRejectedException>(() => Catalog().Resolve(Req(AppsModule.SetServiceManual, key, value)));
        Assert.Contains(reason, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Types_d_actions_et_droits()
    {
        _w.Service("Indexer", @"C:\Program Files\Indexer\idx.exe");
        var catalog = Catalog();
        Assert.True(catalog.Resolve(Req(AppsModule.SetServiceManual, "service", "Indexer")).RequiresAdmin);
        Assert.Equal(6, catalog.Types.Count);
    }

    // ---------------------------------------------------------------- actions : execution

    [Fact]
    public async Task Desactiver_puis_reactiver_ecrit_StartupApproved_sans_toucher_a_l_entree_Run()
    {
        var item = HeavyBackgroundChat();
        var catalog = Catalog();
        var off = Req(AppsModule.DisableStartupItem, "id", item.Id);

        Assert.True((await catalog.Resolve(off).Execute(off, true, default)).Ok);
        Assert.Empty(_w.Registry.Writes);   // simulation : rien d'ecrit

        Assert.True((await catalog.Resolve(off).Execute(off, false, default)).Ok);
        var w = Assert.Single(_w.Registry.Writes);
        Assert.Equal((RegistryHive.CurrentUser, RegistryView.Registry64, AppsWorld.Approved + @"\Run", "Chat"), (w.Hive, w.View, w.Path, w.Name));
        Assert.Equal(0x03, w.Data[0]);
        Assert.False(Scan().Items.Single(i => i.Id == item.Id).Enabled);
        Assert.Equal("\"C:\\Program Files\\Chat\\chat.exe\" --background", _w.Registry.Peek(RegistryHive.CurrentUser, RegistryView.Registry64, AppsWorld.Run, "Chat"));

        var on = Req(AppsModule.EnableStartupItem, "id", item.Id);
        Assert.True((await catalog.Resolve(on).Execute(on, false, default)).Ok);
        Assert.Equal(0x02, _w.Registry.Writes[^1].Data[0]);
        Assert.True(Scan().Items.Single(i => i.Id == item.Id).Enabled);
    }

    [Fact]
    public async Task Element_du_dossier_commun_ecrit_dans_StartupFolder_de_HKLM()
    {
        _w.Folders.Entries.Add(new StartupFolderEntry(AppScope.Machine, @"C:\ProgramData\Startup", "Remote.lnk", @"C:\Program Files\Remote\remote.exe", null));
        var item = Scan().Items.Single();
        var req = Req(AppsModule.DisableMachineStartupItem, "id", item.Id);

        Assert.True((await Catalog().Resolve(req).Execute(req, false, default)).Ok);

        var w = Assert.Single(_w.Registry.Writes);
        Assert.Equal((RegistryHive.LocalMachine, AppsWorld.Approved + @"\StartupFolder", "Remote.lnk"), (w.Hive, w.Path, w.Name));
    }

    [Fact]
    public async Task Arret_epargne_fenetres_autres_dossiers_et_autres_sessions()
    {
        var item = HeavyBackgroundChat();
        var windowed = _w.Process(@"C:\Program Files\Chat\chat-ui.exe", 100, window: true);
        var neighbour = _w.Process(@"C:\Program Files\ChatPlus\x.exe", 100);
        var otherSession = _w.Process(@"C:\Program Files\Chat\svc.exe", 100, session: 0);
        var targets = _w.Processes.Processes.Where(p => p.Name.StartsWith("chat", StringComparison.Ordinal) && !p.HasWindow && p.SessionId == 1).Select(p => p.Pid).ToList();
        var req = Req(AppsModule.StopAppProcesses, "appId", item.AppId!);
        var catalog = Catalog();

        var dry = await catalog.Resolve(req).Execute(req, true, default);
        Assert.True(dry.Ok);
        Assert.Empty(_w.Processes.Killed);

        var outcome = await catalog.Resolve(req).Execute(req, false, default);

        Assert.True(outcome.Ok, outcome.Message);
        Assert.Equal(targets.Order(), _w.Processes.Killed.Order());
        Assert.DoesNotContain(windowed.Pid, _w.Processes.Killed);
        Assert.DoesNotContain(neighbour.Pid, _w.Processes.Killed);
        Assert.DoesNotContain(otherSession.Pid, _w.Processes.Killed);
    }

    [Fact]
    public async Task Service_manuel_appelle_sc_avec_des_arguments_separes()
    {
        _w.Service("Indexer", @"C:\Program Files\Indexer\idx.exe");
        var req = Req(AppsModule.SetServiceManual, "service", "Indexer");

        var outcome = await Catalog().Resolve(req).Execute(req, false, default);

        Assert.True(outcome.Ok);
        var call = Assert.Single(_runner.Calls);
        Assert.EndsWith("sc.exe", call.File, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["config", "Indexer", "start=", "demand"], call.Args);
    }

    [Fact]
    public async Task Service_Windows_refuse_a_l_execution_sans_lancer_sc()
    {
        _w.Service("Indexer", @"C:\Program Files\Indexer\idx.exe");
        var req = Req(AppsModule.SetServiceManual, "service", "Indexer");
        var def = Catalog().Resolve(req);
        _w.Service("Indexer", @"C:\Windows\System32\idx.exe");   // binaire remplace entre validation et execution

        var outcome = await def.Execute(req, false, default);

        Assert.False(outcome.Ok);
        Assert.Contains("service Windows", outcome.Message, StringComparison.Ordinal);
        Assert.Empty(_runner.Calls);
    }

    [Fact]
    public async Task Echec_de_sc_rendu_en_echec()
    {
        _w.Service("Indexer", @"C:\Program Files\Indexer\idx.exe");
        _runner.Respond = (_, _) => new ProcessResult(5, "[SC] OpenService FAILED 5", string.Empty, false);
        var req = Req(AppsModule.SetServiceManual, "service", "Indexer");
        var outcome = await Catalog().Resolve(req).Execute(req, false, default);
        Assert.False(outcome.Ok);
        Assert.Contains("code 5", outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Inventaire_relu_apres_une_action_reussie()
    {
        var item = HeavyBackgroundChat();
        var m = Module();
        Assert.Single((await m.EvaluateAsync(AppsWorld.Context(_time), default)).Proposals);
        _w.ApprovedValue(RegistryHive.CurrentUser, "Run", "Chat", 0x03);   // desactive (par l'action)

        Assert.Single((await m.EvaluateAsync(AppsWorld.Context(_time), default)).Proposals);   // cache encore valide
        m.OnActionCompleted(Req(AppsModule.DisableStartupItem, "id", item.Id), new ActionResult(AppsModule.DisableStartupItem, true, "ok", 0, false));
        Assert.Empty((await m.EvaluateAsync(AppsWorld.Context(_time), default)).Proposals);
    }

    public void Dispose() => _dir.Dispose();
}
