# Matériel et protocoles

Notes tirées de la mise au point sur les deux PC (novembre 2026). À compléter avec les composants d'autres PC.

## PC d'origine (ASUS)

| Élément | Détail |
|---|---|
| Carte mère | ASUS ROG STRIX B550-A GAMING, contrôleur AURA USB `0B05:1939` interface 2, firmware `AULA3-AR42-0207` |
| Sorties AURA | Table de config : 2 LED « carte » (= 2 prises RGB 12 V), 1 prise ARGB. Canal direct `0x04` = prises RGB, `0x00` = ARGB |
| RAM | 4 × Corsair Dominator Platinum RGB DDR4 (PID `0x0201`, protocole 4, firmware 1.4.28, 12 LED, numérotées à l'envers) aux adresses `0x58`–`0x5B` |
| SMBus | AMD PIIX4 `1022:790B`, port 0 (= `0x0B00`) |
| Watercooling | Corsair iCUE Commander CORE `1B1C:0C1C`, firmware 2.11.221, rapports 97 octets. Pompe 29 LED, ports 1–5 : 34 LED (6e non connecté). Pompe ~2 270 tr/min |
| Boîtier | be quiet! Pure Base 500DX : ruban ARGB **14 LED** sur la prise ARGB de la carte mère |

## Second PC (MSI), ex-LED Control

Ce PC était piloté par LED Control, un script PowerShell séparé, fusionné ici en novembre 2026. Ces appareils n'ont pas de mode direct utilisé ici : **couleur fixe**, une écriture à chaque changement de réglage (les effets animés y donnent la couleur choisie, fixe).

| Élément | Détail |
|---|---|
| Carte mère | MSI B550-A PRO (MS-7C56), contrôleur Mystic Light « 185 octets » USB `1462:7C56`, rapport feature `0x52` (tampon Windows de 725 octets) |
| RAM | 4 × Crucial Ballistix RGB DDR4 (`BL8G32C16U4WL`), contrôleurs Micron aux adresses `0x39`–`0x3C` après réadressage, 8 LED chacune |
| SMBus | AMD PIIX4 `1022:790B`, port 0, comme le PC ASUS |
| Clavier | Logitech G513 `046D:C33C`, collection HID++ longue (page `0xFF43`, rapports de 20 octets), 1 zone d'éclairage |

Leçons :

1. **MSI, relecture** : après l'envoi, le firmware remet à 0 la seconde couleur de la 5e LED intégrée (octet 129). On ne compare donc que l'effet et la couleur principale de chaque zone (4 premiers octets).
2. **MSI** : jamais le drapeau de sauvegarde (octet 184) : l'état reste volatil, réappliqué à chaque lancement. Les services `Mystic_Light_Service` et `MSI_Case_Service` (MSI Center) réécrivent l'éclairage : l'appli les arrête s'ils tournent.
3. **Crucial** : au démarrage à froid, toutes les barrettes répondent en `0x27` et doivent être réadressées (`0x82EE` = emplacement, `0x82EF` = nouvelle adresse << 1, `0x82F0` = `0xF0`). Les adresses Crucial ne sont sondées que si WMI (`Win32_PhysicalMemory`) annonce des références `BL…`.
4. **G513** : Windows expose aussi le clavier en *LampArray* (éclairage dynamique de Windows 11). Si l'éclairage dynamique est activé pour ce clavier dans les Paramètres, il peut reprendre la main.

## Leçons apprises (PC ASUS)

1. **Les effets matériels ne s'affichent pas** sur la RAM Corsair (registre `0x82`, CRC pourtant correct) ni sur la carte mère AURA (`0x35`/`0x36`). Seul le **mode direct** fonctionne → l'appli envoie les couleurs elle-même, toutes les 3 s en statique, en continu pour les animations.
2. **AURA, mode direct ARGB** : ne pas envoyer plus de LED que le ruban n'en a. Avec 60 ou 120 LED (plusieurs paquets de 20), le dernier paquet porte le drapeau « appliquer » (`0x80`) et l'image entière est ignorée → ruban éteint. 14 LED = 1 paquet = OK.
3. **Pure Base 500DX** : le contrôleur du boîtier ignore la carte mère tant qu'il n'est pas en mode « sync » (**appui long 3 s** sur le bouton LED). En mode sync sans signal, le ruban est éteint.
4. **BIOS** : *Advanced → Onboard Devices Configuration → RGB LED lighting* doit être activé, sinon les prises RGB/ARGB ne sortent rien, quelles que soient les commandes.
5. **Commander CORE** : en mode logiciel, il faut renvoyer les couleurs au moins toutes les 10 s, sinon il repasse en mode matériel. En quittant, on le remet explicitement en mode matériel (`01 03 00 01`). On lit la vitesse de la pompe (point de terminaison `0x17`) mais on n'écrit jamais de vitesse.
6. **Journal** : un journal dans `%APPDATA%` n'était pas écrit par l'instance lancée par la tâche planifiée → tout est dans `C:\ProgramData\RgbManager`.
7. **WinRing0** (ancien OpenRGB 0.81 portable) est bloqué par Defender (`VulnerableDriver:WinNT/Winring0`) : PawnIO le remplace.

## Protocoles (résumé)

### ASUS AURA USB (HID, rapport `0xEC`, 65 octets)
- `EC 82` → version firmware (réponse `EC 02 …`), `EC B0` → table de config (réponse `EC 30`, table en `[4..63]`) : `[0x02]` = nb prises ARGB, `[0x1B]` = nb LED carte, `[0x1D]` = nb prises RGB.
- `EC 52 53 00 01` : mode « Gen1 » (envoyé à l'ouverture).
- `EC 35 <canal effet> 00 00 <mode>` : effet ; `0xFF` = direct.
- `EC 40 <canal direct | 0x80 si dernier paquet> <offset> <nb LED> RGB…` : mode direct, 20 LED max par paquet.

### Corsair DRAM (SMBus)
- Détection : `0x43` ∈ {1A,1B,1C} et `0x44` ∈ {01,03,04}. Infos : écrire `0x61`=0, `0x21`=0, lire 32 × `0x40`, CRC en `0x42` (CRC-8, poly `0x07`).
- Mode direct (protocole ≥ 4) : paquet `[nb LED][RGB × n][CRC-8]`, écrit en bloc sur `0x31` (32 premiers octets) puis `0x32` (reste). Repli possible en écritures de mots (`0x90`/`0xA0`).
- **Ne jamais écrire en `0x50`–`0x57`** (puces SPD de la RAM).

### MSI Mystic Light 185 octets (HID, rapport feature `0x52`)
- Lire le rapport (`GetFeature`), modifier les zones, renvoyer l'état d'origine puis le nouveau (`SetFeature`), relire pour confirmer.
- Zone = 10 octets : effet (1 = statique), R, G, B, vitesse | luminosité << 2 (10 = max), R2, G2, B2, drapeaux (`0x80` couleur perso), bourrage. Zones de la B550-A PRO : JRGB1 `1`, JRAINBOW1 `31`, JRAINBOW2 `42`, carte `74`, puis 6 LED intégrées à `84 + 10 × i`.
- Retirer les drapeaux de synchro : `[74 + 4] &= ~0x80`, `[74 + 8] &= ~0x30`.

### Crucial Ballistix (SMBus, contrôleur Micron)
- Détection : read byte sur l'adresse, puis `0xA0`–`0xAF` renvoient 0…15, puis chaîne « Micron » en `0x1025` ou `0x1030`.
- Registres 16 bits : écrire le mot `(reg << 8 | reg >> 8)` en commande `0x00`, puis lire en `0x81` ou écrire en `0x01`.
- Couleur fixe : mode `0x820F` = `0xCF` (statique), puis pour chaque LED : masque `0x82E9` = `1 << led`, `0x82ED`/`EE`/`EF` = R, G, B, commande `0x82F0` = `0x01` ; attendre que `0x82F0` change avant la commande suivante.

### Logitech HID++ 2.0, Color LED Effects `0x8070`
- Rapport long : `11 FF <index fonction> <fonction | 0x0E> 16 octets`. Index de `0x8070` demandé à la fonction racine (index 0).
- `0x00` infos (nb de zones), `0x10` infos de zone (nb d'effets en [3]), `0x20` infos d'effet (ID en [2..3] : 0 éteint, 1 fixe), `0x30` régler : zone, effet, R, G, B, `0x02`, octet 12 (persistance) à 0.

### Corsair Commander CORE (HID, 97 octets)
- Commande : `00 08 <c0> <c1> données…` ; réponse `00 <c0> <statut> …`.
- `02 13` version ; `01 03 00 02` mode logiciel ; `01 03 00 01` mode matériel.
- Points de terminaison : ouvrir `0D 00 <ep>`, lire `08 00`, écrire `06 00` (+ `07 00` suite), fermer `05 01 00`.
  - `0x1E` / type `0D 00` : config des ports LED ; `0x20` / `0F 00` : nb de LED ; `0x22` / `12 00` : couleurs ; `0x17` / `06 00` : vitesses (pompe en premier).

Références : sources d'[OpenRGB](https://gitlab.com/CalcProgrammer1/OpenRGB) (`Controllers/AsusAuraUSBController`, `CorsairDRAMController`, `CorsairCommanderCoreController`, `MSIMysticLightController`, `CrucialController`, `LogitechController`), [liquidctl](https://github.com/liquidctl/liquidctl) pour le Commander CORE, [PawnIO.Modules](https://github.com/namazso/PawnIO.Modules) pour le SMBus.
