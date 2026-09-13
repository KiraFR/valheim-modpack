# Valheim mods

English | [Français](README.fr.md)

BepInEx/Harmony mods for Valheim. Solution: `Valheim.Mods.sln` (open with Rider).

In-game texts and configuration keys are in English.

## Install (players)

With Valheim closed, in PowerShell:

```powershell
irm https://raw.githubusercontent.com/KiraFR/valheim-modpack/main/install.ps1 | iex
```

The script finds Valheim in your Steam libraries (or asks for its folder), then shows a menu navigated with the arrow
keys:

- **Install or update mods**: one checkbox per mod of the latest release, with its version and description. Space
  checks or unchecks, Enter confirms. Already installed mods are checked (on a first install, every mod except the
  experimental ones), so running the command again updates the same set; unchecking an installed mod removes it. BepInExPack Valheim (Thunderstore) is installed if it is missing,
  and each checked mod is copied into `BepInEx/plugins/<Mod>/`.
- **Uninstall**: one checkbox per installed mod of the modpack, then whether to delete their settings (`.cfg`) and
  whether to remove BepInEx itself, which gives back a vanilla game folder.

Mods installed from elsewhere are never touched, and `.cfg` files are only deleted when asked.

## Install (dedicated server)

With the server stopped, on the Windows machine hosting it:

```powershell
irm https://raw.githubusercontent.com/KiraFR/valheim-modpack/main/install-server.ps1 | iex
```

Same menus, but in the `Valheim dedicated server` folder of the Steam libraries, and the install menu only offers
the mods needed server-side (`StackMax`, `PortalMenu`, `QuickBrew`, `GrowTime`, `BerryFarm`). Linux servers and hosting providers without
PowerShell access need a manual copy of the archive.

## Script parameters

Use `& ([scriptblock]::Create((irm <url>))) <parameters>` or `powershell -ExecutionPolicy Bypass -File <script> <parameters>`.
`-Mods`, `-Uninstall` and `-BepInExOnly` skip the menus, and so does running outside an interactive console (CI,
redirected input): the parameters decide, and the default is to install every mod except experimental ones (every server mod
for `install-server.ps1`).

| Parameter | Effect |
|---|---|
| `-ValheimPath <folder>` (`install.ps1`) | Game folder, if Steam detection fails. |
| `-ServerPath <folder>` (`install-server.ps1`) | Dedicated server folder (holding `valheim_server.exe`), if Steam detection fails. |
| `-Mods StackMax, ChestCraft` | Installs only these mods (or uninstalls only these with `-Uninstall`). |
| `-Uninstall` | Uninstalls every installed mod of the modpack, or only `-Mods`. |
| `-RemoveConfig` | With `-Uninstall`: also deletes the settings of the uninstalled mods (`BepInEx/config/valheim.<mod>.*`). |
| `-RemoveBepInEx` | With `-Uninstall`: also removes BepInEx, with every other BepInEx mod and setting in the folder. |
| `-Version v1.2.0` | Uses a specific release instead of the latest one. |
| `-ZipPath <zip>` | Uses a local archive. |
| `-BepInExOnly`, `-ForceBepInEx` | Installs BepInEx only; reinstalls BepInEx even if it is already present. |

Each script refuses the other's folder (game folder for the server script, server folder for the game script).

## CI and releases

`.github/workflows/build.yml` builds the solution on every push and pull request, on Windows. The game DLLs are
not in the repository: the CI downloads the Valheim dedicated server with SteamCMD (anonymous login), whose
`valheim_server_Data/Managed` folder contains the same game code (`Directory.Build.props` switches to it when
`valheim_Data` is missing), then installs BepInEx with `install-server.ps1`. That `Managed` folder is cached under the
server's Steam build id, so the 2 GB server is only downloaded again after a Valheim patch; the build summary shows
the game version the mods were compiled against. It produces the `valheim-modpack.zip`
artifact (`BepInEx/plugins/<Mod>/<Mod>.dll` for each project in the solution) and checks that both scripts install
this archive correctly into empty folders (every mod for the game, exactly the server mods for the server), then
exercises `-Mods`, `-Uninstall`, `-RemoveConfig` and `-RemoveBepInEx`. A first step checks that the block of
functions shared by the two scripts is identical in both and that both are ASCII.

To publish a release: `git tag v1.0.0 && git push origin v1.0.0`. This is the release both scripts download.

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
| `StackMax` | Configurable max stack size per item type (`[Types]`, multiplier or fixed value) and per prefab name (`[Items]`). Generates `BepInEx/config/valheim.stackmax.items.txt` (every item type and stackable item with its vanilla stack size). Console commands `stackmax_list` and `stackmax_reload`. Everyone must have the mod with the same config, including the dedicated server. |
| `Uncraft` | "Uncraft" tab in the crafting panel, visible near a workbench: gives back the materials (crafting + upgrades) of items whose recipe is made at that station. Ratio, required station level and exclusions in the config. |
| `BerryFarm` | Adds four saplings to the cultivator menu that grow into berry bushes: raspberry, blueberry, cloudberry and lingonberry (there are no strawberries in Valheim). Planting one costs `Cost` berries of that kind (5 by default); it grows like a crop (4000 to 5000 s, affected by `GrowTime`), on cultivated ground by default (`NeedCultivatedGround`), in the sun and with `GrowRadius` metres of free space (1 m by default, which is also the spacing between bushes). The grown bush is the game's own prefab: berries come back like on a wild bush, it can be chopped down, and it stays in the world if the mod is removed. Each sapling is a runtime copy of the carrot sapling registered in `ZNetScene`, and each berry can be taken out of the menu under `[Plants]`. Mushrooms are left out on purpose: their prefab has no `Destructible`, so a planted mushroom could never be removed. **Every player and the dedicated server need it**: a sapling is a prefab that only exists with the mod. Without it, the sapling is simply invisible and does not grow on that machine (the bush, once grown, shows for everyone). |
| `ChestCraft` | Pulls from chests around the player (configurable radius, 20 m by default) for crafting, hammer building, and feeding stations (smelter, charcoal kiln, blast furnace, windmill, spinning wheel, eitr refinery, fermenter, fires, ballistas), both raw materials and fuel. Cooking stations and ovens are excluded by default (`Cooking`), since their ingredient is unpredictable. Holding `Shift` while interacting fills the station to its maximum (coal, ore, wood); without the modifier, one press adds one unit as in vanilla. The recipe list refreshes by itself when the contents of a nearby chest change while the panel is open. While aiming at a station, the `R` key cycles through what it may take from chests: Automatic, each ingredient it can convert, then Nothing (the station goes back to vanilla). The choice is shown on hover, remembered per station type and saved in the config. Separate `Crafting`, `Building` and `Stations` switches, `ChestsFirst` to empty chests before the inventory, chest exclusions by prefab. Console commands `chestcraft_list` and `chestcraft_reload`. Client only: neither the server nor other players need the mod. |
| `ChestStack` | Stores the whole inventory into nearby chests in one press (20 m radius like ChestCraft): each item joins its kind, nothing goes to a chest that doesn't already hold one, and there are no rules to declare (to assign an item to a chest, put a stack in it by hand once). Three triggers lead to the same grouped storing: `Shift+R` while aiming at a chest or with a chest open, the "Place stacks" button, and holding the interact key. The targeted chest has no priority: an item goes to the neighbour if the neighbour is the one already holding it. `ExtendGameControls` restores vanilla behaviour for the two game controls. Goes through `Container.StackAll()`, so through the game's network handshake: ZDO ownership requested, chests being browsed by another player respected, private chests and wards respected, deposit visual effect on each chest served. A single summary on screen instead of one message per chest. The hotbar (`ProtectHotbar`) and anything edible (`ProtectFood`) stay in the inventory; equipped gear is already spared by the game, raw meat is not (a material with no food value). Client only. |
| `GearSlots` | Dedicated equipment slots (head, chest, legs, shoulders, utility, trinket) in a panel to the right of the inventory. Adds a row to the inventory through the vanilla `Player.SetInventorySize` API, takes it out of the grid and repositions its slots: items remain real inventory items, saved normally. An item placed in its slot is equipped, removing it unequips it. `BagRows` sets the size of the inventory itself, `OffsetX`/`OffsetY`/`ColumnSpacing` the position of the panel. Client only. |
| `GrowTime` | Changes how long plants take to grow: `GrowTimeMultiplier` × vanilla growth time of crops and saplings (about 3000 to 5000 s), 2 by default, i.e. twice as long (0.5 = twice as fast). `RespawnTimeMultiplier` does the same for the regrowth of wild berry bushes, mushrooms and thistle (1, vanilla, by default). The mod scales the duration read by `Plant.GetGrowTime` rather than shifting the planting time stored in the ZDO, so the vanilla health checks (sun, space, cultivated ground) keep working and a config change also applies to plants already in the ground. `ShowRemainingTime` adds "Grows in …" on hover over a healthy plant. Growth is triggered by the plant's network owner (a nearby player), whose config decides: install it for every player with the same config. **Also install it on the dedicated server**: it permanently owns the plants around the world spawn, like QuickBrew's barrels. |
| `PortalMenu` | A portal is no longer linked to a single other one: interacting with it opens the list of every portal in the world (name, biome, distance) and clicking a row teleports you. Lets you run a whole network with one portal per location instead of one pair per link. No `ZDOExtraData.ConnectionType.Portal` connection is written: travel reuses `Player.TeleportTo` with the chosen coordinates, so the save stays vanilla and uninstalling the mod gives back normal portals. The game's safeguards are kept as they are (`NoPortals` / `NoBossPortals` global keys, no ore through portals). `Rename` reopens the vanilla name field, `Sort` toggles name/distance sorting, `Esc` closes. Config: `UnnamedPortals`, `SortByDistance`, `AllowAllItems`, `MaxDistance`, `ShowBiome`, `DisableVanillaPairing`, panel size, `Scale` and colour. The panel has its own `Canvas` with forced sorting to sit above the HUD, and input is blocked in `PlayerController.TakeInput` (movement, camera) as well as in `Player.TakeInput` (interaction). **Also install it on the dedicated server**: only the server holds the full portal registry (`ZDOMan.GetPortalList`), a client only knows its nearby sectors. Without the mod on the server, the panel only shows nearby portals and says so. |
| `QuickBrew` | Shortens the fermentation time of barrels (meads, potions): `Multiplier` × vanilla duration (2400 s), 0.025 by default, i.e. 1 min. The mod backdates the start time stored in the barrel's ZDO instead of changing the duration locally, so every player, with or without the mod, sees the barrel ready at the same moment. Catches up barrels filled before the install and timers reset for lack of a roof. `ShowRemainingTime` adds "Ready in …" on hover. Only the barrel's network owner (a nearby player) applies the offset: if that player doesn't have the mod, the barrel ferments at vanilla speed, without breaking anything. Install it for every player to guarantee the effect. **Also install it on the dedicated server**: the server's reference position stays at the world centre, so it permanently owns the barrels around the spawn (`ZDOMan.ReleaseNearbyZDOS` never hands them to a player); without the mod on the server, those barrels ferment at vanilla speed. |
| `RowTogether` | Seated passengers row with the helmsman, without any key: when the helmsman rows, each seated passenger ("sit" emote) adds their thrust, regardless of side: x(1 + bonus × rowers). `LimitToSailSpeed` caps rowing at the hull's sailing speed. Client only, but for every player who boards: physics runs on the ship's network owner, who is a player on board, not necessarily the helmsman. Useless on the dedicated server. |
| `VoiceChat` | **Experimental: unchecked by default in the install menu.** Proximity voice chat: hold `B` to talk (or switch to open microphone), and voices play in 3D from the speaking player's head, fading with distance. Capture and compression go through the Steam voice API, so the microphone, input volume and transmission threshold are set in Steam > Settings > Voice, and crossplay players without Steam can neither talk nor hear. `F7` opens a settings panel: transmission mode, push-to-talk key, microphone test with a level meter (nothing is sent during the test), volume (up to 400%), automatic gain that evens out quiet and loud microphones, distances and latency buffer. Talking players are listed on the left of the screen and get a microphone icon next to their name above their head. Voice travels through routed RPCs addressed to each player in range, which the server forwards without needing the mod. Every player who talks or listens needs it. |

## Creating a new mod

1. Copy a small existing mod such as `QuickBrew/` to `MyMod/`, rename the `.csproj` and replace `QuickBrew` inside it.
2. In `Plugin.cs`, change the namespace, `PluginGuid` (`valheim.mymod`), `PluginName` and `PluginVersion`, and remove the patches.
3. `dotnet sln Valheim.Mods.sln add MyMod/MyMod.csproj`
