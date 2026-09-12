# Valheim mods

English | [Français](README.fr.md)

BepInEx/Harmony mods for Valheim. Solution: `Valheim.Mods.sln` (open with Rider).

In-game texts and configuration keys are in French.

## Install (players)

With Valheim closed, in PowerShell:

```powershell
irm https://raw.githubusercontent.com/KiraFR/valheim-modpack/main/install.ps1 | iex
```

The script finds Valheim in your Steam libraries, installs BepInExPack Valheim (Thunderstore) if it is missing,
downloads `valheim-modpack.zip` from the latest release and copies each mod into `BepInEx/plugins/<Mod>/`.
Other mods and `.cfg` files are left untouched. Running the same command again updates the mods.

With parameters, use `& ([scriptblock]::Create((irm <url>))) <parameters>` or `powershell -ExecutionPolicy Bypass -File install.ps1 <parameters>`:

| Parameter | Effect |
|---|---|
| `-ValheimPath <folder>` | Game folder, if Steam detection fails. |
| `-Server` | Targets the dedicated server and only installs the mods needed server-side (`StackMax`, `PortalMenu`). |
| `-Version v1.2.0` | Installs a specific release instead of the latest one. |
| `-ZipPath <zip>` | Installs from a local archive. |
| `-BepInExOnly`, `-ForceBepInEx` | Installs BepInEx only; reinstalls BepInEx even if it is already present. |

## CI and releases

`.github/workflows/build.yml` builds the solution on every push and pull request, on Windows. The game DLLs are
not in the repository: the CI downloads the Valheim dedicated server with SteamCMD (anonymous login), whose
`valheim_server_Data/Managed` folder contains the same game code (`Directory.Build.props` switches to it when
`valheim_Data` is missing), then installs BepInEx with `install.ps1`. It produces the `valheim-modpack.zip`
artifact (`BepInEx/plugins/<Mod>/<Mod>.dll` for each project in the solution) and checks that this archive
installs correctly into an empty folder.

To publish a release: `git tag v1.0.0 && git push origin v1.0.0`. This is the release `install.ps1` downloads.

## Requirements (development)

- Valheim installed (default Steam path). Other path: `VALHEIM_INSTALL` environment variable.
- BepInExPack Valheim installed in the game folder.
- .NET SDK (any version ≥ 6; the mods target `net48` for the game's Mono runtime).

## Dev loop

```bash
dotnet build RowTogether/RowTogether.csproj -c Release
```

The DLL is copied automatically to `Valheim/BepInEx/plugins/<ModName>/`.
Launch the game, then read `Valheim/BepInEx/LogOutput.log`.
Config files are generated in `Valheim/BepInEx/config/valheim.<mod>.cfg`.

## Reading the game code

```bash
ilspycmd -t Ship -r "C:/Program Files (x86)/Steam/steamapps/common/Valheim/valheim_Data/Managed" "C:/Program Files (x86)/Steam/steamapps/common/Valheim/valheim_Data/Managed/assembly_valheim.dll" > decompiled/Ship.cs
```

The `decompiled/` folder is ignored by git. Useful classes: `Player`, `Character`, `Ship`, `ShipControlls`,
`ObjectDB`, `ZNetScene`, `ZDO`, `ZNetView`, `InventoryGui`, `MessageHud`.

## Mods

| Project | Description |
|---|---|
| `StackMax` | Configurable max stack size per item type (`[Types]`, multiplier or fixed value) and per prefab name (`[Objets]`). Generates `BepInEx/config/valheim.stackmax.objets.txt` (every item type and stackable item with its vanilla stack size). Console commands `stackmax_list` and `stackmax_reload`. Everyone must have the mod with the same config, including the dedicated server. |
| `Uncraft` | "Décrafter" (uncraft) tab in the crafting panel, visible near a workbench: gives back the materials (crafting + upgrades) of items whose recipe is made at that station. Ratio, required station level and exclusions in the config. |
| `ChestCraft` | Pulls from chests around the player (configurable radius, 20 m by default) for crafting, hammer building, and feeding stations (smelter, charcoal kiln, blast furnace, windmill, spinning wheel, eitr refinery, fermenter, fires, ballistas), both raw materials and fuel. Cooking stations and ovens are excluded by default (`Cuisson`), since their ingredient is unpredictable. Holding `Shift` while interacting fills the station to its maximum (coal, ore, wood); without the modifier, one press adds one unit as in vanilla. The recipe list refreshes by itself when the contents of a nearby chest change while the panel is open. While aiming at a station, the `R` key cycles through what it may take from chests: Automatic, each ingredient it can convert, then Nothing (the station goes back to vanilla). The choice is shown on hover, remembered per station type and saved in the config. Separate `Fabrication`, `Construction` and `Appareils` switches, `PrioriteCoffres` to empty chests before the inventory, chest exclusions by prefab. Console commands `chestcraft_list` and `chestcraft_reload`. Client only: neither the server nor other players need the mod. |
| `ChestStack` | Stores the whole inventory into nearby chests in one press (20 m radius like ChestCraft): each item joins its kind, nothing goes to a chest that doesn't already hold one, and there are no rules to declare (to assign an item to a chest, put a stack in it by hand once). Three triggers lead to the same grouped storing: `Shift+R` while aiming at a chest or with a chest open, the "Place stacks" button, and holding the interact key. The targeted chest has no priority: an item goes to the neighbour if the neighbour is the one already holding it. `EtendreControlesJeu` restores vanilla behaviour for the two game controls. Goes through `Container.StackAll()`, so through the game's network handshake: ZDO ownership requested, chests being browsed by another player respected, private chests and wards respected, deposit visual effect on each chest served. A single summary on screen instead of one message per chest. The hotbar (`ProtegerBarreAction`) and anything edible (`ProtegerNourriture`) stay in the inventory; equipped gear is already spared by the game, raw meat is not (a material with no food value). Client only. |
| `GearSlots` | Dedicated equipment slots (head, chest, legs, shoulders, utility, trinket) in a panel to the right of the inventory. Adds a row to the inventory through the vanilla `Player.SetInventorySize` API, takes it out of the grid and repositions its slots: items remain real inventory items, saved normally. An item placed in its slot is equipped, removing it unequips it. `RangeesSac` sets the size of the inventory itself, `DecalageX`/`DecalageY`/`EcartColonnes` the position of the panel. Client only. |
| `PortalMenu` | A portal is no longer linked to a single other one: interacting with it opens the list of every portal in the world (name, biome, distance) and clicking a row teleports you. Lets you run a whole network with one portal per location instead of one pair per link. No `ZDOExtraData.ConnectionType.Portal` connection is written: travel reuses `Player.TeleportTo` with the chosen coordinates, so the save stays vanilla and uninstalling the mod gives back normal portals. The game's safeguards are kept as they are (`NoPortals` / `NoBossPortals` global keys, no ore through portals). `Renommer` reopens the vanilla name field, `Tri` toggles name/distance sorting, `Esc` closes. Config: `PortailsSansNom`, `TrierParDistance`, `AutoriserTousObjets`, `DistanceMaximale`, `AfficherBiome`, `DesactiverAppairageVanilla`, panel size, `Echelle` and colour. The panel has its own `Canvas` with forced sorting to sit above the HUD, and input is blocked in `PlayerController.TakeInput` (movement, camera) as well as in `Player.TakeInput` (interaction). **Also install it on the dedicated server**: only the server holds the full portal registry (`ZDOMan.GetPortalList`), a client only knows its nearby sectors. Without the mod on the server, the panel only shows nearby portals and says so. |
| `QuickBrew` | Shortens the fermentation time of barrels (meads, potions): `Multiplicateur` × vanilla duration (2400 s), 0.025 by default, i.e. 1 min. The mod backdates the start time stored in the barrel's ZDO instead of changing the duration locally, so every player, with or without the mod, sees the barrel ready at the same moment. Catches up barrels filled before the install and timers reset for lack of a roof. `AfficherTempsRestant` adds "Prêt dans …" (ready in …) on hover. Only the barrel's network owner (a nearby player) applies the offset: if that player doesn't have the mod, the barrel ferments at vanilla speed, without breaking anything. Install it for every player to guarantee the effect; useless on the dedicated server. |
| `RowTogether` | Seated passengers row with the helmsman, without any key: when the helmsman rows, each seated passenger ("sit" emote) adds their thrust, regardless of side: x(1 + bonus × rowers). `LimitToSailSpeed` caps rowing at the hull's sailing speed. Client only, but for every player who boards: physics runs on the ship's network owner, who is a player on board, not necessarily the helmsman. Useless on the dedicated server. |

## Creating a new mod

1. Copy a small existing mod such as `QuickBrew/` to `MyMod/`, rename the `.csproj` and replace `QuickBrew` inside it.
2. In `Plugin.cs`, change the namespace, `PluginGuid` (`valheim.mymod`), `PluginName` and `PluginVersion`, and remove the patches.
3. `dotnet sln Valheim.Mods.sln add MyMod/MyMod.csproj`
