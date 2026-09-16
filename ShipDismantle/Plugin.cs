using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace ShipDismantle
{
    /// <summary>
    /// Dismantles ships with the hammer without a workbench nearby.
    ///
    /// - Player.RemovePiece and Player.Repair both go through Player.CheckCanRemovePiece, which refuses a piece whose
    ///   m_craftingStation (the workbench for the raft, karve and longship) is not in range ("Requires a workbench
    ///   nearby"). The mod lets that check pass for pieces carrying a Ship, which is how Piece.CanBeRemoved finds one.
    /// - Every other vanilla rule stays: wards, no-build zones, the "can't remove now" check of Piece.CanBeRemoved
    ///   (player aboard a raft, private cargo that is not empty) and the resources dropped at the ship's position.
    /// - Repair applies the same bypass to repairing a ship; it is told apart from removal by a flag set while
    ///   Player.Repair runs.
    ///
    /// Multiplayer: the workbench check only runs on the machine of the player holding the hammer. The removal itself
    /// is WearNTear.Remove, an RPC to the ship's owner that does not check the workbench again. Client only: neither
    /// the server nor the other players need the mod.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "valheim.shipdismantle";
        public const string PluginName = "ShipDismantle";
        public const string PluginVersion = "1.0.0";

        internal static ManualLogSource Log;

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> Repair;

        /// <summary>True while Player.Repair runs, to tell repairing apart from removal in CheckCanRemovePiece.</summary>
        internal static bool InRepair;

        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;

            Enabled = Config.Bind("General", "Enabled", true,
                "Enables or disables the mod: dismantling a ship with the hammer no longer needs a workbench nearby.");

            Repair = Config.Bind("General", "Repair", true,
                "Also repairs ships with the hammer without a workbench nearby.");

            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll(typeof(Plugin).Assembly);

            Log.LogInfo($"{PluginName} {PluginVersion} loaded.");
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
        }
    }

    /// <summary>Marks the repair so CheckCanRemovePiece can apply the Repair setting.</summary>
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

    /// <summary>Skips the workbench requirement for ships.</summary>
    [HarmonyPatch(typeof(Player), nameof(Player.CheckCanRemovePiece))]
    internal static class Player_CheckCanRemovePiece_Patch
    {
        private static bool Prefix(Piece piece, ref bool __result)
        {
            if (!Plugin.Enabled.Value || piece == null) return true;
            if (Plugin.InRepair && !Plugin.Repair.Value) return true;
            if (piece.GetComponentInChildren<Ship>() == null) return true;

            __result = true;
            return false;
        }
    }
}
