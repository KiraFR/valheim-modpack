using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace GrowTime
{
    /// <summary>
    /// Changes how long plants take to grow, and optionally how long wild bushes take to regrow.
    ///
    /// - Plant (crops, saplings, vines): the ZDO only stores the planting time (ZDOVars.s_plantTime). Every
    ///   10 s, SUpdate compares "now - planted" with GetGrowTime(), a deterministic value drawn between m_growTime
    ///   and m_growTimeMax from the plant's seed. The mod scales that value, so both the owner's Grow() and the
    ///   half-grown mesh shown on each client follow the multiplier.
    /// - The planting time is not shifted in the ZDO (unlike QuickBrew): pushing it into the future to lengthen growth
    ///   would make UpdateHealth report "Healthy" for as long as "now - planted" stays under 10 s, hiding a missing
    ///   sun or a lack of space. Scaling the duration keeps the vanilla health checks intact, and a config change
    ///   applies at once to plants already in the ground.
    /// - Pickable (berry bushes, mushrooms, thistle...): ShouldRespawn reads m_respawnTimeMinutes, which the mod
    ///   scales for the duration of the call.
    /// - Spinning wheel (flax to linen thread), windmill (barley and oats to flour) and eitr refinery (soft tissue to
    ///   eitr): all three are Smelters, and m_secPerProduct is only read by UpdateSmelter on the station's owner, so
    ///   the mod scales it for the duration of that call. The progress lives in the ZDO (bake timer), so a load
    ///   already in the station just finishes sooner, and the fuel spent per product does not change (UpdateSmelter
    ///   burns m_secPerProduct / m_fuelPerProduct per product either way). The windmill still depends on the wind:
    ///   its progress per second is Windmill.GetPowerOutput().
    ///
    /// Multiplayer: Grow(), the respawn of a bush and the stations only run on the object's network owner (a nearby
    /// player), so the owner's multiplier decides. Install the mod for every player with the same config AND on the dedicated
    /// server: the server's reference position stays at the world centre, so it permanently owns the plants around
    /// the spawn, where the first farm usually is.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "valheim.growtime";
        public const string PluginName = "GrowTime";
        public const string PluginVersion = "1.3.0";

        /// <summary>Smelter.m_name of the vanilla spinning wheel (prefab piece_spinningwheel).</summary>
        internal const string SpinningWheelName = "$piece_spinningwheel";

        /// <summary>Smelter.m_name of the vanilla windmill (prefab windmill).</summary>
        internal const string WindmillName = "$piece_windmill";

        /// <summary>Smelter.m_name of the vanilla eitr refinery (prefab eitrrefinery).</summary>
        internal const string EitrRefineryName = "$piece_eitrrefinery";

        internal static ManualLogSource Log;

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> GrowTimeMultiplier;
        internal static ConfigEntry<float> RespawnTimeMultiplier;
        internal static ConfigEntry<bool> ShowRemainingTime;
        internal static ConfigEntry<float> SpinningWheelMultiplier;
        internal static ConfigEntry<float> WindmillMultiplier;
        internal static ConfigEntry<float> EitrRefineryMultiplier;

        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;

            Enabled = Config.Bind("General", "Enabled", true, "Enables or disables the mod.");

            GrowTimeMultiplier = Config.Bind("Plants", "GrowTimeMultiplier", 0.1f,
                new ConfigDescription(
                    "Multiplier applied to the growth time of planted crops and saplings. Vanilla crops take about " +
                    "4000 to 5000 s. 0.1 = about 8 min, 0.5 = twice as fast, 1 = vanilla, 2 = twice as long. " +
                    "Also applies to plants already in the ground.",
                    new AcceptableValueRange<float>(0f, 20f)));

            RespawnTimeMultiplier = Config.Bind("Plants", "RespawnTimeMultiplier", 0.05f,
                new ConfigDescription(
                    "Multiplier applied to the regrowth time of wild pickables (berry bushes, mushrooms, thistle...). " +
                    "Berry bushes take 300 min in vanilla. 0.05 = 15 min, 0.1 = 30 min, 0.5 = twice as fast, 1 = vanilla.",
                    new AcceptableValueRange<float>(0f, 20f)));

            ShowRemainingTime = Config.Bind("Plants", "ShowRemainingTime", true,
                "Shows the time left before a healthy plant grows, on hover.");

            SpinningWheelMultiplier = Config.Bind("Processing", "SpinningWheelMultiplier", 0.1f,
                new ConfigDescription(
                    "Multiplier applied to the spinning wheel's time per flax. Vanilla: 30 s per linen thread, 20 min " +
                    "for a full load of 40. 0.1 = 3 s (2 min for 40), 0.5 = twice as fast, 1 = vanilla. The wheel " +
                    "advances in 1 s steps, so it never makes more than one thread per second.",
                    new AcceptableValueRange<float>(0.01f, 20f)));

            WindmillMultiplier = Config.Bind("Processing", "WindmillMultiplier", 0.1f,
                new ConfigDescription(
                    "Multiplier applied to the windmill's time per grain (barley and oats). Vanilla: 10 s per flour in " +
                    "full wind, about 8 min for a full load of 50. 0.1 = 1 s (50 s for 50), 0.5 = twice as fast, " +
                    "1 = vanilla. Weaker wind still slows it down, and it never makes more than one flour per second.",
                    new AcceptableValueRange<float>(0.01f, 20f)));

            EitrRefineryMultiplier = Config.Bind("Processing", "EitrRefineryMultiplier", 0.1f,
                new ConfigDescription(
                    "Multiplier applied to the eitr refinery's time per eitr. Vanilla: 40 s per eitr, about 13 min for " +
                    "a full load of 20 soft tissue. 0.1 = 4 s (80 s for 20), 0.5 = twice as fast, 1 = vanilla. The " +
                    "sap spent per eitr does not change.",
                    new AcceptableValueRange<float>(0.01f, 20f)));

            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll(typeof(Plugin).Assembly);

            Log.LogInfo($"{PluginName} {PluginVersion} loaded.");
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
        }

        internal static string FormatDuration(double seconds)
        {
            int total = Math.Max(0, (int)Math.Ceiling(seconds));
            int h = total / 3600;
            int min = total % 3600 / 60;
            int sec = total % 60;
            if (h > 0) return $"{h} h {min:00} min";
            return min > 0 ? $"{min} min {sec:00} s" : $"{sec} s";
        }
    }

    /// <summary>Scales the plant's growth time, read by SUpdate for both the Grow() call and the grown mesh.</summary>
    [HarmonyPatch(typeof(Plant), nameof(Plant.GetGrowTime))]
    internal static class Plant_GetGrowTime_Patch
    {
        private static void Postfix(ref float __result)
        {
            if (!Plugin.Enabled.Value) return;
            __result *= Plugin.GrowTimeMultiplier.Value;
        }
    }

    /// <summary>Hover: time left before growing, only while the plant is healthy (otherwise it will not grow).</summary>
    [HarmonyPatch(typeof(Plant), nameof(Plant.GetHoverText))]
    internal static class Plant_GetHoverText_Patch
    {
        private static void Postfix(Plant __instance, ref string __result)
        {
            if (!Plugin.Enabled.Value || !Plugin.ShowRemainingTime.Value) return;
            if (__instance.m_nview == null || !__instance.m_nview.IsValid()) return;
            if (__instance.GetStatus() != Plant.Status.Healthy) return;

            double remaining = __instance.GetGrowTime() - __instance.TimeSincePlanted();
            // Growth is checked every 10 s: a plant past its time shows nothing rather than a stuck "0 s".
            if (remaining <= 0.0) return;
            __result += "\nGrows in " + Plugin.FormatDuration(remaining);
        }
    }

    /// <summary>Scales the respawn time of a wild pickable for the duration of the check, restored in the Postfix.</summary>
    [HarmonyPatch(typeof(Pickable), nameof(Pickable.ShouldRespawn))]
    internal static class Pickable_ShouldRespawn_Patch
    {
        private static void Prefix(Pickable __instance, out float __state)
        {
            __state = __instance.m_respawnTimeMinutes;
            if (Plugin.Enabled.Value) __instance.m_respawnTimeMinutes *= Plugin.RespawnTimeMultiplier.Value;
        }

        private static void Postfix(Pickable __instance, float __state)
        {
            __instance.m_respawnTimeMinutes = __state;
        }
    }

    /// <summary>
    /// Scales the time per product of the spinning wheel, the windmill and the eitr refinery for the duration of
    /// UpdateSmelter, the only reader of m_secPerProduct (it returns before using it when not the owner). Restored in
    /// the Postfix. Other stations (smelter, kiln, blast furnace) keep their vanilla speed.
    /// </summary>
    [HarmonyPatch(typeof(Smelter), nameof(Smelter.UpdateSmelter))]
    internal static class Smelter_UpdateSmelter_Patch
    {
        private static void Prefix(Smelter __instance, out float __state)
        {
            __state = __instance.m_secPerProduct;
            if (!Plugin.Enabled.Value) return;

            switch (__instance.m_name)
            {
                case Plugin.SpinningWheelName:
                    __instance.m_secPerProduct *= Plugin.SpinningWheelMultiplier.Value;
                    break;
                case Plugin.WindmillName:
                    __instance.m_secPerProduct *= Plugin.WindmillMultiplier.Value;
                    break;
                case Plugin.EitrRefineryName:
                    __instance.m_secPerProduct *= Plugin.EitrRefineryMultiplier.Value;
                    break;
            }
        }

        private static void Postfix(Smelter __instance, float __state)
        {
            __instance.m_secPerProduct = __state;
        }
    }
}
