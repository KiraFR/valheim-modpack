using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace FieldHarvest
{
    /// <summary>
    /// Harvests every ripe plant of the same kind around the one being picked, when the interact key is pressed while
    /// the game's alternate key is held (Shift, or the gamepad's alt keys).
    ///
    /// - Hooks Pickable.Interact rather than reading a key: Player.Update already passes "alt" (the AltPlace /
    ///   JoyAltPlace bindings, as rebound in the settings) and only calls it on the hovered object, so the gesture needs
    ///   no binding of its own and works on a gamepad. Only the press counts: holding the key repeats Interact every
    ///   0.2 s with repeat = true.
    /// - Same kind = same prefab (ZDO prefab hash): looking at a carrot harvests the carrots and leaves the turnips,
    ///   looking at a raspberry bush leaves the blueberry bushes.
    /// - Neighbours are found the way the scythe does it (Piece.OnPlaced): an overlap sphere on the piece,
    ///   piece_nonsolid and item layers, keeping the pickables whose CanBePicked() is true.
    /// - Each neighbour goes through Pickable.Interact, like a scythe harvest: skill raise, bonus yield, stats, tar check,
    ///   and RPC_Pick sent to the plant's network owner, who drops the items and broadcasts the picked state. Nothing is
    ///   needed on the server or on the other players.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "valheim.fieldharvest";
        public const string PluginName = "FieldHarvest";
        public const string PluginVersion = "1.0.0";

        internal static ManualLogSource Log;

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> Radius;
        internal static ConfigEntry<bool> ShowCount;

        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;

            Enabled = Config.Bind("General", "Enabled", true, "Enables or disables the mod.");

            Radius = Config.Bind("General", "Radius", 6f,
                new ConfigDescription(
                    "Distance in metres around the picked plant within which every ripe plant of the same kind is harvested.",
                    new AcceptableValueRange<float>(1f, 30f)));

            ShowCount = Config.Bind("General", "ShowCount", true,
                "Shows how many extra plants were harvested, in the top left corner.");

            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll(typeof(Plugin).Assembly);

            Log.LogInfo($"{PluginName} {PluginVersion} loaded.");
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
        }
    }

    [HarmonyPatch(typeof(Pickable), nameof(Pickable.Interact))]
    internal static class Pickable_Interact_Patch
    {
        private static int s_mask;
        private static readonly HashSet<Pickable> s_seen = new HashSet<Pickable>();

        private static void Postfix(Pickable __instance, Humanoid character, bool repeat, bool alt)
        {
            if (!Plugin.Enabled.Value || !alt || repeat) return;
            if (!(character is Player player) || player != Player.m_localPlayer) return;

            ZNetView origin = __instance.m_nview;
            if (origin == null || !origin.IsValid()) return;

            // The neighbours are picked with alt = false, so this postfix does not run again for them
            int harvested = Harvest(player, __instance, origin.GetZDO().GetPrefab());
            if (harvested > 0 && Plugin.ShowCount.Value)
                player.Message(MessageHud.MessageType.TopLeft, $"Harvested {harvested} more around");
        }

        private static int Harvest(Player player, Pickable origin, int prefab)
        {
            if (s_mask == 0) s_mask = LayerMask.GetMask("piece", "piece_nonsolid", "item");

            s_seen.Clear();
            s_seen.Add(origin);
            int harvested = 0;

            foreach (Collider collider in Physics.OverlapSphere(origin.transform.position, Plugin.Radius.Value, s_mask))
            {
                Pickable pickable = collider.GetComponentInParent<Pickable>();
                if (pickable == null || !s_seen.Add(pickable)) continue;

                ZNetView nview = pickable.m_nview;
                if (nview == null || !nview.IsValid() || nview.GetZDO().GetPrefab() != prefab) continue;
                if (!pickable.CanBePicked()) continue;

                pickable.Interact(player, repeat: false, alt: false);
                harvested++;
            }

            s_seen.Clear();
            return harvested;
        }
    }
}
