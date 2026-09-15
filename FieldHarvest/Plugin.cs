using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace FieldHarvest
{
    /// <summary>
    /// Harvests every ripe plant of the same kind around the one being looked at, when the interact key is pressed while
    /// the game's alternate key is held (Shift, or the gamepad's alt keys).
    ///
    /// - The press is read in a Player.Update postfix with the same inputs Player.Update uses ("Use" / "JoyUse" pressed
    ///   this frame, "alt" from AltPlace / JoyAltPlace / JoyAltKeys), not through Pickable.Interact: the game only
    ///   interacts with the first thing the crosshair ray hits, and after a first harvest that is often a dropped item
    ///   lying in front of the plants, or a plant that is not ripe yet. Holding the key does not repeat the area harvest.
    /// - The kind comes from the hovered pickable, or else from the first pickable along the crosshair ray within
    ///   interact distance, looking through dropped items and unripe plants. Same kind = same prefab (ZDO prefab hash):
    ///   looking at a carrot harvests the carrots and leaves the turnips.
    /// - Neighbours are found the way the scythe does it (Piece.OnPlaced): an overlap sphere on the piece,
    ///   piece_nonsolid and item layers, keeping the pickables whose CanBePicked() is true.
    /// - Each one goes through Pickable.Interact, like a scythe harvest: skill raise, bonus yield, stats, tar check, and
    ///   RPC_Pick sent to the plant's network owner, who drops the items and broadcasts the picked state. The hovered
    ///   pickable is left to the vanilla Interact that ran earlier in the same Update. Nothing is needed on the server
    ///   or on the other players.
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
        internal static ConfigEntry<bool> ShowHint;

        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;

            Enabled = Config.Bind("General", "Enabled", true, "Enables or disables the mod.");

            Radius = Config.Bind("General", "Radius", 6f,
                new ConfigDescription(
                    "Distance in metres around the aimed plant within which every ripe plant of the same kind is harvested.",
                    new AcceptableValueRange<float>(1f, 30f)));

            ShowCount = Config.Bind("General", "ShowCount", true,
                "Shows how many plants the area harvest picked, in the top left corner.");

            ShowHint = Config.Bind("General", "ShowHint", true,
                "Adds the area harvest key under the game's pick up hint when hovering a ripe plant.");

            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll(typeof(Plugin).Assembly);

            Log.LogInfo($"{PluginName} {PluginVersion} loaded.");
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
        }

        /// <summary>The "alt" of Player.Update, read the same way.</summary>
        internal static bool AltHeld()
        {
            if (ZInput.IsNonClassicFunctionality() && ZInput.IsGamepadActive()) return ZInput.GetButton("JoyAltKeys");
            return ZInput.GetButton("AltPlace") || ZInput.GetButton("JoyAltPlace");
        }

        /// <summary>Name of the key AltHeld reads, from the current bindings.</summary>
        internal static string AltKeyLabel()
        {
            string button = !ZInput.IsGamepadActive() ? "AltPlace"
                : ZInput.IsNonClassicFunctionality() ? "JoyAltKeys" : "JoyAltPlace";
            string label = ZInput.instance != null ? ZInput.instance.GetBoundKeyString(button, emptyStringOnMissing: true) : "";
            return string.IsNullOrEmpty(label) ? "Shift" : Localization.instance.Localize(label);
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.Update))]
    internal static class Player_Update_Patch
    {
        private static int s_harvestMask;
        private static readonly RaycastHit[] s_hits = new RaycastHit[32];
        private static readonly HashSet<Pickable> s_seen = new HashSet<Pickable>();

        private static void Postfix(Player __instance)
        {
            if (!Plugin.Enabled.Value || __instance != Player.m_localPlayer) return;
            if (!(ZInput.GetButtonDown("Use") || ZInput.GetButtonDown("JoyUse")) || Hud.InRadial()) return;
            if (!Plugin.AltHeld() || !__instance.TakeInput()) return;
            if (__instance.InPlaceMode() || __instance.IsDead() || __instance.InAttack()) return;

            GameObject hovering = __instance.m_hovering;
            Pickable hovered = hovering != null ? hovering.GetComponentInParent<Pickable>() : null;
            Pickable target = hovered != null ? hovered : AimedPickable(__instance);
            if (target == null || target.m_nview == null || !target.m_nview.IsValid()) return;

            int harvested = Harvest(__instance, target, hovered);
            if (harvested > 0 && Plugin.ShowCount.Value)
                __instance.Message(MessageHud.MessageType.TopLeft, $"Harvested {harvested} around");
        }

        /// <summary>
        /// First pickable along the crosshair ray within interact distance. Dropped items and unripe plants do not stop
        /// the ray, anything else (ground, wall, creature) does.
        /// </summary>
        private static Pickable AimedPickable(Player player)
        {
            if (GameCamera.instance == null) return null;
            Transform camera = GameCamera.instance.transform;

            int count = Physics.RaycastNonAlloc(camera.position, camera.forward, s_hits, 50f, player.m_interactMask);
            System.Array.Sort(s_hits, 0, count, ByDistance.Instance);

            for (int i = 0; i < count; i++)
            {
                RaycastHit hit = s_hits[i];
                if (hit.collider.attachedRigidbody != null && hit.collider.attachedRigidbody.gameObject == player.gameObject) continue;
                if (Vector3.Distance(player.m_eye.position, hit.point) > player.m_maxInteractDistance) return null;

                Pickable pickable = hit.collider.GetComponentInParent<Pickable>();
                if (pickable != null) return pickable;
                if (hit.collider.GetComponentInParent<ItemDrop>() != null || hit.collider.GetComponentInParent<Plant>() != null) continue;
                return null;
            }
            return null;
        }

        /// <summary>RaycastNonAlloc returns hits in no particular order.</summary>
        private sealed class ByDistance : IComparer<RaycastHit>
        {
            internal static readonly ByDistance Instance = new ByDistance();
            public int Compare(RaycastHit a, RaycastHit b) => a.distance.CompareTo(b.distance);
        }

        private static int Harvest(Player player, Pickable target, Pickable hovered)
        {
            if (s_harvestMask == 0) s_harvestMask = LayerMask.GetMask("piece", "piece_nonsolid", "item");
            int prefab = target.m_nview.GetZDO().GetPrefab();

            s_seen.Clear();
            // The vanilla Interact already picked the hovered one this frame
            if (hovered != null) s_seen.Add(hovered);
            int harvested = 0;

            foreach (Collider collider in Physics.OverlapSphere(target.transform.position, Plugin.Radius.Value, s_harvestMask))
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
            return hovered != null && harvested > 0 ? harvested + 1 : harvested;
        }
    }

    /// <summary>Hover: the area harvest key under the vanilla "[E] Pick up", which the game leaves empty once picked.</summary>
    [HarmonyPatch(typeof(Pickable), nameof(Pickable.GetHoverText))]
    internal static class Pickable_GetHoverText_Patch
    {
        private static void Postfix(ref string __result)
        {
            if (!Plugin.Enabled.Value || !Plugin.ShowHint.Value || string.IsNullOrEmpty(__result)) return;
            __result += "\n[<color=yellow><b>" + Plugin.AltKeyLabel() + " + " + Localization.instance.Localize("$KEY_Use") +
                        "</b></color>] Harvest around";
        }
    }
}
