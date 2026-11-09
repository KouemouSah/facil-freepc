# FreeRes.RealCheck — vérifications réelles avant une version

Les tests automatiques simulent le registre, les processus et les programmes externes. Cet outil exécute les
**vraies** actions des modules sur la machine, puis **restaure l'état d'origine** (même en cas d'erreur).
Il utilise exactement le catalogue d'actions de l'application (`ModulePack`) et prévient les modules
observateurs comme le moteur.

```powershell
dotnet build tools\FreeRes.RealCheck -c Release
$rc = 'tools\FreeRes.RealCheck\bin\Release\net10.0-windows10.0.17763.0\FreeRes.RealCheck.exe'
& $rc inventory                     # lecture seule : applications, démarrage, services, tâches, consommation
& $rc startup-toggle <id>           # élément de démarrage UTILISATEUR actif : désactive, réactive, octets restaurés
& $rc process-stop                  # application de test (copie de ping.exe) arrêtée, témoin d'un autre dossier épargné
& $rc service-manual <nom>          # (admin) service en manuel puis type de démarrage d'origine restauré
& $rc docker-cycle                  # Docker démarré : fstrim + mesure réels
& $rc docker-compact                # (admin) Docker arrêté : compactage diskpart réel
& $rc browsers                      # lecture seule : navigateurs, économiseur, profils, redémarrage possible
& $rc browser-restart <id>          # RÉEL, avec accord : fermeture propre puis relance (terminal NON élevé)
& $rc ai                            # lecture seule : applications d'IA, modèles Ollama chargés et installés
& $rc ai-unload <modèle>            # RÉEL : décharge un modèle Ollama chargé, résultat vérifié
```

Code de sortie 0 = vérification réussie. Fichiers de travail : `%TEMP%\FreeResRealCheck` (supprimables).

## Dernière campagne (2026-11-09, Dell Latitude 5430, Windows 11 25H2)

| Vérification | Résultat |
|---|---|
| Inventaire | 99 applications, 36 éléments de démarrage ; **défaut trouvé et corrigé** : une application déclarant `C:\WINDOWS\system32` se voyait attribuer 67 processus Windows |
| `startup-toggle` (Microsoft Edge Update) | `03`+FILETIME puis `02`, inventaire à jour, entrée Run intacte, octets d'origine restaurés |
| `process-stop` | cible arrêtée, témoin (même nom, autre dossier) intact, entrée Run et fichiers de test retirés |
| `service-manual gupdate` | `Start=3`, puis démarrage automatique **différé** d'origine restauré (vérifié par `sc qc`) |
| `docker-cycle` | `fstrim` code 0 ; mesure 5,74 GB de données / VHDX 7,74 GB (identique à la mesure manuelle) |
| `docker-compact` | diskpart par entrée standard : 7,71 → 7,70 GB, VHDX détaché ; après redémarrage de Docker : volumes et images identiques, données Postgres lisibles |
| `browser-restart firefox-dev` (accord de l'utilisateur, lancé **non élevé** via l'explorateur) | Fermeture par Ctrl+Maj+Q puis relance : 2 fenêtres / 13 onglets avant **et** après, fenêtres fermées conservées, Firefox relancé sans droits administrateur sur le bon profil. Mémoire 0,96 → 1,43 GB à +15 s (Firefox était léger : le redémarrage n'est proposé qu'au-delà de ~5,5 GB et 12 h). Constat : la relance « -profile chemin » fait écrire à Firefox des erreurs de raccourci de barre des tâches → relance **sans argument** pour le profil par défaut de l'édition |
| `ai` | Aucune application d'IA détectée, aucun serveur Ollama en boucle locale : conforme à l'analyse manuelle. Le processus `LMS.exe` présent (service Intel, pas LM Studio) n'est **pas** pris pour LM Studio grâce à la détection par emplacement |

> Lancer `browser-restart` depuis un terminal **non élevé** : depuis un terminal administrateur, le navigateur
> serait relancé avec des droits administrateur.
