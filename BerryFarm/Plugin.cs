using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace BerryFarm
{
    /// <summary>
    /// Adds cultivator saplings that grow into the game's own berry bushes (raspberry, blueberry, cloudberry, lingonberry).
    ///
    /// - Each sapling is a runtime copy of the vanilla carrot sapling (sapling_carrot: Plant, Destructible, Piece, fern
    ///   mesh), whose Plant.m_grownPrefabs points to the vanilla bush. The copy is instantiated under an inactive
    ///   holder, so its ZNetView.Awake never runs and no stray ZDO is created, then added to ZNetScene.m_prefabs
    ///   before ZNetScene.Awake indexes that list by name hash.
    /// - The grown bush is the vanilla prefab (RaspberryBush...): it respawns its berries like a wild one, can be
    ///   chopped down (it has a Destructible), and stays in the world, visible to everyone, if the mod is removed.
    /// - The copies are built once and kept across world loads (DontDestroyOnLoad holder); the config is applied again
    ///   on each world load. The cultivator's PieceTable is a shared prefab asset, so pieces are added only once.
    ///
    /// Multiplayer: every player and the dedicated server need the mod, since a sapling ZDO carries the hash of a
    /// prefab that only exists with it. Without the mod, ZNetScene.CreateObject logs "Missing prefab hash" and
    /// simply does not show the sapling (the ZDO is kept); once grown, the bush is vanilla and shows for everyone.
    /// On the server it matters most: it owns the plants around the world spawn and is the one calling Grow() there.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "valheim.berryfarm";
        public const string PluginName = "BerryFarm";
        public const string PluginVersion = "1.0.0";

        /// <summary>Vanilla sapling copied for every berry: it already has the crop placement rules and effects.</summary>
        internal const string TemplateSapling = "sapling_carrot";

        internal sealed class Berry
        {
            public string Key;
            /// <summary>Name of the created prefab. Persisted as a hash in every planted sapling's ZDO: never rename.</summary>
            public string SaplingPrefab;
            public string BushPrefab;
            public string ItemPrefab;
            public string DisplayName;
            public ConfigEntry<bool> Enabled;
        }

        internal static readonly Berry[] Berries =
        {
            new Berry { Key = "Raspberry", SaplingPrefab = "BerryFarm_sapling_raspberry", BushPrefab = "RaspberryBush", ItemPrefab = "Raspberry", DisplayName = "Raspberry bush" },
            new Berry { Key = "Blueberry", SaplingPrefab = "BerryFarm_sapling_blueberry", BushPrefab = "BlueberryBush", ItemPrefab = "Blueberries", DisplayName = "Blueberry bush" },
            new Berry { Key = "Cloudberry", SaplingPrefab = "BerryFarm_sapling_cloudberry", BushPrefab = "CloudberryBush", ItemPrefab = "Cloudberry", DisplayName = "Cloudberry bush" },
            new Berry { Key = "Lingonberry", SaplingPrefab = "BerryFarm_sapling_lingonberry", BushPrefab = "LingonberryBush", ItemPrefab = "Lingonberry", DisplayName = "Lingonberry bush" },
        };

        internal static ManualLogSource Log;

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<int> Cost;
        internal static ConfigEntry<bool> NeedCultivatedGround;
        internal static ConfigEntry<float> GrowRadius;

        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;

            Enabled = Config.Bind("General", "Enabled", true,
                "Adds the berry saplings to the cultivator menu. Saplings already planted keep growing either way. " +
                "Applies at the next world load.");

            Cost = Config.Bind("General", "Cost", 5,
                new ConfigDescription("Number of berries of the matching kind consumed to plant one sapling.",
                    new AcceptableValueRange<int>(1, 50)));

            NeedCultivatedGround = Config.Bind("General", "NeedCultivatedGround", true,
                "Saplings must be planted on cultivated ground, like crops. Off: anywhere on the ground, like tree saplings.");

            GrowRadius = Config.Bind("General", "GrowRadius", 1f,
                new ConfigDescription(
                    "Free space needed around a sapling to grow, in metres (crops: 0.5). A grown bush counts as an " +
                    "obstacle, so this is also the minimum spacing between bushes.",
                    new AcceptableValueRange<float>(0.3f, 3f)));

            foreach (Berry berry in Berries)
                berry.Enabled = Config.Bind("Plants", berry.Key, true, $"Offers the {berry.DisplayName.ToLowerInvariant()} sapling in the cultivator menu.");

            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll(typeof(Plugin).Assembly);

            Log.LogInfo($"{PluginName} {PluginVersion} loaded.");
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
        }
    }

    /// <summary>
    /// Registers the saplings before ZNetScene.Awake builds its name hash dictionary from m_prefabs, and updates the
    /// cultivator menu. ZNetScene is recreated on every world load, the copies and the PieceTable are not.
    /// </summary>
    [HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.Awake))]
    internal static class ZNetScene_Awake_Patch
    {
        private static readonly Dictionary<string, GameObject> Saplings = new Dictionary<string, GameObject>();
        private static GameObject _holder;

        private static void Prefix(ZNetScene __instance)
        {
            var prefabs = new Dictionary<string, GameObject>();
            foreach (GameObject prefab in __instance.m_prefabs)
                if (prefab != null) prefabs[prefab.name] = prefab;

            if (!prefabs.TryGetValue(Plugin.TemplateSapling, out GameObject template))
            {
                Plugin.Log.LogWarning($"Prefab {Plugin.TemplateSapling} not found: no berry sapling registered.");
                return;
            }

            PieceTable cultivator = null;
            if (prefabs.TryGetValue("Cultivator", out GameObject cultivatorPrefab))
                cultivator = cultivatorPrefab.GetComponent<ItemDrop>()?.m_itemData.m_shared.m_buildPieces;
            if (cultivator == null) Plugin.Log.LogWarning("Cultivator piece table not found: saplings will not be offered.");

            int registered = 0, offered = 0;
            foreach (Plugin.Berry berry in Plugin.Berries)
            {
                if (!prefabs.TryGetValue(berry.BushPrefab, out GameObject bush) ||
                    !prefabs.TryGetValue(berry.ItemPrefab, out GameObject item) || item.GetComponent<ItemDrop>() == null)
                {
                    Plugin.Log.LogWarning($"{berry.BushPrefab} or {berry.ItemPrefab} not found: {berry.DisplayName} skipped.");
                    continue;
                }

                GameObject sapling = GetOrCreate(berry, template);
                Configure(sapling, berry, bush, item.GetComponent<ItemDrop>());

                if (!prefabs.ContainsKey(sapling.name)) __instance.m_prefabs.Add(sapling);
                registered++;

                if (cultivator == null) continue;
                bool offer = Plugin.Enabled.Value && berry.Enabled.Value;
                bool listed = cultivator.m_pieces.Contains(sapling);
                if (offer && !listed) cultivator.m_pieces.Add(sapling);
                else if (!offer && listed) cultivator.m_pieces.Remove(sapling);
                if (offer) offered++;
            }

            Plugin.Log.LogInfo($"{registered} berry saplings registered, {offered} offered in the cultivator menu.");
        }

        private static GameObject GetOrCreate(Plugin.Berry berry, GameObject template)
        {
            if (Saplings.TryGetValue(berry.SaplingPrefab, out GameObject existing) && existing != null) return existing;

            if (_holder == null)
            {
                // Inactive parent: components of the copies (ZNetView, Plant, Piece) do not wake up.
                _holder = new GameObject("BerryFarm_Prefabs");
                _holder.SetActive(false);
                Object.DontDestroyOnLoad(_holder);
            }

            GameObject sapling = Object.Instantiate(template, _holder.transform, false);
            sapling.name = berry.SaplingPrefab;

            // The carrot sapling shows a carrot once half grown: keep only the fern.
            foreach (Transform child in sapling.GetComponentsInChildren<Transform>(true))
                if (child.name == "carrot") child.gameObject.SetActive(false);

            Saplings[berry.SaplingPrefab] = sapling;
            return sapling;
        }

        private static void Configure(GameObject sapling, Plugin.Berry berry, GameObject bush, ItemDrop item)
        {
            Plant plant = sapling.GetComponent<Plant>();
            plant.m_name = berry.DisplayName;
            plant.m_grownPrefabs = new[] { bush };
            plant.m_needCultivatedGround = Plugin.NeedCultivatedGround.Value;
            plant.m_growRadius = Plugin.GrowRadius.Value;
            // Bushes keep their vanilla size (the carrot template picks a random 0.9-1.1 scale).
            plant.m_minScale = 1f;
            plant.m_maxScale = 1f;

            Piece piece = sapling.GetComponent<Piece>();
            piece.m_name = berry.DisplayName;
            piece.m_description = $"Grows into a {berry.DisplayName.ToLowerInvariant()} that can be picked again and again.";
            piece.m_icon = item.m_itemData.GetIcon();
            piece.m_cultivatedGroundOnly = Plugin.NeedCultivatedGround.Value;
            piece.m_groundOnly = true;
            piece.m_resources = new[]
            {
                new Piece.Requirement { m_resItem = item, m_amount = Plugin.Cost.Value, m_recover = false },
            };
        }
    }
}
