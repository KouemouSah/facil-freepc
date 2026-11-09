using System.Diagnostics;
using System.Globalization;
using FreeRes.Core;
using FreeRes.Core.Actions;
using FreeRes.Core.Engine;
using FreeRes.Core.Machine;
using FreeRes.Core.Metrics;
using FreeRes.Core.Platform;
using FreeRes.Core.Policy;
using FreeRes.Core.Storage;
using FreeRes.Modules;
using FreeRes.Modules.Apps;
using FreeRes.Modules.Docker;
using Microsoft.Win32;

namespace FreeRes.RealCheck;

/// <summary>
/// Verifications REELLES des actions des modules (complement des tests simules). Chaque commande restaure l'etat
/// d'origine, meme en cas d'erreur. Les actions administrateur exigent un terminal eleve.
///   inventory                  inventaire Applications (lecture seule)
///   startup-toggle &lt;id&gt;       desactive puis reactive un element de demarrage UTILISATEUR, octets verifies et restaures
///   process-stop               StopAppProcesses sur une application de test (copie de ping.exe), temoin epargne
///   service-manual &lt;nom&gt;      SetServiceManual reel puis restauration du type de demarrage d'origine (admin)
///   docker-cycle               Docker demarre : fstrim + mesure reels par le module
///   docker-compact             Docker arrete : compactage reel du VHDX par diskpart (admin)
/// </summary>
internal static class Program
{
    private static readonly string Work = Path.Combine(Path.GetTempPath(), "FreeResRealCheck");

    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("Commandes : inventory | startup-toggle <id> | process-stop | service-manual <nom> | docker-cycle | docker-compact");
            return 2;
        }

        Directory.CreateDirectory(Work);
        using var db = new FreeResDatabase(Path.Combine(Work, "state.db"));
        var paths = new AppPaths(Path.Combine(Work, "data"));
        var probe = new WindowsSystemProbe();
        var services = new ModuleServices(paths, new Core.Logging.EventLog(Path.Combine(Work, "logs"), TimeProvider.System), TimeProvider.System, probe, new ProcessRunner(), db);
        var setup = ModulePack.Create(services);
        var catalog = new ActionCatalog();
        setup.RegisterActions(catalog);
        var profile = MachineProfileReader.Read(probe);
        EngineContext Context() => new(probe.Capture(), profile, ThresholdCalculator.FromProfile(profile), new AppSettings());

        async Task<ActionOutcome> Run(string type, params (string Key, string Value)[] p)
        {
            var req = new ActionRequest(type, p.ToDictionary(x => x.Key, x => x.Value));
            var outcome = await catalog.Resolve(req).Execute(req, false, CancellationToken.None);
            Console.WriteLine($"  -> {type} : {(outcome.Ok ? "OK" : "ECHEC")} {outcome.Message}");
            // Comme le moteur : les modules observateurs sont prevenus (rafraichissement de leur inventaire).
            foreach (var observer in setup.Modules.OfType<IActionObserver>())
            {
                observer.OnActionCompleted(req, new ActionResult(type, outcome.Ok, outcome.Message, 0, false));
            }

            return outcome;
        }

        var apps = setup.Modules.OfType<AppsModule>().Single();
        switch (args[0])
        {
            case "inventory":
                await apps.EvaluateAsync(Context(), CancellationToken.None);
                PrintInventory(apps.LastInventory!);
                return 0;

            case "startup-toggle" when args.Length == 2:
                return await StartupToggle(apps, Context, Run, args[1]);

            case "process-stop":
                return await ProcessStop(Context, Run, services);

            case "service-manual" when args.Length == 2:
                return await ServiceManual(Run, args[1]);

            case "docker-cycle":
                return await DockerCycle(setup, Context, catalog, db);

            case "docker-compact":
                return (await Run(DockerModule.CompactDockerVhdx)).Ok ? 0 : 1;

            default:
                Console.WriteLine("Commande inconnue");
                return 2;
        }
    }

    private static void PrintInventory(AppsInventory inv)
    {
        Console.WriteLine($"Applications : {inv.Apps.Count} ; elements de demarrage : {inv.StartupItems.Count} ; avertissements : {string.Join(" | ", inv.Warnings)}");
        foreach (var s in inv.StartupItems.OrderBy(s => s.Kind).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine($"  {s.Id,-28} {s.Kind,-14} {s.Scope,-8} {(s.Enabled ? "actif " : "inactif")} {s.Name,-40} protection={s.Protection ?? "-"}");
        }

        foreach (var u in inv.Usage.OrderByDescending(u => u.WorkingSetBytes).Take(8))
        {
            Console.WriteLine($"  usage {u.Name,-40} {u.WorkingSetGB,6} GB  {u.ProcessCount} proc, {u.WindowedProcessCount} avec fenetre{(u.Background ? "  [arriere-plan]" : string.Empty)}");
        }
    }

    // ---------------------------------------------------------------- demarrage (StartupApproved)

    private static async Task<int> StartupToggle(AppsModule apps, Func<EngineContext> ctx, Func<string, (string, string)[], Task<ActionOutcome>> run, string id)
    {
        await apps.EvaluateAsync(ctx(), CancellationToken.None);
        var item = apps.LastInventory!.StartupItems.SingleOrDefault(i => i.Id == id) ?? throw new InvalidOperationException($"Element {id} introuvable");
        if (item.Scope != AppScope.User || item.ApprovedSubKey is null || item.Protection is not null || !item.Enabled)
        {
            throw new InvalidOperationException("Choisir un element UTILISATEUR, actif, non protege, de type Run ou dossier Demarrage");
        }

        var key = $@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\{item.ApprovedSubKey}";
        var original = Registry.CurrentUser.OpenSubKey(key)?.GetValue(item.Name) as byte[];
        Console.WriteLine($"Element : {item.Name} ({item.Kind}) ; StartupApproved d'origine : {Hex(original)}");
        var ok = true;
        try
        {
            ok &= (await run(AppsModule.DisableStartupItem, [("id", id)])).Ok;
            var disabled = Registry.CurrentUser.OpenSubKey(key)?.GetValue(item.Name) as byte[];
            await apps.EvaluateAsync(ctx(), CancellationToken.None);
            var seenDisabled = apps.LastInventory!.StartupItems.Single(i => i.Id == id).Enabled;
            Console.WriteLine($"  apres desactivation : {Hex(disabled)} ; octet0 impair={disabled is { Length: > 0 } && (disabled[0] & 1) == 1} ; inventaire actif={seenDisabled}");
            ok &= disabled is { Length: 12 } && (disabled[0] & 1) == 1 && !seenDisabled;

            ok &= (await run(AppsModule.EnableStartupItem, [("id", id)])).Ok;
            var enabled = Registry.CurrentUser.OpenSubKey(key)?.GetValue(item.Name) as byte[];
            await apps.EvaluateAsync(ctx(), CancellationToken.None);
            var seenEnabled = apps.LastInventory!.StartupItems.Single(i => i.Id == id).Enabled;
            Console.WriteLine($"  apres reactivation  : {Hex(enabled)} ; inventaire actif={seenEnabled}");
            ok &= enabled is { Length: > 0 } && (enabled[0] & 1) == 0 && seenEnabled;

            using var run0 = Registry.CurrentUser.OpenSubKey(item.Kind == StartupKind.Run ? @"Software\Microsoft\Windows\CurrentVersion\Run" : key);
            Console.WriteLine($"  entree d'origine toujours presente : {item.Kind != StartupKind.Run || run0?.GetValue(item.Name) is not null}");
        }
        finally
        {
            // Restauration EXACTE des octets d'origine (ou suppression si la valeur n'existait pas).
            using var k = Registry.CurrentUser.CreateSubKey(key);
            if (original is null)
            {
                k.DeleteValue(item.Name, throwOnMissingValue: false);
            }
            else
            {
                k.SetValue(item.Name, original, RegistryValueKind.Binary);
            }

            Console.WriteLine($"  restaure : {Hex(Registry.CurrentUser.OpenSubKey(key)?.GetValue(item.Name) as byte[])}");
        }

        Console.WriteLine(ok ? "RESULTAT : OK" : "RESULTAT : ECHEC");
        return ok ? 0 : 1;
    }

    // ---------------------------------------------------------------- arret des processus d'arriere-plan

    private static async Task<int> ProcessStop(Func<EngineContext> ctx, Func<string, (string, string)[], Task<ActionOutcome>> run, ModuleServices services)
    {
        // Application de test : dossier dedie + entree Run temporaire (l'application est deduite de son dossier).
        var appDir = Path.Combine(Work, "AppTest");
        var otherDir = Path.Combine(Work, "Temoin");
        Directory.CreateDirectory(appDir);
        Directory.CreateDirectory(otherDir);
        var exe = Path.Combine(appDir, "realcheck-bg.exe");
        var witnessExe = Path.Combine(otherDir, "realcheck-bg.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), exe, overwrite: true);
        File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), witnessExe, overwrite: true);
        const string RunName = "FreeResRealCheck";
        using var runKey = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        runKey.SetValue(RunName, $"\"{exe}\" -n 600 127.0.0.1");

        using var target = Start(exe);
        using var witness = Start(witnessExe);   // meme nom, AUTRE dossier : ne doit pas etre touche
        var ok = true;
        try
        {
            await Task.Delay(1500);
            var apps = new AppsModule(services);   // nouvel inventaire incluant l'entree Run de test
            await apps.EvaluateAsync(ctx(), CancellationToken.None);
            var app = apps.LastInventory!.Apps.SingleOrDefault(a => a.Folder is not null && string.Equals(a.Folder.TrimEnd('\\'), appDir, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("Application de test non deduite de l'entree Run");
            Console.WriteLine($"Application de test : {app.Id} ({app.Folder}) ; cible pid {target.Id}, temoin pid {witness.Id}");

            ok &= (await run(AppsModule.StopAppProcesses, [("appId", app.Id)])).Ok;
            await Task.Delay(1000);
            Console.WriteLine($"  cible arretee : {target.HasExited} ; temoin intact : {!witness.HasExited}");
            ok &= target.HasExited && !witness.HasExited;
        }
        finally
        {
            runKey.DeleteValue(RunName, throwOnMissingValue: false);
            foreach (var p in new[] { target, witness }.Where(p => !p.HasExited))
            {
                p.Kill();
                p.WaitForExit(5000);
            }

            Directory.Delete(appDir, recursive: true);
            Directory.Delete(otherDir, recursive: true);
            Console.WriteLine($"  nettoye : entree Run retiree={Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")?.GetValue(RunName) is null}");
        }

        Console.WriteLine(ok ? "RESULTAT : OK" : "RESULTAT : ECHEC");
        return ok ? 0 : 1;
    }

    private static Process Start(string exe) => Process.Start(new ProcessStartInfo(exe)
    {
        ArgumentList = { "-n", "600", "127.0.0.1" },
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
    })!;

    // ---------------------------------------------------------------- service en demarrage manuel

    private static async Task<int> ServiceManual(Func<string, (string, string)[], Task<ActionOutcome>> run, string name)
    {
        var keyPath = $@"SYSTEM\CurrentControlSet\Services\{name}";
        using var key = Registry.LocalMachine.OpenSubKey(keyPath) ?? throw new InvalidOperationException($"Service {name} introuvable");
        var start = Convert.ToInt32(key.GetValue("Start"), CultureInfo.InvariantCulture);
        var delayed = Convert.ToInt32(key.GetValue("DelayedAutostart") ?? 0, CultureInfo.InvariantCulture);
        Console.WriteLine($"Service {name} : Start={start} DelayedAutostart={delayed}");
        var ok = true;
        try
        {
            ok &= (await run("SetServiceManual", [("service", name)])).Ok;
            using var after = Registry.LocalMachine.OpenSubKey(keyPath);
            var now = Convert.ToInt32(after!.GetValue("Start"), CultureInfo.InvariantCulture);
            Console.WriteLine($"  apres action : Start={now} (3 = manuel)");
            ok &= now == 3;
        }
        finally
        {
            var mode = start switch { 2 when delayed == 1 => "delayed-auto", 2 => "auto", 3 => "demand", 4 => "disabled", _ => "auto" };
            var r = await new ProcessRunner().RunAsync(Path.Combine(Environment.SystemDirectory, "sc.exe"), ["config", name, "start=", mode], TimeSpan.FromSeconds(30), CancellationToken.None);
            using var back = Registry.LocalMachine.OpenSubKey(keyPath);
            Console.WriteLine($"  restaure ({mode}, code {r.ExitCode}) : Start={back!.GetValue("Start")} DelayedAutostart={back.GetValue("DelayedAutostart") ?? 0}");
        }

        Console.WriteLine(ok ? "RESULTAT : OK" : "RESULTAT : ECHEC");
        return ok ? 0 : 1;
    }

    // ---------------------------------------------------------------- Docker

    private static async Task<int> DockerCycle(ModuleSetup setup, Func<EngineContext> ctx, ActionCatalog catalog, FreeResDatabase db)
    {
        var docker = setup.Modules.OfType<DockerModule>().Single();
        for (var round = 1; round <= 2; round++)
        {
            var eval = await docker.EvaluateAsync(ctx(), CancellationToken.None);
            Console.WriteLine($"Cycle {round} : {eval.AutoActions.Count} action(s) auto, {eval.Proposals.Count} proposition(s) [{string.Join(", ", eval.Proposals.Select(p => $"{p.Id} ~{p.EstGainGB} GB"))}]");
            foreach (var auto in eval.AutoActions)
            {
                var outcome = await catalog.Resolve(auto).Execute(auto, false, CancellationToken.None);
                Console.WriteLine($"  -> {auto.Type} : {(outcome.Ok ? "OK" : "ECHEC")} {outcome.Message}");
                docker.OnActionCompleted(auto, new ActionResult(auto.Type, outcome.Ok, outcome.Message, 0, false));
            }
        }

        // Etat memorise par le module (mesure reelle df -B1 + docker system df, date du trim).
        Console.WriteLine($"  mesure : {db.GetState("module.docker.diskUsage")}");
        Console.WriteLine($"  trimmedAt : {db.GetState("module.docker.trimmedAt")}");
        return db.GetState("module.docker.diskUsage") is null ? 1 : 0;
    }

    private static string Hex(byte[]? b) => b is null ? "(absente)" : Convert.ToHexString(b);
}
