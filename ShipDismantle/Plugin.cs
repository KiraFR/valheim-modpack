using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ShipDismantle
{
    /// <summary>
    /// Dismantles a ship from its helm, materials straight into the inventory, and lifts the workbench requirement
    /// when dismantling or repairing a ship with the hammer.
    ///
    /// - Helm action: the rudder (ShipControlls) already receives the alternate interaction (Shift + E) and ignores it,
    ///   so the mod takes it over. It avoids the hammer entirely: Player.RemovePiece needs a piece within 5 m of the
    ///   eye through a raycast, which a hull in the water often refuses, and it drops everything at the ship's position.
    ///   Here the materials of Piece.m_resources are added to the inventory (what does not fit drops at the player's
    ///   feet) and the ship is destroyed with WearNTear.Remove(blockDrop: true), so nothing falls into the water.
    /// - Hammer: Player.RemovePiece and Player.Repair both go through Player.CheckCanRemovePiece, which refuses a piece
    ///   whose m_craftingStation (the workbench for the raft, karve and longship) is not in range. The mod lets that
    ///   check pass for pieces carrying a Ship, which is how Piece.CanBeRemoved finds one.
    /// - Kept from vanilla: wards (PrivateArea), the cargo must be empty, and the vanilla amounts, one third for a
    ///   ship the players did not build.
    ///
    /// Multiplayer: WearNTear.Remove is an RPC to the ship's owner, which may be another player or the dedicated
    /// server; the owner destroys it without checking anything, so the mod stays client only. The materials are added
    /// locally to the player's own inventory. Neither the server nor the other players need the mod.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "valheim.shipdismantle";
        public const string PluginName = "ShipDismantle";
        public const string PluginVersion = "1.1.0";

        internal static ManualLogSource Log;

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> HelmDismantle;
        internal static ConfigEntry<bool> ShowHint;
        internal static ConfigEntry<bool> Hammer;
        internal static ConfigEntry<bool> Repair;
        internal static ConfigEntry<bool> Diagnostics;

        /// <summary>True while Player.Repair runs, to tell repairing apart from removal in CheckCanRemovePiece.</summary>
        internal static bool InRepair;

        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;

            Enabled = Config.Bind("General", "Enabled", true, "Enables or disables the mod.");

            HelmDismantle = Config.Bind("General", "HelmDismantle", true,
                "Dismantles the ship with the alternate interaction on its helm (Shift + E by default): the materials " +
                "go straight into your inventory instead of falling into the water, and no hammer is needed.");

            ShowHint = Config.Bind("General", "ShowHint", true,
                "Adds the dismantle key under the helm's hover text.");

            Hammer = Config.Bind("General", "Hammer", true,
                "Dismantles ships with the hammer without a workbench nearby (vanilla behaviour otherwise: the " +
                "materials drop at the ship's position).");

            Repair = Config.Bind("General", "Repair", true,
                "Repairs ships with the hammer without a workbench nearby.");

            Diagnostics = Config.Bind("General", "Diagnostics", false,
                "Writes to BepInEx/LogOutput.log what the hammer aimed at on every remove press, and which check " +
                "refused it. Only useful to find out why the hammer will not dismantle a ship.");

            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll(typeof(Plugin).Assembly);

            Log.LogInfo($"{PluginName} {PluginVersion} loaded.");
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
        }

        /// <summary>Name of the alternate interaction key, from the current bindings.</summary>
        internal static string AltKeyLabel()
        {
            string button = !ZInput.IsGamepadActive() ? "AltPlace"
                : ZInput.IsNonClassicFunctionality() ? "JoyAltKeys" : "JoyAltPlace";
            string label = ZInput.instance != null ? ZInput.instance.GetBoundKeyString(button, emptyStringOnMissing: true) : "";
            return string.IsNullOrEmpty(label) ? "Shift" : Localization.instance.Localize(label);
        }
    }

    /// <summary>Takes the ship apart and hands its materials to the player.</summary>
    internal static class Dismantle
    {
        /// <summary>Tries to dismantle the ship for that player, telling them why when it refuses.</summary>
        internal static bool TryDismantle(Player player, Ship ship)
        {
            if (player == null || ship == null) return false;

            Piece piece = ship.GetComponentInParent<Piece>();
            WearNTear wearNTear = ship.GetComponent<WearNTear>();
            ZNetView nview = ship.m_nview;
            if (piece == null || wearNTear == null || nview == null || !nview.IsValid())
            {
                player.Message(MessageHud.MessageType.Center, "This ship cannot be dismantled");
                return false;
            }

            if (!PrivateArea.CheckAccess(ship.transform.position))
            {
                player.Message(MessageHud.MessageType.Center, "$msg_privatezone");
                return false;
            }

            Container cargo = ship.GetComponentInChildren<Container>();
            if (cargo != null && cargo.GetInventory().NrOfItems() > 0)
            {
                player.Message(MessageHud.MessageType.Center, "Empty the cargo first");
                return false;
            }

            foreach (Player aboard in ship.m_players)
            {
                if (aboard != null && aboard != player)
                {
                    player.Message(MessageHud.MessageType.Center, "Someone else is aboard");
                    return false;
                }
            }

            List<KeyValuePair<GameObject, int>> refund = Refund(piece);

            // The ship goes first: the owner destroys it, and only then are the materials handed out, so a refused
            // removal cannot leave the player with both the ship and its materials.
            wearNTear.Remove(blockDrop: true);
            piece.m_placeEffect.Create(ship.transform.position, Quaternion.identity, null, 1f, -1, player.GetZDOID());

            int given = 0;
            bool dropped = false;
            foreach (KeyValuePair<GameObject, int> resource in refund)
            {
                given += resource.Value;
                dropped |= !GiveOrDrop(player, resource.Key, resource.Value);
            }

            player.Message(MessageHud.MessageType.Center, dropped
                ? $"Ship dismantled, {given} materials recovered (inventory full, the rest is at your feet)"
                : $"Ship dismantled, {given} materials recovered");
            return true;
        }

        /// <summary>What the ship gives back, following the vanilla rules of Piece.DropResources.</summary>
        private static List<KeyValuePair<GameObject, int>> Refund(Piece piece)
        {
            var refund = new List<KeyValuePair<GameObject, int>>();
            if (ZoneSystem.instance != null && ZoneSystem.instance.GetGlobalKey(piece.FreeBuildKey())) return refund;

            foreach (Piece.Requirement requirement in piece.m_resources)
            {
                if (requirement.m_resItem == null || !requirement.m_recover) continue;

                GameObject prefab = ObjectDB.instance.GetItemPrefab(Utils.GetPrefabName(requirement.m_resItem.name));
                if (prefab == null) continue;

                int amount = requirement.m_amount;
                if (!piece.IsPlacedByPlayer()) amount = Mathf.Max(1, amount / 3);
                if (amount > 0) refund.Add(new KeyValuePair<GameObject, int>(prefab, amount));
            }

            return refund;
        }

        /// <summary>Adds the items to the inventory, stack by stack, and drops at the player's feet what does not fit.</summary>
        private static bool GiveOrDrop(Player player, GameObject prefab, int amount)
        {
            Inventory inventory = player.GetInventory();
            bool everythingFit = true;

            while (amount > 0)
            {
                ItemDrop.ItemData item = prefab.GetComponent<ItemDrop>().m_itemData.Clone();
                item.m_dropPrefab = prefab;
                item.m_worldLevel = (byte)Game.m_worldLevel;
                item.m_stack = Mathf.Min(amount, item.m_shared.m_maxStackSize);
                amount -= item.m_stack;

                // AddItem fills the free stacks and slots it can, and leaves what is left in m_stack.
                if (inventory.AddItem(item)) continue;

                everythingFit = false;
                if (item.m_stack > 0)
                    ItemDrop.DropItem(item, item.m_stack, player.transform.position + Vector3.up, Quaternion.identity);
            }

            return everythingFit;
        }
    }

    /// <summary>Helm: the alternate interaction dismantles the ship instead of doing nothing.</summary>
    [HarmonyPatch(typeof(ShipControlls), nameof(ShipControlls.Interact))]
    internal static class ShipControlls_Interact_Patch
    {
        private static bool Prefix(ShipControlls __instance, Humanoid character, bool repeat, bool alt, ref bool __result)
        {
            if (!Plugin.Enabled.Value || !Plugin.HelmDismantle.Value || !alt || repeat) return true;
            if (!(character is Player player) || player != Player.m_localPlayer) return true;
            if (!__instance.InUseDistance(character)) return true;

            __result = Dismantle.TryDismantle(player, __instance.m_ship);
            return false;
        }
    }

    /// <summary>Hover: the dismantle key under the helm's vanilla "[E] Use".</summary>
    [HarmonyPatch(typeof(ShipControlls), nameof(ShipControlls.GetHoverText))]
    internal static class ShipControlls_GetHoverText_Patch
    {
        private static void Postfix(ShipControlls __instance, ref string __result)
        {
            if (!Plugin.Enabled.Value || !Plugin.HelmDismantle.Value || !Plugin.ShowHint.Value) return;
            if (string.IsNullOrEmpty(__result) || !__instance.InUseDistance(Player.m_localPlayer)) return;

            __result += "\n[<color=yellow><b>" + Plugin.AltKeyLabel() + " + " + Localization.instance.Localize("$KEY_Use") +
                        "</b></color>] Dismantle the ship";
        }
    }

    /// <summary>Marks the repair so CheckCanRemovePiece can tell it apart from a removal.</summary>
    [HarmonyPatch(typeof(Player), nameof(Player.Repair))]
    internal static class Player_Repair_Patch
    {
        private static void Prefix()
        {
            Plugin.InRepair = true;
        }

        private static Exception Finalizer(Exception __exception)
        {
            Plugin.InRepair = false;
            return __exception;
        }
    }

    /// <summary>Hammer: skips the workbench requirement for ships.</summary>
    [HarmonyPatch(typeof(Player), nameof(Player.CheckCanRemovePiece))]
    internal static class Player_CheckCanRemovePiece_Patch
    {
        private static bool Prefix(Piece piece, ref bool __result)
        {
            if (!Plugin.Enabled.Value || piece == null) return true;
            if (!(Plugin.InRepair ? Plugin.Repair.Value : Plugin.Hammer.Value)) return true;
            if (piece.GetComponentInChildren<Ship>() == null) return true;

            __result = true;
            return false;
        }
    }

    /// <summary>
    /// Says in the log what the hammer aimed at and which check refused the removal, since most of the vanilla
    /// refusals in Player.RemovePiece are silent (nothing found, out of the 5 m reach, piece flagged as not
    /// removable). No line at all on a remove press means the game never got that far.
    /// </summary>
    [HarmonyPatch(typeof(Player), nameof(Player.RemovePiece))]
    internal static class Player_RemovePiece_Patch
    {
        private static void Prefix(Player __instance)
        {
            if (!Plugin.Diagnostics.Value) return;

            GameCamera camera = GameCamera.instance;
            if (camera == null) return;

            if (!Physics.Raycast(camera.transform.position, camera.transform.forward, out RaycastHit hit, 50f,
                    __instance.m_removeRayMask))
            {
                Plugin.Log.LogInfo("Remove press: the ray hit nothing removable within 50 m.");
                return;
            }

            float distance = Vector3.Distance(hit.point, __instance.m_eye.position);
            Piece piece = hit.collider.GetComponentInParent<Piece>();
            Plugin.Log.LogInfo($"Remove press: hit {hit.collider.name} " +
                               $"(layer {LayerMask.LayerToName(hit.collider.gameObject.layer)}) at {distance:0.0} m, " +
                               $"reach {__instance.m_maxPlaceDistance} m, piece {(piece ? piece.name : "none")}.");

            if (piece == null) return;

            Ship ship = piece.GetComponentInChildren<Ship>();
            Plugin.Log.LogInfo($"  canBeRemoved={piece.m_canBeRemoved}, CanBeRemoved()={piece.CanBeRemoved()}, " +
                               $"ship={(ship ? ship.name : "none")}" +
                               (ship ? $" (players aboard {ship.m_players.Count})" : "") +
                               $", station={(piece.m_craftingStation ? piece.m_craftingStation.m_name : "none")}, " +
                               $"nview={(piece.GetComponent<ZNetView>() != null)}.");
        }

        private static void Postfix(bool __result)
        {
            if (Plugin.Diagnostics.Value) Plugin.Log.LogInfo($"Remove press: removed={__result}.");
        }
    }
}
