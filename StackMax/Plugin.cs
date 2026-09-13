using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace StackMax
{
    /// <summary>
    /// Increases the stack size of stackable items.
    ///
    /// - The stack size lives in ItemDrop.ItemData.SharedData.m_maxStackSize, shared by every instance of the same
    ///   prefab: changing the ObjectDB prefab is enough for the whole game.
    /// - Rule per item type ([Types] section): multiplier or fixed value depending on Mode.
    /// - Overrides per prefab name ([Items] section): always fixed values, and they take priority.
    /// - Only items stackable in vanilla (stack > 1) are touched: equipment stays at 1.
    /// - Console commands: stackmax_list (lists stackable items), stackmax_reload (reloads the config).
    /// - Every time the ObjectDB loads, writes BepInEx/config/valheim.stackmax.items.txt: every item type in the
    ///   game and every stackable item with its vanilla and current stack.
    ///
    /// Multiplayer: every player must have the mod with the same config, otherwise a vanilla client
    /// truncates stacks above its own value on pickup.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "valheim.stackmax";
        public const string PluginName = "StackMax";
        public const string PluginVersion = "1.1.1";

        public enum StackMode
        {
            Multiplier,
            FixedValue,
        }

        internal static ManualLogSource Log;
        internal static Plugin Instance;

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<StackMode> Mode;
        internal static ConfigEntry<int> Cap;
        internal static ConfigEntry<bool> NeverBelowVanilla;
        internal static ConfigEntry<string> Overrides;

        /// <summary>
        /// Item types stackable in vanilla, one config entry each, with the default value
        /// (fixed stack of 500) and example items. The exact vanilla stacks are in the reference file.
        /// </summary>
        internal static readonly TypeInfo[] StackableTypes =
        {
            new TypeInfo(ItemDrop.ItemData.ItemType.Material, 500,
                "Materials (295 items, vanilla 3 to 999): Wood, Stone, Resin, Flint, Coal, CopperOre, IronScrap, " +
                "DeerHide, LinenThread, raw meats, seeds, Amber, Coins and AncientCoin (999), FishRaw..."),
            new TypeInfo(ItemDrop.ItemData.ItemType.Consumable, 500,
                "Consumables (112 items, vanilla 10 to 50): cooked food (CookedMeat, Bread, LoxPie...), " +
                "berries and mushrooms, Honey, meads and potions (Mead*)."),
            new TypeInfo(ItemDrop.ItemData.ItemType.Ammo, 500,
                "Equippable ammo (29 items, vanilla 20 to 100): arrows (Arrow*), bolts (Bolt*), baits (FishingBait*)."),
            new TypeInfo(ItemDrop.ItemData.ItemType.AmmoNonEquipable, 500,
                "Non-equippable ammo (5 items, vanilla 100): turret bolts (TurretBolt*)."),
            new TypeInfo(ItemDrop.ItemData.ItemType.OneHandedWeapon, 500,
                "Stackable one-handed weapons (15 items, vanilla 10 to 50): bombs (BombOoze, BombBile, BombDynamite, " +
                "BombBlob_*), projectiles (Snowball, SnowballBig), throwing spears (GoblinSpear*). " +
                "Real weapons (stack 1) are never touched."),
            new TypeInfo(ItemDrop.ItemData.ItemType.Trophy, 500,
                "Trophies (72 items, vanilla 20): TrophyBoar, TrophyDeer, TrophyGreydwarf..."),
            new TypeInfo(ItemDrop.ItemData.ItemType.Fish, 500,
                "Live fish (12 items, vanilla 10): Fish1 to Fish12. Raw fish FishRaw is a Material."),
            new TypeInfo(ItemDrop.ItemData.ItemType.Misc, 500,
                "Miscellaneous (13 items, vanilla 9 to 50): eggs (ChickenEgg, AsksvinEgg, VoltureEgg), AncientSeed, DragonTear, " +
                "GoblinTotem, YagluthDrop, BarleyFlour, OatFlour, HatefulBlood, Bell..."),
        };

        /// <summary>Types never stackable in vanilla (stack 1): listed in the reference file, ignored by the mod.</summary>
        internal static readonly ItemDrop.ItemData.ItemType[] EquipmentTypes =
        {
            ItemDrop.ItemData.ItemType.TwoHandedWeapon,
            ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft,
            ItemDrop.ItemData.ItemType.Bow,
            ItemDrop.ItemData.ItemType.Shield,
            ItemDrop.ItemData.ItemType.Helmet,
            ItemDrop.ItemData.ItemType.Chest,
            ItemDrop.ItemData.ItemType.Legs,
            ItemDrop.ItemData.ItemType.Shoulder,
            ItemDrop.ItemData.ItemType.Hands,
            ItemDrop.ItemData.ItemType.Torch,
            ItemDrop.ItemData.ItemType.Tool,
            ItemDrop.ItemData.ItemType.Utility,
            ItemDrop.ItemData.ItemType.Trinket,
            ItemDrop.ItemData.ItemType.Customization,
            ItemDrop.ItemData.ItemType.Attach_Atgeir,
            ItemDrop.ItemData.ItemType.None,
        };

        internal sealed class TypeInfo
        {
            public readonly ItemDrop.ItemData.ItemType Type;
            public readonly int DefaultValue;
            public readonly string Examples;

            public TypeInfo(ItemDrop.ItemData.ItemType type, int defaultValue, string examples)
            {
                Type = type;
                DefaultValue = defaultValue;
                Examples = examples;
            }
        }

        internal static readonly Dictionary<ItemDrop.ItemData.ItemType, ConfigEntry<int>> TypeValues =
            new Dictionary<ItemDrop.ItemData.ItemType, ConfigEntry<int>>();

        /// <summary>Vanilla stack per prefab name, remembered on the first application.</summary>
        internal static readonly Dictionary<string, int> VanillaStacks = new Dictionary<string, int>();

        private Harmony _harmony;
        private bool _applying;

        private void Awake()
        {
            Log = Logger;
            Instance = this;

            Enabled = Config.Bind("General", "Enabled", true, "Enables or disables the mod.");

            MigrateModeValue();
            Mode = Config.Bind("General", "Mode", StackMode.FixedValue,
                "How the values of the [Types] section are interpreted. " +
                "FixedValue: the value is the max stack directly (Material = 500: wood -> 500). " +
                "Multiplier: vanilla stack x value (Material = 2: wood 50 -> 100).");

            NeverBelowVanilla = Config.Bind("General", "NeverBelowVanilla", true,
                "Never goes below an item's vanilla stack (e.g. coins at 999 stay at 999 even with Misc = 500). " +
                "Avoids truncating existing stacks on load.");
            MigrateKey(NeverBelowVanilla, "General", "JamaisSousVanilla");

            Cap = Config.Bind("General", "Cap", 0,
                new ConfigDescription("Absolute max stack after the rules are applied (0 = no cap).",
                    new AcceptableValueRange<int>(0, 100000)));
            MigrateKey(Cap, "General", "Plafond");

            foreach (TypeInfo info in StackableTypes)
            {
                TypeValues[info.Type] = Config.Bind("Types", info.Type.ToString(), info.DefaultValue,
                    new ConfigDescription(
                        $"{info.Examples} " +
                        "Fixed value or multiplier depending on General.Mode. 0 = leave untouched. " +
                        "Full list of items and vanilla stacks in valheim.stackmax.items.txt (same folder).",
                        new AcceptableValueRange<int>(0, 100000)));
            }

            Overrides = Config.Bind("Items", "Overrides", "",
                "Overrides per prefab name, always as fixed values and taking priority over [Types]. " +
                "Format: Name:value separated by commas, e.g. Coins:9999, Wood:1000, Resin:200. " +
                "Prefab names: see valheim.stackmax.items.txt (same folder) or the stackmax_list console command.");
            MigrateKey(Overrides, "Objets", "Overrides");

            Config.SettingChanged += OnSettingChanged;

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

        /// <summary>
        /// Mode values were French before the mod was translated to English (Multiplicateur, ValeurFixe). Must run before
        /// Mode is bound: rewrites the old value still waiting in the .cfg orphans to its English name, otherwise Bind
        /// fails to parse it and silently falls back to the default.
        /// </summary>
        private void MigrateModeValue()
        {
            var definition = new ConfigDefinition("General", "Mode");
            if (!Config.OrphanedEntries.TryGetValue(definition, out string value)) return;

            string mapped;
            switch (value.Trim().ToLowerInvariant())
            {
                case "multiplicateur": mapped = nameof(StackMode.Multiplier); break;
                case "valeurfixe": mapped = nameof(StackMode.FixedValue); break;
                default: return;
            }

            Config.OrphanedEntries[definition] = mapped;
            Log.LogInfo($"Config value [General] Mode = {value.Trim()} migrated to {mapped}.");
        }

        private void OnDestroy()
        {
            Config.SettingChanged -= OnSettingChanged;
            _harmony?.UnpatchSelf();
        }

        private void OnSettingChanged(object sender, SettingChangedEventArgs e)
        {
            if (_applying) return;
            if (ObjectDB.instance != null) ApplyStacks(ObjectDB.instance);
        }

        /// <summary>Parses "Name:value, Name:value" into a dictionary, logging invalid entries.</summary>
        internal static Dictionary<string, int> ParseOverrides(string raw)
        {
            var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(raw)) return result;

            foreach (string entry in raw.Split(','))
            {
                string trimmed = entry.Trim();
                if (trimmed.Length == 0) continue;

                string[] parts = trimmed.Split(':');
                if (parts.Length != 2 || !int.TryParse(parts[1].Trim(), out int value) || value < 1)
                {
                    Log.LogWarning($"Override ignored, expected format Name:value: \"{trimmed}\"");
                    continue;
                }
                result[parts[0].Trim()] = value;
            }
            return result;
        }

        /// <summary>Computes the desired stack for an item, or -1 if no rule applies.</summary>
        internal static int ComputeStack(string prefabName, ItemDrop.ItemData.ItemType type, int vanilla,
            Dictionary<string, int> overrides)
        {
            int result;
            if (overrides.TryGetValue(prefabName, out int forced))
            {
                result = forced;
            }
            else if (TypeValues.TryGetValue(type, out ConfigEntry<int> entry) && entry.Value > 0)
            {
                result = Mode.Value == StackMode.Multiplier
                    ? (int)Math.Min((long)vanilla * entry.Value, int.MaxValue)
                    : entry.Value;
            }
            else
            {
                return -1;
            }

            if (Cap.Value > 0) result = Math.Min(result, Cap.Value);
            if (NeverBelowVanilla.Value) result = Math.Max(result, vanilla);
            return Math.Max(result, 1);
        }

        /// <summary>Applies the config to every stackable prefab of the ObjectDB.</summary>
        internal void ApplyStacks(ObjectDB db)
        {
            _applying = true;
            try
            {
                Dictionary<string, int> overrides = ParseOverrides(Overrides.Value);
                var usedOverrides = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int changed = 0;

                foreach (GameObject prefab in db.m_items)
                {
                    ItemDrop drop = prefab.GetComponent<ItemDrop>();
                    if (drop == null || drop.m_itemData == null) continue;

                    ItemDrop.ItemData.SharedData shared = drop.m_itemData.m_shared;
                    string name = prefab.name;

                    if (!VanillaStacks.TryGetValue(name, out int vanilla))
                    {
                        vanilla = shared.m_maxStackSize;
                        VanillaStacks[name] = vanilla;
                    }

                    // Equipment (stack 1) keeps durability and quality per instance: leave it alone.
                    if (vanilla <= 1) continue;

                    int target = Enabled.Value ? ComputeStack(name, shared.m_itemType, vanilla, overrides) : -1;
                    if (target < 0) target = vanilla;
                    if (overrides.ContainsKey(name)) usedOverrides.Add(name);

                    if (shared.m_maxStackSize != target)
                    {
                        shared.m_maxStackSize = target;
                        changed++;
                    }
                }

                foreach (string key in overrides.Keys)
                {
                    if (!usedOverrides.Contains(key))
                        Log.LogWarning($"Override \"{key}\": no stackable item with that name in the ObjectDB.");
                }

                int stackable = VanillaStacks.Count(kv => kv.Value > 1);
                Log.LogInfo($"Stacks applied: {changed} item(s) changed out of {stackable} stackable, mode {Mode.Value}.");
                WriteReferenceFile(db);
            }
            finally
            {
                _applying = false;
            }
        }

        internal static string ReferencePath => Path.Combine(Paths.ConfigPath, PluginGuid + ".items.txt");

        /// <summary>Reference file written under its French name before the translation, deleted so only the current one remains.</summary>
        internal static string LegacyReferencePath => Path.Combine(Paths.ConfigPath, PluginGuid + ".objets.txt");

        /// <summary>
        /// Writes the reference file next to the config: every item type in the game, then every
        /// stackable item grouped by type with its vanilla and current stack.
        /// </summary>
        internal static void WriteReferenceFile(ObjectDB db)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine($"# {PluginName} {PluginVersion}: reference file, regenerated every time the game loads.");
                sb.AppendLine("# The names in the Prefab column are the ones to use in [Items] Overrides.");
                sb.AppendLine();
                sb.AppendLine("## Item types in the game (ItemDrop.ItemData.ItemType)");
                sb.AppendLine();
                sb.AppendLine("Stackable, one entry each in the [Types] section of the config:");
                foreach (TypeInfo info in StackableTypes)
                    sb.AppendLine($"  {info.Type,-18} default {info.DefaultValue,-4} {info.Examples}");
                sb.AppendLine();
                sb.AppendLine("Never stackable in vanilla (equipment, stack 1), ignored by the mod:");
                sb.AppendLine("  " + string.Join(", ", EquipmentTypes.Select(t => t.ToString())));
                sb.AppendLine();
                sb.AppendLine("## Stackable items");
                sb.AppendLine();
                sb.AppendLine($"{"Prefab",-28} {"Localized name",-28} {"Type",-18} {"Vanilla",7} {"Current",7}");

                var rows = new List<Tuple<string, string, ItemDrop.ItemData.ItemType, int, int>>();
                foreach (GameObject prefab in db.m_items)
                {
                    ItemDrop drop = prefab.GetComponent<ItemDrop>();
                    if (drop == null || drop.m_itemData == null) continue;
                    ItemDrop.ItemData.SharedData shared = drop.m_itemData.m_shared;
                    if (!VanillaStacks.TryGetValue(prefab.name, out int vanilla)) vanilla = shared.m_maxStackSize;
                    if (vanilla <= 1) continue;
                    rows.Add(Tuple.Create(prefab.name, shared.m_name, shared.m_itemType, vanilla, shared.m_maxStackSize));
                }

                var configuredTypes = new HashSet<ItemDrop.ItemData.ItemType>(StackableTypes.Select(t => t.Type));
                ItemDrop.ItemData.ItemType? current = null;
                foreach (var row in rows.OrderBy(r => r.Item3.ToString()).ThenBy(r => r.Item1))
                {
                    if (current != row.Item3)
                    {
                        current = row.Item3;
                        sb.AppendLine();
                        sb.AppendLine(configuredTypes.Contains(row.Item3)
                            ? $"[{current}]"
                            : $"[{current}]  (no entry in [Types]: only [Items] Overrides applies)");
                    }
                    sb.AppendLine($"{row.Item1,-28} {row.Item2,-28} {row.Item3,-18} {row.Item4,7} {row.Item5,7}");
                }

                sb.AppendLine();
                sb.AppendLine($"{rows.Count} stackable item(s).");
                File.WriteAllText(ReferencePath, sb.ToString());
                if (File.Exists(LegacyReferencePath)) File.Delete(LegacyReferencePath);
                Log.LogInfo($"Reference file written: {ReferencePath}");
            }
            catch (Exception e)
            {
                Log.LogWarning($"Could not write the reference file: {e.Message}");
            }
        }

        /// <summary>List of stackable items: name, type, vanilla stack, current stack.</summary>
        internal static string DescribeStackables(ObjectDB db)
        {
            var sb = new StringBuilder();
            var rows = new List<Tuple<string, ItemDrop.ItemData.ItemType, int, int>>();

            foreach (GameObject prefab in db.m_items)
            {
                ItemDrop drop = prefab.GetComponent<ItemDrop>();
                if (drop == null || drop.m_itemData == null) continue;
                if (!VanillaStacks.TryGetValue(prefab.name, out int vanilla)) vanilla = drop.m_itemData.m_shared.m_maxStackSize;
                if (vanilla <= 1) continue;
                rows.Add(Tuple.Create(prefab.name, drop.m_itemData.m_shared.m_itemType, vanilla, drop.m_itemData.m_shared.m_maxStackSize));
            }

            foreach (var row in rows.OrderBy(r => r.Item2.ToString()).ThenBy(r => r.Item1))
                sb.AppendLine($"{row.Item1,-28} {row.Item2,-18} vanilla {row.Item3,5}  current {row.Item4,6}");

            sb.AppendLine($"{rows.Count} stackable item(s).");
            return sb.ToString();
        }
    }

    /// <summary>Main menu ObjectDB: first load of the prefabs.</summary>
    [HarmonyPatch(typeof(ObjectDB), nameof(ObjectDB.Awake))]
    internal static class ObjectDB_Awake_Patch
    {
        private static void Postfix(ObjectDB __instance)
        {
            Plugin.Instance.ApplyStacks(__instance);
        }
    }

    /// <summary>In-game ObjectDB: a copy of the menu one, to apply again.</summary>
    [HarmonyPatch(typeof(ObjectDB), nameof(ObjectDB.CopyOtherDB))]
    internal static class ObjectDB_CopyOtherDB_Patch
    {
        private static void Postfix(ObjectDB __instance)
        {
            Plugin.Instance.ApplyStacks(__instance);
        }
    }

    /// <summary>Console commands stackmax_list and stackmax_reload.</summary>
    [HarmonyPatch(typeof(Terminal), nameof(Terminal.InitTerminal))]
    internal static class Terminal_InitTerminal_Patch
    {
        private static bool _registered;

        private static void Postfix()
        {
            if (_registered) return;
            _registered = true;

            new Terminal.ConsoleCommand("stackmax_list",
                "Lists stackable items with their type, vanilla stack and current stack (also written to LogOutput.log).",
                delegate (Terminal.ConsoleEventArgs args)
                {
                    if (ObjectDB.instance == null)
                    {
                        args.Context.AddString("ObjectDB not loaded.");
                        return;
                    }
                    string text = Plugin.DescribeStackables(ObjectDB.instance);
                    Plugin.Log.LogInfo("stackmax_list:\n" + text);
                    foreach (string line in text.Split('\n'))
                    {
                        if (line.Trim().Length > 0) args.Context.AddString(line);
                    }
                });

            new Terminal.ConsoleCommand("stackmax_reload",
                "Reloads the StackMax config from the file and applies the stacks again.",
                delegate (Terminal.ConsoleEventArgs args)
                {
                    Plugin.Instance.Config.Reload();
                    if (ObjectDB.instance != null) Plugin.Instance.ApplyStacks(ObjectDB.instance);
                    args.Context.AddString("StackMax config reloaded.");
                });
        }
    }
}
