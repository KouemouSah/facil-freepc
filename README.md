# Facil FreePC

Libération et contrôle des ressources Windows (mémoire, disque, navigateurs, applications, IA).
Une solution de la marque **Facil** — © 2026 EMAC SAH AI Solutions Architect (Emac SAH).

> État : **Phase 0 — socle** (fenêtre, serveur local sécurisé, installeur). Les modules arrivent
> phase par phase (plan : `C:\Users\User\.claude\plans\2026-11-09-freeres-exe.md`).

## Construire (local)

Prérequis : SDK .NET 10 (`global.json`), Inno Setup 7 (ou 6).

```powershell
.\build.ps1          # compilation (avertissements = erreurs) -> tests -> exe unique -> installeur -> SHA-256
```

Résultat dans `artifacts\` : `FacilFreePC-Setup-<version>.exe` et son `.sha256`.
La version vient de `Directory.Build.props` (source unique).

## Architecture

| Projet | Rôle |
|---|---|
| `src/FreeRes.Core` | Identité, chemins de données (`%LOCALAPPDATA%\FacilFreePC`) ; moteur (phase 1) |
| `src/FreeRes.Host` | Serveur web local Kestrel **127.0.0.1** + API + interface embarquée (`wwwroot`) |
| `src/FreeRes.App` | `FacilFreePC.exe` : instance unique, icône de notification, fenêtre WebView2 |
| `tests/*` | xUnit : Core, sécurité du serveur (requêtes HTTP réelles), coquille Windows |
| `installer/` | Inno Setup : installation par utilisateur, démarrage de session, désinstallation propre |
| `tools/New-AppIcon.ps1` | Génère `assets/app.ico` multi-tailles depuis l'icône de marque |

Nom de code interne des projets : `FreeRes` ; nom commercial : **Facil FreePC**.

## Sécurité de l'interface locale

Un serveur sur 127.0.0.1 n'est pas privé : toute page web ouverte peut l'appeler. Trois barrières
(`LocalSecurityMiddleware`) : hôte strictement `127.0.0.1` (anti « DNS rebinding »), jeton de session
aléatoire de 256 bits en cookie HttpOnly posé uniquement dans le profil WebView2 dédié, et origine
identique exigée pour toute requête modifiante (anti-CSRF). L'application tourne avec les droits de
l'utilisateur ; aucune écoute réseau hors de la machine.

## Distribution sans certificat (risque accepté)

L'exe et l'installeur ne sont pas signés : Windows SmartScreen affiche un avertissement au premier
lancement. Atténuations : construction reproductible, empreintes SHA-256 publiées avec chaque version,
soumission de chaque version à l'analyse Microsoft, aucun compresseur ni brouilleur de code.

## Installation / désinstallation

`FacilFreePC-Setup-x.y.z.exe` (assistant, ou `/VERYSILENT /TASKS=startup`). Installation dans
`%LOCALAPPDATA%\Programs\Facil FreePC`, sans droits administrateur. La désinstallation ferme
l'application, retire le démarrage automatique et propose (jamais en silencieux) de supprimer les données.
