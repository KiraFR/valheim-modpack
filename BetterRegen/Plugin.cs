using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace BetterRegen
{
    /// <summary>
    /// Health, stamina and eitr come back faster and more often.
    ///
    /// The three bars do not regenerate the same way in vanilla, so the mod has one patch per mechanic:
    ///
    /// - Stamina and eitr regenerate continuously, every frame in Player.UpdateStats, at a rate read from the
    ///   player's own m_staminaRegen / m_eiterRegen fields (5/s), but only once m_staminaRegenTimer /
    ///   m_eitrRegenTimer have run out. That timer is refilled with m_staminaRegenDelay / m_eitrRegenDelay (1 s)
    ///   on every single use, in RPC_UseStamina / RPC_UseEitr. There are therefore two knobs per bar: the rate
    ///   (RegenMultiplier) and the pause that follows a use (DelayMultiplier). Both are applied by multiplying
    ///   the vanilla field for the length of the call and restoring it right after (Prefix/Postfix with
    ///   __state), so nothing is rewritten and everything around it stays vanilla: regen still stops while
    ///   attacking, dodging, swimming or encumbered, is still reduced while blocking, status effects (Rested,
    ///   meads, eitr gear) still apply their own multiplier on top, and the world modifiers still count.
    /// - Health regenerates in chunks: UpdateFood adds dt to m_foodRegenTimer and, every 10 s, heals the sum of
    ///   the m_foodRegen of the foods eaten. That 10 is a literal inside the method, not a field, so it cannot
    ///   be overridden the same way. Instead of rewriting the tick, the mod makes the timer run faster: it adds
    ///   dt * (10 / TickInterval - 1) before the vanilla dt, so the threshold of 10 is reached after
    ///   TickInterval seconds. The healed amount is the other knob, and since it is a local variable it is
    ///   scaled in Character.Heal, which the mod only touches during that one call (the player being updated is
    ///   remembered for the length of UpdateFood).
    ///
    /// Health regen still comes from food, as in vanilla: with an empty food bar there is nothing to multiply.
    ///
    /// Multiplayer: all of this is the local player's own state, computed only by the client that owns the
    /// character (UpdateStats only runs for the owner of the local player). Client only: neither the dedicated
    /// server nor the other players need the mod, and each player's own config decides.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "valheim.betterregen";
        public const string PluginName = "BetterRegen";
        public const string PluginVersion = "1.0.0";

        /// <summary>Vanilla interval between two health regen ticks, the literal tested in Player.UpdateFood.</summary>
        internal const float VanillaTickInterval = 10f;

        internal static ManualLogSource Log;

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> HealthRegenMultiplier;
        internal static ConfigEntry<float> HealthTickInterval;
        internal static ConfigEntry<float> StaminaRegenMultiplier;
        internal static ConfigEntry<float> StaminaDelayMultiplier;
        internal static ConfigEntry<float> EitrRegenMultiplier;
        internal static ConfigEntry<float> EitrDelayMultiplier;

        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;

            Enabled = Config.Bind("General", "Enabled", true, "Enables or disables the mod.");

            HealthRegenMultiplier = Config.Bind("Health", "RegenMultiplier", 2f,
                "Multiplies the health given by each regen tick (1 = vanilla). The vanilla amount is the sum of " +
                "the regen values of the foods eaten, so it still depends on what you ate, and an empty food bar " +
                "still heals nothing.");

            HealthTickInterval = Config.Bind("Health", "TickInterval", 2f,
                "Seconds between two health regen ticks (10 = vanilla). Lower means more frequent healing, of " +
                "RegenMultiplier times the vanilla amount each time.");

            StaminaRegenMultiplier = Config.Bind("Stamina", "RegenMultiplier", 2f,
                "Multiplies the stamina regen rate (1 = vanilla). Stamina still stops regenerating while " +
                "attacking, dodging, swimming or encumbered.");

            StaminaDelayMultiplier = Config.Bind("Stamina", "DelayMultiplier", 0.2f,
                "Multiplies the pause that follows every stamina use before regen starts again (1 = vanilla, " +
                "i.e. 1 second; 0 = regen starts again immediately).");

            EitrRegenMultiplier = Config.Bind("Eitr", "RegenMultiplier", 2f,
                "Multiplies the eitr regen rate (1 = vanilla). Eitr still stops regenerating while attacking " +
                "or dodging.");

            EitrDelayMultiplier = Config.Bind("Eitr", "DelayMultiplier", 0.2f,
                "Multiplies the pause that follows every eitr use before regen starts again (1 = vanilla, " +
                "i.e. 1 second; 0 = regen starts again immediately).");

            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll(typeof(Plugin).Assembly);

            Log.LogInfo($"{PluginName} {PluginVersion} loaded.");
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
        }

        /// <summary>Health regen interval asked for in the config, kept away from 0 (it is a divisor).</summary>
        internal static float TickInterval()
        {
            return Mathf.Max(0.1f, HealthTickInterval.Value);
        }
    }

    /// <summary>
    /// Stamina and eitr regen rates: multiplies the two vanilla fields for the length of the stats update, then
    /// puts them back. The whole vanilla formula runs untouched, including the boost that comes as the bar
    /// empties (m_staminaRegenTimeMultiplier), the status effect multipliers and the world modifiers.
    /// </summary>
    [HarmonyPatch(typeof(Player), "UpdateStats", new Type[] { typeof(float) })]
    internal static class Player_UpdateStats_Patch
    {
        internal struct Rates
        {
            public float Stamina;
            public float Eitr;
        }

        private static void Prefix(Player __instance, ref Rates __state)
        {
            __state.Stamina = __instance.m_staminaRegen;
            __state.Eitr = __instance.m_eiterRegen;

            if (!Plugin.Enabled.Value) return;

            __instance.m_staminaRegen *= Mathf.Max(0f, Plugin.StaminaRegenMultiplier.Value);
            __instance.m_eiterRegen *= Mathf.Max(0f, Plugin.EitrRegenMultiplier.Value);
        }

        private static void Postfix(Player __instance, Rates __state)
        {
            __instance.m_staminaRegen = __state.Stamina;
            __instance.m_eiterRegen = __state.Eitr;
        }
    }

    /// <summary>
    /// Pause after a stamina use: RPC_UseStamina is the only place that refills m_staminaRegenTimer, and it
    /// copies m_staminaRegenDelay into it, so scaling that field for the length of the call scales the pause.
    /// </summary>
    [HarmonyPatch(typeof(Player), "RPC_UseStamina")]
    internal static class Player_RPC_UseStamina_Patch
    {
        private static void Prefix(Player __instance, ref float __state)
        {
            __state = __instance.m_staminaRegenDelay;

            if (!Plugin.Enabled.Value) return;

            __instance.m_staminaRegenDelay *= Mathf.Max(0f, Plugin.StaminaDelayMultiplier.Value);
        }

        private static void Postfix(Player __instance, float __state)
        {
            __instance.m_staminaRegenDelay = __state;
        }
    }

    /// <summary>Same as the stamina one, for eitr: RPC_UseEitr copies m_eitrRegenDelay into the regen timer.</summary>
    [HarmonyPatch(typeof(Player), "RPC_UseEitr")]
    internal static class Player_RPC_UseEitr_Patch
    {
        private static void Prefix(Player __instance, ref float __state)
        {
            __state = __instance.m_eitrRegenDelay;

            if (!Plugin.Enabled.Value) return;

            __instance.m_eitrRegenDelay *= Mathf.Max(0f, Plugin.EitrDelayMultiplier.Value);
        }

        private static void Postfix(Player __instance, float __state)
        {
            __instance.m_eitrRegenDelay = __state;
        }
    }

    /// <summary>
    /// Health regen frequency, and the player whose regen tick is running.
    ///
    /// UpdateFood counts dt into m_foodRegenTimer and heals once it reaches 10. The mod adds the missing part
    /// of that count beforehand, so the timer gains VanillaTickInterval / TickInterval per second instead of 1
    /// and the tick fires after TickInterval seconds. The forceUpdate calls (eating, loading) are skipped:
    /// they pass dt = 0 and return before the timer anyway.
    /// </summary>
    [HarmonyPatch(typeof(Player), "UpdateFood", new Type[] { typeof(float), typeof(bool) })]
    internal static class Player_UpdateFood_Patch
    {
        /// <summary>Set only while the vanilla regen tick of that player is running, read by the Heal patch.</summary>
        internal static Player RegenTarget;

        private static void Prefix(Player __instance, float dt, bool forceUpdate)
        {
            RegenTarget = null;

            if (!Plugin.Enabled.Value || forceUpdate || dt <= 0f) return;

            float interval = Plugin.TickInterval();
            if (interval != Plugin.VanillaTickInterval)
            {
                __instance.m_foodRegenTimer += dt * (Plugin.VanillaTickInterval / interval - 1f);
                if (__instance.m_foodRegenTimer < 0f) __instance.m_foodRegenTimer = 0f;
            }

            if (Plugin.HealthRegenMultiplier.Value != 1f) RegenTarget = __instance;
        }

        private static void Postfix()
        {
            RegenTarget = null;
        }
    }

    /// <summary>
    /// Health regen amount: the healed value is a local variable of UpdateFood, computed from the foods eaten
    /// and the status effects, so it is scaled where it is spent instead. Every other heal in the game (food
    /// eaten, potions, healing effects) goes through the same method and is left alone, since the patch only
    /// acts on the player whose regen tick is currently running.
    /// </summary>
    [HarmonyPatch(typeof(Character), nameof(Character.Heal))]
    internal static class Character_Heal_Patch
    {
        private static void Prefix(Character __instance, ref float hp)
        {
            if (Player_UpdateFood_Patch.RegenTarget == null) return;
            if (!ReferenceEquals(__instance, Player_UpdateFood_Patch.RegenTarget)) return;

            hp *= Mathf.Max(0f, Plugin.HealthRegenMultiplier.Value);
        }
    }
}
