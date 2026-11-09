<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="assets/logo-dark.png">
    <img src="assets/logo.png" alt="Facil — simplificarte la vida" width="260">
  </picture>
</p>

<h1 align="center">Facil FreePC</h1>

<p align="center">
  <strong>Votre PC Windows reste fluide : la mémoire et le disque sous contrôle, sans rien sacrifier.</strong><br>
  Une solution de la marque <strong>Facil</strong>, éditée par Emac SAH.
</p>

<p align="center">
  <img alt="Windows 10 | 11 (x64)" src="https://img.shields.io/badge/Windows-10%20%7C%2011%20(x64)-0078D4">
  <img alt=".NET 10" src="https://img.shields.io/badge/.NET-10-512BD4">
  <img alt="Version 0.1.0" src="https://img.shields.io/badge/version-0.1.0-2EA043">
  <img alt="Statut : en développement" src="https://img.shields.io/badge/statut-en%20d%C3%A9veloppement-orange">
</p>

---

## Le problème

Un PC Windows ne tombe pas en panne d'un coup : il **se fige**. Le plus souvent, ce n'est pas le
processeur qui manque, mais la **mémoire** : les navigateurs ouverts depuis des heures, les outils de
développement, Docker et les applications d'IA locales finissent par épuiser la mémoire disponible.
Ajoutez un disque système presque plein, et Windows n'a plus de marge : tout ralentit, puis plus rien
ne répond.

Les solutions habituelles sont soit trop timides (un nettoyeur de fichiers temporaires), soit trop
brutales (fermer des programmes de force, au risque de perdre son travail).

> **Constat réel, sur la machine de développement** : Firefox, ouvert depuis environ 17 heures,
> occupait **9,85 GB** de mémoire ; après un redémarrage propre (session restaurée), il est redescendu
> à **1,06 GB**. Il s'agit d'un cas observé, pas d'une promesse de résultat.

## La solution

Facil FreePC surveille en continu les ressources de votre PC, **fait seul ce qui est sans risque**, et
**vous laisse décider pour tout le reste**, avec une explication claire de chaque proposition.

| Principe | Ce que cela veut dire pour vous |
|---|---|
| **Automatique quand c'est sans risque** | Les actions sûres (par exemple, le nettoyage de zones temporaires connues) se font sans vous déranger. |
| **Votre décision pour le reste** | Tout ce qui touche à vos programmes ou à vos sessions vous est proposé, jamais imposé. |
| **Jamais de geste irréversible automatique** | Aucune désinstallation automatique, aucune suppression de modèles d'IA ni de volumes Docker, aucune fermeture forcée d'un programme inconnu. |
| **Aucune télémétrie** | Rien n'est envoyé à Emac SAH ni à un tiers. Vos données restent sur votre PC. |
| **Sans droits administrateur** | L'application tourne avec vos droits d'utilisateur. Une action qui exige davantage déclenche une demande Windows (UAC) pour cette seule action. |
| **Tous vos navigateurs** | Firefox, Chrome, Edge, Brave, Opera et Vivaldi, dès qu'ils sont détectés sur le PC. |
| **Applications d'IA locales** | Ollama, LM Studio et les autres : modèles chargés, mémoire vidéo, place occupée par les modèles. |

Facil FreePC reprend et étend les capacités d'un outil interne éprouvé (une version PowerShell en
service sur la machine de développement), pour en faire un produit installable par tous.

## Disponible aujourd'hui, et feuille de route

**Version 0.1.0 — le socle.** Ce qui fonctionne aujourd'hui :

- une application discrète dans la **zone de notification** (à côté de l'horloge), qui ne se lance
  qu'une fois même si vous la rouvrez ;
- une **fenêtre moderne, de type web**, qui s'adapte à la taille et à l'échelle de votre écran ;
- un **serveur local sécurisé**, accessible uniquement depuis votre PC et uniquement par cette fenêtre ;
- un **installateur sans droits administrateur**, avec démarrage automatique à l'ouverture de session (option) ;
- une **désinstallation propre** : l'application est fermée, le démarrage automatique retiré, et vos
  données ne sont supprimées que si vous le confirmez.

> À ce stade, Facil FreePC **ne mesure ni ne libère encore aucune ressource** : la fenêtre affiche
> l'état du socle. Les fonctions de surveillance et d'action arrivent avec les phases suivantes.

| Phase | Contenu | Statut |
|---|---|---|
| **0 — Socle** | Application de notification, fenêtre WebView2, serveur local sécurisé, installateur, désinstallation propre | Terminée |
| **1 — Moteur** | Mesures de la mémoire et du disque, profil automatique du PC avec seuils adaptés à sa mémoire, file de décisions (approuver, rejeter, reporter), journal et historique | À venir |
| **2 — Modules système** | Nettoyage du disque limité aux zones sûres, maintenance Windows Update (avec accord UAC), Docker et WSL s'ils sont présents, inventaire des applications et de ce qui démarre avec Windows | À venir |
| **3 — Navigateurs** | Détection de Firefox, Chrome, Edge, Brave, Opera et Vivaldi ; mise en veille automatique des onglets inactifs ; redémarrage propre du navigateur sur votre clic, avec contrôle de la restauration de session | À venir |
| **4 — IA** | Détection des applications d'IA, modèles chargés, mémoire vidéo et disque ; propositions de décharger un modèle inactif ou d'arrêter un serveur inutilisé (jamais de suppression) | À venir |
| **5 — Interface** | Tableau de bord sans défilement (santé, décisions, actions faites, applications, navigateurs, IA, historique), écran « Comment ça marche », notifications Windows | À venir |
| **6 — Distribution** | Installateur final, mises à jour, tests sur Windows 10 et 11 vierges, première version publiée avec son empreinte SHA-256 | À venir |

## Installation

**Configuration requise** : Windows 10 (version 1809 ou ultérieure) ou Windows 11, 64 bits.
Le moteur Microsoft Edge WebView2, présent sur les Windows à jour, est nécessaire à l'affichage de la
fenêtre ; s'il manque, l'application propose d'ouvrir la page de téléchargement officielle de Microsoft.

> Aucune version publique n'est encore diffusée : la première sera publiée en Phase 6.
> D'ici là, l'installateur est produit à partir du code (voir « Pour les développeurs »).

### 1. Vérifier le fichier

Chaque installateur `FacilFreePC-Setup-<version>.exe` est accompagné d'un fichier `.sha256` contenant
son empreinte. Avant d'installer, comparez-la avec celle du fichier téléchargé :

```powershell
Get-FileHash .\FacilFreePC-Setup-0.1.0.exe -Algorithm SHA256
```

Les deux empreintes doivent être **strictement identiques**. Sinon, n'exécutez pas le fichier.

### 2. L'avertissement de Windows SmartScreen

Facil FreePC n'est **pas signé numériquement** (pas de certificat de signature de code, un choix assumé
à ce stade). Au premier lancement, Windows SmartScreen affiche donc un avertissement indiquant que
l'application n'est pas reconnue. Si l'empreinte SHA-256 correspond, cliquez sur
**Informations complémentaires**, puis **Exécuter quand même**.

Ce que nous faisons pour compenser l'absence de signature : construction reproductible, empreintes
publiées avec chaque version, soumission des versions à l'analyse Microsoft, aucun compresseur ni
outil de brouillage du code (souvent à l'origine de fausses alertes antivirus).

### 3. Installer

L'assistant installe Facil FreePC **pour votre seul compte**, sans demander de droits administrateur,
dans `%LOCALAPPDATA%\Programs\Facil FreePC`. Il propose :

- le démarrage à l'ouverture de session (coché par défaut : la surveillance tourne en arrière-plan) ;
- une icône sur le bureau (décochée par défaut).

Ensuite, Facil FreePC vit dans la zone de notification : **double-clic** pour ouvrir la fenêtre,
**clic droit > Quitter** pour l'arrêter. Fermer la fenêtre ne l'arrête pas.

### 4. Mettre à jour ou désinstaller

- **Mise à jour** : lancez simplement le nouvel installateur ; il ferme proprement la version en cours
  avant de la remplacer.
- **Désinstallation** : Paramètres Windows > Applications > Facil FreePC. L'application est fermée, le
  démarrage automatique retiré, puis on vous demande si vous voulez aussi supprimer vos données
  (réponse par défaut : non). En désinstallation silencieuse, les données sont toujours conservées.

## Confidentialité et sécurité

- **Vos données restent chez vous.** Tout est stocké dans `%LOCALAPPDATA%\FacilFreePC`, sur votre PC.
  Facil FreePC n'envoie rien à Emac SAH ni à personne : pas de compte, pas de télémétrie, pas de
  publicité.
- **Rien n'est ouvert sur le réseau.** L'interface est servie par un petit serveur qui n'écoute que sur
  votre PC (adresse `127.0.0.1`), jamais sur votre réseau local ni sur Internet.
- **Seule la fenêtre de Facil FreePC peut le commander.** Un site web ouvert dans votre navigateur ne
  peut pas l'utiliser à votre insu : chaque requête doit présenter une clé secrète, renouvelée à chaque
  lancement et connue de la seule fenêtre de l'application.
- **Vos droits, pas plus.** L'application fonctionne avec les droits de votre compte. Aucun service
  système permanent n'est installé.
- **Liens externes dans votre navigateur.** Un lien vers Internet s'ouvre dans votre navigateur
  habituel, jamais dans la fenêtre de l'application.

---

## Pour les développeurs

Nom commercial : **Facil FreePC** (exécutable `FacilFreePC.exe`). Nom de code interne des projets :
`FreeRes`.

### Construire

Prérequis : SDK .NET 10 (version fixée par `global.json`), Inno Setup 7 (Inno Setup 6 accepté).

```powershell
.\build.ps1                 # séquence complète
.\build.ps1 -Iscc <chemin>  # ISCC.exe explicite (sinon : variable INNO_ISCC, puis Inno Setup 7, puis 6)
.\build.ps1 -SkipTests      # essai rapide uniquement : une version distribuée passe TOUJOURS les tests
```

Séquence de `build.ps1` :

1. compilation Release de `FreeRes.sln`, **avertissements compilateur et MSBuild traités comme des erreurs** ;
2. tests xUnit (`dotnet test`) ;
3. publication d'un **exe unique autonome** win-x64 (aucun runtime .NET à installer), avec contrôle
   qu'aucun autre fichier n'est produit ;
4. compilation de l'installeur Inno Setup (`installer/FacilFreePC.iss`) ;
5. calcul des empreintes SHA-256.

Résultat dans `artifacts\` : `FacilFreePC-Setup-<version>.exe`, `publish\FacilFreePC.exe` et un
fichier `.sha256` pour chacun. La version a une source unique : `Directory.Build.props`.

Tests seuls : `dotnet test FreeRes.sln`.

### Architecture

| Élément | Rôle |
|---|---|
| `src/FreeRes.Core` | Identité de l'application (lue depuis les attributs d'assembly), chemins de données ; accueillera le moteur (Phase 1) |
| `src/FreeRes.Host` | Serveur Kestrel sur **127.0.0.1** (port choisi par le système), middleware de sécurité, API (`/api/health`), interface embarquée (`wwwroot`) |
| `src/FreeRes.App` | `FacilFreePC.exe` : instance unique, icône de notification, fenêtre WebView2 avec profil dédié |
| `tests/FreeRes.*.Tests` | xUnit : Core, sécurité du serveur par requêtes HTTP réelles, coquille Windows (instance unique, dimensionnement de la fenêtre) |
| `installer/FacilFreePC.iss` | Inno Setup : installation par utilisateur, démarrage de session, fermeture avant mise à jour ou désinstallation |
| `tools/New-AppIcon.ps1` | Génère `assets/app.ico` (multi-tailles) à partir de l'icône de marque |
| `tools/New-DarkLogo.ps1` | Génère `assets/logo-dark.png`, variante du logo pour fond sombre |
| `assets/` | Logo (servi aussi par l'interface), icône, images de l'assistant d'installation |
| `.github/workflows/` | `ci.yml` et `release.yml`, préparés mais **désactivés** : la construction se fait aujourd'hui en local |

Règles de qualité communes (`Directory.Build.props`) : nullable activé, analyseurs
`latest-recommended`, style de code vérifié à la compilation, build déterministe, symboles embarqués.

Arguments de `FacilFreePC.exe` :

| Argument | Effet |
|---|---|
| *(aucun)* | Démarre et ouvre la fenêtre ; si une instance tourne déjà, c'est elle qui s'affiche |
| `--background` | Démarre dans la zone de notification sans ouvrir la fenêtre (utilisé à l'ouverture de session) |
| `--quit` | Demande à l'instance en cours de se fermer et **attend sa fin réelle** (15 s au plus) ; code 1 si elle ne s'est pas fermée |

Données : `%LOCALAPPDATA%\FacilFreePC` (sous-dossiers `logs` et `WebView2`). La variable
d'environnement `FREERES_DATA_DIR` permet d'isoler ce dossier, notamment pour les tests.

### Sécurité de l'interface locale

Un serveur sur 127.0.0.1 n'est **pas** privé : n'importe quelle page web ouverte dans un navigateur
peut lui envoyer des requêtes. `LocalSecurityMiddleware` applique trois barrières, dans cet ordre :

| Contrôle | Menace bloquée | Réponse |
|---|---|---|
| En-tête `Host` strictement égal à `127.0.0.1` | DNS rebinding | 400 |
| Cookie de session égal au jeton (256 bits aléatoires, comparaison en temps constant) | Toute page ou tout client autre que la fenêtre de l'application | 401 |
| Pour les méthodes modifiantes : `Origin` identique et `Sec-Fetch-Site: same-origin` | Requêtes inter-sites (CSRF) | 403 |

Le jeton est généré à chaque lancement et posé en cookie **HttpOnly, SameSite=Strict** dans un profil
WebView2 dédié : il n'apparaît ni dans l'URL ni dans les navigateurs de l'utilisateur. Toutes les
réponses portent une CSP stricte, `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`,
`Referrer-Policy: no-referrer` et `Cache-Control: no-store`. Dans la fenêtre, les outils de
développement (sauf sous débogueur), le menu contextuel et la saisie automatique sont désactivés, et toute navigation hors de
l'interface locale est renvoyée vers le navigateur par défaut.

L'exe est lancé avec le niveau `asInvoker` (`app.manifest`) : jamais d'élévation au démarrage. Les
actions administrateur prévues dans les phases suivantes relanceront l'exe en `runas` pour **une**
action typée, sans service système permanent.

### Distribution sans certificat (risque accepté)

L'exe et l'installeur ne sont pas signés : SmartScreen avertit au premier lancement et des faux
positifs antivirus sont possibles. Atténuations retenues : construction reproductible, empreintes
SHA-256 publiées avec chaque version, soumission de chaque version à l'analyse Microsoft, aucun
compresseur ni brouilleur de code. Installation silencieuse possible :
`FacilFreePC-Setup-<version>.exe /VERYSILENT /TASKS=startup`.

---

<p align="center">
  <img src="assets/wizard-small-110.png" alt="Facil FreePC" width="55"><br>
  <strong>Facil FreePC</strong> — une solution de la marque <strong>Facil</strong> · <em>simplificarte la vida</em><br>
  Emac SAH · © 2026 EMAC SAH AI Solutions Architect · Tous droits réservés.
</p>
