namespace FreeRes.Modules.Apps;

/// <summary>
/// Rattachement des processus aux applications et aux services.
///   * Session de l'utilisateur : image = executable d'un element de demarrage (rattachement exact), sinon dossier
///     d'application le plus long qui la contient (comparaison par segment : « C:\App » ne capture pas « C:\AppData »).
///   * Session 0 (services) : image = binaire d'un service tiers. Ces processus ne comptent PAS dans la memoire de
///     l'application : desactiver un element Run ou arreter des processus utilisateur ne les libererait pas.
/// </summary>
internal static class ProcessAttribution
{
    public static (IReadOnlyList<AppUsage> Apps, IReadOnlyList<ServiceUsage> Services) Measure(
        IReadOnlyList<InstalledApp> apps,
        IReadOnlyList<StartupItem> items,
        IReadOnlyList<ProcessSample> processes,
        int sessionId)
    {
        var byFolder = apps.Where(a => a.Folder is not null).OrderByDescending(a => a.Folder!.Length).ToList();
        var byExe = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var serviceByExe = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var i in items.Where(i => i.ExecutablePath is not null))
        {
            if (i.Kind == StartupKind.Service)
            {
                if (!serviceByExe.TryGetValue(i.ExecutablePath!, out var names))
                {
                    serviceByExe[i.ExecutablePath!] = names = [];
                }

                names.Add(i.ServiceName ?? i.Name);
            }
            else if (i.AppId is not null)
            {
                byExe.TryAdd(i.ExecutablePath!, i.AppId);
            }
        }

        var appTotals = new Dictionary<string, (int Count, int Windowed, long Bytes)>(StringComparer.Ordinal);
        var svcTotals = new Dictionary<string, (int Count, long Bytes)>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in processes)
        {
            if (AppPathRules.Normalize(p.ImagePath) is not { } image)
            {
                continue;
            }

            if (p.SessionId == sessionId)
            {
                var appId = AppOf(image, byExe, byFolder);
                if (appId is not null)
                {
                    appTotals.TryGetValue(appId, out var t);
                    appTotals[appId] = (t.Count + 1, t.Windowed + (p.HasWindow ? 1 : 0), t.Bytes + p.WorkingSetBytes);
                }
            }
            else if (p.SessionId == 0 && serviceByExe.TryGetValue(image, out var names))
            {
                foreach (var name in names)
                {
                    svcTotals.TryGetValue(name, out var t);
                    svcTotals[name] = (t.Count + 1, t.Bytes + p.WorkingSetBytes);
                }
            }
        }

        var appNames = apps.ToDictionary(a => a.Id, a => a.Name, StringComparer.Ordinal);
        var usage = appTotals
            .Select(kv => new AppUsage(kv.Key, appNames.GetValueOrDefault(kv.Key, kv.Key), kv.Value.Count, kv.Value.Windowed, kv.Value.Bytes))
            .OrderByDescending(u => u.WorkingSetBytes)
            .ToList();
        var services = svcTotals
            .Select(kv => new ServiceUsage(kv.Key, kv.Value.Count, kv.Value.Bytes))
            .OrderByDescending(s => s.WorkingSetBytes)
            .ToList();
        return (usage, services);
    }

    /// <summary>Application d'une image normalisee, ou null.</summary>
    internal static string? AppOf(string image, IReadOnlyDictionary<string, string> byExe, IReadOnlyList<InstalledApp> byFolderLongestFirst) =>
        byExe.TryGetValue(image, out var id)
            ? id
            : byFolderLongestFirst.FirstOrDefault(a => AppPathRules.IsUnderNormalized(image, a.Folder!))?.Id;
}
