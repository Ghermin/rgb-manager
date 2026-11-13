# Gestionnaire RGB : notes pour les sessions de développement

Appli Windows (zone de notification) qui pilote l'éclairage RGB en mode direct. Voir `README.md` (usage) et `docs/HARDWARE.md` (matériel, protocoles, leçons apprises : à lire avant de toucher aux protocoles).

## Compiler / installer

- `pwsh -File build.ps1` → `out\RgbManager.exe`. Compilateur = Roslyn embarqué dans **PowerShell 7 64 bits** (`C:\Program Files\PowerShell\7\pwsh.exe`), cible **.NET Framework 4.8**. Pas de .NET SDK ni Visual Studio sur ce PC : ne pas créer de `.csproj`.
- Conséquence .NET 4.8 : pas de `System.Text.Json`, `Math.Clamp`, `NativeLibrary`, plages `a[1..]`, `Environment.ProcessPath` → utiliser `Compat.cs`. La syntaxe C# récente (switch expressions, patterns, raw strings, `global using`) fonctionne.
- L'exe est autonome (module `SmbusPIIX4.bin` embarqué en ressource par `build.ps1`) et **s'installe tout seul** comme Averse / CamTel : lancé hors de `C:\Program Files\RgbManager` sans option, il demande l'UAC, ferme la version installée (signal `Local\RgbManager.Quit`, puis de force), se copie, recrée la tâche planifiée `RgbManager`, les raccourcis Bureau + menu Démarrer, l'entrée de désinstallation (HKLM, `--uninstall`), propose PawnIO (installateur officiel téléchargé depuis `namazso/PawnIO.Setup`, signature `CN=namazso.eu` vérifiée) puis relance via la tâche. Code : `src/Installer.cs`. `--portable` évite l'installation.
- Mises à jour : `src/Updater.cs` (2 min après le lancement puis toutes les 6 h, seulement la copie installée en admin) télécharge `RgbManager.exe` de la dernière release dans le dossier d'installation (réservé aux admins, jamais `%TEMP%` : l'exe sera lancé en admin), vérifie taille + SHA-256, le lance avec `--updated` (installation sans fenêtre) et se ferme.
- Publier une version : `AssemblyVersion` dans `src/Compat.cs`, commit, étiquette `vX.Y.Z` poussée → `.github/workflows/build.yml` compile, vérifie que l'étiquette = la version de l'exe, crée la release avec `RgbManager.exe`.
- `pwsh -File deploy.ps1` : lance `out\RgbManager.exe --updated` (une fenêtre UAC, puis installation silencieuse).
- Pas de TargetFramework dans l'exe → .NET 4.8 n'active pas TLS 1.2 tout seul : `GitHub.cs` le force. JSON : `JavaScriptSerializer` (System.Web.Extensions). Ne pas utiliser `dynamic` (pas de Microsoft.CSharp référencé).
- Le PowerShell par défaut de cette machine est **32 bits** : passer par `C:\Program Files\PowerShell\7\pwsh.exe` pour tout ce qui touche au 64 bits (registre, `System32`, modules de processus).

## Tester

- `out\RgbManager.exe` lancé sans option **s'installe** : pour un essai, passer une option (`--test N`, `--portable --no-elevate`…).
- Arrêter l'appli installée : `schtasks /End /TN RgbManager` ; la relancer : `schtasks /Run /TN RgbManager`.
- Journal : `C:\ProgramData\RgbManager\log.txt` (une exécution non admin écrit dans `%TEMP%\RgbManager-log.txt`).
- HID (carte mère, Commander CORE) : testable sans admin avec `--no-elevate`. RAM : admin obligatoire (PawnIO).
- Options de diagnostic : `--test N`, `--aura-probe`, `--aura-probe2`, `--argb-count` (voir `Program.cs`).
- Le rendu se vérifie **à l'œil** : demander à l'utilisateur ce qu'il voit.

## Règles de sécurité (ne pas assouplir)

- SMBus : n'écrire qu'aux adresses `0x58`–`0x5F` (`CorsairDram.Guard`). Jamais `0x50`–`0x57` (SPD).
- Commander CORE : ne jamais écrire de vitesse de pompe/ventilateur ; garder la surveillance de la pompe (< 400 tr/min → mode matériel) et le retour au mode matériel en quittant.
- Mutex SMBus global `Global\Access_SMBUS.HTP.Method` autour de chaque accès (partagé avec HWiNFO, LibreHardwareMonitor…).

## Ajouter un appareil (autre PC)

1. Inventaire : `Get-PnpDevice -PresentOnly` (VID/PID USB), journal de détection, éventuellement un ancien profil OpenRGB (`.orp`) qui liste zones et nombres de LED.
2. Chercher le contrôleur dans les sources d'OpenRGB (`Controllers/…`), privilégier le **mode direct**.
3. Nouvelle classe dans `src/`, détection dans `Engine.Detect()`, envoi d'images dans `Engine.Loop()`, statut dans le menu (`TrayApp`).
4. Documenter dans `docs/HARDWARE.md`.

## Git

- Dépôt public `github.com/Ghermin/rgb-manager`, remote SSH.
- Identité des commits : `Ghermin <Ghermin@users.noreply.github.com>` (configurée en local dans le dépôt ; la config globale contient une adresse pro à ne pas utiliser ici).

## Bored Dev Playground

Ce projet fait partie du Bored Dev Playground : https://ghermin.github.io/ (repo `Ghermin/Ghermin.github.io`, en local `../Ghermin.github.io`).
Sa page GitHub (`docs/index.html`, publiée sur https://ghermin.github.io/rgb-manager/) suit le kit commun du playground.

Avant de modifier la page ou le style, se demander si le changement est local ou global :

- **Global** (bandeau, cartes, boutons, puces, pied de page, couleurs neutres, comportement clair / sombre…) : ne pas le faire ici. `docs/playground.css` est une copie de `kit/playground.css` du playground : modifier le kit là-bas, lancer `sh kit/sync.sh`, puis commiter dans chaque projet.
- **Local** (couleurs du projet) : le faire dans la page, en redéfinissant les variables `--pg-*` après le kit.
- Si le nom, la description, les tags, l'icône ou l'adresse du projet changent, mettre à jour sa carte dans `index.html` du playground.
- Le menu reproduit sur la page reprend les entrées de `TrayApp.BuildMenu()` : le mettre à jour si le menu change.
- Le vrai menu a le même design que la page (`src/MenuTheme.cs` : couleurs `Palette.Light` / `Palette.Dark` = variables `--pg-*` de `docs/index.html`, coins arrondis DWM sous Windows 11). Il suit le mode clair / sombre de Windows, relu à chaque ouverture du menu. Changer les couleurs aux deux endroits.
- Icône : celle de l'appli (`assets/make-icon.ps1`), copiée dans `docs/icon.png` et `docs/favicon.ico`, et dans `assets/projects/rgb-manager.png` du playground. Pas (encore) de SVG dans `brand/icons/` : ne pas ajouter `rgb-manager` à `TARGETS` de `brand/build.js` sans revoir l'icône, il écraserait `assets/icon.ico`.
- L'appli porte le lien discret « un projet du bored dev playground » en bas de son menu (`TrayApp.Playground`).

À garder sur la page : le bandeau `pg-strip` qui ramène au playground, le thème clair / sombre automatique, le responsive (pas de défilement horizontal sur mobile), 2 polices max (Sora + JetBrains Mono).
