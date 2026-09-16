using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ArrowRecovery
{
    /// <summary>
    /// Gives a chance to get back arrows and bolts after they hit something.
    ///
    /// - Attack.UseAmmo takes one arrow out of the inventory and Projectile.Setup keeps it in m_ammo. When the projectile
    ///   stops on a hit, Projectile.OnHit sets m_didHit; bounces, invalid targets and parried hits return before that.
    /// - On that hit, RecoveryChance percent of the time, one arrow of the same kind (same quality) drops at the hit
    ///   point, pulled back a little along the trajectory so it does not spawn inside the target or the ground.
    ///   Arrows that fly until their lifetime runs out without hitting anything are lost, as in vanilla.
    /// - Only player-shot ammo of type Ammo counts (arrows, bolts): catapult ammo and throwing weapons are left alone,
    ///   the latter already come back through m_respawnItemOnHit.
    ///
    /// Multiplayer: the projectile is created by the shooter and its FixedUpdate, hence OnHit, only runs on its owner.
    /// The dropped arrow is a regular networked ItemDrop. Client only: neither the server nor the other players need
    /// the mod, and players without it simply do not get their arrows back.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "valheim.arrowrecovery";
        public const string PluginName = "ArrowRecovery";
        public const string PluginVersion = "1.0.0";

        internal static ManualLogSource Log;

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> RecoveryChance;

        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;

            Enabled = Config.Bind("General", "Enabled", true, "Enables or disables the mod.");

            RecoveryChance = Config.Bind("General", "RecoveryChance", 50f,
                new ConfigDescription(
                    "Chance, in percent, to get an arrow or bolt back when it hits something (creature, ground, tree, " +
                    "building, water). 0 = vanilla, 100 = always. Arrows that hit nothing are lost.",
                    new AcceptableValueRange<float>(0f, 100f)));

            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll(typeof(Plugin).Assembly);

            Log.LogInfo($"{PluginName} {PluginVersion} loaded.");
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
        }
    }

    /// <summary>Drops the arrow back at the hit point when the projectile stops.</summary>
    [HarmonyPatch(typeof(Projectile), nameof(Projectile.OnHit))]
    internal static class Projectile_OnHit_Patch
    {
        /// <summary>Distance the arrow is pulled back along its trajectory, out of the target's collider.</summary>
        private const float PullBack = 0.3f;

        private static void Prefix(Projectile __instance, out bool __state)
        {
            __state = __instance.m_didHit;
        }

        private static void Postfix(Projectile __instance, Vector3 hitPoint, bool __state)
        {
            if (!Plugin.Enabled.Value || __state || !__instance.m_didHit) return;

            ItemDrop.ItemData ammo = __instance.m_ammo;
            if (ammo == null || ammo.m_dropPrefab == null) return;
            if (ammo.m_shared.m_itemType != ItemDrop.ItemData.ItemType.Ammo) return;
            if (!(__instance.m_owner is Player)) return;

            if (UnityEngine.Random.value * 100f >= Plugin.RecoveryChance.Value) return;

            Vector3 back = __instance.m_vel.sqrMagnitude > 0.001f ? __instance.m_vel.normalized * PullBack : Vector3.zero;
            ItemDrop.DropItem(ammo, 1, hitPoint - back, Quaternion.identity);
        }
    }
}
