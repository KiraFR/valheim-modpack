using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace NoStumps
{
    /// <summary>
    /// Felling a tree also clears its stump.
    ///
    /// - The stump is not a state of the tree: when its health reaches 0, TreeBase.RPC_Damage calls SpawnLog, which
    ///   spawns the falling log and a separate m_stubPrefab object (a Destructible, with DropOnDestroyed for its wood).
    /// - The prefix hides m_stubPrefab from SpawnLog, the postfix puts it back and spawns the stump itself so it holds
    ///   the instance, then destroys it at once through Destructible.Destroy: drops, destruction effect and network
    ///   removal all go through the game's own code, exactly as if the stump had been chopped.
    /// - DropStumpWood = false removes the stump without drops or effect.
    ///
    /// Multiplayer: RPC_Damage only runs on the tree's network owner, which spawns and owns the stump, so the stump is
    /// destroyed by the same peer that created it. Install it for every player and on the dedicated server, which
    /// permanently owns the trees around the world spawn.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "valheim.nostumps";
        public const string PluginName = "NoStumps";
        public const string PluginVersion = "1.0.0";

        internal static ManualLogSource Log;

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> DropStumpWood;

        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;

            Enabled = Config.Bind("General", "Enabled", true, "Enables or disables the mod.");

            DropStumpWood = Config.Bind("General", "DropStumpWood", true,
                "Drops the stump's wood as if it had been chopped. false = the stump just disappears, without drops.");

            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll(typeof(Plugin).Assembly);

            Log.LogInfo($"{PluginName} {PluginVersion} loaded.");
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
        }

        /// <summary>Spawns the stump the way SpawnLog does, then destroys it right away.</summary>
        internal static void SpawnAndClearStump(TreeBase tree, GameObject stubPrefab)
        {
            Transform t = tree.transform;
            GameObject stub = Object.Instantiate(stubPrefab, t.position, t.rotation);
            ZNetView nview = stub.GetComponent<ZNetView>();
            nview.SetLocalScale(t.localScale);

            Destructible destructible = stub.GetComponent<Destructible>();
            if (DropStumpWood.Value && destructible != null)
                destructible.Destroy();
            else
                ZNetScene.instance.Destroy(stub);
        }
    }

    /// <summary>The tree falls: SpawnLog runs on the owner, which then clears the stump it would have left.</summary>
    [HarmonyPatch(typeof(TreeBase), nameof(TreeBase.SpawnLog))]
    internal static class TreeBase_SpawnLog_Patch
    {
        private static void Prefix(TreeBase __instance, out GameObject __state)
        {
            __state = null;
            if (!Plugin.Enabled.Value || __instance.m_stubPrefab == null) return;

            __state = __instance.m_stubPrefab;
            __instance.m_stubPrefab = null;
        }

        private static void Postfix(TreeBase __instance, GameObject __state)
        {
            if (__state == null) return;

            __instance.m_stubPrefab = __state;
            Plugin.SpawnAndClearStump(__instance, __state);
        }
    }
}
