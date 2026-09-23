using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace FriendlyBallista
{
    /// <summary>
    /// In "everything" target mode, the ballista no longer shoots players (and, by default, tamed creatures).
    ///
    /// - Turret.UpdateTarget passes m_targetPlayers and m_targetTamed (prefab fields, both true on the vanilla ballista)
    ///   to BaseAI.FindClosestCreature. The patch clears them for the duration of the call and restores them afterwards.
    /// - In "configured" mode (trophies), the game uses m_targetTamedConfig and only matches the chosen creature names,
    ///   so players were already excluded: that mode is left untouched.
    /// - A player targeted before the change is dropped at the next target refresh (at most a couple of seconds).
    ///
    /// Multiplayer: only the network owner of the ballista picks targets (FixedUpdate calls UpdateTarget when
    /// m_nview.IsOwner()), and the choice is replicated through RPC_SetTarget. Install it for every player and on the
    /// dedicated server, which permanently owns the ballistas around the world spawn.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "valheim.friendlyballista";
        public const string PluginName = "FriendlyBallista";
        public const string PluginVersion = "1.0.0";

        internal static ManualLogSource Log;

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> TargetTamed;

        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;

            Enabled = Config.Bind("General", "Enabled", true, "Enables or disables the mod.");

            TargetTamed = Config.Bind("General", "TargetTamed", false,
                "In \"everything\" mode, also shoots tamed creatures (vanilla behaviour). Players are never targeted.");

            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll(typeof(Plugin).Assembly);

            Log.LogInfo($"{PluginName} {PluginVersion} loaded.");
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
        }
    }

    /// <summary>Clears the player (and tamed) flags while the owner looks for a target, then restores the prefab values.</summary>
    [HarmonyPatch(typeof(Turret), nameof(Turret.UpdateTarget))]
    internal static class Turret_UpdateTarget_Patch
    {
        internal struct Saved
        {
            public bool Applied;
            public bool TargetPlayers;
            public bool TargetTamed;
        }

        private static void Prefix(Turret __instance, out Saved __state)
        {
            __state = default;
            // Turrets that never target enemies are not real weapons: leave them alone.
            if (!Plugin.Enabled.Value || !__instance.m_targetEnemies) return;

            __state = new Saved
            {
                Applied = true,
                TargetPlayers = __instance.m_targetPlayers,
                TargetTamed = __instance.m_targetTamed,
            };
            __instance.m_targetPlayers = false;
            if (!Plugin.TargetTamed.Value) __instance.m_targetTamed = false;
        }

        private static void Postfix(Turret __instance, Saved __state)
        {
            if (!__state.Applied) return;
            __instance.m_targetPlayers = __state.TargetPlayers;
            __instance.m_targetTamed = __state.TargetTamed;
        }
    }
}
