<p align="center"><img src="assets/icon-256.png" width="96" alt="Icône Gestionnaire RGB"></p>

# Gestionnaire RGB

Petit gestionnaire d'éclairage RGB pour Windows, **sans Armoury Crate, iCUE ni OpenRGB** : une icône dans la zone de notification pour régler la couleur et l'effet de la carte mère, de la RAM, du watercooling et du boîtier en même temps.

- Un seul exécutable de ~60 Ko, basé sur **.NET Framework 4.8** (inclus dans Windows 10/11, rien à installer).
- Aucun service en arrière-plan, aucune télémétrie, aucun pilote vulnérable : la RAM passe par [PawnIO](https://pawnio.eu), le remplaçant signé et sécurisé de WinRing0.
- Démarre avec Windows via une tâche planifiée (droits admin sans fenêtre UAC).

> ⚠️ Projet personnel, écrit pour un PC précis (voir [matériel pris en charge](#matériel-pris-en-charge)). Sur un autre PC, il n'allume que ce qu'il reconnaît. Pour du matériel varié, [OpenRGB](https://openrgb.org) est la solution générique.

## Fonctionnalités

- Couleurs prédéfinies ou personnalisées, effets **statique**, **respiration**, **arc-en-ciel**, **éteint**
- Luminosité et vitesse réglables, chaque appareil activable séparément
- Tous les appareils animés ensemble (l'appli calcule chaque image en « mode direct »)
- **Surveillance de la pompe** du watercooling : si elle passe sous 400 tr/min, l'appli rend la main au boîtier Corsair
- Reprise automatique après la veille, nouvelle tentative de détection si un appareil n'est pas prêt au démarrage
- Relancer l'appli (raccourci du bureau) ouvre son menu

## Matériel pris en charge

| Appareil | Pris en charge | Comment |
|---|---|---|
| Carte mère ASUS | Contrôleur AURA USB `0B05:1939` (ex. ROG STRIX B550-A GAMING) | HID, mode direct : 2 prises RGB 12 V + 1 prise ARGB (14 LED) |
| RAM | Corsair DDR4 RGB (Dominator Platinum, Vengeance RGB Pro/SL/RS) sur chipset **AMD** | SMBus via PawnIO (module PIIX4), mode direct, adresses `0x58`–`0x5F` uniquement |
| Watercooling | Corsair iCUE **Commander CORE** `1B1C:0C1C` (pompe + 6 ports ventilateurs) | HID, mode logiciel, vitesses jamais modifiées |
| Boîtier | Ruban ARGB branché sur la prise ARGB de la carte mère (ex. be quiet! Pure Base 500DX) | Via la carte mère |

Pas pris en charge (pour l'instant) : autres cartes mères, RAM DDR5 ou d'autres marques, chipsets Intel, cartes graphiques, périphériques.

## Installation

Prérequis : Windows 10/11 64 bits, [PawnIO](https://github.com/namazso/PawnIO.Setup/releases) installé (uniquement pour la RAM), [PowerShell 7](https://aka.ms/powershell) pour compiler.

```powershell
pwsh -File deploy.ps1 -Build
```

Ce script compile, installe dans `C:\Program Files\RgbManager`, crée la tâche de démarrage et le raccourci « Gestionnaire RGB » sur le bureau. Il demande les droits admin une fois.

Le journal et les réglages sont dans `C:\ProgramData\RgbManager\`.

## Développement

Pas besoin de Visual Studio ni du .NET SDK : `build.ps1` utilise le compilateur C# (Roslyn) fourni avec PowerShell 7 et compile contre .NET Framework 4.8.

```powershell
pwsh -File build.ps1        # → out\RgbManager.exe
pwsh -File deploy.ps1       # installe out\ et relance l'appli
pwsh -File assets\make-icon.ps1   # régénère l'icône
```

Options de l'exécutable, utiles pour tester :

| Option | Effet |
|---|---|
| `--test N` | Applique les réglages pendant N secondes puis quitte |
| `--aura-probe [s]` | Couleur différente sur chaque sortie de la carte mère (identifier où est branché un ruban) |
| `--aura-probe2 [s]` | Alterne mode direct / effet statique / effet animé |
| `--argb-count` | Bandes de couleurs sur 60 LED pour mesurer un ruban |
| `--no-elevate` | Ne pas redemander les droits admin |

Arrêter l'appli installée avant un test : `schtasks /End /TN RgbManager`.

```
src/              code C# (.NET Framework 4.8)
  Engine.cs         boucle de rendu, détection, surveillance de la pompe
  AuraMainboard.cs  contrôleur ASUS AURA USB
  CorsairDram.cs    RAM Corsair sur SMBus
  CommanderCore.cs  Corsair Commander CORE
  PawnSmbus.cs      accès SMBus via PawnIO
  HidDevice.cs      HID brut (setupapi / hid.dll)
  TrayApp.cs        icône et menu
vendor/pawnio/    module SMBus PIIX4 signé de PawnIO (LGPL-2.1)
assets/           icône + script de génération
docs/             notes matériel et protocoles
```

## Dépannage

- **La RAM ne change pas** : PawnIO est-il installé ? L'appli tourne-t-elle en admin (lancée par la tâche planifiée) ? Voir le journal.
- **Le ruban du boîtier reste éteint ou blanc** : dans le BIOS, *Advanced → Onboard Devices Configuration → RGB LED lighting* doit être activé ; le contrôleur du boîtier doit être en mode « carte mère » (sur une Pure Base 500DX : appui long de 3 s sur le bouton LED) et son câble 3 broches branché sur la prise **ADD_GEN2**.
- **Les effets « matériels » ne s'affichent pas** : c'est normal sur ce matériel, d'où le mode direct.

## Crédits et licence

Les protocoles de communication (ASUS AURA USB, Corsair DRAM, Corsair Commander CORE) ont été implémentés à partir de la documentation et du code du projet [OpenRGB](https://gitlab.com/CalcProgrammer1/OpenRGB) (GPL-2.0-or-later). L'accès SMBus utilise [PawnIO](https://github.com/namazso/PawnIO) et son module `SmbusPIIX4` (LGPL-2.1, voir `vendor/pawnio/COPYING`).

Ce projet est distribué sous licence **GPL-2.0-or-later** (voir [LICENSE](LICENSE)).
