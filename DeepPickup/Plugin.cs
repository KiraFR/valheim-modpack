using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace DeepPickup
{
    /// <summary>
    /// Picks up items lying too deep underwater to reach.
    ///
    /// - Items without a Floating component (ores, metal bars, stone...) sink to the bottom. Player.AutoPickup only looks
    ///   in a sphere of m_autoPickupRange (2 m) around a point 1 m above the player's feet, and a swimming player stays at
    ///   the surface: anything lying deeper than about 3 m can never be picked up.
    /// - After the vanilla pass, the mod looks in a vertical capsule going down MaxDepth metres under that point, with a
    ///   Radius metre radius, for items lying below the water level (ZoneSystem.m_waterLevel: every lake and sea in the
    ///   game is at that height). Those are pulled straight towards the player at PullSpeed; once inside the vanilla
    ///   sphere, the game picks them up as usual.
    /// - The checks are the vanilla ones: auto-pickup items only, no pieces, no second unique key, room and carry weight
    ///   for the item. An item the player does not own is asked for (ItemDrop.RequestOwn) and only moved once owned, so
    ///   two players never pull the same item. Its rigidbody velocity is reset while pulled, or the gravity built up
    ///   during the climb would end up faster than the pull.
    ///
    /// Client only: items are moved by their network owner, which the player becomes before moving them.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "valheim.deeppickup";
        public const string PluginName = "DeepPickup";
        public const string PluginVersion = "1.0.0";

        internal static ManualLogSource Log;

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> MaxDepth;
        internal static ConfigEntry<float> Radius;
        internal static ConfigEntry<float> PullSpeed;

        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;

            Enabled = Config.Bind("General", "Enabled", true, "Enables or disables the mod.");

            MaxDepth = Config.Bind("General", "MaxDepth", 50f,
                new ConfigDescription("How far below the player (in metres) sunken items are pulled up from.",
                    new AcceptableValueRange<float>(2f, 200f)));

            Radius = Config.Bind("General", "Radius", 3f,
                new ConfigDescription(
                    "Horizontal radius (in metres) of the column searched under the player. Vanilla auto-pickup range is 2 m.",
                    new AcceptableValueRange<float>(0.5f, 10f)));

            PullSpeed = Config.Bind("General", "PullSpeed", 15f,
                new ConfigDescription("Speed (in metres per second) at which sunken items rise. Vanilla auto-pickup uses 15.",
                    new AcceptableValueRange<float>(1f, 50f)));

            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll(typeof(Plugin).Assembly);

            Log.LogInfo($"{PluginName} {PluginVersion} loaded.");
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.AutoPickup))]
    internal static class PlayerAutoPickupPatch
    {
        private static readonly Collider[] Colliders = new Collider[128];

        private static void Postfix(Player __instance, float dt)
        {
            if (!Plugin.Enabled.Value || __instance != Player.m_localPlayer) return;
            if (__instance.IsTeleporting() || !Player.m_enableAutoPickup || ZoneSystem.instance == null) return;

            float waterLevel = ZoneSystem.instance.m_waterLevel;
            Vector3 center = __instance.transform.position + Vector3.up;
            // Nothing to fish out if even the bottom of the column is above the water.
            if (center.y - Plugin.MaxDepth.Value > waterLevel) return;

            Vector3 bottom = center + Vector3.down * Plugin.MaxDepth.Value;
            int count = Physics.OverlapCapsuleNonAlloc(center, bottom, Plugin.Radius.Value, Colliders, __instance.m_autoPickupMask);

            for (int i = 0; i < count; i++)
            {
                Rigidbody body = Colliders[i].attachedRigidbody;
                if (!body) continue;

                ItemDrop item = body.GetComponent<ItemDrop>();
                if (item == null) continue;

                Vector3 position = item.transform.position;
                // Only sunken items below the player; anything the vanilla sphere reaches is left to the game.
                if (position.y >= waterLevel || position.y >= center.y) continue;
                if (Vector3.Distance(position, center) <= __instance.m_autoPickupRange) continue;

                if (!item.m_autoPickup || item.IsPiece() || __instance.HaveUniqueKey(item.m_itemData.m_shared.m_name)) continue;
                ZNetView nview = item.GetComponent<ZNetView>();
                if (nview == null || !nview.IsValid()) continue;

                if (!item.CanPickup())
                {
                    item.RequestOwn();
                    continue;
                }

                item.Load();
                if (!__instance.m_inventory.CanAddItem(item.m_itemData) ||
                    item.m_itemData.GetWeight() + __instance.m_inventory.GetTotalWeight() > __instance.GetMaxCarryWeight())
                    continue;

                item.transform.position = Vector3.MoveTowards(position, center, Plugin.PullSpeed.Value * dt);
                body.linearVelocity = Vector3.zero;
            }
        }
    }
}
