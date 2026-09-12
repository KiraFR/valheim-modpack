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
    /// Augmente la taille de stack des objets empilables.
    ///
    /// - La taille de stack vit dans ItemDrop.ItemData.SharedData.m_maxStackSize, partagé par toutes les
    ///   instances d'un même prefab : modifier le prefab de l'ObjectDB suffit pour tout le jeu.
    /// - Règle par type d'objet (section [Types]) : multiplicateur ou valeur fixe selon Mode.
    /// - Surcharges par nom de prefab (section [Objets]) : toujours des valeurs fixes, prioritaires.
    /// - Seuls les objets empilables en vanilla (stack > 1) sont touchés : l'équipement reste à 1.
    /// - Commandes console : stackmax_list (liste les objets empilables), stackmax_reload (recharge la config).
    /// - À chaque chargement de l'ObjectDB, écrit BepInEx/config/valheim.stackmax.objets.txt : tous les types
    ///   d'objets du jeu et tous les objets empilables avec leur stack vanilla et actuel.
    ///
    /// Multijoueur : tous les joueurs doivent avoir le mod avec la même config, sinon un client vanilla
    /// tronque les stacks au-dessus de sa propre valeur au ramassage.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "valheim.stackmax";
        public const string PluginName = "StackMax";
        public const string PluginVersion = "1.1.0";

        public enum StackMode
        {
            Multiplicateur,
            ValeurFixe,
        }

        internal static ManualLogSource Log;
        internal static Plugin Instance;

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<StackMode> Mode;
        internal static ConfigEntry<int> Plafond;
        internal static ConfigEntry<bool> JamaisSousVanilla;
        internal static ConfigEntry<string> Overrides;

        /// <summary>
        /// Types d'objets empilables en vanilla, une entrée de config chacun, avec la valeur par défaut
        /// (stack fixe de 500) et des exemples d'objets. Les stacks vanilla exacts sont dans le fichier de référence.
        /// </summary>
        internal static readonly TypeInfo[] StackableTypes =
        {
            new TypeInfo(ItemDrop.ItemData.ItemType.Material, 500,
                "Matériaux (295 objets, vanilla 3 à 999) : Wood, Stone, Resin, Flint, Coal, CopperOre, IronScrap, " +
                "DeerHide, LinenThread, viandes crues, graines, Amber, Coins et AncientCoin (999), FishRaw..."),
            new TypeInfo(ItemDrop.ItemData.ItemType.Consumable, 500,
                "Consommables (112 objets, vanilla 10 à 50) : nourriture cuite (CookedMeat, Bread, LoxPie...), " +
                "baies et champignons, Honey, hydromels et potions (Mead*)."),
            new TypeInfo(ItemDrop.ItemData.ItemType.Ammo, 500,
                "Munitions équipables (29 objets, vanilla 20 à 100) : flèches (Arrow*), carreaux (Bolt*), appâts (FishingBait*)."),
            new TypeInfo(ItemDrop.ItemData.ItemType.AmmoNonEquipable, 500,
                "Munitions non équipables (5 objets, vanilla 100) : carreaux de tourelle (TurretBolt*)."),
            new TypeInfo(ItemDrop.ItemData.ItemType.OneHandedWeapon, 500,
                "Armes à une main empilables (15 objets, vanilla 10 à 50) : bombes (BombOoze, BombBile, BombDynamite, " +
                "BombBlob_*), projectiles (Snowball, SnowballBig), lances jetables (GoblinSpear*). " +
                "Les vraies armes (stack 1) ne sont jamais touchées."),
            new TypeInfo(ItemDrop.ItemData.ItemType.Trophy, 500,
                "Trophées (72 objets, vanilla 20) : TrophyBoar, TrophyDeer, TrophyGreydwarf..."),
            new TypeInfo(ItemDrop.ItemData.ItemType.Fish, 500,
                "Poissons vivants (12 objets, vanilla 10) : Fish1 à Fish12. Le poisson cru FishRaw est un Material."),
            new TypeInfo(ItemDrop.ItemData.ItemType.Misc, 500,
                "Divers (13 objets, vanilla 9 à 50) : œufs (ChickenEgg, AsksvinEgg, VoltureEgg), AncientSeed, DragonTear, " +
                "GoblinTotem, YagluthDrop, BarleyFlour, OatFlour, HatefulBlood, Bell..."),
        };

        /// <summary>Types jamais empilables en vanilla (stack 1) : listés dans le fichier de référence, ignorés par le mod.</summary>
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

        /// <summary>Stack vanilla par nom de prefab, mémorisé à la première application.</summary>
        internal static readonly Dictionary<string, int> VanillaStacks = new Dictionary<string, int>();

        private Harmony _harmony;
        private bool _applying;

        private void Awake()
        {
            Log = Logger;
            Instance = this;

            Enabled = Config.Bind("General", "Enabled", true, "Active ou désactive le mod.");

            Mode = Config.Bind("General", "Mode", StackMode.ValeurFixe,
                "Interprétation des valeurs de la section [Types]. " +
                "ValeurFixe : la valeur est le stack max directement (Material = 500 : bois -> 500). " +
                "Multiplicateur : stack vanilla x valeur (Material = 2 : bois 50 -> 100).");

            JamaisSousVanilla = Config.Bind("General", "JamaisSousVanilla", true,
                "Ne descend jamais sous le stack vanilla d'un objet (ex : pièces 999 restent à 999 même avec Misc = 500). " +
                "Évite de tronquer les piles existantes au chargement.");

            Plafond = Config.Bind("General", "Plafond", 0,
                new ConfigDescription("Stack max absolu après application des règles (0 = aucun plafond).",
                    new AcceptableValueRange<int>(0, 100000)));

            foreach (TypeInfo info in StackableTypes)
            {
                TypeValues[info.Type] = Config.Bind("Types", info.Type.ToString(), info.DefaultValue,
                    new ConfigDescription(
                        $"{info.Examples} " +
                        "Valeur fixe ou multiplicateur selon General.Mode. 0 = ne pas toucher. " +
                        "Liste complète des objets et stacks vanilla dans valheim.stackmax.objets.txt (même dossier).",
                        new AcceptableValueRange<int>(0, 100000)));
            }

            Overrides = Config.Bind("Objets", "Overrides", "",
                "Surcharges par nom de prefab, toujours en valeur fixe et prioritaires sur [Types]. " +
                "Format : Nom:valeur séparés par des virgules, ex : Coins:9999, Wood:1000, Resin:200. " +
                "Noms de prefab : voir valheim.stackmax.objets.txt (même dossier) ou la commande console stackmax_list.");

            Config.SettingChanged += OnSettingChanged;

            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll(typeof(Plugin).Assembly);

            Log.LogInfo($"{PluginName} {PluginVersion} chargé.");
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

        /// <summary>Parse "Nom:valeur, Nom:valeur" en dictionnaire, avec log des entrées invalides.</summary>
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
                    Log.LogWarning($"Surcharge ignorée, format attendu Nom:valeur : \"{trimmed}\"");
                    continue;
                }
                result[parts[0].Trim()] = value;
            }
            return result;
        }

        /// <summary>Calcule le stack voulu pour un objet, ou -1 si aucune règle ne s'applique.</summary>
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
                result = Mode.Value == StackMode.Multiplicateur
                    ? (int)Math.Min((long)vanilla * entry.Value, int.MaxValue)
                    : entry.Value;
            }
            else
            {
                return -1;
            }

            if (Plafond.Value > 0) result = Math.Min(result, Plafond.Value);
            if (JamaisSousVanilla.Value) result = Math.Max(result, vanilla);
            return Math.Max(result, 1);
        }

        /// <summary>Applique la config à tous les prefabs empilables de l'ObjectDB.</summary>
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

                    // L'équipement (stack 1) garde durabilité et qualité par instance : on n'y touche pas.
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
                        Log.LogWarning($"Surcharge \"{key}\" : aucun objet empilable de ce nom dans l'ObjectDB.");
                }

                int stackable = VanillaStacks.Count(kv => kv.Value > 1);
                Log.LogInfo($"Stacks appliqués : {changed} objet(s) modifié(s) sur {stackable} empilable(s), mode {Mode.Value}.");
                WriteReferenceFile(db);
            }
            finally
            {
                _applying = false;
            }
        }

        internal static string ReferencePath => Path.Combine(Paths.ConfigPath, PluginGuid + ".objets.txt");

        /// <summary>
        /// Écrit le fichier de référence à côté de la config : tous les types d'objets du jeu, puis tous les
        /// objets empilables groupés par type avec leur stack vanilla et actuel.
        /// </summary>
        internal static void WriteReferenceFile(ObjectDB db)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine($"# {PluginName} {PluginVersion} : fichier de référence, régénéré à chaque chargement du jeu.");
                sb.AppendLine("# Les noms de la colonne Prefab sont ceux à utiliser dans [Objets] Overrides.");
                sb.AppendLine();
                sb.AppendLine("## Types d'objets du jeu (ItemDrop.ItemData.ItemType)");
                sb.AppendLine();
                sb.AppendLine("Empilables, une entrée chacun dans la section [Types] de la config :");
                foreach (TypeInfo info in StackableTypes)
                    sb.AppendLine($"  {info.Type,-18} défaut {info.DefaultValue,-4} {info.Examples}");
                sb.AppendLine();
                sb.AppendLine("Jamais empilables en vanilla (équipement, stack 1), ignorés par le mod :");
                sb.AppendLine("  " + string.Join(", ", EquipmentTypes.Select(t => t.ToString())));
                sb.AppendLine();
                sb.AppendLine("## Objets empilables");
                sb.AppendLine();
                sb.AppendLine($"{"Prefab",-28} {"Nom localisé",-28} {"Type",-18} {"Vanilla",7} {"Actuel",7}");

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
                            : $"[{current}]  (aucune entrée dans [Types] : seul [Objets] Overrides s'applique)");
                    }
                    sb.AppendLine($"{row.Item1,-28} {row.Item2,-28} {row.Item3,-18} {row.Item4,7} {row.Item5,7}");
                }

                sb.AppendLine();
                sb.AppendLine($"{rows.Count} objet(s) empilable(s).");
                File.WriteAllText(ReferencePath, sb.ToString());
                Log.LogInfo($"Fichier de référence écrit : {ReferencePath}");
            }
            catch (Exception e)
            {
                Log.LogWarning($"Impossible d'écrire le fichier de référence : {e.Message}");
            }
        }

        /// <summary>Liste des objets empilables : nom, type, stack vanilla, stack actuel.</summary>
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
                sb.AppendLine($"{row.Item1,-28} {row.Item2,-18} vanilla {row.Item3,5}  actuel {row.Item4,6}");

            sb.AppendLine($"{rows.Count} objet(s) empilable(s).");
            return sb.ToString();
        }
    }

    /// <summary>ObjectDB du menu principal : premier chargement des prefabs.</summary>
    [HarmonyPatch(typeof(ObjectDB), nameof(ObjectDB.Awake))]
    internal static class ObjectDB_Awake_Patch
    {
        private static void Postfix(ObjectDB __instance)
        {
            Plugin.Instance.ApplyStacks(__instance);
        }
    }

    /// <summary>ObjectDB de la partie : copie de celui du menu, à réappliquer.</summary>
    [HarmonyPatch(typeof(ObjectDB), nameof(ObjectDB.CopyOtherDB))]
    internal static class ObjectDB_CopyOtherDB_Patch
    {
        private static void Postfix(ObjectDB __instance)
        {
            Plugin.Instance.ApplyStacks(__instance);
        }
    }

    /// <summary>Commandes console stackmax_list et stackmax_reload.</summary>
    [HarmonyPatch(typeof(Terminal), nameof(Terminal.InitTerminal))]
    internal static class Terminal_InitTerminal_Patch
    {
        private static bool _registered;

        private static void Postfix()
        {
            if (_registered) return;
            _registered = true;

            new Terminal.ConsoleCommand("stackmax_list",
                "Liste les objets empilables avec leur type, leur stack vanilla et leur stack actuel (aussi écrit dans LogOutput.log).",
                delegate (Terminal.ConsoleEventArgs args)
                {
                    if (ObjectDB.instance == null)
                    {
                        args.Context.AddString("ObjectDB non chargé.");
                        return;
                    }
                    string text = Plugin.DescribeStackables(ObjectDB.instance);
                    Plugin.Log.LogInfo("stackmax_list :\n" + text);
                    foreach (string line in text.Split('\n'))
                    {
                        if (line.Trim().Length > 0) args.Context.AddString(line);
                    }
                });

            new Terminal.ConsoleCommand("stackmax_reload",
                "Recharge la config StackMax depuis le fichier et réapplique les stacks.",
                delegate (Terminal.ConsoleEventArgs args)
                {
                    Plugin.Instance.Config.Reload();
                    if (ObjectDB.instance != null) Plugin.Instance.ApplyStacks(ObjectDB.instance);
                    args.Context.AddString("Config StackMax rechargée.");
                });
        }
    }
}
