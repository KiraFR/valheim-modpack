# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

BepInEx/Harmony mods for Valheim, one C# project per mod, grouped in `Valheim.Mods.sln`. Comments, config
descriptions and in-game messages are written in French; keep that convention.

Git commit messages are written in English.

Git repository published at https://github.com/KiraFR/valheim-modpack. `decompiled/` holds ILSpy output of game
classes for reference only and is gitignored. `Transmute/` is gitignored too: it stays local, out of the solution
and out of the modpack. `dist/` holds old hand-made zips and is ignored.

CI (`.github/workflows/build.yml`, Windows runner) cannot use the game's proprietary DLLs, so it installs the Valheim
dedicated server with SteamCMD (anonymous, app 896660) and builds against `valheim_server_Data/Managed`. Only that
folder is cached (`actions/cache`), keyed on the server's public-branch `buildid` read with `app_info_print`, so the
2 GB download only happens after a game patch. SteamCMD is run once with `+quit` first: its self-update run fails any
command passed with it ("Missing configuration"). `.github/scripts/Get-ValheimVersion.ps1` reads the game version
from the IL of `Version..cctor` (the DLL's own version is 0.0.0.0) for the build summary. It
installs BepInEx through `install-server.ps1` and smoke-tests the package with both install scripts, so script
changes are exercised by CI (the server test reads its expected mod list from `$ServerMods` in the script).
A `v*` tag publishes a release with `valheim-modpack.zip`, which both scripts download. Every project in the
solution ends up in the package.

Two install scripts: `install.ps1` (game, `-ValheimPath`, every mod) and `install-server.ps1` (dedicated server,
`-ServerPath`, only `$ServerMods`). They are deliberately self-contained and share their functions by copy, so each
one runs alone through `irm | iex` without fetching a second file that GitHub's raw cache could serve out of date:
any change to a shared function must be made in both. Each refuses the other's folder (`valheim.exe` /
`valheim_server.exe`). Add a mod to `$ServerMods` when it acts on objects the dedicated server can own.
The CI workflow and everything under `.github/` are written in English (comments, step names, messages).
Both install scripts must stay compatible with Windows PowerShell 5.1 and be written in English, ASCII only (no BOM), unlike the mods' French convention: 5.1 decodes `irm` downloads and BOM-less `-File` scripts as a legacy code page, and a UTF-8 BOM turns into
garbage before `<#` that breaks parsing of the whole script.

## Commands

Build one mod (Release is the normal configuration):

```bash
dotnet build RowTogether/RowTogether.csproj -c Release
```

Build everything:

```bash
dotnet build Valheim.Mods.sln -c Release
```

There are no tests or linters. Verification is manual: launch Valheim, then read
`<Valheim>/BepInEx/LogOutput.log`. Config files are generated at `<Valheim>/BepInEx/config/valheim.<mod>.cfg`.

Decompile a game class for reading (output goes to `decompiled/`):

```bash
ilspycmd -t Ship -r "C:/Program Files (x86)/Steam/steamapps/common/Valheim/valheim_Data/Managed" "C:/Program Files (x86)/Steam/steamapps/common/Valheim/valheim_Data/Managed/assembly_valheim.dll" > decompiled/Ship.cs
```

Create a new mod: copy a small existing mod such as `QuickBrew/` to `MyMod/`, rename the `.csproj` and replace
`QuickBrew` inside it, change the namespace, `PluginGuid` (`valheim.mymod`), `PluginName` and `PluginVersion` in
`Plugin.cs` and remove its patches, then `dotnet sln Valheim.Mods.sln add MyMod/MyMod.csproj`.

## Build architecture

`Directory.Build.props` centralises everything shared: target `net48` (the game's Mono runtime), nullable and
implicit usings disabled, and the game path. The path defaults to the Steam location and is overridden by the
`VALHEIM_INSTALL` environment variable. Every `.csproj` references BepInEx, 0Harmony, the game assemblies
(`assembly_valheim`, `assembly_utils`, `assembly_guiutils`) and Unity DLLs straight from that install with
`Private=false`, so nothing is copied next to the mod DLL.

Game assemblies are referenced with `Publicize="true"` via `BepInEx.AssemblyPublicizer.MSBuild`. Private members
such as `Ship.m_backwardForce` or `Player.m_nview` are therefore accessed directly, without reflection or
`AccessTools`.

A `DeployToPlugins` target in each `.csproj` copies the built DLL to `<Valheim>/BepInEx/plugins/<AssemblyName>/`
after every build, so building is deploying.

## Mod conventions

Each mod is a single `Plugin.cs` containing a `Plugin : BaseUnityPlugin` class and its Harmony patches as
nested-level static classes in the same file. The pattern in both mods:

- Constants `PluginGuid` (`valheim.<mod>`, lowercase, no personal name), `PluginName`, `PluginVersion`; the
  `.csproj` `Version` should match. The GUID also names the generated `.cfg`, so renaming one means renaming
  the config file in `<Valheim>/BepInEx/config/` to keep existing settings.
- `Awake()` stores `Logger` in a static `Log`, binds every `ConfigEntry` into static fields, then
  `new Harmony(PluginGuid).PatchAll(typeof(Plugin).Assembly)`. `OnDestroy()` calls `UnpatchSelf()`.
- Patches are `[HarmonyPatch(typeof(X), nameof(X.Method))]` static classes with `Prefix`/`Postfix`, and check
  `Plugin.Enabled.Value` first.

Multiplayer rules that RowTogether follows and new mods should too: prefer state the game already replicates
(RowTogether reads `Player.IsSitting()`, which is the animator tag synced by `ZSyncAnimation`) over custom
state. When custom per-player state is unavoidable, store it in the player's own ZDO under a stable hash key
(`"Name.key".GetStableHashCode()`), written only when `m_nview.IsOwner()`. Physics or gameplay changes on
shared objects run only on the owner (`m_nview.IsOwner()` on the ship), and are applied by temporarily
overriding a field in a Prefix and restoring it in the Postfix via `__state`. Key input, when a mod needs
any, is gated by `Console`, `Menu`, `TextInput`, `InventoryGui`, `Minimap` and `Chat` visibility checks so
shortcuts do not fire while typing; avoid `G`, which opens the game's radial menu.
