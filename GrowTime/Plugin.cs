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
    ///
    /// Multiplayer: Grow() and the respawn of a bush only run on the object's network owner (a nearby player), so
    /// the owner's multiplier decides. Install the mod for every player with the same config AND on the dedicated
    /// server: the server's reference position stays at the world centre, so it permanently owns the plants around
    /// the spawn, where the first farm usually is.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "valheim.growtime";
        public const string PluginName = "GrowTime";
        public const string PluginVersion = "1.0.0";

        internal static ManualLogSource Log;

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> GrowTimeMultiplier;
        internal static ConfigEntry<float> RespawnTimeMultiplier;
        internal static ConfigEntry<bool> ShowRemainingTime;

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

            RespawnTimeMultiplier = Config.Bind("Plants", "RespawnTimeMultiplier", 1f,
                new ConfigDescription(
                    "Multiplier applied to the regrowth time of wild pickables (berry bushes, mushrooms, thistle...). " +
                    "1 = vanilla, 2 = twice as long, 0.5 = twice as fast.",
                    new AcceptableValueRange<float>(0f, 20f)));

            ShowRemainingTime = Config.Bind("Plants", "ShowRemainingTime", true,
                "Shows the time left before a healthy plant grows, on hover.");

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
}
