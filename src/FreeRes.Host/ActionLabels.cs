using System.Collections.Frozen;

namespace FreeRes.Host;

/// <summary>
/// Intitules affiches a l'utilisateur pour chaque type d'action du catalogue (historique, « Fait automatiquement »).
/// Un test verifie que TOUTES les actions du catalogue reel ont un intitule.
/// </summary>
public static class ActionLabels
{
    private static readonly FrozenDictionary<string, string> Labels = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["CleanUserZone"] = "Nettoyage automatique",
        ["CleanSystemZones"] = "Nettoyage des zones système",
        ["NpmCacheClean"] = "Nettoyage du cache npm",
        ["EmptyRecycleBin"] = "Corbeille vidée",
        ["ScheduleReboot"] = "Redémarrage planifié",
        ["WindowsUpdateCacheClean"] = "Nettoyage du cache Windows Update",
        ["DismComponentCleanup"] = "Nettoyage des composants Windows",
        ["DockerPrune"] = "Purge Docker",
        ["DockerTrim"] = "Récupération de l'espace dans Docker",
        ["WslTerminate"] = "Arrêt de la machine virtuelle Docker inutilisée",
        ["CompactDockerVhdx"] = "Compactage du disque Docker",
        ["DisableStartupItem"] = "Démarrage automatique désactivé",
        ["EnableStartupItem"] = "Démarrage automatique réactivé",
        ["DisableMachineStartupItem"] = "Démarrage automatique désactivé",
        ["EnableMachineStartupItem"] = "Démarrage automatique réactivé",
        ["StopAppProcesses"] = "Application arrêtée",
        ["SetServiceManual"] = "Service passé en démarrage manuel",
        ["OpenBrowserSettings"] = "Réglages du navigateur ouverts",
        ["RestartBrowser"] = "Navigateur redémarré",
        ["InstallFirefoxExtension"] = "Installation de l'extension Firefox",
        ["UnloadOllamaModel"] = "Modèle Ollama déchargé",
        ["InstallUpdate"] = "Mise à jour de Facil FreePC",
        ["OpenModelsFolder"] = "Dossier des modèles ouvert",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    public static bool IsKnown(string type) => Labels.ContainsKey(type);

    /// <summary>Intitule de l'action, ou son type si inconnu (jamais vide).</summary>
    public static string For(string type) => Labels.TryGetValue(type, out var label) ? label : type;

    /// <summary>
    /// Message du journal (« Type : detail » ou « Type (élevé) : detail », « (eleve) » dans les journaux anterieurs a 0.6) reecrit avec l'intitule.
    /// Un message d'une autre forme est rendu tel quel.
    /// </summary>
    public static string Rewrite(string type, string message)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(message);
        foreach (var prefix in new[] { type + " (élevé) : ", type + " (eleve) : ", type + " : " })
        {
            if (message.StartsWith(prefix, StringComparison.Ordinal))
            {
                return For(type) + " : " + message[prefix.Length..];
            }
        }

        return message;
    }
}
