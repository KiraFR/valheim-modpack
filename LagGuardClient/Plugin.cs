using System.Runtime.CompilerServices;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace LagGuardClient
{
    /// <summary>
    /// Client half of LagGuard: unsticks pickups, and tells the server how this game is doing.
    ///
    /// Pickups:
    /// - Pressing the use key on an item owned by another player (ItemDrop.Pickup) sends a single ownership request, then
    ///   checks every 0.05 s whether ownership arrived. If that request is lost (owner changed meanwhile, owner lagging),
    ///   the check runs forever and logs "Im still nto the owner" 20 times a second per item.
    /// - The mod asks again on each check (ItemDrop.RequestOwn has its own backoff: 0.2 s, 0.4 s, 0.8 s...) and gives up
    ///   after PickupTimeout seconds; pressing the key again starts over. The vanilla log line is skipped while waiting.
    ///
    /// Health report:
    /// - Every second, the local player's ZDO gets the frame rate (LagGuard.fps) and a counter (LagGuard.beat). Written
    ///   by the owner of its own player object only, and replicated by the game. LagGuard on the server reads them to
    ///   spot a slow computer (low fps) or a frozen game or clogged upload (counter not moving), which the ping misses.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "valheim.lagguardclient";
        public const string PluginName = "LagGuardClient";
        public const string PluginVersion = "1.0.0";

        internal static ManualLogSource Log;

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> RetryPickup;
        internal static ConfigEntry<float> PickupTimeout;
        internal static ConfigEntry<bool> ReportHealth;

        // Read by LagGuard on the server; the strings must match there.
        private static readonly int FpsKey = "LagGuard.fps".GetStableHashCode();
        private static readonly int BeatKey = "LagGuard.beat".GetStableHashCode();

        private Harmony _harmony;
        private int _frames;
        private float _elapsed;
        private int _beat;

        private void Awake()
        {
            Log = Logger;

            Enabled = Config.Bind("General", "Enabled", true, "Enables or disables the mod.");

            RetryPickup = Config.Bind("Pickup", "RetryPickup", true,
                "Asks again for an item that does not come after pressing the use key, instead of waiting forever.");
            PickupTimeout = Config.Bind("Pickup", "PickupTimeout", 10f,
                new ConfigDescription("Seconds after which a pickup still waiting for the item is abandoned.",
                    new AcceptableValueRange<float>(1f, 60f)));

            ReportHealth = Config.Bind("Server", "ReportHealth", true,
                "Sends the frame rate and a heartbeat to the server, so LagGuard can tell when this game is struggling.");

            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll(typeof(Plugin).Assembly);

            Log.LogInfo($"{PluginName} {PluginVersion} loaded.");
        }

        private void Update()
        {
            _frames++;
            _elapsed += Time.unscaledDeltaTime;
            if (_elapsed < 1f) return;

            int fps = Mathf.RoundToInt(_frames / _elapsed);
            _frames = 0;
            _elapsed = 0f;

            if (!Enabled.Value || !ReportHealth.Value) return;

            Player player = Player.m_localPlayer;
            if (player == null || player.m_nview == null || !player.m_nview.IsValid() || !player.m_nview.IsOwner()) return;

            ZDO zdo = player.m_nview.GetZDO();
            zdo.Set(FpsKey, Mathf.Max(1, fps));
            // Never 0: the server reads 0 as "no LagGuardClient".
            _beat = _beat == int.MaxValue ? 1 : _beat + 1;
            zdo.Set(BeatKey, _beat);
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
        }
    }

    [HarmonyPatch(typeof(ItemDrop), nameof(ItemDrop.Pickup))]
    internal static class ItemDropPickupPatch
    {
        /// <summary>Time the current wait for each item started.</summary>
        internal static readonly ConditionalWeakTable<ItemDrop, StrongBox<float>> WaitStart =
            new ConditionalWeakTable<ItemDrop, StrongBox<float>>();

        private static void Prefix(ItemDrop __instance)
        {
            // A new press starts a new wait.
            WaitStart.Remove(__instance);
        }
    }

    [HarmonyPatch(typeof(ItemDrop), nameof(ItemDrop.PickupUpdate))]
    internal static class ItemDropPickupUpdatePatch
    {
        private static bool Prefix(ItemDrop __instance)
        {
            if (!Plugin.Enabled.Value || !Plugin.RetryPickup.Value) return true;
            if (!__instance.m_nview.IsValid() || __instance.CanPickup())
            {
                ItemDropPickupPatch.WaitStart.Remove(__instance);
                return true;
            }

            StrongBox<float> start = ItemDropPickupPatch.WaitStart.GetValue(__instance, _ => new StrongBox<float>(Time.time));
            if (Time.time - start.Value > Plugin.PickupTimeout.Value)
            {
                __instance.CancelInvoke(nameof(ItemDrop.PickupUpdate));
                ItemDropPickupPatch.WaitStart.Remove(__instance);
                return false;
            }

            __instance.RequestOwn();
            return false;
        }
    }
}
