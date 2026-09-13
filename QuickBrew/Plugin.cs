using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace QuickBrew
{
    /// <summary>
    /// Shortens the fermentation time of barrels (meads, potions).
    ///
    /// - The barrel only stores its start time (ZDOVars.s_startTime); each client compares
    ///   "now - start" with m_fermentationDuration to know whether it is ready.
    /// - Rather than changing m_fermentationDuration locally (a vanilla owner would then refuse
    ///   to tap it), the mod backdates the start time in the ZDO. The state is replicated by the game: everyone,
    ///   with or without the mod, sees the barrel ready at the same moment.
    /// - The backdated value is kept in the ZDO under ShiftedStartTimeKey. When s_startTime no longer matches it
    ///   (new filling, barrel filled before the mod was installed, timer reset for lack of a
    ///   roof), the offset is applied again.
    /// - ShowRemainingTime adds the remaining time when hovering over the barrel.
    ///
    /// Multiplayer: only the network owner of the barrel (a nearby player) writes the ZDO. If that
    /// owner does not have the mod, the barrel ferments at vanilla speed, without inconsistency. For a guaranteed
    /// effect, install the mod for every player AND on the dedicated server: a dedicated server's reference
    /// position is never set (only Player and Game call ZNet.SetReferencePosition) and stays at the
    /// center of the world. ZDOMan.ReleaseNearbyZDOS therefore gives it the barrels near the spawn point and never
    /// hands them over to a player while they stay in its active area: the server is the one receiving RPC_AddItem.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "valheim.quickbrew";
        public const string PluginName = "QuickBrew";
        public const string PluginVersion = "1.0.1";

        internal static ManualLogSource Log;

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> Multiplier;
        internal static ConfigEntry<bool> ShowRemainingTime;

        /// <summary>Last s_startTime value written by the mod: if it differs, the offset is still to be applied.</summary>
        // The French string is kept on purpose: this key is persisted in the ZDO of every barrel in existing worlds.
        internal static readonly int ShiftedStartTimeKey = "QuickBrew.startTimeDecale".GetStableHashCode();

        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;

            Enabled = Config.Bind("General", "Enabled", true, "Enables or disables the mod.");

            Multiplier = Config.Bind("General", "Multiplier", 0.025f,
                new ConfigDescription(
                    "Share of the vanilla duration to wait. Vanilla: 2400 s (40 min). 0.025 = 1 min, 0.25 = 10 min, 0.5 = 20 min, " +
                    "0 = ready almost immediately, 1 = vanilla. Only applies to barrels filled after the change.",
                    new AcceptableValueRange<float>(0f, 1f)));
            MigrateKey(Multiplier, "General", "Multiplicateur");

            ShowRemainingTime = Config.Bind("General", "ShowRemainingTime", true,
                "Shows the time left before the barrel is ready, on hover.");
            MigrateKey(ShowRemainingTime, "General", "AfficherTempsRestant");

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
        }

        /// <summary>
        /// Backdates the barrel's start time so it is ready after Multiplier x vanilla duration.
        /// Does nothing when not the owner, when the barrel is empty, or when the offset was already applied to this start.
        /// </summary>
        internal static void ApplyOffset(Fermenter fermenter)
        {
            if (!Enabled.Value) return;

            ZNetView nview = fermenter.m_nview;
            if (nview == null || !nview.IsValid() || !nview.IsOwner()) return;

            ZDO zdo = nview.GetZDO();
            long start = zdo.GetLong(ZDOVars.s_startTime, 0L);
            if (start == 0L || zdo.GetInt(ZDOVars.s_content) == 0) return;
            if (start == zdo.GetLong(ShiftedStartTimeKey, 0L)) return;

            // At least 1 s of fermentation: GetStatus requires a time strictly greater than the duration.
            double target = Math.Max(1.0, fermenter.m_fermentationDuration * Multiplier.Value);
            double advance = fermenter.m_fermentationDuration - target;

            long newStart = advance > 0.0 ? start - TimeSpan.FromSeconds(advance).Ticks : start;
            if (newStart != start) zdo.Set(ZDOVars.s_startTime, newStart);
            zdo.Set(ShiftedStartTimeKey, newStart);
        }

        internal static string FormatDuration(double seconds)
        {
            int total = Math.Max(0, (int)Math.Ceiling(seconds));
            int min = total / 60;
            int sec = total % 60;
            return min > 0 ? $"{min} min {sec:00} s" : $"{sec} s";
        }
    }

    /// <summary>Filling: immediate offset on the owner, which receives the RPC.</summary>
    [HarmonyPatch(typeof(Fermenter), nameof(Fermenter.RPC_AddItem))]
    internal static class Fermenter_RPC_AddItem_Patch
    {
        private static void Postfix(Fermenter __instance)
        {
            Plugin.ApplyOffset(__instance);
        }
    }

    /// <summary>
    /// Every 2 s: catches up barrels filled before the install, those whose timer was reset
    /// (UpdateCover calls ResetFermentationTimer when the roof is missing), and ownership changes.
    /// </summary>
    [HarmonyPatch(typeof(Fermenter), nameof(Fermenter.SlowUpdate))]
    internal static class Fermenter_SlowUpdate_Patch
    {
        private static void Postfix(Fermenter __instance)
        {
            Plugin.ApplyOffset(__instance);
        }
    }

    /// <summary>Hover: remaining time, read from the replicated ZDO, so correct even when not the owner.</summary>
    [HarmonyPatch(typeof(Fermenter), nameof(Fermenter.GetHoverText))]
    internal static class Fermenter_GetHoverText_Patch
    {
        private static void Postfix(Fermenter __instance, ref string __result)
        {
            if (!Plugin.Enabled.Value || !Plugin.ShowRemainingTime.Value) return;
            if (__instance.m_nview == null || !__instance.m_nview.IsValid()) return;
            if (__instance.GetStatus() != Fermenter.Status.Fermenting) return;
            if (__instance.m_exposed || !__instance.m_hasRoof) return;
            if (!PrivateArea.CheckAccess(__instance.transform.position, 0f, false)) return;

            double remaining = __instance.m_fermentationDuration - __instance.GetFermentationTime();
            __result += "\nReady in " + Plugin.FormatDuration(remaining);
        }
    }
}
