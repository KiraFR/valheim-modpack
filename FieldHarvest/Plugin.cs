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
    /// - The kind is the item a pickable gives (shared name of Pickable.m_itemPrefab), so it can come from what the
    ///   crosshair is on: a ripe pickable, an unripe plant (the pickable in Plant.m_grownPrefabs), or a dropped item.
    ///   Looking at a carrot, a carrot sapling or a carrot on the ground harvests the carrots and leaves the turnips.
    /// - Neighbours are searched on the layers the game lets the player interact with (Player.m_interactMask without
    ///   terrain and characters), keeping the pickables whose CanBePicked() is true.
    /// - Each one goes through Pickable.Interact, like a scythe harvest: skill raise, bonus yield, stats, tar check, and
    ///   RPC_Pick sent to the plant's network owner, who drops the items and broadcasts the picked state. The hovered
    ///   pickable is left to the vanilla Interact that ran earlier in the same Update. Nothing is needed on the server
    ///   or on the other players.
    /// - Every press logs what was aimed at and how many plants of that kind were found and harvested.
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
        private static int s_searchMask;
        private static readonly RaycastHit[] s_hits = new RaycastHit[32];
        private static readonly HashSet<Pickable> s_seen = new HashSet<Pickable>();

        /// <summary>What to harvest: the item the pickables give, where to search, and what it was read from.</summary>
        private struct Target
        {
            public string Kind;
            public Vector3 Center;
            /// <summary>Pickable the vanilla Interact already picked this frame, if any.</summary>
            public Pickable Hovered;
            public string Source;
        }

        private static void Postfix(Player __instance)
        {
            if (!Plugin.Enabled.Value || __instance != Player.m_localPlayer) return;
            if (!(ZInput.GetButtonDown("Use") || ZInput.GetButtonDown("JoyUse")) || Hud.InRadial()) return;
            if (!Plugin.AltHeld()) return;

            string blocked = !__instance.TakeInput() ? "input taken by a menu or a cutscene"
                : __instance.InPlaceMode() ? "a build tool is in hand"
                : __instance.IsDead() ? "dead"
                : __instance.InAttack() ? "attacking" : null;
            if (blocked != null)
            {
                Plugin.Log.LogInfo($"Area harvest skipped: {blocked}.");
                return;
            }

            if (!FindTarget(__instance, out Target target))
            {
                Plugin.Log.LogInfo($"Area harvest: nothing to harvest under the crosshair (first hit: {FirstHit(__instance)}).");
                return;
            }

            int harvested = Harvest(__instance, target, out int sameKind, out int ripe);
            Plugin.Log.LogInfo($"Area harvest from {target.Source}: kind {target.Kind}, {sameKind} of that kind within " +
                               $"{Plugin.Radius.Value:0.#} m, {ripe} ripe, {harvested} harvested.");

            int total = harvested + (target.Hovered != null ? 1 : 0);
            if (harvested > 0 && Plugin.ShowCount.Value)
                __instance.Message(MessageHud.MessageType.TopLeft, $"Harvested {total} around");
        }

        /// <summary>Shared name of the item a pickable gives ("$item_carrot"), or its prefab name when it gives none.</summary>
        private static string KindOf(Pickable pickable)
        {
            ItemDrop item = pickable.m_itemPrefab != null ? pickable.m_itemPrefab.GetComponent<ItemDrop>() : null;
            return item != null ? item.m_itemData.m_shared.m_name : "prefab " + NameOf(pickable);
        }

        /// <summary>Kind of the pickable an unripe plant grows into, or null for a plant that does not (a tree sapling).</summary>
        private static string GrownKind(Plant plant)
        {
            foreach (GameObject grown in plant.m_grownPrefabs)
            {
                Pickable pickable = grown != null ? grown.GetComponent<Pickable>() : null;
                if (pickable != null) return KindOf(pickable);
            }
            return null;
        }

        private static string NameOf(Component component)
        {
            return component.gameObject.name.Replace("(Clone)", "");
        }

        /// <summary>
        /// The hovered pickable, or else the first pickable or unripe plant along the crosshair ray within interact distance,
        /// or else the first dropped item on it. Dropped items do not stop the ray, anything else (ground, wall, creature) does.
        /// </summary>
        private static bool FindTarget(Player player, out Target target)
        {
            target = default;
            GameObject hovering = player.m_hovering;
            Pickable hovered = hovering != null ? hovering.GetComponentInParent<Pickable>() : null;
            if (hovered != null)
            {
                target = new Target { Kind = KindOf(hovered), Center = hovered.transform.position, Hovered = hovered, Source = "hovered " + NameOf(hovered) };
                return true;
            }

            bool hasDropped = false;
            Target dropped = default;
            int count = CastCrosshair(player);
            for (int i = 0; i < count; i++)
            {
                RaycastHit hit = s_hits[i];
                if (IsSelf(player, hit)) continue;
                if (Vector3.Distance(player.m_eye.position, hit.point) > player.m_maxInteractDistance + 1f) break;

                Pickable pickable = hit.collider.GetComponentInParent<Pickable>();
                if (pickable != null)
                {
                    target = new Target { Kind = KindOf(pickable), Center = pickable.transform.position, Source = "aimed " + NameOf(pickable) };
                    return true;
                }

                Plant plant = hit.collider.GetComponentInParent<Plant>();
                if (plant != null)
                {
                    string kind = GrownKind(plant);
                    if (kind == null) continue;
                    target = new Target { Kind = kind, Center = plant.transform.position, Source = "unripe " + NameOf(plant) };
                    return true;
                }

                ItemDrop item = hit.collider.GetComponentInParent<ItemDrop>();
                if (item != null)
                {
                    if (!hasDropped)
                    {
                        dropped = new Target { Kind = item.m_itemData.m_shared.m_name, Center = hit.point, Source = "dropped " + NameOf(item) };
                        hasDropped = true;
                    }
                    continue;
                }
                break;
            }

            target = dropped;
            return hasDropped;
        }

        private static int CastCrosshair(Player player)
        {
            if (GameCamera.instance == null) return 0;
            Transform camera = GameCamera.instance.transform;
            int count = Physics.RaycastNonAlloc(camera.position, camera.forward, s_hits, 50f, player.m_interactMask);
            System.Array.Sort(s_hits, 0, count, ByDistance.Instance);
            return count;
        }

        private static bool IsSelf(Player player, RaycastHit hit)
        {
            return hit.collider.attachedRigidbody != null && hit.collider.attachedRigidbody.gameObject == player.gameObject;
        }

        /// <summary>For the log: what the crosshair ray hits first, and how far from the eyes.</summary>
        private static string FirstHit(Player player)
        {
            int count = CastCrosshair(player);
            for (int i = 0; i < count; i++)
            {
                RaycastHit hit = s_hits[i];
                if (IsSelf(player, hit)) continue;
                float distance = Vector3.Distance(player.m_eye.position, hit.point);
                return $"{hit.collider.name} on layer {LayerMask.LayerToName(hit.collider.gameObject.layer)} at {distance:0.0} m";
            }
            return "nothing";
        }

        /// <summary>RaycastNonAlloc returns hits in no particular order.</summary>
        private sealed class ByDistance : IComparer<RaycastHit>
        {
            internal static readonly ByDistance Instance = new ByDistance();
            public int Compare(RaycastHit a, RaycastHit b) => a.distance.CompareTo(b.distance);
        }

        private static int Harvest(Player player, Target target, out int sameKind, out int ripe)
        {
            // Player.m_interactMask without terrain and characters: whatever the player can pick, the search can find
            if (s_searchMask == 0)
                s_searchMask = LayerMask.GetMask("item", "piece", "piece_nonsolid", "Default", "static_solid", "Default_small", "vehicle");

            s_seen.Clear();
            if (target.Hovered != null) s_seen.Add(target.Hovered);
            sameKind = 0;
            ripe = 0;
            int harvested = 0;

            foreach (Collider collider in Physics.OverlapSphere(target.Center, Plugin.Radius.Value, s_searchMask))
            {
                Pickable pickable = collider.GetComponentInParent<Pickable>();
                if (pickable == null || !s_seen.Add(pickable)) continue;

                ZNetView nview = pickable.m_nview;
                if (nview == null || !nview.IsValid() || KindOf(pickable) != target.Kind) continue;
                sameKind++;
                if (!pickable.CanBePicked()) continue;
                ripe++;

                pickable.Interact(player, repeat: false, alt: false);
                harvested++;
            }

            s_seen.Clear();
            return harvested;
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
