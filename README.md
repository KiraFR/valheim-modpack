# Valheim mods

Mods BepInEx/Harmony pour Valheim. Solution : `Valheim.Mods.sln` (ouvrir avec Rider).

## Installer (joueurs)

Valheim fermé, dans PowerShell :

```powershell
irm https://raw.githubusercontent.com/KiraFR/valheim-modpack/main/install.ps1 | iex
```

Le script trouve Valheim dans les bibliothèques Steam, installe BepInExPack Valheim (Thunderstore) s'il manque,
télécharge `valheim-modpack.zip` depuis la dernière release et copie chaque mod dans `BepInEx/plugins/<Mod>/`.
Les autres mods et les fichiers `.cfg` ne sont pas touchés. Relancer la même commande met à jour.

Avec des paramètres, via `& ([scriptblock]::Create((irm <url>))) <paramètres>` ou `powershell -ExecutionPolicy Bypass -File install.ps1 <paramètres>` :

| Paramètre | Effet |
|---|---|
| `-ValheimPath <dossier>` | Dossier du jeu, si la détection Steam échoue. |
| `-Server` | Cible le serveur dédié et n'installe que les mods utiles côté serveur (`StackMax`, `PortalMenu`). |
| `-Version v1.2.0` | Installe une release précise au lieu de la dernière. |
| `-ZipPath <zip>` | Installe depuis une archive locale. |
| `-BepInExOnly`, `-ForceBepInEx` | N'installe que BepInEx ; réinstalle BepInEx même s'il est présent. |

## CI et releases

`.github/workflows/build.yml` compile la solution à chaque push et pull request, sur Windows. Les DLL du jeu ne
sont pas dans le dépôt : la CI télécharge le serveur dédié Valheim par SteamCMD (connexion anonyme), dont
`valheim_server_Data/Managed` contient le même code de jeu (`Directory.Build.props` bascule dessus quand
`valheim_Data` est absent), puis installe BepInEx avec `install.ps1`. Elle produit l'artefact
`valheim-modpack.zip` (`BepInEx/plugins/<Mod>/<Mod>.dll` pour chaque projet de la solution) et vérifie
l'installation de cette archive dans un dossier vierge.

Publier une release : `git tag v1.0.0 && git push origin v1.0.0`. C'est cette release que `install.ps1` télécharge.

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
| `HelloValheim` | Modèle pour créer un nouveau mod (message de bienvenue au spawn). Hors solution et non installé dans le jeu : à copier, pas à compiler. |
| `StackMax` | Stack max configurable par type d'objet (`[Types]`, multiplicateur ou valeur fixe) et par nom de prefab (`[Objets]`). Génère `BepInEx/config/valheim.stackmax.objets.txt` (tous les types et objets empilables avec leur stack vanilla). Commandes console `stackmax_list` et `stackmax_reload`. Tout le monde doit avoir le mod avec la même config. |
| `Uncraft` | Onglet « Décrafter » dans le panneau d'artisanat, visible près d'un atelier : rend les matériaux (fabrication + améliorations) des objets dont la recette se fabrique à cet atelier. Ratio, niveau d'atelier requis, exclusions en config. |
| `ChestCraft` | Puise dans les coffres autour du joueur (rayon configurable, 20 m par défaut) pour la fabrication, la construction au marteau, et l'alimentation des appareils (fondoir, four à charbon, haut fourneau, moulin, rouet, raffinerie d'eitr, fermenteur, feux, balistes), matière première comme carburant. Les grils et fours à pain en sont exclus par défaut (`Cuisson`), leur ingrédient étant imprévisible. Maintenir `Shift` pendant l'interaction remplit l'appareil jusqu'à son maximum (charbon, minerai, bois) ; sans le modificateur, un appui ajoute une unité comme en vanilla. La liste des recettes se rafraîchit d'elle-même quand le contenu d'un coffre proche change, panneau ouvert. En visant un appareil, la touche `R` fait défiler ce qu'il a le droit de prendre dans les coffres : Automatique, chaque ingrédient qu'il sait convertir, puis Rien (l'appareil redevient vanilla). Le choix s'affiche sur le survol, est retenu par type d'appareil et persiste dans la config. `Fabrication`, `Construction` et `Appareils` séparés, `PrioriteCoffres` pour vider les coffres avant le sac, exclusions de coffres par prefab. Commandes console `chestcraft_list` et `chestcraft_reload`. Client uniquement : ni le serveur ni les autres joueurs n'ont besoin du mod. |
| `ChestStack` | Range tout le sac dans les coffres alentour en un appui (rayon 20 m comme ChestCraft) : chaque objet rejoint ses semblables, rien ne part vers un coffre qui n'en contient pas déjà un exemplaire, et aucune règle n'est à déclarer (pour affecter un objet à un coffre, y déposer une pile à la main une fois). Trois déclencheurs menant au même rangement groupé : `Shift+R` en visant un coffre ou l'écran d'un coffre ouvert, le bouton « Objets similaires », et le maintien de la touche d'interaction. Le coffre visé n'a aucune priorité : un objet file chez le voisin si c'est le voisin qui en détient déjà. `EtendreControlesJeu` rend leur comportement vanilla aux deux contrôles du jeu. Passe par `Container.StackAll()`, donc par le handshake réseau du jeu : propriété du ZDO demandée, coffre fouillé par un autre joueur respecté, coffres privés et cercles protecteurs respectés, effet visuel de dépôt sur chaque coffre servi. Un seul récapitulatif à l'écran au lieu d'un message par coffre. La barre d'action (`ProtegerBarreAction`) et tout ce qui nourrit (`ProtegerNourriture`) restent dans le sac ; l'équipement porté est déjà épargné par le jeu, la viande crue non (matériau sans valeur nutritive). Client uniquement. |
| `GearSlots` | Emplacements d'équipement dédiés (tête, torse, jambes, épaules, utilitaire, babiole) dans un panneau à droite de l'inventaire. Ajoute une rangée au sac via l'API vanilla `Player.SetInventorySize`, la sort de la grille et en repositionne les cases : les objets restent de vrais objets d'inventaire, sauvegardés normalement. Un objet posé dans son emplacement est équipé, l'en retirer le déséquipe. `RangeesSac` règle la taille du sac lui-même, `DecalageX`/`DecalageY`/`EcartColonnes` la position du panneau. Client uniquement. |
| `PortalMenu` | Un portail n'est plus lié à un seul autre : interagir avec lui ouvre la liste de tous les portails du monde (nom, biome, distance) et cliquer sur une ligne téléporte. Permet de tenir un réseau entier avec un portail par lieu au lieu d'une paire par liaison. Aucune connexion `ZDOExtraData.ConnectionType.Portal` n'est écrite : le voyage réutilise `Player.TeleportTo` avec les coordonnées choisies, donc la sauvegarde reste vanilla et désinstaller le mod rend des portails normaux. Les garde-fous du jeu sont repris tels quels (clés globales `NoPortals` / `NoBossPortals`, minerai interdit). `Renommer` rouvre le champ de nom vanilla, `Tri` bascule nom/distance, `Échap` ferme. Config : `PortailsSansNom`, `TrierParDistance`, `AutoriserTousObjets`, `DistanceMaximale`, `AfficherBiome`, `DesactiverAppairageVanilla`, taille, `Echelle` et couleur du panneau. Le panneau a son propre `Canvas` en tri forcé pour passer devant le HUD, et les entrées sont coupées dans `PlayerController.TakeInput` (déplacements, regard) autant que dans `Player.TakeInput` (interaction). **À installer aussi sur le serveur dédié** : seul le serveur tient le registre complet des portails (`ZDOMan.GetPortalList`), un client ne connaît que ses secteurs proches. Sans le mod côté serveur, le panneau n'affiche que les portails alentour et l'annonce. |
| `QuickBrew` | Réduit la durée de fermentation des tonneaux (hydromels, potions) : `Multiplicateur` × durée vanilla (2400 s), 0.025 par défaut soit 1 min. Le mod antidate l'heure de départ stockée dans le ZDO du tonneau au lieu de changer la durée localement, donc tous les joueurs, avec ou sans le mod, voient le tonneau prêt au même moment. Rattrape les tonneaux déjà remplis avant l'installation et les chronos remis à zéro faute de toit. `AfficherTempsRestant` ajoute « Prêt dans … » au survol. Seul le propriétaire réseau du tonneau (un joueur proche) applique le décalage : s'il n'a pas le mod, le tonneau fermente à la vitesse vanilla, sans rien casser. À installer chez tous les joueurs pour un effet garanti, inutile sur le serveur dédié. |
| `RowTogether` | Les passagers assis rament avec le barreur, sans aucune touche : quand le barreur rame, chaque passager assis (emote « s'asseoir ») ajoute sa poussée, sans notion de côté : x(1 + bonus × rameurs). `LimitToSailSpeed` bride la rame à la vitesse voile de la coque. Client uniquement, mais chez tous les joueurs qui montent à bord : la physique tourne chez le propriétaire réseau du bateau, qui est un joueur à bord, pas forcément le barreur. Inutile sur le serveur dédié. |

## Créer un nouveau mod

1. Copier `HelloValheim/` vers `MonMod/`, renommer le `.csproj` et remplacer `HelloValheim` dedans.
2. Changer le GUID `valheim.monmod` dans `Plugin.cs`.
3. `dotnet sln Valheim.Mods.sln add MonMod/MonMod.csproj`
