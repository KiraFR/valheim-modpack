using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace InfiniteTorch
{
    /// <summary>
    /// Torches never run out: the ones you carry and the ones you place burn forever.
    ///
    /// Two different vanilla mechanics wear a torch down, so the mod has one patch for each:
    ///
    /// - A carried torch is an item with durability. Humanoid.UpdateEquipment calls DrainEquipedItemDurability
    ///   every frame for each equipped item whose m_shared.m_useDurability is set, and the torch goes out (it is
    ///   unequipped, not destroyed) when m_durability reaches 0. The mod skips that drain for items of type
    ///   ItemType.Torch and tops their durability back up, which also cancels the wear from hitting with the
    ///   torch (Attack spends m_useDurabilityDrain on every swing).
    /// - A placed torch is a Fireplace whose fuel lives in its ZDO (ZDOVars.s_fuel) and is decremented every 2 s
    ///   by UpdateFireplace. Rather than rewriting that loop, the mod turns on the vanilla m_infiniteFuel field
    ///   for the length of the call (Prefix/Postfix with __state) and refills the ZDO once when it is below the
    ///   maximum. Outside the call the field stays off, so the hover text keeps showing the fuel (10/10) and
    ///   uninstalling the mod simply gives back a torch full of resin. Everything else stays vanilla: the torch
    ///   can still be turned off, and it still stops burning when covered by terrain.
    ///
    /// PieceFilter decides which fires are concerned, by fragment of prefab name: "torch" by default, which
    /// covers piece_groundtorch and its variants (wood, green, blue, mist) as well as piece_walltorch. Add
    /// "brazier" or empty the list (every fire, campfires and hearths included) to widen it.
    ///
    /// Multiplayer: fuel is only consumed by the network owner of the fire, so its config is the one that
    /// decides. Install the mod for every player, and **on the dedicated server**: its reference position stays
    /// at the centre of the world, so ZDOMan.ReleaseNearbyZDOS leaves it the owner of the pieces around the
    /// spawn point, like QuickBrew's barrels. Carried torches are client side, the server is not involved.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "valheim.infinitetorch";
        public const string PluginName = "InfiniteTorch";
        public const string PluginVersion = "1.0.0";

        internal static ManualLogSource Log;

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> CarriedTorches;
        internal static ConfigEntry<bool> PlacedTorches;
        internal static ConfigEntry<string> PieceFilter;

        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;

            Enabled = Config.Bind("General", "Enabled", true, "Enables or disables the mod.");

            CarriedTorches = Config.Bind("General", "CarriedTorches", true,
                "The torch held in your hand never wears out: its durability stays full, so it never goes out, " +
                "including when it is used to hit something.");

            PlacedTorches = Config.Bind("General", "PlacedTorches", true,
                "Placed torches never run out of fuel: they stay lit and never ask for resin or wood again.");

            PieceFilter = Config.Bind("General", "PieceFilter", "torch",
                "Which fires PlacedTorches applies to, as a comma-separated list of prefab name fragments " +
                "(case insensitive). \"torch\" covers the standing torches (piece_groundtorch and its wood, green, " +
                "blue and mist variants) and the wall torch. Add \"brazier\" or \"jackoturnip\" for those, or leave " +
                "the list empty to make every fire infinite, campfires, hearths and bonfires included.");

            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll(typeof(Plugin).Assembly);

            Log.LogInfo($"{PluginName} {PluginVersion} loaded.");
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
        }

        private static string _filterRaw;
        private static List<string> _fragments = new List<string>();

        /// <summary>Fragments of PieceFilter, parsed again only when the config value changes.</summary>
        private static List<string> Fragments()
        {
            string raw = PieceFilter.Value ?? "";
            if (raw == _filterRaw) return _fragments;

            var fragments = new List<string>();
            foreach (string part in raw.Split(','))
            {
                string fragment = part.Trim();
                if (fragment.Length > 0) fragments.Add(fragment);
            }

            _filterRaw = raw;
            _fragments = fragments;
            return _fragments;
        }

        /// <summary>Prefab name, without the "(Clone)" suffix added by Unity on instantiation.</summary>
        private static string PrefabName(Component component)
        {
            string name = component.gameObject.name;
            int clone = name.IndexOf("(Clone)", StringComparison.Ordinal);
            return clone >= 0 ? name.Substring(0, clone) : name;
        }

        /// <summary>A fire the mod keeps burning: its prefab name contains one of the fragments, or the list is empty.</summary>
        internal static bool IsTarget(Fireplace fireplace)
        {
            List<string> fragments = Fragments();
            if (fragments.Count == 0) return true;

            string name = PrefabName(fireplace);
            foreach (string fragment in fragments)
            {
                if (name.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Carried torch: skips the durability drain of the equipment update and keeps the torch full, so it never
    /// goes out. Every other equipped item keeps wearing out as in vanilla.
    /// </summary>
    [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.DrainEquipedItemDurability))]
    internal static class Humanoid_DrainEquipedItemDurability_Patch
    {
        private static bool Prefix(ItemDrop.ItemData item)
        {
            if (!Plugin.Enabled.Value || !Plugin.CarriedTorches.Value) return true;
            if (item == null || item.m_shared == null) return true;
            if (item.m_shared.m_itemType != ItemDrop.ItemData.ItemType.Torch) return true;

            float max = item.GetMaxDurability();
            if (item.m_durability < max) item.m_durability = max;
            return false;
        }
    }

    /// <summary>
    /// Placed torch: refills the fuel stored in the ZDO, then lets the vanilla update run with m_infiniteFuel on
    /// so it consumes nothing. The field is restored right after, so the hover text and the interactions stay
    /// vanilla. Only the network owner writes the ZDO; the other clients just skip the consumption they do not do.
    /// </summary>
    [HarmonyPatch(typeof(Fireplace), nameof(Fireplace.UpdateFireplace))]
    internal static class Fireplace_UpdateFireplace_Patch
    {
        private static void Prefix(Fireplace __instance, ref bool __state)
        {
            __state = __instance.m_infiniteFuel;

            if (!Plugin.Enabled.Value || !Plugin.PlacedTorches.Value) return;
            if (__instance.m_infiniteFuel || !Plugin.IsTarget(__instance)) return;

            ZNetView nview = __instance.m_nview;
            if (nview != null && nview.IsValid() && nview.IsOwner())
            {
                ZDO zdo = nview.GetZDO();
                if (zdo.GetFloat(ZDOVars.s_fuel) < __instance.m_maxFuel) zdo.Set(ZDOVars.s_fuel, __instance.m_maxFuel);
            }

            __instance.m_infiniteFuel = true;
        }

        private static void Postfix(Fireplace __instance, bool __state)
        {
            __instance.m_infiniteFuel = __state;
        }
    }
}
