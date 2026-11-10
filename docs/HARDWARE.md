# Matériel et protocoles

Notes tirées de la mise au point sur le PC d'origine (novembre 2026). À compléter avec les composants d'autres PC.

## PC d'origine

| Élément | Détail |
|---|---|
| Carte mère | ASUS ROG STRIX B550-A GAMING, contrôleur AURA USB `0B05:1939` interface 2, firmware `AULA3-AR42-0207` |
| Sorties AURA | Table de config : 2 LED « carte » (= 2 prises RGB 12 V), 1 prise ARGB. Canal direct `0x04` = prises RGB, `0x00` = ARGB |
| RAM | 4 × Corsair Dominator Platinum RGB DDR4 (PID `0x0201`, protocole 4, firmware 1.4.28, 12 LED, numérotées à l'envers) aux adresses `0x58`–`0x5B` |
| SMBus | AMD PIIX4 `1022:790B`, port 0 (= `0x0B00`) |
| Watercooling | Corsair iCUE Commander CORE `1B1C:0C1C`, firmware 2.11.221, rapports 97 octets. Pompe 29 LED, ports 1–5 : 34 LED (6e non connecté). Pompe ~2 270 tr/min |
| Boîtier | be quiet! Pure Base 500DX : ruban ARGB **14 LED** sur la prise ARGB de la carte mère |

## Leçons apprises

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

### Corsair Commander CORE (HID, 97 octets)
- Commande : `00 08 <c0> <c1> données…` ; réponse `00 <c0> <statut> …`.
- `02 13` version ; `01 03 00 02` mode logiciel ; `01 03 00 01` mode matériel.
- Points de terminaison : ouvrir `0D 00 <ep>`, lire `08 00`, écrire `06 00` (+ `07 00` suite), fermer `05 01 00`.
  - `0x1E` / type `0D 00` : config des ports LED ; `0x20` / `0F 00` : nb de LED ; `0x22` / `12 00` : couleurs ; `0x17` / `06 00` : vitesses (pompe en premier).

Références : sources d'[OpenRGB](https://gitlab.com/CalcProgrammer1/OpenRGB) (`Controllers/AsusAuraUSBController`, `CorsairDRAMController`, `CorsairCommanderCoreController`), [liquidctl](https://github.com/liquidctl/liquidctl) pour le Commander CORE, [PawnIO.Modules](https://github.com/namazso/PawnIO.Modules) pour le SMBus.
