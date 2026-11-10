# Gestionnaire RGB : notes pour les sessions de développement

Appli Windows (zone de notification) qui pilote l'éclairage RGB en mode direct. Voir `README.md` (usage) et `docs/HARDWARE.md` (matériel, protocoles, leçons apprises : à lire avant de toucher aux protocoles).

## Compiler / installer

- `pwsh -File build.ps1` → `out\RgbManager.exe`. Compilateur = Roslyn embarqué dans **PowerShell 7 64 bits** (`C:\Program Files\PowerShell\7\pwsh.exe`), cible **.NET Framework 4.8**. Pas de .NET SDK ni Visual Studio sur ce PC : ne pas créer de `.csproj`.
- Conséquence .NET 4.8 : pas de `System.Text.Json`, `Math.Clamp`, `NativeLibrary`, plages `a[1..]`, `Environment.ProcessPath` → utiliser `Compat.cs`. La syntaxe C# récente (switch expressions, patterns, raw strings, `global using`) fonctionne.
- `pwsh -File deploy.ps1` (admin) : arrête la tâche, copie `out\` dans `C:\Program Files\RgbManager`, recrée la tâche planifiée `RgbManager`, relance, met à jour le raccourci du bureau.
- Le PowerShell par défaut de cette machine est **32 bits** : passer par `C:\Program Files\PowerShell\7\pwsh.exe` pour tout ce qui touche au 64 bits (registre, `System32`, modules de processus).

## Tester

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
