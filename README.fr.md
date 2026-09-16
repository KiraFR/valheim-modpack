# Valheim mods

[English](README.md) | Français

Mods BepInEx/Harmony pour Valheim. Solution : `Valheim.Mods.sln` (ouvrir avec Rider).

## Installer (joueurs)

Valheim fermé, dans PowerShell :

```powershell
irm https://raw.githubusercontent.com/KiraFR/valheim-modpack/main/install.ps1 | iex
```

Le script trouve Valheim dans les bibliothèques Steam (ou demande son dossier), puis affiche un menu qui se parcourt
avec les flèches :

- **Install or update mods** : une case à cocher par mod de la dernière release, avec sa version et sa description.
  Espace coche ou décoche, Entrée valide. Les mods déjà installés sont cochés (au premier lancement, tous les mods sauf
  les expérimentaux), donc relancer la commande met à jour la même sélection ; décocher un mod installé le retire. BepInExPack Valheim (Thunderstore) est installé s'il manque,
  et chaque mod coché est copié dans `BepInEx/plugins/<Mod>/`.
- **Reset settings to default** : une case par mod du modpack qui a des réglages dans `BepInEx/config` (installé ou
  non), aucune cochée au départ (`A` coche tout). Leurs fichiers (`valheim.<mod>.*`) sont supprimés après
  confirmation ; les mods restent installés et réécrivent leurs réglages par défaut au lancement suivant.
- **Uninstall** : une case par mod du modpack installé, puis s'il faut supprimer leurs réglages (`.cfg`) et retirer
  BepInEx lui-même, ce qui rend un dossier de jeu vanilla.

Les mods installés par ailleurs ne sont jamais touchés, et les fichiers `.cfg` ne sont supprimés que sur demande.

## Installer (serveur dédié)

Serveur arrêté, sur la machine Windows qui l'héberge :

```powershell
irm https://raw.githubusercontent.com/KiraFR/valheim-modpack/main/install-server.ps1 | iex
```

Mêmes menus, mais dans le dossier `Valheim dedicated server` des bibliothèques Steam, et le menu d'installation ne
propose que les mods utiles côté serveur (`StackMax`, `PortalMenu`, `QuickBrew`, `GrowTime`, `BerryFarm`, `PartialSleep`). Les serveurs Linux et les
hébergeurs sans accès PowerShell demandent une copie manuelle de l'archive.

## Paramètres des scripts

Via `& ([scriptblock]::Create((irm <url>))) <paramètres>` ou `powershell -ExecutionPolicy Bypass -File <script> <paramètres>`.
`-Mods`, `-Uninstall`, `-ResetConfig` et `-BepInExOnly` sautent les menus, tout comme un lancement hors console interactive (CI,
entrée redirigée) : les paramètres décident, et par défaut tous les mods sauf les expérimentaux sont installés (tous les mods
serveur pour `install-server.ps1`).

| Paramètre | Effet |
|---|---|
| `-ValheimPath <dossier>` (`install.ps1`) | Dossier du jeu, si la détection Steam échoue. |
| `-ServerPath <dossier>` (`install-server.ps1`) | Dossier du serveur dédié (celui de `valheim_server.exe`), si la détection Steam échoue. |
| `-Mods StackMax, ChestCraft` | N'installe que ces mods (ou ne désinstalle que ceux-là avec `-Uninstall`). |
| `-Uninstall` | Désinstalle tous les mods du modpack installés, ou seulement `-Mods`. |
| `-RemoveConfig` | Avec `-Uninstall` : supprime aussi les réglages des mods désinstallés (`BepInEx/config/valheim.<mod>.*`). |
| `-ResetConfig` | Supprime les réglages (`BepInEx/config/valheim.<mod>.*`) de tous les mods du modpack présents ici, ou seulement de `-Mods` : les mods restent installés et repartent de leurs réglages par défaut. |
| `-RemoveBepInEx` | Avec `-Uninstall` : retire aussi BepInEx, avec tous les autres mods et réglages BepInEx du dossier. |
| `-Version v1.2.0` | Utilise une release précise au lieu de la dernière. |
| `-ZipPath <zip>` | Utilise une archive locale. |
| `-BepInExOnly`, `-ForceBepInEx` | N'installe que BepInEx ; réinstalle BepInEx même s'il est présent. |

Chaque script refuse le dossier de l'autre (dossier du jeu pour le script serveur, dossier serveur pour le script du jeu).

## CI et releases

`.github/workflows/build.yml` compile la solution à chaque push et pull request, sur Windows. Les DLL du jeu ne
sont pas dans le dépôt : la CI télécharge le serveur dédié Valheim par SteamCMD (connexion anonyme), dont
`valheim_server_Data/Managed` contient le même code de jeu (`Directory.Build.props` bascule dessus quand
`valheim_Data` est absent), puis installe BepInEx avec `install-server.ps1`. Ce dossier `Managed` est mis en cache sous le
buildid Steam du serveur : les 2 Go du serveur ne sont retéléchargés qu'après un patch de Valheim, et le résumé du
build indique la version du jeu contre laquelle les mods ont été compilés. Elle produit l'artefact
`valheim-modpack.zip` (`BepInEx/plugins/<Mod>/<Mod>.dll` pour chaque projet de la solution) et vérifie que les deux
scripts installent cette archive dans des dossiers vierges (tous les mods pour le jeu, exactement les mods serveur
pour le serveur), puis teste `-Mods`, `-Uninstall`, `-RemoveConfig`, `-ResetConfig` et `-RemoveBepInEx`. Une première étape vérifie
que le bloc de fonctions partagé par les deux scripts est identique dans les deux et qu'ils sont en ASCII.

Publier une release : `git tag v1.0.0 && git push origin v1.0.0`. C'est cette release que les deux scripts téléchargent.

## Prérequis (développement)

- Valheim installé (chemin par défaut Steam). Autre chemin : variable d'environnement `VALHEIM_INSTALL`.
- BepInExPack Valheim installé dans le dossier du jeu (déjà fait, version 5.4.2333).
- .NET SDK (n'importe lequel ≥ 6, on compile en `net48` pour le runtime Mono du jeu).

## Boucle de dev

```bash
dotnet build RowTogether/RowTogether.csproj -c Release
```

La DLL est copiée automatiquement dans `Valheim/BepInEx/plugins/<NomDuMod>/`.
Lancer le jeu, puis lire `Valheim/BepInEx/LogOutput.log`.
Les fichiers de config sont générés dans `Valheim/BepInEx/config/valheim.<mod>.cfg`.

## Lire le code du jeu

```bash
ilspycmd -t Ship -r "C:/Program Files (x86)/Steam/steamapps/common/Valheim/valheim_Data/Managed" "C:/Program Files (x86)/Steam/steamapps/common/Valheim/valheim_Data/Managed/assembly_valheim.dll" > decompiled/Ship.cs
```

Le dossier `decompiled/` est ignoré par git. Classes utiles : `Player`, `Character`, `Ship`, `ShipControlls`,
`ObjectDB`, `ZNetScene`, `ZDO`, `ZNetView`, `InventoryGui`, `MessageHud`.

## Mods

| Projet | Description |
|---|---|
| `StackMax` | Stack max configurable par type d'objet (`[Types]`, multiplicateur ou valeur fixe) et par nom de prefab (`[Items]`). Génère `BepInEx/config/valheim.stackmax.items.txt` (tous les types et objets empilables avec leur stack vanilla). Commandes console `stackmax_list` et `stackmax_reload`. Tout le monde doit avoir le mod avec la même config. |
| `Uncraft` | Onglet « Uncraft » dans le panneau d'artisanat, visible près d'un atelier : rend les matériaux (fabrication + améliorations) des objets dont la recette se fabrique à cet atelier. Ratio, niveau d'atelier requis, exclusions en config. |
| `BerryFarm` | Ajoute au menu du cultivateur quatre pousses qui deviennent des buissons de baies : framboise, myrtille, mûre arctique et airelle (il n'y a pas de fraises dans Valheim). Planter une pousse coûte `Cost` baies de la même sorte (5 par défaut) ; elle pousse comme une culture (4000 à 5000 s, influencé par `GrowTime`), sur sol cultivé par défaut (`NeedCultivatedGround`), au soleil et avec `GrowRadius` mètres d'espace libre (1 m par défaut, c'est aussi l'écart entre deux buissons). Le buisson adulte est le prefab du jeu : les baies repoussent comme sur un buisson sauvage, il peut être abattu, et il reste dans le monde si le mod est retiré. Chaque pousse est une copie à l'exécution de la pousse de carotte enregistrée dans `ZNetScene`, et chaque baie peut être retirée du menu dans `[Plants]`. Les champignons sont exclus exprès : leur prefab n'a pas de `Destructible`, un champignon planté ne pourrait plus jamais être enlevé. **Nécessaire chez tous les joueurs et sur le serveur dédié** : une pousse est un prefab qui n'existe qu'avec le mod. Sans lui, la pousse est simplement invisible et ne pousse pas sur cette machine (le buisson, une fois adulte, est visible par tous). |
| `ChestCraft` | Puise dans les coffres autour du joueur (rayon configurable, 20 m par défaut) pour la fabrication, la construction au marteau, et l'alimentation des appareils (fondoir, four à charbon, haut fourneau, moulin, rouet, raffinerie d'eitr, fermenteur, feux, balistes), matière première comme carburant. Les grils et fours à pain en sont exclus par défaut (`Cooking`), leur ingrédient étant imprévisible. Maintenir `Shift` pendant l'interaction remplit l'appareil jusqu'à son maximum (charbon, minerai, bois) ; sans le modificateur, un appui ajoute une unité comme en vanilla. La liste des recettes se rafraîchit d'elle-même quand le contenu d'un coffre proche change, panneau ouvert. En visant un appareil, la touche `R` fait défiler ce qu'il a le droit de prendre dans les coffres : Automatique, chaque ingrédient qu'il sait convertir, puis Rien (l'appareil redevient vanilla). Le choix s'affiche sur le survol, est retenu par type d'appareil et persiste dans la config. `Crafting`, `Building` et `Stations` séparés, `ChestsFirst` pour vider les coffres avant le sac, exclusions de coffres par prefab. Commandes console `chestcraft_list` et `chestcraft_reload`. Client uniquement : ni le serveur ni les autres joueurs n'ont besoin du mod. |
| `ChestStack` | Range tout le sac dans les coffres alentour en un appui (rayon de 200 m par défaut) : chaque objet rejoint ses semblables, rien ne part vers un coffre qui n'en contient pas déjà un exemplaire, et aucune règle n'est à déclarer (pour affecter un objet à un coffre, y déposer une pile à la main une fois). Trois déclencheurs menant au même rangement groupé : `Shift+R` en visant un coffre ou l'écran d'un coffre ouvert, le bouton « Objets similaires », et le maintien de la touche d'interaction. Le coffre visé n'a aucune priorité : un objet file chez le voisin si c'est le voisin qui en détient déjà. `ExtendGameControls` rend leur comportement vanilla aux deux contrôles du jeu. Passe par `Container.StackAll()`, donc par le handshake réseau du jeu : propriété du ZDO demandée, coffre fouillé par un autre joueur respecté, coffres privés et cercles protecteurs respectés, effet visuel de dépôt sur chaque coffre servi. Un seul récapitulatif à l'écran au lieu d'un message par coffre. La barre d'action (`ProtectHotbar`), tout ce qui nourrit (`ProtectFood`) et les hydromels et autres potions (`ProtectMeads`, les bases d'hydromel partent quand même au coffre) restent dans le sac ; l'équipement porté est déjà épargné par le jeu, la viande crue non (matériau sans valeur nutritive). Client uniquement. |
| `Compass` | Boussole en haut au centre de l'écran, qui défile quand on tourne la caméra : N, NE, E... (le nord en orange), les degrés tous les 15° (`ShowNumbers`) et des graduations tous les 5°, avec le cap exact sous le repère central (`ShowHeading`). Le nord est le haut de la carte du jeu. Le cap suit la caméra et non le personnage : il indique où l'on regarde, y compris sur un bateau ou en vue à la troisième personne. Les graduations s'estompent vers les bords. `VisibleAngle` règle l'angle couvert par la barre (180° par défaut, de votre gauche à votre droite), `Width`, `PositionY` et `BackgroundOpacity` sa taille, sa position et son fond (la descendre avec `PositionY` si elle chevauche la barre de vie d'un boss). Masquée avec le HUD et derrière l'inventaire, la grande carte et le menu pause. Client uniquement : ni le serveur ni les autres joueurs n'ont besoin du mod. |
| `FieldHarvest` | Récolte un champ entier d'un coup : appuyer sur la touche d'interaction sur une plante mûre en tenant la touche alternative (`Shift` par défaut, le raccourci `AltPlace` du jeu, aussi à la manette) récolte aussi toutes les plantes mûres **de la même sorte** dans un rayon de `Radius` mètres (6 par défaut). Viser une carotte récolte les carottes et laisse les navets d'à côté ; cela marche de la même façon pour les buissons de baies, les champignons, le lin ou tout ce qui se cueille avec la touche d'interaction. La visée traverse les objets tombés au sol et les plantes pas encore mûres pour trouver la sorte, donc un second appui marche encore sur un champ jonché de la première récolte ; maintenir la touche ne répète pas la récolte de zone. Chaque plante passe par la cueillette du jeu, comme avec la faux : compétence d'agriculture, récolte bonus et statistiques comptent, et les objets tombent au pied de chaque plante. `ShowCount` affiche le nombre de plantes récoltées, et `ShowHint` ajoute « [Shift + E] Harvest around » sous l'indication de ramassage du jeu. Les outils de construction (cultivateur, marteau) doivent être rangés, car le jeu désactive le survol en mode placement. Client uniquement : ni le serveur ni les autres joueurs n'ont besoin du mod. |
| `FieldPlanter` | Plante une rangée ou une grille de cultures en un clic avec le cultivateur. `N` fait défiler Simple (vanilla) / Rangée / Grille quand une plante est sélectionnée ; `Shift` + molette règle la longueur de la rangée et `Alt` + molette le nombre de rangées (de 1 à 10 chacun, retenus dans la config) : ajouter une rangée à une rangée seule passe en mode Grille, revenir à une seule rangée repasse en mode Rangée. Les touches de gauche comme de droite fonctionnent (Alt Gr compris), et le message à l'écran rappelle les touches. La rangée part du fantôme vanilla et s'éloigne du joueur dans la direction de la caméra, arrondie à `SnapAngle` (45° par défaut) pour garder des rangées parallèles ; la grille ajoute les rangées vers la droite. L'espacement est calculé pour chaque plante à partir de son rayon de pousse et de la taille de ses colliders et de ce qu'elle devient, pour qu'aucune plante ne reste bloquée sur « pas assez de place » (`ExtraSpacing` ajoute une marge, `SpacingOverride` impose une distance). Un fantôme montre chaque emplacement supplémentaire : en rouge quand l'emplacement ne peut pas recevoir de plante (sol non cultivé, mauvais biome, trop chaud ou trop froid, pente, cercle protecteur, place déjà prise), en jaune quand les graines, l'endurance ou la durabilité de l'outil s'épuisent avant lui. Les coûts sont ceux du jeu, par plante (graines, usure, compétence d'agriculture), sauf l'endurance : avec `FreeStamina` (activé par défaut) seule la plante principale en consomme, et le désactiver fait payer l'endurance à chaque plante, si bien qu'une grande grille peut alors demander plus qu'une barre pleine. Les emplacements sont remplis rangée par rangée, en commençant par celle du fantôme, jusqu'à ce que quelque chose manque, et un message indique alors combien de plantes ont été laissées et pourquoi. Les vignes gardent la pose simple. Client uniquement : ni le serveur ni les autres joueurs n'ont besoin du mod. |
| `GearSlots` | Emplacements d'équipement dédiés (tête, torse, jambes, épaules, utilitaire, babiole) dans un panneau à droite de l'inventaire. Ajoute une rangée au sac via l'API vanilla `Player.SetInventorySize`, la sort de la grille et en repositionne les cases : les objets restent de vrais objets d'inventaire, sauvegardés normalement. Un objet posé dans son emplacement est équipé, l'en retirer le déséquipe. `BagRows` règle la taille du sac lui-même, `OffsetX`/`OffsetY`/`ColumnSpacing` la position du panneau. Client uniquement. |
| `GrowTime` | Change le temps de pousse des plantes : `GrowTimeMultiplier` × temps de pousse vanilla des cultures et jeunes arbres (environ 4000 à 5000 s), 0.1 par défaut soit environ 8 min pour une culture (0.5 = deux fois plus rapide, 2 = deux fois plus long). `RespawnTimeMultiplier` fait de même pour la repousse des buissons de baies (ceux de BerryFarm compris), champignons et chardons : 0.05 par défaut, soit 15 min pour les baies au lieu de 300 (1 = vanilla). Le mod multiplie la durée lue par `Plant.GetGrowTime` au lieu de décaler l'heure de plantation stockée dans le ZDO, donc les vérifications vanilla (soleil, espace, sol cultivé) continuent de fonctionner et un changement de config s'applique aussi aux plantes déjà en terre. `SpinningWheelMultiplier` accélère le rouet : 30 s par lin en vanilla, 0.1 par défaut soit 3 s (2 min pour un plein de 40 au lieu de 20), et `WindmillMultiplier` le moulin : 10 s par farine à plein vent, 0.1 par défaut soit 1 s (50 s pour un plein de 50 au lieu d'environ 8 min ; un vent faible le ralentit toujours). `ShowRemainingTime` ajoute « Grows in … » au survol d'une plante en bonne santé. La pousse est déclenchée par le propriétaire réseau de la plante (un joueur proche), dont la config décide : l'installer chez tous les joueurs avec la même config. **L'installer aussi sur le serveur dédié** : il possède à vie les plantes autour du spawn du monde, comme les tonneaux de QuickBrew. |
| `PortalMenu` | Un portail n'est plus lié à un seul autre : interagir avec lui ouvre la liste de tous les portails du monde (nom, biome, distance) et cliquer sur une ligne téléporte. Permet de tenir un réseau entier avec un portail par lieu au lieu d'une paire par liaison. Aucune connexion `ZDOExtraData.ConnectionType.Portal` n'est écrite : le voyage réutilise `Player.TeleportTo` avec les coordonnées choisies, donc la sauvegarde reste vanilla et désinstaller le mod rend des portails normaux. Les garde-fous du jeu sont repris tels quels (clés globales `NoPortals` / `NoBossPortals`, minerai interdit). `Rename` rouvre le champ de nom vanilla, `Sort` bascule nom/distance, `Échap` ferme. Config : `UnnamedPortals`, `SortByDistance`, `AllowAllItems`, `MaxDistance`, `ShowBiome`, `DisableVanillaPairing`, taille, `Scale` et couleur du panneau. Le panneau a son propre `Canvas` en tri forcé pour passer devant le HUD, et les entrées sont coupées dans `PlayerController.TakeInput` (déplacements, regard) autant que dans `Player.TakeInput` (interaction). **À installer aussi sur le serveur dédié** : seul le serveur tient le registre complet des portails (`ZDOMan.GetPortalList`), un client ne connaît que ses secteurs proches. Sans le mod côté serveur, le panneau n'affiche que les portails alentour et l'annonce. |
| `QuickBrew` | Réduit la durée de fermentation des tonneaux (hydromels, potions) : `Multiplier` × durée vanilla (2400 s), 0.025 par défaut soit 1 min. Le mod antidate l'heure de départ stockée dans le ZDO du tonneau au lieu de changer la durée localement, donc tous les joueurs, avec ou sans le mod, voient le tonneau prêt au même moment. Rattrape les tonneaux déjà remplis avant l'installation et les chronos remis à zéro faute de toit. `ShowRemainingTime` ajoute « Ready in … » au survol. Seul le propriétaire réseau du tonneau (un joueur proche) applique le décalage : s'il n'a pas le mod, le tonneau fermente à la vitesse vanilla, sans rien casser. À installer chez tous les joueurs pour un effet garanti. **À installer aussi sur le serveur dédié** : sa position de référence reste au centre du monde, il possède donc en permanence les tonneaux proches du point d'apparition (`ZDOMan.ReleaseNearbyZDOS` ne les cède jamais à un joueur) ; sans le mod côté serveur, ces tonneaux fermentent à la vitesse vanilla. |
| `RowTogether` | Les passagers assis rament avec le barreur, sans aucune touche : quand le barreur rame, chaque passager assis (emote « s'asseoir ») ajoute sa poussée, sans notion de côté : x(1 + bonus × rameurs). `LimitToSailSpeed` bride la rame à la vitesse voile de la coque. Client uniquement, mais chez tous les joueurs qui montent à bord : la physique tourne chez le propriétaire réseau du bateau, qui est un joueur à bord, pas forcément le barreur. Inutile sur le serveur dédié. |
| `SkillCarry` | Augmente le poids maximum transportable selon le niveau des compétences : chaque compétence a son propre bonus dans `[Skills]`, atteint au niveau 100 et proportionnel en dessous (niveau 50 = moitié). Par défaut seules les compétences physiques comptent : `Pickaxes`, `WoodCutting` et `Unarmed` +75 ; `Run`, `Blocking`, `Swim`, `Jump` et `Dodge` +50 ; les armes de mêlée (`Swords`, `Knives`, `Clubs`, `Polearms`, `Spears`, `Axes`) +25, soit jusqu'à +625 en plus des 300 vanilla. Armes à distance, magie, discrétion, artisanat, pêche et équitation sont à 0 et peuvent être activés. Une compétence absente de la version du jeu est ignorée avec un avertissement dans le log. Le bonus s'ajoute à `Player.GetMaxCarryWeight`, donc la surcharge, le ramassage automatique et le poids affiché dans l'inventaire le suivent ; il se cumule avec Megingjord, suit le modificateur de poids du monde, et les bonus temporaires de compétence comptent tant qu'ils durent. Client uniquement : ni le serveur ni les autres joueurs n'ont besoin du mod. |
| `TeamHealth` | Liste les autres joueurs à gauche de l'écran, centrée verticalement, avec leur barre de vie et leurs points de vie `actuels / max` (`ShowNumbers`). La barre passe à l'orange sous 30 %, et un joueur en attente de réapparition affiche « Dead ». La vie est lue dans le ZDO du joueur, que le jeu réplique déjà : elle est exacte et ne demande rien aux autres. Sa limite est celle du jeu : la vie d'un joueur n'est connue que tant qu'il est dans les zones chargées autour de vous ; plus loin, la ligne affiche « Too far », suivi de sa distance s'il partage sa position sur la carte (`ShowFarPlayers` les masque). Les lignes sont triées par nom. Masquée avec le HUD et derrière l'inventaire, la grande carte et le menu pause. `PositionX`, `PositionY` et `Width` déplacent et dimensionnent la liste (la liste des joueurs qui parlent de VoiceChat est au même endroit par défaut, déplacer l'une des deux si les deux mods sont utilisés). Client uniquement : ni le serveur ni les autres joueurs n'ont besoin du mod. |
| `PartialSleep` | Fait passer la nuit quand `SleepPercent` % des joueurs connectés sont au lit au lieu de tous : 50 par défaut, arrondi au supérieur (1 sur 2, 2 sur 3, 2 sur 4), 100 = vanilla. Seuls les joueurs couchés ont l'écran noir, « Good morning » et le bonus Reposé ; les autres continuent de jouer et voient simplement le jour revenir. Le jeu vanilla envoie le début du sommeil à tout le monde, ce qui figerait les joueurs éveillés et les ferait descendre des chaises et des bateaux : le mod ne l'envoie qu'aux joueurs couchés. `ShowProgress` indique à tous, en haut à gauche, combien de joueurs sont au lit et combien il en faut, à chaque changement. Les règles vanilla du lit s'appliquent toujours (seulement la nuit, toit, feu, pas d'ennemis proches). **Serveur uniquement** : l'installer sur le serveur dédié, ou chez le joueur qui héberge la partie ; les autres joueurs n'en ont pas besoin. |
| `KeepSkills` | Supprime la perte de niveau des compétences à la mort. `LossMultiplier` règle la perte vanilla (25 % du niveau de chaque compétence, modulé par le modificateur de pénalité de mort du monde) : 0 par défaut, soit aucune perte (0.5 = moitié, 1 = vanilla). Avec la pénalité de mort hardcore, qui remet toutes les compétences à zéro, chaque compétence perd `LossMultiplier` × son niveau à la place, donc rien à 0. Le message « Compétences diminuées » n'apparaît que si une perte a vraiment lieu. Client uniquement : ni le serveur ni les autres joueurs n'ont besoin du mod. |
| `ShipDismantle` | Démonte les bateaux (radeau, karve, drakkar) au marteau sans établi à proximité, par exemple sur une plage lointaine. `Repair` (activé par défaut) permet aussi de réparer les bateaux au marteau sans établi. Toutes les autres règles vanilla restent : balises de protection, zones interdites à la construction, « impossible pour l'instant » quand quelqu'un est à bord d'un radeau, et les matériaux et la cargaison tombent à la position du bateau comme d'habitude. Client uniquement : ni le serveur ni les autres joueurs n'ont besoin du mod. |
| `VoiceChat` | **Expérimental : décoché par défaut dans le menu d'installation.** Chat vocal de proximité : maintenir `B` pour parler (ou passer en micro ouvert), les voix sont jouées en 3D depuis la tête du joueur qui parle et baissent avec la distance. La capture et la compression passent par l'API voix de Steam : le micro, le volume d'entrée et le seuil de transmission se règlent dans Steam > Paramètres > Voix, et les joueurs crossplay sans Steam ne peuvent ni parler ni entendre. `F7` ouvre un panneau de réglages : mode de transmission, touche push-to-talk, test du micro avec vumètre (rien n'est envoyé pendant le test), volume (jusqu'à 400 %), gain automatique qui égalise les micros faibles et forts, distances et réserve de latence. Les joueurs qui parlent sont listés à gauche de l'écran et ont une icône de micro à côté de leur pseudo au-dessus de leur tête. La voix passe par des RPC routés adressés à chaque joueur à portée, que le serveur relaie sans avoir besoin du mod. Tous les joueurs qui parlent ou écoutent en ont besoin. |

## Créer un nouveau mod

1. Copier un petit mod existant comme `QuickBrew/` vers `MonMod/`, renommer le `.csproj` et remplacer `QuickBrew` dedans.
2. Dans `Plugin.cs`, changer le namespace, `PluginGuid` (`valheim.monmod`), `PluginName` et `PluginVersion`, et retirer les patches.
3. `dotnet sln Valheim.Mods.sln add MonMod/MonMod.csproj`
