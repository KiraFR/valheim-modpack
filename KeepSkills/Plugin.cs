using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace KeepSkills
{
    /// <summary>
    /// Removes or reduces the skill loss on death.
    ///
    /// - Player.OnDeath has two ways to take levels away: Skills.OnDeath, which lowers every skill by
    ///   m_DeathLowerFactor (25 %) x Game.m_skillReductionRate (the world's death penalty modifier), and Skills.Clear
    ///   when the world has the DeathSkillsReset global key (hardcore death penalty), which wipes them all.
    ///   A death shortly after respawning (the "No skill drain" status effect) already loses nothing.
    /// - LossMultiplier scales both: 0 keeps every level, 1 is vanilla. For the hardcore reset, a multiplier below 1
    ///   lowers every skill by that share instead of wiping them (0.5 = half of each level lost).
    /// - Skills.Clear is also called by Player.ResetCharacter, so it is only intercepted while Player.OnDeath runs.
    ///
    /// Multiplayer: skills are saved in the player's profile on their own machine and Player.OnDeath only runs on the
    /// owner of the Player object. Client only: neither the server nor the other players need the mod.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "valheim.keepskills";
        public const string PluginName = "KeepSkills";
        public const string PluginVersion = "1.0.0";

        internal static ManualLogSource Log;

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> LossMultiplier;

        /// <summary>True while Player.OnDeath runs, to tell the death reset apart from other Skills.Clear calls.</summary>
        internal static bool InPlayerDeath;

        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;

            Enabled = Config.Bind("General", "Enabled", true, "Enables or disables the mod.");

            LossMultiplier = Config.Bind("General", "LossMultiplier", 0f,
                new ConfigDescription(
                    "Share of the vanilla skill loss applied on death. 0 = no loss, 0.5 = half, 1 = vanilla. " +
                    "Vanilla loss: 25 % of each skill level, scaled by the world's death penalty modifier. " +
                    "With the hardcore death penalty (skills reset), each skill loses this share of its level instead.",
                    new AcceptableValueRange<float>(0f, 1f)));

            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll(typeof(Plugin).Assembly);

            Log.LogInfo($"{PluginName} {PluginVersion} loaded.");
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
        }
    }

    /// <summary>Marks the death so the hardcore skill reset can be recognised in Skills.Clear.</summary>
    [HarmonyPatch(typeof(Player), nameof(Player.OnDeath))]
    internal static class Player_OnDeath_Patch
    {
        private static void Prefix()
        {
            Plugin.InPlayerDeath = true;
        }

        private static Exception Finalizer(Exception __exception)
        {
            Plugin.InPlayerDeath = false;
            return __exception;
        }
    }

    /// <summary>Regular death penalty: lowers the skills by LossMultiplier x the vanilla share, or not at all.</summary>
    [HarmonyPatch(typeof(Skills), nameof(Skills.OnDeath))]
    internal static class Skills_OnDeath_Patch
    {
        private static bool Prefix(Skills __instance)
        {
            if (!Plugin.Enabled.Value) return true;

            float multiplier = Plugin.LossMultiplier.Value;
            if (multiplier >= 1f) return true;

            // LowerAllSkills also shows "Skills lowered", which would be wrong when nothing is lost.
            if (multiplier > 0f)
                __instance.LowerAllSkills(__instance.m_DeathLowerFactor * Game.m_skillReductionRate * multiplier);
            return false;
        }
    }

    /// <summary>Hardcore death penalty (DeathSkillsReset): lowers each skill by LossMultiplier instead of wiping them.</summary>
    [HarmonyPatch(typeof(Skills), nameof(Skills.Clear))]
    internal static class Skills_Clear_Patch
    {
        private static bool Prefix(Skills __instance)
        {
            if (!Plugin.Enabled.Value || !Plugin.InPlayerDeath) return true;

            float multiplier = Plugin.LossMultiplier.Value;
            if (multiplier >= 1f) return true;

            if (multiplier > 0f) __instance.LowerAllSkills(multiplier);
            return false;
        }
    }
}
