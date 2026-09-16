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
- **Reset settings to default**: one checkbox per mod of the modpack that has settings in `BepInEx/config`
  (installed or not), none checked at first (`A` checks all). Their files (`valheim.<mod>.*`) are deleted after a
  confirmation; the mods stay installed and write their default settings again at the next launch.
- **Uninstall**: one checkbox per installed mod of the modpack, then whether to delete their settings (`.cfg`) and
  whether to remove BepInEx itself, which gives back a vanilla game folder.

Mods installed from elsewhere are never touched, and `.cfg` files are only deleted when asked.

## Install (dedicated server)

With the server stopped, on the Windows machine hosting it:

```powershell
irm https://raw.githubusercontent.com/KiraFR/valheim-modpack/main/install-server.ps1 | iex
```

Same menus, but in the `Valheim dedicated server` folder of the Steam libraries, and the install menu only offers
the mods needed server-side (`StackMax`, `PortalMenu`, `QuickBrew`, `GrowTime`, `BerryFarm`, `PartialSleep`). Linux servers and hosting providers without
PowerShell access need a manual copy of the archive.

## Script parameters

Use `& ([scriptblock]::Create((irm <url>))) <parameters>` or `powershell -ExecutionPolicy Bypass -File <script> <parameters>`.
`-Mods`, `-Uninstall`, `-ResetConfig` and `-BepInExOnly` skip the menus, and so does running outside an interactive console (CI,
redirected input): the parameters decide, and the default is to install every mod except experimental ones (every server mod
for `install-server.ps1`).

| Parameter | Effect |
|---|---|
| `-ValheimPath <folder>` (`install.ps1`) | Game folder, if Steam detection fails. |
| `-ServerPath <folder>` (`install-server.ps1`) | Dedicated server folder (holding `valheim_server.exe`), if Steam detection fails. |
| `-Mods StackMax, ChestCraft` | Installs only these mods (or uninstalls only these with `-Uninstall`). |
| `-Uninstall` | Uninstalls every installed mod of the modpack, or only `-Mods`. |
| `-RemoveConfig` | With `-Uninstall`: also deletes the settings of the uninstalled mods (`BepInEx/config/valheim.<mod>.*`). |
| `-ResetConfig` | Deletes the settings (`BepInEx/config/valheim.<mod>.*`) of every mod of the modpack found here, or only of `-Mods`: the mods stay installed and start again from their default settings. |
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
exercises `-Mods`, `-Uninstall`, `-RemoveConfig`, `-ResetConfig` and `-RemoveBepInEx`. A first step checks that the block of
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
| `ChestStack` | Stores the whole inventory into nearby chests in one press (200 m radius by default): each item joins its kind, nothing goes to a chest that doesn't already hold one, and there are no rules to declare (to assign an item to a chest, put a stack in it by hand once). Three triggers lead to the same grouped storing: `Shift+R` while aiming at a chest or with a chest open, the "Place stacks" button, and holding the interact key. The targeted chest has no priority: an item goes to the neighbour if the neighbour is the one already holding it. `ExtendGameControls` restores vanilla behaviour for the two game controls. Goes through `Container.StackAll()`, so through the game's network handshake: ZDO ownership requested, chests being browsed by another player respected, private chests and wards respected, deposit visual effect on each chest served. A single summary on screen instead of one message per chest. The hotbar (`ProtectHotbar`), anything edible (`ProtectFood`) and meads and other potions (`ProtectMeads`, mead bases still go to the chest) stay in the inventory; equipped gear is already spared by the game, raw meat is not (a material with no food value). Client only. |
| `Compass` | Compass bar at the top centre of the screen that scrolls as you turn the camera: N, NE, E... (north in orange), the degrees every 15° (`ShowNumbers`) and ticks every 5°, with the exact heading under the centre mark (`ShowHeading`). North is the top of the game's map. The heading follows the camera, not the character, so it shows where you look, also on a ship or in third person. Marks fade out towards the edges. `VisibleAngle` sets the angle the bar covers (180° by default, from your left to your right), `Width`, `PositionY` and `BackgroundOpacity` its size, position and background (move it down with `PositionY` if it overlaps the boss health bar). Hidden with the HUD and behind the inventory, the large map and the pause menu. Client only: neither the server nor the other players need the mod. |
| `FieldHarvest` | Harvests a whole field at once: pressing the interact key on a ripe plant while holding the alternate key (`Shift` by default, the game's `AltPlace` binding, also on gamepad) also harvests every ripe plant **of the same kind** within `Radius` metres (6 by default). Looking at a carrot harvests the carrots and leaves the turnips next to them; it works the same for berry bushes, mushrooms, flax or anything picked with the interact key. The crosshair looks through dropped items and unripe plants to find the kind, so a second press still works on a field strewn with the first harvest; holding the key does not repeat the area harvest. Each plant goes through the game's own picking, as with the scythe: farming skill, bonus yield and statistics count, and the items drop at the foot of each plant. `ShowCount` shows how many plants were harvested, and `ShowHint` adds "[Shift + E] Harvest around" under the game's pick up hint. The build tools (cultivator, hammer) must be put away, since the game disables hovering in placement mode. Client only: neither the server nor the other players need the mod. |
| `FieldPlanter` | Plants a row or a grid of crops in one click with the cultivator. `N` cycles Single (vanilla) / Row / Grid while a plant is selected; `Shift` + mouse wheel sets the row length and `Alt` + mouse wheel the number of rows (1 to 10 each, remembered in the config): adding a row to a single row switches to Grid, going back to one row returns to Row. Both the left and right keys work (AltGr included), and the on-screen message recalls the keys. The row starts at the vanilla ghost and runs away from the player along the camera direction, rounded to `SnapAngle` (45° by default) so rows stay parallel; the grid adds rows to the right. The spacing is computed per plant from its grow radius and the size of its colliders and of what it grows into, so no plant ends up stuck on "not enough space" (`ExtraSpacing` adds a margin, `SpacingOverride` forces a distance). A ghost shows every extra spot: red when the spot cannot take a plant (ground not cultivated, wrong biome, too hot or too cold, slope, ward, space already taken), yellow when the seeds, stamina or tool durability run out before it. Costs are vanilla per plant (seeds, durability, farming skill), except stamina: with `FreeStamina` (on by default) only the main plant uses it, and turning it off makes every plant cost stamina, so a large grid can then take more than a full bar. The spots are filled row by row, starting with the ghost's row, until something runs out, and a message then says how many plants were left out and why. Vines keep the single placement. Client only: neither the server nor the other players need the mod. |
| `GearSlots` | Dedicated equipment slots (head, chest, legs, shoulders, utility, trinket) in a panel to the right of the inventory. Adds a row to the inventory through the vanilla `Player.SetInventorySize` API, takes it out of the grid and repositions its slots: items remain real inventory items, saved normally. An item placed in its slot is equipped, removing it unequips it. `BagRows` sets the size of the inventory itself, `OffsetX`/`OffsetY`/`ColumnSpacing` the position of the panel. Client only. |
| `GrowTime` | Changes how long plants take to grow: `GrowTimeMultiplier` × vanilla growth time of crops and saplings (about 4000 to 5000 s), 0.1 by default, i.e. about 8 min for a crop (0.5 = twice as fast, 2 = twice as long). `RespawnTimeMultiplier` does the same for the regrowth of berry bushes (including BerryFarm's), mushrooms and thistle: 0.05 by default, i.e. 15 min for berries instead of 300 (1 = vanilla). The mod scales the duration read by `Plant.GetGrowTime` rather than shifting the planting time stored in the ZDO, so the vanilla health checks (sun, space, cultivated ground) keep working and a config change also applies to plants already in the ground. `SpinningWheelMultiplier` speeds up the spinning wheel: 30 s per flax in vanilla, 0.1 by default, i.e. 3 s (2 min for a full load of 40 instead of 20). `ShowRemainingTime` adds "Grows in …" on hover over a healthy plant. Growth is triggered by the plant's network owner (a nearby player), whose config decides: install it for every player with the same config. **Also install it on the dedicated server**: it permanently owns the plants around the world spawn, like QuickBrew's barrels. |
| `PortalMenu` | A portal is no longer linked to a single other one: interacting with it opens the list of every portal in the world (name, biome, distance) and clicking a row teleports you. Lets you run a whole network with one portal per location instead of one pair per link. No `ZDOExtraData.ConnectionType.Portal` connection is written: travel reuses `Player.TeleportTo` with the chosen coordinates, so the save stays vanilla and uninstalling the mod gives back normal portals. The game's safeguards are kept as they are (`NoPortals` / `NoBossPortals` global keys, no ore through portals). `Rename` reopens the vanilla name field, `Sort` toggles name/distance sorting, `Esc` closes. Config: `UnnamedPortals`, `SortByDistance`, `AllowAllItems`, `MaxDistance`, `ShowBiome`, `DisableVanillaPairing`, panel size, `Scale` and colour. The panel has its own `Canvas` with forced sorting to sit above the HUD, and input is blocked in `PlayerController.TakeInput` (movement, camera) as well as in `Player.TakeInput` (interaction). **Also install it on the dedicated server**: only the server holds the full portal registry (`ZDOMan.GetPortalList`), a client only knows its nearby sectors. Without the mod on the server, the panel only shows nearby portals and says so. |
| `QuickBrew` | Shortens the fermentation time of barrels (meads, potions): `Multiplier` × vanilla duration (2400 s), 0.025 by default, i.e. 1 min. The mod backdates the start time stored in the barrel's ZDO instead of changing the duration locally, so every player, with or without the mod, sees the barrel ready at the same moment. Catches up barrels filled before the install and timers reset for lack of a roof. `ShowRemainingTime` adds "Ready in …" on hover. Only the barrel's network owner (a nearby player) applies the offset: if that player doesn't have the mod, the barrel ferments at vanilla speed, without breaking anything. Install it for every player to guarantee the effect. **Also install it on the dedicated server**: the server's reference position stays at the world centre, so it permanently owns the barrels around the spawn (`ZDOMan.ReleaseNearbyZDOS` never hands them to a player); without the mod on the server, those barrels ferment at vanilla speed. |
| `RowTogether` | Seated passengers row with the helmsman, without any key: when the helmsman rows, each seated passenger ("sit" emote) adds their thrust, regardless of side: x(1 + bonus × rowers). `LimitToSailSpeed` caps rowing at the hull's sailing speed. Client only, but for every player who boards: physics runs on the ship's network owner, who is a player on board, not necessarily the helmsman. Useless on the dedicated server. |
| `SkillCarry` | Raises the max carry weight with skill levels: each skill has its own bonus under `[Skills]`, reached at level 100 and scaled linearly below (level 50 gives half). By default only physical skills count: `Pickaxes`, `WoodCutting` and `Unarmed` +75; `Run`, `Blocking`, `Swim`, `Jump` and `Dodge` +50; the melee weapons (`Swords`, `Knives`, `Clubs`, `Polearms`, `Spears`, `Axes`) +25, i.e. up to +625 on top of the vanilla 300. Ranged weapons, magic, sneaking, crafts, fishing and riding are at 0 and can be turned on. A skill missing from the game version is ignored with a warning in the log. The bonus is added to `Player.GetMaxCarryWeight`, so encumbrance, auto pickup and the weight shown in the inventory all follow it; it stacks with Megingjord, is scaled by the world's carry weight modifier, and temporary skill boosts count while they last. Client only: neither the server nor the other players need the mod. |
| `TeamHealth` | Lists the other players on the left of the screen, centred vertically, with their health bar and `current / max` health (`ShowNumbers`). The bar turns orange below 30%, and a player waiting to respawn shows "Dead". Health is read from the player's ZDO, which the game already replicates, so it is exact and needs nothing from the others. Its limit is the game's own: a player's health is only known while they are in the zones loaded around you; further away the row says "Too far", followed by their distance when they share their position on the map (`ShowFarPlayers` hides them). Rows are sorted by name. Hidden with the HUD and behind the inventory, the large map and the pause menu. `PositionX`, `PositionY` and `Width` move and size the list (VoiceChat's speaker list sits at the same spot by default, move one of them if both are used). Client only: neither the server nor the other players need the mod. |
| `PartialSleep` | Skips the night when `SleepPercent` of the connected players are in bed instead of all of them: 50 by default, rounded up (1 of 2, 2 of 3, 2 of 4), 100 = vanilla. Only the players in bed get the black screen, "Good morning" and the Rested bonus; the others keep playing and simply see the day come back. The vanilla game sends the sleep start to everybody, which would freeze awake players and pull them off chairs and ships, so the mod sends it only to those in bed. `ShowProgress` tells everyone, top left, how many players are in bed and how many are needed whenever that changes. Vanilla bed rules still apply (only at night, roof, fire, no enemies nearby). **Server only**: install it on the dedicated server, or with the player hosting the game; the other players do not need it. |
| `VoiceChat` | **Experimental: unchecked by default in the install menu.** Proximity voice chat: hold `B` to talk (or switch to open microphone), and voices play in 3D from the speaking player's head, fading with distance. Capture and compression go through the Steam voice API, so the microphone, input volume and transmission threshold are set in Steam > Settings > Voice, and crossplay players without Steam can neither talk nor hear. `F7` opens a settings panel: transmission mode, push-to-talk key, microphone test with a level meter (nothing is sent during the test), volume (up to 400%), automatic gain that evens out quiet and loud microphones, distances and latency buffer. Talking players are listed on the left of the screen and get a microphone icon next to their name above their head. Voice travels through routed RPCs addressed to each player in range, which the server forwards without needing the mod. Every player who talks or listens needs it. |

## Creating a new mod

1. Copy a small existing mod such as `QuickBrew/` to `MyMod/`, rename the `.csproj` and replace `QuickBrew` inside it.
2. In `Plugin.cs`, change the namespace, `PluginGuid` (`valheim.mymod`), `PluginName` and `PluginVersion`, and remove the patches.
3. `dotnet sln Valheim.Mods.sln add MyMod/MyMod.csproj`
