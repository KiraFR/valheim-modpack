using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ChestCraft
{
    /// <summary>
    /// Craft, upgrade and build by drawing directly from nearby chests.
    ///
    /// - Materials in nearby chests count as if they were in the backpack: the crafting panel shows the
    ///   total, the Craft button becomes enabled, and crafting takes whatever is missing from the
    ///   chests.
    /// - Same for the hammer (building) and for stations (smelter, charcoal kiln, windmill, spinning wheel,
    ///   eitr refinery, fermenter, fires, ballistas), each one can be disabled separately. Cooking stations
    ///   and ovens are left out by default: the ingredient they would take is unpredictable. The choice key
    ///   re-enables them one by one, or General.Cooking all at once.
    /// - The game does not centralise inventory reads: every path ends up on leaf methods of Inventory
    ///   (CountItems, HaveItem, GetAmmoItem, and the four RemoveItem). Those are the ones patched, and only
    ///   during a "scope" opened by the entry points of crafting, building and stations. Outside that
    ///   scope the patches do nothing.
    /// - The scope is closed by a Harmony Finalizer and not by a Postfix: a Postfix does not run if the
    ///   original method throws, which would leave the scope open for the rest of the session.
    /// - Stations designate their item by reference, not by name. Three patches redirect the removal to
    ///   the chest that holds the item; without them the item would be consumed without leaving the chest.
    /// - Loaded chests register themselves through a patch on Container.Awake; the mod makes no physics
    ///   query, it filters that list by distance every 0.25 s.
    ///
    /// Multiplayer: a chest is a ZDO, writing to it without owning it would be lost. Before each removal
    /// the mod takes ownership (ClaimOwnership) then forces a save (Container.Save).
    /// Chests opened by another player are ignored by default to avoid concurrent removals.
    /// Only the crafting player needs the mod; the server and the other clients do not.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "valheim.chestcraft";
        public const string PluginName = "ChestCraft";
        public const string PluginVersion = "1.2.0";

        internal static ManualLogSource Log;
        internal static Plugin Instance;

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> Radius;
        internal static ConfigEntry<bool> Crafting;
        internal static ConfigEntry<bool> Building;
        internal static ConfigEntry<bool> Stations;
        internal static ConfigEntry<bool> Cooking;
        internal static ConfigEntry<bool> FillAtOnce;
        internal static ConfigEntry<bool> ChestsFirst;
        internal static ConfigEntry<bool> IgnoreOpenChests;
        internal static ConfigEntry<bool> IgnoreCarts;
        internal static ConfigEntry<string> ExcludedChests;
        internal static ConfigEntry<KeyboardShortcut> ChoiceKey;
        internal static ConfigEntry<KeyboardShortcut> FillKey;
        internal static ConfigEntry<string> StationChoices;
        internal static ConfigEntry<bool> ShowOwned;
        internal static ConfigEntry<bool> AmountPicker;
        internal static ConfigEntry<string> CraftAmounts;

        /// <summary>Choice value meaning "take nothing from chests for this station".</summary>
        internal const string ChoiceNone = "-";

        /// <summary>Choice value meaning "the first ingredient found".</summary>
        internal const string ChoiceAuto = "*";

        /// <summary>Refresh interval of the nearby chest list and of the count cache.</summary>
        private const float ScanInterval = 0.25f;

        /// <summary>Every instantiated chest, fed by the patch on Container.Awake.</summary>
        private static readonly HashSet<Container> AllContainers = new HashSet<Container>();

        /// <summary>Nearby chests kept at the last scan.</summary>
        private static readonly List<Container> NearbyContainers = new List<Container>();

        /// <summary>Counts already computed since the last scan, key "name|quality|worldLevel".</summary>
        private static readonly Dictionary<string, int> CountCache = new Dictionary<string, int>();

        private static float _lastScan = -999f;

        /// <summary>Fingerprint of the nearby chests' contents, recomputed at every scan.</summary>
        private static int _signature;

        /// <summary>Last fingerprint for which the crafting panel was refreshed.</summary>
        private static int _refreshedSignature;

        private static bool _hasSignature;

        /// <summary>Depth of the crafting/building scope: the patches on Inventory only act if &gt; 0.</summary>
        private static int _depth;

        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;
            Instance = this;

            Enabled = Config.Bind("General", "Enabled", true, "Enables or disables the mod.");

            Radius = Config.Bind("General", "Radius", 20f,
                new ConfigDescription(
                    "Distance in metres around the player within which chests are used. " +
                    "Unloaded chests (zone too far away) never count, whatever the value.",
                    new AcceptableValueRange<float>(1f, 200f)));
            MigrateKey(Radius, "General", "Rayon");

            Crafting = Config.Bind("General", "Crafting", true,
                "Uses chests to craft and upgrade at the workbench, the forge, etc.");
            MigrateKey(Crafting, "General", "Fabrication");

            Building = Config.Bind("General", "Building", true,
                "Uses chests to build with the hammer (and with the cultivator, the chisel, etc.).");
            MigrateKey(Building, "General", "Construction");

            Stations = Config.Bind("General", "Stations", true,
                "Uses chests to feed stations: cooking station (cooking food above " +
                "the fire), oven, smelter, charcoal kiln, blast furnace, windmill, spinning wheel, eitr " +
                "refinery, fermenter, fires and torches (wood), ballistas (ammo), shield generators (charcoal). " +
                "Covers the raw material as well as the fuel.");
            MigrateKey(Stations, "General", "Appareils");

            Cooking = Config.Bind("General", "Cooking", false,
                "Default for cooking stations and ovens, the only stations where the ingredient taken would be " +
                "unpredictable (deer or boar meat, pie or bread). Disabled, they stay " +
                "vanilla and only use the backpack. The choice key still takes precedence: setting " +
                "a cooking station in game overrides this default for it, and the setting is remembered.");
            MigrateKey(Cooking, "General", "Cuisson");

            FillAtOnce = Config.Bind("General", "FillAtOnce", true,
                "Allows filling to the maximum by holding Controls.FillKey during " +
                "the interaction. Applies to coal and ore in smelters, fuel in cooking stations " +
                "and wood in fires. Works from the backpack as well as from chests. " +
                "Disabled, each press adds one unit as in vanilla.");
            MigrateKey(FillAtOnce, "General", "RemplirDUnCoup");

            ChestsFirst = Config.Bind("General", "ChestsFirst", false,
                "Takes from chests first and completes with the backpack. " +
                "By default it is the other way round: the backpack is emptied first.");
            MigrateKey(ChestsFirst, "General", "PrioriteCoffres");

            IgnoreOpenChests = Config.Bind("General", "IgnoreOpenChests", true,
                "Ignores chests another player is currently looking into, so that two " +
                "simultaneous removals do not overwrite each other. No effect in single player.");
            MigrateKey(IgnoreOpenChests, "General", "IgnorerCoffresOuverts");

            IgnoreCarts = Config.Bind("General", "IgnoreCarts", false,
                "Ignores carts and ships: only chests placed on the ground are used.");
            MigrateKey(IgnoreCarts, "General", "IgnorerChariots");

            ShowOwned = Config.Bind("Crafting panel", "ShowOwned", true,
                "Shows in small type, next to each required amount, how many you own in total: the backpack, " +
                "plus the nearby chests while General.Crafting (General.Building for the hammer menu) is on.");

            AmountPicker = Config.Bind("Crafting panel", "AmountPicker", true,
                "Adds a dropdown below the craft button to choose how many to craft at once, instead of " +
                "the game's fixed x5 while holding the multi-craft key. Upgrades always craft one. " +
                "Takes effect after a restart.");

            CraftAmounts = Config.Bind("Crafting panel", "CraftAmounts", "1, 5, 10, 20, 50, 100, Max",
                "Choices offered by the dropdown, separated by commas: whole numbers, and Max to craft as many as " +
                "the materials and the free backpack space allow. Takes effect after a restart.");

            ChoiceKey = Config.Bind("Controls", "ChoiceKey", new KeyboardShortcut(KeyCode.R),
                "Key that cycles the ingredient taken from chests, while aiming at a station. " +
                "The cycle is: Automatic, then each ingredient the station can convert, then Nothing. " +
                "The choice is remembered per station type and survives a restart.");
            MigrateKey(ChoiceKey, "Controls", "ToucheChoix");

            FillKey = Config.Bind("Controls", "FillKey", new KeyboardShortcut(KeyCode.LeftShift),
                "Key to hold during the interaction to fill the station up to its maximum. " +
                "Without it, a press adds a single unit, as in vanilla. " +
                "Shift is the game's run key, which does nothing while standing still in front of a station.");
            MigrateKey(FillKey, "Controls", "ToucheRemplir");

            StationChoices = Config.Bind("Items", "StationChoices", "",
                "Remembered choices, written by the mod when you use the choice key. " +
                "Format: station:ingredient separated by commas, e.g.: piece_cookingstation:DeerMeat, smelter:-. " +
                $"\"{ChoiceNone}\" means the station never draws from chests, " +
                $"\"{ChoiceAuto}\" the first ingredient found. " +
                "A missing entry keeps the default, set by General.Cooking for cooking stations and ovens.");
            MigrateKey(StationChoices, "Objets", "AppareilsChoix");

            ExcludedChests = Config.Bind("Items", "ExcludedChests", "",
                "Prefab names of containers never used, separated by commas " +
                "(e.g.: piece_chest_private, Karve, piece_cartographytable).");
            MigrateKey(ExcludedChests, "Objets", "CoffresExclus");

            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll(typeof(Plugin).Assembly);

            Log.LogInfo($"{PluginName} {PluginVersion} loaded.");
        }

        /// <summary>
        /// Keeps the value of a config key renamed when the mod was translated to English: if the old key is still in the
        /// .cfg (BepInEx keeps unbound keys as orphans), its value moves to the new entry and the old line is dropped.
        /// </summary>
        private void MigrateKey(ConfigEntryBase entry, string oldSection, string oldKey)
        {
            var old = new ConfigDefinition(oldSection, oldKey);
            if (!Config.OrphanedEntries.TryGetValue(old, out string value)) return;

            entry.SetSerializedValue(value);
            Config.OrphanedEntries.Remove(old);
            Config.Save();
            Log.LogInfo($"Config key [{oldSection}] {oldKey} migrated to [{entry.Definition.Section}] {entry.Definition.Key}.");
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
            AllContainers.Clear();
            NearbyContainers.Clear();
            CountCache.Clear();
        }

        // ------------------------------------------------------------------------------------------------
        // Scope: opened by the crafting/building entry points, closed by their Postfix
        // ------------------------------------------------------------------------------------------------

        /// <summary>True when the patches on Inventory must include chests.</summary>
        internal static bool Active => Enabled.Value && _depth > 0 && Player.m_localPlayer != null;

        /// <summary>Opens the scope if <paramref name="wanted"/>, or if a scope is already open (nested call).</summary>
        internal static bool Open(bool wanted)
        {
            if (!Enabled.Value) return false;
            if (!wanted && _depth == 0) return false;
            _depth++;
            return true;
        }

        internal static void Close(bool opened)
        {
            if (opened && _depth > 0) _depth--;
        }

        /// <summary>Only the local player's inventory is supplemented by chests.</summary>
        internal static bool IsPlayerInventory(Inventory inventory)
        {
            Player player = Player.m_localPlayer;
            return player != null && inventory == player.GetInventory();
        }

        // ------------------------------------------------------------------------------------------------
        // Nearby chests
        // ------------------------------------------------------------------------------------------------

        internal static void Register(Container container)
        {
            if (container != null) AllContainers.Add(container);
        }

        /// <summary>Splits a list "a, b, c" keeping the order and ignoring empty entries.</summary>
        private static List<string> ParseList(string raw)
        {
            var list = new List<string>();
            foreach (string entry in (raw ?? "").Split(','))
            {
                string trimmed = entry.Trim();
                if (trimmed.Length > 0) list.Add(trimmed);
            }
            return list;
        }

        internal static HashSet<string> ParseExclusions()
        {
            return new HashSet<string>(ParseList(ExcludedChests.Value), StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>Prefab name, without the "(Clone)" suffix added by Unity on instantiation.</summary>
        internal static string PrefabName(Component component)
        {
            string name = component.gameObject.name;
            int clone = name.IndexOf("(Clone)", StringComparison.Ordinal);
            return clone >= 0 ? name.Substring(0, clone) : name;
        }

        // ------------------------------------------------------------------------------------------------
        // Ingredient choice, per station type
        // ------------------------------------------------------------------------------------------------

        private static string _choicesRaw;
        private static Dictionary<string, string> _choicesCache;

        /// <summary>
        /// Current choices. Read on every scope call, so several times per frame: the result is
        /// memoised as long as the config string does not change, to avoid reparsing in a loop.
        /// </summary>
        private static Dictionary<string, string> Choices()
        {
            string raw = StationChoices.Value ?? "";
            if (_choicesCache != null && _choicesRaw == raw) return _choicesCache;

            _choicesCache = ParseChoices();
            _choicesRaw = raw;
            return _choicesCache;
        }

        private static Dictionary<string, string> ParseChoices()
        {
            var choices = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string entry in ParseList(StationChoices.Value))
            {
                int separator = entry.IndexOf(':');
                if (separator <= 0 || separator == entry.Length - 1)
                {
                    Log.LogWarning($"Choice ignored, expected format station:ingredient: \"{entry}\"");
                    continue;
                }
                choices[entry.Substring(0, separator).Trim()] = entry.Substring(separator + 1).Trim();
            }
            return choices;
        }

        /// <summary>Writing to the ConfigEntry is enough to persist: BepInEx saves on assignment.</summary>
        private static void SaveChoices(Dictionary<string, string> choices)
        {
            StationChoices.Value = string.Join(", ", choices.Select(kv => $"{kv.Key}:{kv.Value}").ToArray());
        }

        /// <summary>Ingredient kept for this station type, or null for automatic mode.</summary>
        /// <summary>
        /// Current setting of the station: always ChoiceAuto, ChoiceNone, or an ingredient prefab name.
        /// Without an explicit entry it falls back to the default, which is Nothing for cooking stations and
        /// ovens while Cooking is disabled: we do not know what will be cooked, so better take nothing on our own.
        /// </summary>
        internal static string ChoiceFor(Component station)
        {
            if (station == null) return ChoiceAuto;
            if (IsHardBlocked(station)) return ChoiceNone;
            return Choices().TryGetValue(PrefabName(station), out string choice) ? choice : ChoiceAuto;
        }

        /// <summary>
        /// Cooking disabled shuts cooking stations and ovens off unconditionally: a choice saved with the
        /// R key does not bypass it. Otherwise the setting would be useless on a station already
        /// set once, which is precisely the case we want to cover.
        /// </summary>
        internal static bool IsHardBlocked(Component station)
        {
            return station is CookingStation && !Cooking.Value;
        }

        /// <summary>The station is set to Nothing: it must not draw anything from chests, fuel included.</summary>
        internal static bool IsBlocked(Component station)
        {
            return ChoiceFor(station) == ChoiceNone;
        }

        /// <summary>
        /// Ingredients this station can convert. Fires and ballistas have none: their cycle comes
        /// down to Automatic and Nothing, which is enough since their consumable is unique.
        /// </summary>
        internal static List<ItemDrop> InputsOf(Component station)
        {
            var inputs = new List<ItemDrop>();
            switch (station)
            {
                case CookingStation cooking:
                    inputs.AddRange(cooking.m_conversion.Select(c => c.m_from));
                    break;
                case Smelter smelter:
                    inputs.AddRange(smelter.m_conversion.Select(c => c.m_from));
                    break;
                case Fermenter fermenter:
                    inputs.AddRange(fermenter.m_conversion.Select(c => c.m_from));
                    break;
            }
            inputs.RemoveAll(drop => drop == null);
            return inputs;
        }

        /// <summary>
        /// Chooses which ingredient to take from chests among those the station can convert.
        /// In automatic mode the station's order is kept, which is the game's. Otherwise only the chosen
        /// ingredient can leave a chest; the others stay available from the backpack, as in vanilla.
        /// </summary>
        internal static ItemDrop.ItemData PickFromContainers(Component station)
        {
            string choice = ChoiceFor(station);
            if (choice == ChoiceNone) return null;

            foreach (ItemDrop drop in InputsOf(station))
            {
                if (choice != ChoiceAuto && !string.Equals(choice, drop.name, StringComparison.OrdinalIgnoreCase)) continue;

                ItemDrop.ItemData found = FindByNameInContainers(drop.m_itemData.m_shared.m_name);
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>Label displayed for the current setting of a station.</summary>
        internal static string ChoiceLabel(Component station)
        {
            string choice = ChoiceFor(station);
            if (choice == ChoiceAuto) return "Automatic";
            if (choice == ChoiceNone) return "Nothing";

            ItemDrop drop = InputsOf(station).Find(
                d => string.Equals(choice, d.name, StringComparison.OrdinalIgnoreCase));
            return drop != null ? Localization.instance.Localize(drop.m_itemData.m_shared.m_name) : choice;
        }

        /// <summary>
        /// Advances the choice by one step: Automatic, each ingredient in the station's order,
        /// then Nothing, then back to Automatic.
        /// </summary>
        internal static void CycleChoice(Component station)
        {
            List<ItemDrop> inputs = InputsOf(station);
            var cycle = new List<string> { ChoiceAuto };
            cycle.AddRange(inputs.Select(d => d.name));
            cycle.Add(ChoiceNone);

            string current = ChoiceFor(station);
            int index = cycle.FindIndex(c => string.Equals(c, current, StringComparison.OrdinalIgnoreCase));

            // The choice is always written explicitly, never erased: a missing entry means
            // "default", which is not Automatic for every station.
            var choices = new Dictionary<string, string>(Choices(), StringComparer.OrdinalIgnoreCase);
            choices[PrefabName(station)] = cycle[(index + 1) % cycle.Count];
            SaveChoices(choices);
        }

        /// <summary>Another player is looking into this chest: the ZDO flags it but it is not us.</summary>
        private static bool OpenedBySomeoneElse(Container container)
        {
            ZDO zdo = container.m_nview.GetZDO();
            return zdo != null && zdo.GetInt(ZDOVars.s_inUse) == 1 && !container.IsInUse();
        }

        private static bool AccessAllowed(Container container, long playerID)
        {
            if (container.m_checkGuardStone &&
                !PrivateArea.CheckAccess(container.transform.position, 0f, flash: false))
            {
                return false;
            }

            switch (container.m_privacy)
            {
                case Container.PrivacySetting.Public:
                    return true;
                case Container.PrivacySetting.Private:
                    return container.m_piece != null && container.m_piece.GetCreator() == playerID;
                default:
                    return false;
            }
        }

        /// <summary>List of usable chests, recomputed at most every <see cref="ScanInterval"/> seconds.</summary>
        internal static List<Container> Nearby()
        {
            if (Time.time - _lastScan < ScanInterval) return NearbyContainers;
            _lastScan = Time.time;
            Rescan();
            return NearbyContainers;
        }

        private static void Rescan()
        {
            NearbyContainers.Clear();
            CountCache.Clear();

            Player player = Player.m_localPlayer;
            if (player == null) return;

            Vector3 center = player.transform.position;
            float radius = Radius.Value * Radius.Value;
            long playerID = player.GetPlayerID();
            HashSet<string> excluded = ParseExclusions();
            bool ignoreOpen = IgnoreOpenChests.Value;
            bool ignoreCarts = IgnoreCarts.Value;

            AllContainers.RemoveWhere(c => c == null);

            foreach (Container container in AllContainers)
            {
                if (container.m_nview == null || !container.m_nview.IsValid()) continue;
                if (container.GetInventory() == null) continue;
                if ((container.transform.position - center).sqrMagnitude > radius) continue;
                if (ignoreCarts && (container.m_wagon != null || container.m_rootObjectOverride != null)) continue;
                if (ignoreOpen && OpenedBySomeoneElse(container)) continue;
                if (!AccessAllowed(container, playerID)) continue;
                if (excluded.Count > 0 && excluded.Contains(PrefabName(container))) continue;

                NearbyContainers.Add(container);
            }

            _signature = ComputeSignature();
            if (!_hasSignature)
            {
                _hasSignature = true;
                _refreshedSignature = _signature;
            }
        }

        /// <summary>
        /// Fingerprint of the contents of every kept chest. Only used to detect a change,
        /// not to identify anything: a collision would only miss a refresh.
        /// </summary>
        private static int ComputeSignature()
        {
            int hash = NearbyContainers.Count;
            foreach (Container container in NearbyContainers)
            {
                foreach (ItemDrop.ItemData item in container.GetInventory().m_inventory)
                {
                    hash = hash * 31 + item.m_shared.m_name.GetHashCode();
                    hash = hash * 31 + item.m_stack;
                    hash = hash * 31 + item.m_quality;
                }
            }
            return hash;
        }

        // ------------------------------------------------------------------------------------------------
        // Fill in one press
        // ------------------------------------------------------------------------------------------------

        /// <summary>
        /// "The key is held" test, without going through KeyboardShortcut.IsPressed().
        ///
        /// IsPressed() requires that NO other keyboard key is held down. But this test runs while
        /// the player is holding the interaction key: that key makes the condition fail every
        /// time, so the modifier was never recognised.
        /// </summary>
        private static bool FillRequested()
        {
            KeyboardShortcut key = FillKey.Value;
            if (key.MainKey == KeyCode.None || !Input.GetKey(key.MainKey)) return false;

            foreach (KeyCode modifier in key.Modifiers)
            {
                if (!Input.GetKey(modifier)) return false;
            }
            return true;
        }

        private static bool _filling;

        /// <summary>True during a fill: used to swallow the game's repeated messages.</summary>
        internal static bool Filling => _filling;

        /// <summary>
        /// Repeats the addition the game just made until the station refuses, then summarises in a
        /// single message.
        ///
        /// Two precautions. ZDO ownership is taken first: InvokeRoutedRPC only runs immediately
        /// if we are the recipient, otherwise the addition would go over the network and the
        /// amount read at the next iteration would still be the old one, so the loop would never
        /// stop on its own. And the number of additions is capped by the station's capacity, just in case.
        /// </summary>
        internal static void FillToMax(ZNetView nview, Humanoid user, string label, int capacity, Func<bool> add)
        {
            if (!FillAtOnce.Value || _filling) return;
            if (!FillRequested()) return;
            if (nview != null && nview.IsValid() && !nview.IsOwner()) nview.ClaimOwnership();

            int added = 1; // the first addition is the one the game just made
            _filling = true;
            try
            {
                while (added < Math.Max(1, capacity) && add()) added++;
            }
            catch (Exception e)
            {
                Log.LogWarning($"Fill interrupted: {e.Message}");
            }
            finally
            {
                _filling = false;
            }

            if (user != null) user.Message(MessageHud.MessageType.Center, $"{label} x{added}");
        }

        /// <summary>Readable station name: the one of the built piece, not the prefab's.</summary>
        internal static string StationName(Component station)
        {
            Piece piece = station.GetComponentInParent<Piece>();
            return piece != null ? Localization.instance.Localize(piece.m_name) : PrefabName(station);
        }

        /// <summary>Line appended to a station's hover text to show and remind the setting.</summary>
        internal static string AppendChoice(Component station, string text)
        {
            if (!Enabled.Value || !Stations.Value || string.IsNullOrEmpty(text)) return text;
            if (IsHardBlocked(station)) return text;
            return text + "\n[<color=yellow><b>" + ChoiceKey.Value.MainKey +
                   "</b></color>] Chests: " + ChoiceLabel(station);
        }

        /// <summary>Presentable key name: the Unity enum says "LeftShift" where the player reads "Shift".</summary>
        private static string KeyName(KeyCode key)
        {
            switch (key)
            {
                case KeyCode.LeftShift:
                case KeyCode.RightShift: return "Shift";
                case KeyCode.LeftControl:
                case KeyCode.RightControl: return "Ctrl";
                case KeyCode.LeftAlt:
                case KeyCode.RightAlt: return "Alt";
                default: return key.ToString();
            }
        }

        /// <summary>Reminder of the fill key, on stations that stack several units.</summary>
        internal static string AppendFill(Component station, string text)
        {
            if (!Enabled.Value || !FillAtOnce.Value || string.IsNullOrEmpty(text)) return text;
            if (!(station is Smelter || station is CookingStation || station is Fireplace ||
                  station is ShieldGenerator || station is Turret))
            {
                return text;
            }

            string use = Localization.instance.Localize("$KEY_Use");
            return text + "\n[<color=yellow><b>" + KeyName(FillKey.Value.MainKey) + " + " + use +
                   "</b></color>] Add all";
        }

        /// <summary>Station this switch belongs to, only for those that add something.</summary>
        internal static Component StationOfSwitch(Switch sw)
        {
            CookingStation cooking = sw.GetComponentInParent<CookingStation>();
            if (cooking != null)
            {
                return sw == cooking.m_addFoodSwitch || sw == cooking.m_addFuelSwitch ? cooking : null;
            }

            Smelter smelter = sw.GetComponentInParent<Smelter>();
            if (smelter != null)
            {
                return sw == smelter.m_addOreSwitch || sw == smelter.m_addWoodSwitch ? smelter : null;
            }

            Fermenter fermenter = sw.GetComponentInParent<Fermenter>();
            if (fermenter != null)
            {
                return sw == fermenter.m_addSwitch ? fermenter : null;
            }

            ShieldGenerator shield = sw.GetComponentInParent<ShieldGenerator>();
            if (shield != null)
            {
                return sw == shield.m_addFuelSwitch ? shield : null;
            }

            return null;
        }

        /// <summary>Station currently aimed at by the player, or null.</summary>
        private static Component HoveredStation()
        {
            GameObject hover = Player.m_localPlayer.GetHoverObject();
            if (hover == null) return null;

            CookingStation cooking = hover.GetComponentInParent<CookingStation>();
            if (cooking != null) return cooking;

            Smelter smelter = hover.GetComponentInParent<Smelter>();
            if (smelter != null) return smelter;

            Fermenter fermenter = hover.GetComponentInParent<Fermenter>();
            if (fermenter != null) return fermenter;

            Fireplace fireplace = hover.GetComponentInParent<Fireplace>();
            if (fireplace != null) return fireplace;

            Turret turret = hover.GetComponentInParent<Turret>();
            if (turret != null) return turret;

            ShieldGenerator shield = hover.GetComponentInParent<ShieldGenerator>();
            if (shield != null) return shield;

            return null;
        }

        /// <summary>Same locks as RowTogether: the key does not fire while typing or while a menu is open.</summary>
        private static bool CanUseInput()
        {
            if (Console.IsVisible() || Menu.IsVisible() || TextInput.IsVisible()) return false;
            if (InventoryGui.IsVisible() || Minimap.IsOpen()) return false;
            if (Chat.instance != null && Chat.instance.HasFocus()) return false;
            return true;
        }

        /// <summary>Choice key: advances the setting of the aimed station and confirms it on screen.</summary>
        private static void HandleChoiceKey()
        {
            if (!Stations.Value || !ChoiceKey.Value.IsDown() || !CanUseInput()) return;

            Component station = HoveredStation();
            if (station == null) return;

            if (IsHardBlocked(station))
            {
                Player.m_localPlayer.Message(MessageHud.MessageType.Center,
                    $"{StationName(station)} — chests disabled (General.Cooking)");
                return;
            }

            CycleChoice(station);
            Player.m_localPlayer.Message(MessageHud.MessageType.Center,
                $"{StationName(station)} — chests: {ChoiceLabel(station)}");
        }

        /// <summary>
        /// The game only rebuilds the recipe list on events (panel opening, craft,
        /// inventory change). A chest filling up while the panel is open does not
        /// trigger anything, so a recipe that became craftable would stay greyed out. The displayed amounts,
        /// on the other hand, are recomputed every frame by UpdateRecipe and were already up to date.
        ///
        /// Called from Update, so outside any scope: no reentrancy in the patches.
        /// UpdateCraftingPanel is what the game itself calls after each craft, and it keeps the
        /// selected recipe.
        /// </summary>
        private void Update()
        {
            if (!Enabled.Value || Player.m_localPlayer == null) return;

            HandleChoiceKey();

            if (InventoryGui.instance == null || !InventoryGui.IsVisible()) return;
            if (ZoneSystem.instance == null) return;

            Nearby();
            if (!_hasSignature || _signature == _refreshedSignature) return;

            _refreshedSignature = _signature;
            try
            {
                InventoryGui.instance.UpdateCraftingPanel();
            }
            catch (Exception e)
            {
                Log.LogWarning($"Unable to refresh the panel: {e.Message}");
            }
        }

        /// <summary>Forces a recount on next access (after a removal, the counts are stale).</summary>
        internal static void Invalidate()
        {
            CountCache.Clear();
        }

        // ------------------------------------------------------------------------------------------------
        // Counting and removal
        // ------------------------------------------------------------------------------------------------

        /// <summary>Vanilla count (Inventory.CountItems) applied to a given inventory, without going through the patches again.</summary>
        internal static int CountIn(Inventory inventory, string name, int quality, bool matchWorldLevel)
        {
            int total = 0;
            foreach (ItemDrop.ItemData item in inventory.m_inventory)
            {
                if ((name == null || item.m_shared.m_name == name) &&
                    (quality < 0 || quality == item.m_quality) &&
                    (!matchWorldLevel || item.m_worldLevel >= Game.m_worldLevel))
                {
                    total += item.m_stack;
                }
            }
            return total;
        }

        /// <summary>Total present in nearby chests, cached until the next scan.</summary>
        internal static int CountInContainers(string name, int quality, bool matchWorldLevel)
        {
            if (name == null) return 0;

            List<Container> containers = Nearby();
            if (containers.Count == 0) return 0;

            string key = name + "|" + quality + "|" + (matchWorldLevel ? 1 : 0);
            if (CountCache.TryGetValue(key, out int cached)) return cached;

            int total = 0;
            foreach (Container container in containers)
            {
                total += CountIn(container.GetInventory(), name, quality, matchWorldLevel);
            }

            CountCache[key] = total;
            return total;
        }

        /// <summary>First matching item found in nearby chests, or null.</summary>
        internal static ItemDrop.ItemData FindInContainers(string name, int quality, int minAmount)
        {
            foreach (Container container in Nearby())
            {
                if (CountIn(container.GetInventory(), name, quality, matchWorldLevel: true) < minAmount) continue;
                foreach (ItemDrop.ItemData item in container.GetInventory().m_inventory)
                {
                    if (item.m_shared.m_name == name && (quality < 0 || item.m_quality == quality)) return item;
                }
            }
            return null;
        }

        /// <summary>
        /// Removes up to <paramref name="amount"/> items from nearby chests and returns the
        /// amount actually removed. Takes ZDO ownership before writing, otherwise the server
        /// would overwrite the change at the next synchronisation.
        /// </summary>
        internal static int RemoveFromContainers(string name, int amount, int quality, bool worldLevelBased)
        {
            if (amount <= 0 || name == null) return 0;

            int removed = 0;
            foreach (Container container in Nearby())
            {
                if (removed >= amount) break;

                Inventory inventory = container.GetInventory();
                int available = CountIn(inventory, name, quality, worldLevelBased);
                if (available <= 0) continue;

                int take = Math.Min(available, amount - removed);
                if (WriteTo(container, inv => inv.RemoveItem(name, take, quality, worldLevelBased)))
                {
                    removed += take;
                }
            }

            return removed;
        }

        /// <summary>
        /// Takes ZDO ownership, applies the write, then forces a save. Without ClaimOwnership
        /// the change would be overwritten at the next synchronisation coming from the actual owner.
        /// </summary>
        private static bool WriteTo(Container container, Action<Inventory> write)
        {
            try
            {
                if (!container.m_nview.IsOwner()) container.m_nview.ClaimOwnership();
                write(container.GetInventory());
                container.Save();
                Invalidate();
                return true;
            }
            catch (Exception e)
            {
                Log.LogWarning($"Unable to write to {PrefabName(container)}: {e.Message}");
                return false;
            }
        }

        /// <summary>Nearby chest that physically holds this item, or null if the item comes from the backpack.</summary>
        internal static Container OwnerOf(ItemDrop.ItemData item)
        {
            if (item == null) return null;
            foreach (Container container in Nearby())
            {
                if (container.GetInventory().m_inventory.Contains(item)) return container;
            }
            return null;
        }

        /// <summary>
        /// Removal of an item designated by identity, redirected to the chest that holds it.
        /// Returns false if the item belongs to no nearby chest: it is then up to the game to remove it.
        /// </summary>
        private static bool RemoveExact(ItemDrop.ItemData item, Action<Inventory> write)
        {
            Container container = OwnerOf(item);
            return container != null && WriteTo(container, write);
        }

        internal static bool RemoveOneExact(ItemDrop.ItemData item)
        {
            return RemoveExact(item, inv => inv.RemoveOneItem(item));
        }

        internal static bool RemoveStackExact(ItemDrop.ItemData item)
        {
            return RemoveExact(item, inv => inv.RemoveItem(item));
        }

        internal static bool RemoveAmountExact(ItemDrop.ItemData item, int amount)
        {
            return RemoveExact(item, inv => inv.RemoveItem(item, amount));
        }

        /// <summary>Equivalent of Inventory.GetItem(name) applied to nearby chests.</summary>
        internal static ItemDrop.ItemData FindByNameInContainers(string name)
        {
            if (name == null) return null;
            foreach (Container container in Nearby())
            {
                foreach (ItemDrop.ItemData item in container.GetInventory().m_inventory)
                {
                    if (item.m_shared.m_name == name && item.m_worldLevel >= Game.m_worldLevel) return item;
                }
            }
            return null;
        }

        /// <summary>Equivalent of Inventory.GetAmmoItem applied to nearby chests (ballistas, turrets).</summary>
        internal static ItemDrop.ItemData FindAmmoInContainers(string ammoName, string matchPrefabName)
        {
            foreach (Container container in Nearby())
            {
                foreach (ItemDrop.ItemData item in container.GetInventory().m_inventory)
                {
                    bool isAmmo = item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Ammo ||
                                  item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.AmmoNonEquipable ||
                                  item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Consumable;

                    if (isAmmo && item.m_shared.m_ammoType == ammoName &&
                        (matchPrefabName == null || (item.m_dropPrefab != null && item.m_dropPrefab.name == matchPrefabName)))
                    {
                        return item;
                    }
                }
            }
            return null;
        }

        // ------------------------------------------------------------------------------------------------
        // Crafting panel: owned count and craft amount picker
        // ------------------------------------------------------------------------------------------------

        /// <summary>Dropdown choice that crafts as many as the materials and the backpack space allow.</summary>
        internal const string AmountMax = "Max";

        /// <summary>Hard cap for Max, so a recipe without cost does not queue an absurd batch.</summary>
        private const int MaxCraftCap = 999;

        /// <summary>Height of the dropdown, as a fraction of the craft button's height.</summary>
        private const float PickerHeight = 0.5f;

        /// <summary>Space between the craft button and the dropdown below it, as a fraction of the button's height.</summary>
        private const float PickerGap = 0.2f;

        internal static TMP_Dropdown AmountDropdown;

        private static readonly List<string> AmountOptions = new List<string>();

        /// <summary>Index of the chosen option. Kept while the game runs, so a batch size survives recipe changes.</summary>
        private static int _amountIndex;

        /// <summary>The game's own multi-craft amount (5), restored whenever the dropdown is back on 1.</summary>
        private static int _vanillaMultiCraftAmount = -1;

        /// <summary>True while the mod, not a touch long press, set m_touchMultiCrafting.</summary>
        private static bool _forcingMultiCraft;

        /// <summary>Count for the small owned figure: in full up to 99 999, then k and M so it stays short.</summary>
        internal static string FormatCount(int count)
        {
            if (count < 100000) return count.ToString(CultureInfo.InvariantCulture);
            if (count < 1000000) return (count / 1000).ToString(CultureInfo.InvariantCulture) + "k";
            return (count / 1000000f).ToString("0.#", CultureInfo.InvariantCulture) + "M";
        }

        /// <summary>Rich text appended to the required amount. The size tag keeps large stocks inside the slot.</summary>
        internal static string OwnedSuffix(int owned)
        {
            return "<size=60%><color=#C9C2B4> / " + FormatCount(owned) + "</color></size>";
        }

        /// <summary>Dropdown choices from the config: positive whole numbers and Max, in the order written.</summary>
        private static List<string> ParseAmounts()
        {
            var options = new List<string>();
            foreach (string entry in ParseList(CraftAmounts.Value))
            {
                if (string.Equals(entry, AmountMax, StringComparison.OrdinalIgnoreCase))
                {
                    if (!options.Contains(AmountMax)) options.Add(AmountMax);
                }
                else if (int.TryParse(entry, NumberStyles.Integer, CultureInfo.InvariantCulture, out int amount) && amount >= 1)
                {
                    string text = amount.ToString(CultureInfo.InvariantCulture);
                    if (!options.Contains(text)) options.Add(text);
                }
                else
                {
                    Log.LogWarning($"Craft amount ignored, expected a whole number or {AmountMax}: \"{entry}\"");
                }
            }

            if (!options.Contains("1")) options.Insert(0, "1");
            return options;
        }

        /// <summary>
        /// Builds the dropdown right below the craft button, as its child. TMP_DefaultControls is the factory behind
        /// Unity's own "create dropdown" menu; the result then borrows the craft button's sprite and font to look native.
        /// </summary>
        internal static void CreateAmountPicker(InventoryGui gui)
        {
            if (gui == null || gui.m_craftButton == null || AmountDropdown != null) return;

            RectTransform button = (RectTransform)gui.m_craftButton.transform;
            TMP_Text buttonLabel = gui.m_craftButton.GetComponentInChildren<TMP_Text>(true);
            Image buttonImage = gui.m_craftButton.GetComponent<Image>();

            GameObject go = TMP_DefaultControls.CreateDropdown(new TMP_DefaultControls.Resources());
            go.name = "ChestCraftAmountPicker";

            // A child of the button, placed with anchors in fractions of the button's own rect (negative values
            // reach below it): it takes the button's width whatever the button's anchors, and hides with it during
            // the progress bar. Added as the last child, so the game's GetComponentInChildren<TMP_Text>() still
            // finds the button's own label first.
            go.transform.SetParent(button, false);
            RectTransform rect = (RectTransform)go.transform;
            rect.anchorMin = new Vector2(0f, -PickerGap - PickerHeight);
            rect.anchorMax = new Vector2(1f, -PickerGap);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            TMP_Dropdown dropdown = go.GetComponent<TMP_Dropdown>();

            // Keyboard focus would let the jump key reopen the list: the dropdown is mouse only.
            dropdown.navigation = new Navigation { mode = Navigation.Mode.None };

            Image background = go.GetComponent<Image>();
            if (background != null && buttonImage != null)
            {
                background.sprite = buttonImage.sprite;
                background.type = buttonImage.type;
                background.color = buttonImage.color;
            }

            Transform arrow = go.transform.Find("Arrow");
            if (arrow != null) arrow.gameObject.SetActive(false);

            Transform label = go.transform.Find("Label");
            if (label != null)
            {
                var labelRect = (RectTransform)label;
                labelRect.offsetMin = new Vector2(4f, 0f);
                labelRect.offsetMax = new Vector2(-4f, 0f);
            }

            foreach (TMP_Text text in go.GetComponentsInChildren<TMP_Text>(true))
            {
                if (buttonLabel != null)
                {
                    text.font = buttonLabel.font;
                    text.fontSharedMaterial = buttonLabel.fontSharedMaterial;
                    text.color = buttonLabel.color;
                }
                text.alignment = TextAlignmentOptions.Center;
                text.enableAutoSizing = true;
                text.fontSizeMin = 10f;
                text.fontSizeMax = buttonLabel != null ? buttonLabel.fontSize : 18f;
            }

            var panel = new Color(0.11f, 0.09f, 0.07f, 0.97f);
            var accent = new Color(0.85f, 0.6f, 0.25f, 1f);
            foreach (Image image in dropdown.template.GetComponentsInChildren<Image>(true))
            {
                switch (image.gameObject.name)
                {
                    case "Template":
                    case "Scrollbar":
                        image.color = panel;
                        break;
                    case "Handle":
                        image.color = new Color(0.55f, 0.45f, 0.3f, 1f);
                        break;
                    case "Item Background":
                        image.color = Color.white; // tinted by the toggle's colors below
                        break;
                    case "Item Checkmark":
                        image.gameObject.SetActive(false);
                        break;
                }
            }

            Toggle item = dropdown.template.GetComponentInChildren<Toggle>(true);
            if (item != null)
            {
                ColorBlock colors = item.colors;
                colors.normalColor = new Color(1f, 1f, 1f, 0f);
                colors.highlightedColor = new Color(accent.r, accent.g, accent.b, 0.45f);
                colors.selectedColor = new Color(accent.r, accent.g, accent.b, 0.25f);
                colors.pressedColor = new Color(accent.r, accent.g, accent.b, 0.6f);
                item.colors = colors;
            }

            AmountOptions.Clear();
            AmountOptions.AddRange(ParseAmounts());
            dropdown.ClearOptions();
            dropdown.AddOptions(AmountOptions.Select(option => option == AmountMax ? AmountMax : "x" + option).ToList());
            _amountIndex = Mathf.Clamp(_amountIndex, 0, AmountOptions.Count - 1);
            dropdown.value = _amountIndex;

            dropdown.onValueChanged.AddListener(index =>
            {
                _amountIndex = index;
                if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(null);
            });

            AmountDropdown = dropdown;
        }

        /// <summary>Uncraft, a separate mod, reuses the craft button in its own tab: a craft amount means nothing there.</summary>
        private static bool InUncraftTab(InventoryGui gui)
        {
            if (gui.m_tabCraft == null) return false;
            Transform tab = gui.m_tabCraft.transform.parent.Find("TabUncraft");
            if (tab == null || !tab.gameObject.activeInHierarchy) return false;
            Button button = tab.GetComponent<Button>();
            return button != null && !button.interactable;
        }

        /// <summary>
        /// Turns the dropdown choice into the game's own multi-crafting. m_multiCraftAmount becomes the chosen amount,
        /// and m_touchMultiCrafting, the flag a long press sets on touch screens, stands in for holding the
        /// multi-craft key. Everything downstream follows untouched: button label, requirement check and colors,
        /// consumption, craft duration. Back on 1, the vanilla amount returns, so holding the key still crafts x5.
        /// </summary>
        internal static void ApplyCraftAmount(InventoryGui gui, Player player)
        {
            if (gui == null) return;
            if (_vanillaMultiCraftAmount < 0) _vanillaMultiCraftAmount = gui.m_multiCraftAmount;

            // A craft in progress keeps the amount it was started with, even if Max would now say otherwise.
            if (gui.m_craftTimer >= 0f) return;

            Recipe recipe = gui.m_selectedRecipe.Recipe;
            bool upgrade = gui.m_selectedRecipe.ItemData != null;
            bool pickerOn = Enabled.Value && AmountPicker.Value && AmountDropdown != null && !InUncraftTab(gui);

            if (AmountDropdown != null) AmountDropdown.interactable = pickerOn && recipe != null && !upgrade;

            int amount = pickerOn && recipe != null && !upgrade ? SelectedAmount(player, recipe) : 1;
            if (amount > 1)
            {
                gui.m_multiCraftAmount = amount;
                gui.m_touchMultiCrafting = true;
                _forcingMultiCraft = true;
            }
            else
            {
                gui.m_multiCraftAmount = _vanillaMultiCraftAmount;
                if (_forcingMultiCraft)
                {
                    gui.m_touchMultiCrafting = false;
                    _forcingMultiCraft = false;
                }
            }
        }

        /// <summary>
        /// Closes the open list, and only an open one. TMP_Dropdown.Hide() ends with Select(), which takes the UI focus:
        /// InventoryGui.Update calls Hide() every frame while no player is spawned, so an unconditional call kept
        /// stealing the focus from the server password field.
        /// </summary>
        internal static void CloseAmountList()
        {
            if (AmountDropdown != null && AmountDropdown.IsExpanded) AmountDropdown.Hide();
        }

        /// <summary>The dropdown follows the craft button: hidden during the progress bar and outside the craft tabs.</summary>
        internal static void SyncPickerVisibility(InventoryGui gui)
        {
            if (AmountDropdown == null || gui == null || gui.m_craftButton == null) return;

            bool show = Enabled.Value && AmountPicker.Value && gui.m_craftButton.gameObject.activeSelf && !InUncraftTab(gui);
            if (AmountDropdown.gameObject.activeSelf == show) return;

            if (!show) CloseAmountList();
            AmountDropdown.gameObject.SetActive(show);
        }

        private static int SelectedAmount(Player player, Recipe recipe)
        {
            if (AmountOptions.Count == 0) return 1;

            string option = AmountOptions[Mathf.Clamp(_amountIndex, 0, AmountOptions.Count - 1)];
            if (option == AmountMax) return MaxCraftable(player, recipe);
            return int.Parse(option, CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Largest batch the materials allow, counting nearby chests while crafting from chests is on, then capped by
        /// what the backpack can hold of the result: the game refuses a craft whose output does not fit.
        /// </summary>
        internal static int MaxCraftable(Player player, Recipe recipe)
        {
            if (player == null || recipe == null || recipe.m_item == null) return 1;

            CraftingStation station = player.GetCurrentCraftingStation();
            int best = recipe.m_requireOnlyOneIngredient ? 0 : int.MaxValue;

            bool opened = Open(Crafting.Value);
            try
            {
                foreach (Piece.Requirement requirement in recipe.m_resources)
                {
                    if ((station != null && station.m_upgrader != requirement.m_upgraderResource) ||
                        (station == null && requirement.m_upgraderResource) ||
                        !requirement.m_resItem)
                    {
                        continue;
                    }

                    int perCraft = requirement.GetAmount(1);
                    if (perCraft <= 0) continue;

                    int owned = player.GetInventory().CountItems(requirement.m_resItem.m_itemData.m_shared.m_name);
                    int batches = owned / perCraft;
                    best = recipe.m_requireOnlyOneIngredient ? Math.Max(best, batches) : Math.Min(best, batches);
                }
            }
            finally
            {
                Close(opened);
            }

            bool free = player.NoCostCheat() || (ZoneSystem.instance != null && ZoneSystem.instance.GetGlobalKey(GlobalKeys.NoCraftCost));
            if (free) best = MaxCraftCap;

            best = Math.Min(best, MaxCraftCap);
            if (best <= 1) return 1;

            // Binary search on the space left: CanAddItem already knows about partial stacks and free slots.
            Inventory inventory = player.GetInventory();
            GameObject result = recipe.m_item.gameObject;
            int perBatch = Math.Max(1, recipe.m_amount);
            if (!inventory.CanAddItem(result, perBatch)) return 1;

            int low = 1;
            int high = best;
            while (low < high)
            {
                int middle = (low + high + 1) / 2;
                if (inventory.CanAddItem(result, perBatch * middle)) low = middle;
                else high = middle - 1;
            }
            return low;
        }

        // ------------------------------------------------------------------------------------------------
        // Diagnostics
        // ------------------------------------------------------------------------------------------------

        internal static string DescribeNearby()
        {
            var sb = new StringBuilder();
            Player player = Player.m_localPlayer;
            if (player == null) return "No local player.";

            _lastScan = -999f;
            List<Container> containers = Nearby();
            Vector3 center = player.transform.position;

            foreach (Container container in containers.OrderBy(c => (c.transform.position - center).sqrMagnitude))
            {
                Inventory inventory = container.GetInventory();
                float distance = Vector3.Distance(container.transform.position, center);
                sb.AppendLine($"{PrefabName(container),-28} {distance,5:0.0} m  " +
                              $"{inventory.NrOfItems(),3} stack(s)  owner: {(container.m_nview.IsOwner() ? "me" : "other")}");
            }

            sb.AppendLine($"{containers.Count} chest(s) within a radius of {Radius.Value:0.#} m " +
                          $"out of {AllContainers.Count} loaded.");
            return sb.ToString();
        }
    }

    // ----------------------------------------------------------------------------------------------------
    // Chest registration
    // ----------------------------------------------------------------------------------------------------

    /// <summary>Each instantiated container registers itself: no physics query needed afterwards.</summary>
    [HarmonyPatch(typeof(Container), nameof(Container.Awake))]
    internal static class Container_Awake_Patch
    {
        private static void Postfix(Container __instance)
        {
            Plugin.Register(__instance);
        }
    }

    // ----------------------------------------------------------------------------------------------------
    // Entry points: opening the scope
    // ----------------------------------------------------------------------------------------------------

    /// <summary>Recipe: state of the Craft button, and the check done again at crafting time.</summary>
    [HarmonyPatch(typeof(Player), nameof(Player.HaveRequirements),
        new[] { typeof(Recipe), typeof(bool), typeof(int), typeof(int) })]
    internal static class Player_HaveRequirementsRecipe_Patch
    {
        private static void Prefix(out bool __state)
        {
            __state = Plugin.Open(Plugin.Crafting.Value);
        }

        private static void Finalizer(bool __state)
        {
            Plugin.Close(__state);
        }
    }

    /// <summary>Building piece: greying out in the hammer menu and check before placement.</summary>
    [HarmonyPatch(typeof(Player), nameof(Player.HaveRequirements),
        new[] { typeof(Piece), typeof(Player.RequirementMode) })]
    internal static class Player_HaveRequirementsPiece_Patch
    {
        private static void Prefix(out bool __state)
        {
            __state = Plugin.Open(Plugin.Building.Value);
        }

        private static void Finalizer(bool __state)
        {
            Plugin.Close(__state);
        }
    }

    /// <summary>Material consumption, both for crafting and for placing a piece.</summary>
    [HarmonyPatch(typeof(Player), nameof(Player.ConsumeResources))]
    internal static class Player_ConsumeResources_Patch
    {
        private static void Prefix(out bool __state)
        {
            // During a craft the scope is already opened by DoCrafting: Open keeps it.
            __state = Plugin.Open(Plugin.Building.Value);
        }

        private static void Finalizer(bool __state)
        {
            Plugin.Close(__state);
        }
    }

    /// <summary>"material x amount" line of the crafting panel and of the building menu.</summary>
    [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.SetupRequirement))]
    internal static class InventoryGui_SetupRequirement_Patch
    {
        private static void Prefix(bool craft, out bool __state)
        {
            __state = Plugin.Open(craft ? Plugin.Crafting.Value : Plugin.Building.Value);
        }

        /// <summary>
        /// Appends how many the player owns in total. Runs before the Finalizer, so the scope is still open and the
        /// count includes the nearby chests exactly when the craft would draw from them.
        /// </summary>
        private static void Postfix(Transform elementRoot, Piece.Requirement req, Player player, bool __result)
        {
            if (!__result || !Plugin.Enabled.Value || !Plugin.ShowOwned.Value) return;
            if (req == null || req.m_resItem == null || player == null) return;

            Transform amountRoot = elementRoot.Find("res_amount");
            TMP_Text amount = amountRoot != null ? amountRoot.GetComponent<TMP_Text>() : null;
            if (amount == null) return;

            int owned = player.GetInventory().CountItems(req.m_resItem.m_itemData.m_shared.m_name);
            amount.text += Plugin.OwnedSuffix(owned);
        }

        private static void Finalizer(bool __state)
        {
            Plugin.Close(__state);
        }
    }

    /// <summary>The craft itself: covers consumption and the "only one ingredient of your choice" case.</summary>
    [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.DoCrafting))]
    internal static class InventoryGui_DoCrafting_Patch
    {
        private static void Prefix(out bool __state)
        {
            __state = Plugin.Open(Plugin.Crafting.Value);
        }

        private static void Finalizer(bool __state)
        {
            Plugin.Close(__state);
        }
    }

    /// <summary>
    /// Recipes with a single ingredient of your choice (Recipe.m_requireOnlyOneIngredient): the game looks for
    /// the ingredient in the backpack only. If it is not there, we designate one taken from chests;
    /// the removal that follows goes through Inventory.RemoveItem, hence through the patch below.
    /// </summary>
    [HarmonyPatch(typeof(Player), nameof(Player.GetFirstRequiredItem))]
    internal static class Player_GetFirstRequiredItem_Patch
    {
        private static void Prefix(out bool __state)
        {
            __state = Plugin.Open(Plugin.Crafting.Value);
        }

        private static void Postfix(Player __instance, Recipe recipe, int qualityLevel, int craftMultiplier,
            ref ItemDrop.ItemData __result, ref int amount, ref int extraAmount, bool __state)
        {
            if (!__state || __result != null || !Plugin.Active) return;

            CraftingStation station = __instance.GetCurrentCraftingStation();
            foreach (Piece.Requirement requirement in recipe.m_resources)
            {
                if ((station != null && station.m_upgrader != requirement.m_upgraderResource) ||
                    (station == null && requirement.m_upgraderResource) ||
                    !requirement.m_resItem)
                {
                    continue;
                }

                int need = requirement.GetAmount(qualityLevel) * craftMultiplier;
                if (need <= 0) continue;

                string name = requirement.m_resItem.m_itemData.m_shared.m_name;
                for (int quality = 0; quality <= requirement.m_resItem.m_itemData.m_shared.m_maxQuality; quality++)
                {
                    ItemDrop.ItemData found = Plugin.FindInContainers(name, quality, need);
                    if (found == null) continue;

                    amount = need;
                    extraAmount = requirement.m_extraAmountOnlyOneIngredient;
                    __result = found;
                    return;
                }
            }
        }

        private static void Finalizer(bool __state)
        {
            Plugin.Close(__state);
        }
    }

    // ----------------------------------------------------------------------------------------------------
    // Leaf methods: this is where chests are added to the backpack
    // ----------------------------------------------------------------------------------------------------

    [HarmonyPatch(typeof(Inventory), nameof(Inventory.CountItems),
        new[] { typeof(string), typeof(int), typeof(bool) })]
    internal static class Inventory_CountItems_Patch
    {
        private static void Postfix(Inventory __instance, string name, int quality, bool matchWorldLevel,
            ref int __result)
        {
            if (!Plugin.Active || !Plugin.IsPlayerInventory(__instance)) return;
            __result += Plugin.CountInContainers(name, quality, matchWorldLevel);
        }
    }

    [HarmonyPatch(typeof(Inventory), nameof(Inventory.HaveItem),
        new[] { typeof(string), typeof(bool) })]
    internal static class Inventory_HaveItem_Patch
    {
        private static void Postfix(Inventory __instance, string name, bool matchWorldLevel, ref bool __result)
        {
            if (__result || !Plugin.Active || !Plugin.IsPlayerInventory(__instance)) return;
            __result = Plugin.CountInContainers(name, -1, matchWorldLevel) > 0;
        }
    }

    /// <summary>
    /// Removal by name: the backpack first, chests for the rest (or the other way round depending on ChestsFirst).
    /// The Prefix lowers the amount requested from the game by what was taken elsewhere.
    /// </summary>
    [HarmonyPatch(typeof(Inventory), nameof(Inventory.RemoveItem),
        new[] { typeof(string), typeof(int), typeof(int), typeof(bool) })]
    internal static class Inventory_RemoveItem_Patch
    {
        private static void Prefix(Inventory __instance, string name, ref int amount, int itemQuality,
            bool worldLevelBased, out int __state)
        {
            __state = 0;
            if (!Plugin.Active || !Plugin.IsPlayerInventory(__instance)) return;

            if (Plugin.ChestsFirst.Value)
            {
                amount -= Plugin.RemoveFromContainers(name, amount, itemQuality, worldLevelBased);
            }
            else
            {
                // What the backpack does not cover will be taken from chests afterwards.
                __state = amount - Plugin.CountIn(__instance, name, itemQuality, worldLevelBased);
            }
        }

        private static void Postfix(Inventory __instance, string name, int itemQuality, bool worldLevelBased,
            int __state)
        {
            if (__state <= 0) return;
            if (!Plugin.Active || !Plugin.IsPlayerInventory(__instance)) return;
            Plugin.RemoveFromContainers(name, __state, itemQuality, worldLevelBased);
        }
    }

    // ----------------------------------------------------------------------------------------------------
    // Stations: cooking station, oven, smelter, windmill, spinning wheel, fermenter, fire, ballista
    // ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// These stations do not craft, they convert: they take the item directly from the backpack
    /// through their own path, without going through Player.HaveRequirements or ConsumeResources. A single
    /// multi-target patch opens the scope on each of their entry points, including the tooltip
    /// methods (CanUseItems, TryGetItems) so that the displayed prompt matches what is possible.
    /// </summary>
    [HarmonyPatch]
    internal static class Stations_Scope_Patch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(CookingStation), nameof(CookingStation.OnInteract));
            yield return AccessTools.Method(typeof(CookingStation), nameof(CookingStation.UseItem));
            yield return AccessTools.Method(typeof(CookingStation), nameof(CookingStation.OnAddFuelSwitch));
            yield return AccessTools.Method(typeof(CookingStation), nameof(CookingStation.CanUseItems));
            yield return AccessTools.Method(typeof(CookingStation), nameof(CookingStation.TryGetItems));

            yield return AccessTools.Method(typeof(Smelter), nameof(Smelter.OnAddOre));
            yield return AccessTools.Method(typeof(Smelter), nameof(Smelter.OnAddFuel));
            yield return AccessTools.Method(typeof(Smelter), nameof(Smelter.CanUseItems));
            yield return AccessTools.Method(typeof(Smelter), nameof(Smelter.TryGetItems));

            yield return AccessTools.Method(typeof(Fermenter), nameof(Fermenter.Interact));
            yield return AccessTools.Method(typeof(Fermenter), nameof(Fermenter.UseItem));

            yield return AccessTools.Method(typeof(Fireplace), nameof(Fireplace.Interact));
            yield return AccessTools.Method(typeof(Fireplace), nameof(Fireplace.UseItem));
            yield return AccessTools.Method(typeof(Fireplace), nameof(Fireplace.CanUseItems));
            yield return AccessTools.Method(typeof(Fireplace), nameof(Fireplace.TryGetItems));

            yield return AccessTools.Method(typeof(Turret), nameof(Turret.UseItem));

            yield return AccessTools.Method(typeof(ShieldGenerator), nameof(ShieldGenerator.OnAddFuel));
        }

        private static void Prefix(MonoBehaviour __instance, out bool __state)
        {
            __state = Plugin.Open(Plugin.Stations.Value && !Plugin.IsBlocked(__instance));
        }

        private static void Finalizer(bool __state)
        {
            Plugin.Close(__state);
        }
    }

    /// <summary>
    /// Cooking station and oven: the food to cook. Deliberately separate from FindIncompatibleItem, which stays limited
    /// to the backpack. Otherwise a forbidden item stored in a chest would block cooking.
    /// </summary>
    [HarmonyPatch(typeof(CookingStation), nameof(CookingStation.FindCookableItem))]
    internal static class CookingStation_FindCookableItem_Patch
    {
        private static void Postfix(CookingStation __instance, ref ItemDrop.ItemData __result)
        {
            if (__result != null || !Plugin.Active) return;
            __result = Plugin.PickFromContainers(__instance);
        }
    }

    /// <summary>Smelter, charcoal kiln, windmill, spinning wheel, eitr refinery: the raw material.</summary>
    [HarmonyPatch(typeof(Smelter), nameof(Smelter.FindCookableItem))]
    internal static class Smelter_FindCookableItem_Patch
    {
        private static void Postfix(Smelter __instance, ref ItemDrop.ItemData __result)
        {
            if (__result != null || !Plugin.Active) return;
            __result = Plugin.PickFromContainers(__instance);
        }
    }

    /// <summary>Fermenter: the mead base barrel to ferment.</summary>
    [HarmonyPatch(typeof(Fermenter), nameof(Fermenter.FindCookableItem))]
    internal static class Fermenter_FindCookableItem_Patch
    {
        private static void Postfix(Fermenter __instance, ref ItemDrop.ItemData __result)
        {
            if (__result != null || !Plugin.Active) return;
            __result = Plugin.PickFromContainers(__instance);
        }
    }

    /// <summary>
    /// Setting reminder on the hover text. Two paths exist: stations without a switch
    /// build their text themselves, the others delegate to Switch. CookingStation.GetHoverText returns
    /// an empty string when a switch exists, hence the empty text guard in AppendChoice.
    /// </summary>
    [HarmonyPatch(typeof(CookingStation), nameof(CookingStation.GetHoverText))]
    internal static class CookingStation_GetHoverText_Patch
    {
        private static void Postfix(CookingStation __instance, ref string __result)
        {
            __result = Plugin.AppendFill(__instance, Plugin.AppendChoice(__instance, __result));
        }
    }

    [HarmonyPatch(typeof(Fermenter), nameof(Fermenter.GetHoverText))]
    internal static class Fermenter_GetHoverText_Patch
    {
        private static void Postfix(Fermenter __instance, ref string __result)
        {
            __result = Plugin.AppendChoice(__instance, __result);
        }
    }

    /// <summary>Fires and torches: they build their hover text themselves.</summary>
    [HarmonyPatch(typeof(Fireplace), nameof(Fireplace.GetHoverText))]
    internal static class Fireplace_GetHoverText_Patch
    {
        private static void Postfix(Fireplace __instance, ref string __result)
        {
            __result = Plugin.AppendFill(__instance, __result);
        }
    }

    /// <summary>Add switches: those of the smelter, the oven and the fermenter.</summary>
    [HarmonyPatch(typeof(Switch), nameof(Switch.GetHoverText))]
    internal static class Switch_GetHoverText_Patch
    {
        private static void Postfix(Switch __instance, ref string __result)
        {
            Component station = Plugin.StationOfSwitch(__instance);
            if (station != null) __result = Plugin.AppendFill(station, Plugin.AppendChoice(station, __result));
        }
    }

    /// <summary>Ballista ammo: Turret.FindAmmoItem goes through here.</summary>
    [HarmonyPatch(typeof(Inventory), nameof(Inventory.GetAmmoItem))]
    internal static class Inventory_GetAmmoItem_Patch
    {
        private static void Postfix(Inventory __instance, string ammoName, string matchPrefabName,
            ref ItemDrop.ItemData __result)
        {
            if (__result != null || !Plugin.Active || !Plugin.IsPlayerInventory(__instance)) return;
            __result = Plugin.FindAmmoInContainers(ammoName, matchPrefabName);
        }
    }

    // ----------------------------------------------------------------------------------------------------
    // Removal by identity: the designated item belongs to a chest, not to the backpack
    // ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Stations remove the item they found, by reference and not by name. When that
    /// reference comes from a chest, removing it from the backpack would silently fail and the item would be duplicated:
    /// consumed by the station, still present in the chest. These three patches redirect the removal.
    /// </summary>
    [HarmonyPatch(typeof(Inventory), nameof(Inventory.RemoveOneItem))]
    internal static class Inventory_RemoveOneItem_Patch
    {
        private static bool Prefix(Inventory __instance, ItemDrop.ItemData item, ref bool __result)
        {
            if (!Plugin.Active || !Plugin.IsPlayerInventory(__instance)) return true;
            if (__instance.m_inventory.Contains(item)) return true;
            if (!Plugin.RemoveOneExact(item)) return true;

            __result = true;
            return false;
        }
    }

    [HarmonyPatch(typeof(Inventory), nameof(Inventory.RemoveItem), new[] { typeof(ItemDrop.ItemData) })]
    internal static class Inventory_RemoveItemStack_Patch
    {
        private static bool Prefix(Inventory __instance, ItemDrop.ItemData item, ref bool __result)
        {
            if (!Plugin.Active || !Plugin.IsPlayerInventory(__instance)) return true;
            if (__instance.m_inventory.Contains(item)) return true;
            if (!Plugin.RemoveStackExact(item)) return true;

            __result = true;
            return false;
        }
    }

    [HarmonyPatch(typeof(Inventory), nameof(Inventory.RemoveItem), new[] { typeof(ItemDrop.ItemData), typeof(int) })]
    internal static class Inventory_RemoveItemAmount_Patch
    {
        private static bool Prefix(Inventory __instance, ItemDrop.ItemData item, int amount, ref bool __result)
        {
            if (!Plugin.Active || !Plugin.IsPlayerInventory(__instance)) return true;
            if (__instance.m_inventory.Contains(item)) return true;
            if (!Plugin.RemoveAmountExact(item, amount)) return true;

            __result = true;
            return false;
        }
    }

    // ----------------------------------------------------------------------------------------------------
    // Fill in one press
    // ----------------------------------------------------------------------------------------------------

    /// <summary>The game repeats its message for every unit added: we swallow it and summarise at the end.</summary>
    [HarmonyPatch(typeof(Player), nameof(Player.Message))]
    internal static class Player_Message_Patch
    {
        private static bool Prefix()
        {
            return !Plugin.Filling;
        }
    }

    /// <summary>Coal and wood of smelters, charcoal kilns, blast furnaces.</summary>
    [HarmonyPatch(typeof(Smelter), nameof(Smelter.OnAddFuel))]
    internal static class Smelter_OnAddFuel_Fill_Patch
    {
        private static void Postfix(Smelter __instance, Switch sw, Humanoid user, bool __result)
        {
            if (!__result) return;
            Plugin.FillToMax(__instance.m_nview, user,
                Localization.instance.Localize(__instance.m_fuelItem.m_itemData.m_shared.m_name),
                __instance.m_maxFuel, () => __instance.OnAddFuel(sw, user, null));
        }
    }

    /// <summary>
    /// Charcoal of the shield generators. Its fuel switch removes by item name, so the scope patch above already
    /// lets the charcoal come from a chest; this only repeats the press up to the generator's capacity.
    /// </summary>
    [HarmonyPatch(typeof(ShieldGenerator), nameof(ShieldGenerator.OnAddFuel))]
    internal static class ShieldGenerator_OnAddFuel_Fill_Patch
    {
        private static void Postfix(ShieldGenerator __instance, Switch sw, Humanoid user, bool __result)
        {
            if (!__result) return;

            Plugin.FillToMax(__instance.m_nview, user, Plugin.StationName(__instance),
                __instance.m_maxFuel, () => __instance.OnAddFuel(sw, user, null));
        }
    }

    /// <summary>
    /// Ammo of the ballistas. UseItem also serves trophies, which set the targets and return true as well: the ammo
    /// count before and after tells a real load from one of those, so a trophy never starts a fill.
    /// </summary>
    [HarmonyPatch(typeof(Turret), nameof(Turret.UseItem))]
    internal static class Turret_UseItem_Fill_Patch
    {
        private static void Prefix(Turret __instance, out int __state)
        {
            __state = __instance.GetAmmo();
        }

        private static void Postfix(Turret __instance, Humanoid user, bool __result, int __state)
        {
            if (!__result || __instance.GetAmmo() <= __state) return;

            Plugin.FillToMax(__instance.m_nview, user, Plugin.StationName(__instance),
                __instance.m_maxAmmo, () => __instance.UseItem(user, null));
        }
    }

    /// <summary>Ballistas build their hover text themselves.</summary>
    [HarmonyPatch(typeof(Turret), nameof(Turret.GetHoverText))]
    internal static class Turret_GetHoverText_Patch
    {
        private static void Postfix(Turret __instance, ref string __result)
        {
            __result = Plugin.AppendFill(__instance, __result);
        }
    }

    /// <summary>Ore of smelters, barley of windmills, flax of spinning wheels.</summary>
    [HarmonyPatch(typeof(Smelter), nameof(Smelter.OnAddOre))]
    internal static class Smelter_OnAddOre_Fill_Patch
    {
        private static void Postfix(Smelter __instance, Switch sw, Humanoid user, bool __result)
        {
            if (!__result) return;
            Plugin.FillToMax(__instance.m_nview, user, Plugin.StationName(__instance),
                __instance.m_maxOre, () => __instance.OnAddOre(sw, user, null));
        }
    }

    /// <summary>Fuel of cooking stations and ovens.</summary>
    [HarmonyPatch(typeof(CookingStation), nameof(CookingStation.OnAddFuelSwitch))]
    internal static class CookingStation_OnAddFuel_Fill_Patch
    {
        private static void Postfix(CookingStation __instance, Switch sw, Humanoid user, bool __result)
        {
            if (!__result) return;
            Plugin.FillToMax(__instance.m_nview, user,
                Localization.instance.Localize(__instance.m_fuelItem.m_itemData.m_shared.m_name),
                __instance.m_maxFuel, () => __instance.OnAddFuelSwitch(sw, user, null));
        }
    }

    /// <summary>
    /// Wood of fires and torches. Interact is also used to light and put out: the Prefix detects that
    /// case so as not to mistake it for an addition. The repetition sets alt to true, which is
    /// exactly the condition that skips the lit/unlit toggle.
    /// </summary>
    [HarmonyPatch(typeof(Fireplace), nameof(Fireplace.Interact))]
    internal static class Fireplace_Interact_Fill_Patch
    {
        private static void Prefix(Fireplace __instance, bool hold, bool alt, out bool __state)
        {
            float fuel = __instance.m_nview != null && __instance.m_nview.IsValid()
                ? __instance.m_nview.GetZDO().GetFloat(ZDOVars.s_fuel)
                : 0f;
            __state = __instance.m_canTurnOff && !hold && !alt && fuel > 0f;
        }

        private static void Postfix(Fireplace __instance, Humanoid user, bool __result, bool __state)
        {
            if (!__result || __state) return;
            Plugin.FillToMax(__instance.m_nview, user,
                Localization.instance.Localize(__instance.m_fuelItem.m_itemData.m_shared.m_name),
                Mathf.CeilToInt(__instance.m_maxFuel), () => __instance.Interact(user, hold: false, alt: true));
        }
    }

    // ----------------------------------------------------------------------------------------------------
    // Crafting panel: craft amount picker
    // ----------------------------------------------------------------------------------------------------

    [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.Awake))]
    internal static class InventoryGui_Awake_AmountPicker_Patch
    {
        private static void Postfix(InventoryGui __instance)
        {
            if (!Plugin.Enabled.Value || !Plugin.AmountPicker.Value) return;

            try
            {
                Plugin.CreateAmountPicker(__instance);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Craft amount picker not created: {e.Message}");
            }
        }
    }

    /// <summary>The prefix sets the amount before the game reads it this frame; the postfix mirrors the button.</summary>
    [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.UpdateRecipe))]
    internal static class InventoryGui_UpdateRecipe_AmountPicker_Patch
    {
        private static void Prefix(InventoryGui __instance, Player player)
        {
            Plugin.ApplyCraftAmount(__instance, player);
        }

        private static void Postfix(InventoryGui __instance)
        {
            Plugin.SyncPickerVisibility(__instance);
        }
    }

    /// <summary>OnCraftPressed decides m_multiCrafting for the whole craft: the amount must be in place before it.</summary>
    [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.OnCraftPressed))]
    internal static class InventoryGui_OnCraftPressed_AmountPicker_Patch
    {
        private static void Prefix(InventoryGui __instance)
        {
            Plugin.ApplyCraftAmount(__instance, Player.m_localPlayer);
        }
    }

    /// <summary>
    /// The open list lives on its own canvas and would stay on screen after the inventory closes. Hide() runs every
    /// frame while no player is spawned, hence the IsExpanded guard inside CloseAmountList.
    /// </summary>
    [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.Hide))]
    internal static class InventoryGui_Hide_AmountPicker_Patch
    {
        private static void Postfix()
        {
            Plugin.CloseAmountList();
        }
    }

    // ----------------------------------------------------------------------------------------------------
    // Console
    // ----------------------------------------------------------------------------------------------------

    /// <summary>Console commands chestcraft_list and chestcraft_reload.</summary>
    [HarmonyPatch(typeof(Terminal), nameof(Terminal.InitTerminal))]
    internal static class Terminal_InitTerminal_Patch
    {
        private static bool _registered;

        private static void Postfix()
        {
            if (_registered) return;
            _registered = true;

            new Terminal.ConsoleCommand("chestcraft_list",
                "Lists the chests taken into account around the player, with their distance and their owner.",
                delegate (Terminal.ConsoleEventArgs args)
                {
                    string text = Plugin.DescribeNearby();
                    Plugin.Log.LogInfo("chestcraft_list:\n" + text);
                    foreach (string line in text.Split('\n'))
                    {
                        if (line.Trim().Length > 0) args.Context.AddString(line);
                    }
                });

            new Terminal.ConsoleCommand("chestcraft_reload",
                "Reloads the ChestCraft config from the file.",
                delegate (Terminal.ConsoleEventArgs args)
                {
                    Plugin.Instance.Config.Reload();
                    Plugin.Invalidate();
                    args.Context.AddString("ChestCraft config reloaded.");
                });
        }
    }
}
