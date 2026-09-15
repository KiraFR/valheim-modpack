using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace PartialSleep
{
    /// <summary>
    /// Skips the night when a share of the players is in bed, instead of all of them.
    ///
    /// - Sleeping is decided by the server alone: Game.UpdateSleeping runs every 2 s where ZNet.IsServer(), reads
    ///   ZDOVars.s_inBed from every player's ZDO (replicated by each client) and, when everyone is in bed, calls
    ///   EnvMan.SkipToMorning and sends the SleepStart routed RPC. The mod replaces that method, so only the dedicated
    ///   server (or the host) needs it.
    /// - Vanilla broadcasts SleepStart and SleepStop to everybody. SleepStart sets Player.m_sleeping, which makes
    ///   InCutscene() true (no controls) and shows the sleep black screen, and SleepStop calls AttachStop, which would
    ///   pull a player off a chair or a ship. The mod therefore sends them only to the players who were in bed, and
    ///   SleepStop also to the server itself: its SleepStop handler saves the world.
    /// - When the server is also a player (hosted game) and was awake, AttachStop is skipped during its own SleepStop.
    /// - ShowProgress tells everyone, top left, how many players are in bed and how many are needed, whenever that
    ///   changes. It goes through the vanilla ShowMessage RPC, so clients do not need the mod to see it.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "valheim.partialsleep";
        public const string PluginName = "PartialSleep";
        public const string PluginVersion = "1.0.0";

        internal static ManualLogSource Log;

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<int> SleepPercent;
        internal static ConfigEntry<bool> ShowProgress;

        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;

            Enabled = Config.Bind("General", "Enabled", true, "Enables or disables the mod.");

            SleepPercent = Config.Bind("General", "SleepPercent", 50,
                new ConfigDescription(
                    "Share of the connected players who must be in bed to skip the night, in percent, rounded up and at " +
                    "least one player. 50 = 1 of 2, 2 of 3, 2 of 4; 100 = everyone (vanilla).",
                    new AcceptableValueRange<int>(1, 100)));

            ShowProgress = Config.Bind("General", "ShowProgress", true,
                "Tells every player, top left, how many players are in bed and how many are needed, whenever it changes.");

            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll(typeof(Plugin).Assembly);

            Log.LogInfo($"{PluginName} {PluginVersion} loaded.");
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
        }
    }

    internal static class Sleep
    {
        /// <summary>Peers sent SleepStart for the current skip, which get SleepStop when it ends.</summary>
        private static readonly List<long> Sleepers = new List<long>();

        private static int _shownInBed = -1;
        private static int _shownTotal = -1;

        /// <summary>Set while the server runs its own SleepStop and its player was not asleep.</summary>
        internal static bool KeepAttached;

        /// <summary>Number of players in bed needed to skip the night out of <paramref name="total"/>.</summary>
        internal static int Required(int total)
        {
            return Math.Max(1, (int)Math.Ceiling(total * Plugin.SleepPercent.Value / 100.0));
        }

        /// <summary>
        /// Same players as ZNet.GetAllCharacterZDOS (the server's own character, then every ready peer with one), but
        /// keeping the routed RPC peer ID of those in bed.
        /// </summary>
        private static int CollectInBed(List<long> inBed)
        {
            ZNet net = ZNet.instance;
            int total = 0;

            ZDO local = net.m_zdoMan.GetZDO(net.m_characterID);
            if (local != null)
            {
                total++;
                if (local.GetBool(ZDOVars.s_inBed)) inBed.Add(ZRoutedRpc.instance.m_id);
            }

            foreach (ZNetPeer peer in net.m_peers)
            {
                if (!peer.IsReady() || peer.m_characterID.IsNone()) continue;
                ZDO zdo = net.m_zdoMan.GetZDO(peer.m_characterID);
                if (zdo == null) continue;
                total++;
                if (zdo.GetBool(ZDOVars.s_inBed)) inBed.Add(peer.m_uid);
            }

            return total;
        }

        /// <summary>Replacement for Game.UpdateSleeping, same flow with a threshold and targeted RPCs.</summary>
        internal static void Update(Game game)
        {
            ZNet net = ZNet.instance;
            if (!net.IsServer()) return;

            EnvMan env = EnvMan.instance;
            double now = net.GetTimeSeconds();

            if (game.m_sleeping)
            {
                if (env.IsTimeSkipping() || CinematicsManager.IsPlaying()) return;

                game.m_lastSleepTime = now;
                game.m_sleeping = false;

                long self = ZRoutedRpc.instance.m_id;
                if (!Sleepers.Contains(self)) Sleepers.Add(self);
                foreach (long peer in Sleepers) ZRoutedRpc.instance.InvokeRoutedRPC(peer, "SleepStop");
                Sleepers.Clear();
                return;
            }

            if (env.IsTimeSkipping() || !(EnvMan.IsAfternoon() || EnvMan.IsNight()))
            {
                _shownInBed = _shownTotal = -1;
                return;
            }

            var inBed = new List<long>();
            int total = CollectInBed(inBed);
            int required = Required(total);

            if (inBed.Count < required)
            {
                ShowProgress(inBed.Count, total, required);
                return;
            }

            // Vanilla waits 10 s after a skip before allowing the next one.
            if (now - game.m_lastSleepTime < 10.0) return;

            env.SkipToMorning();
            game.m_sleeping = true;
            _shownInBed = _shownTotal = -1;

            Sleepers.AddRange(inBed);
            foreach (long peer in Sleepers) ZRoutedRpc.instance.InvokeRoutedRPC(peer, "SleepStart");

            Plugin.Log.LogInfo($"{inBed.Count}/{total} players in bed ({required} needed): skipping to morning.");
        }

        private static void ShowProgress(int inBed, int total, int required)
        {
            if (inBed == _shownInBed && total == _shownTotal) return;
            bool first = _shownInBed < 0;
            _shownInBed = inBed;
            _shownTotal = total;

            // Nobody in bed since the evening started: nothing worth announcing.
            if (!Plugin.ShowProgress.Value || (inBed == 0 && first)) return;

            string text = $"In bed: {inBed}/{total} players, {required} needed to skip the night";
            ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, "ShowMessage",
                (int)MessageHud.MessageType.TopLeft, text);
        }
    }

    /// <summary>The server's sleep check, every 2 s: replaced entirely while the mod is enabled.</summary>
    [HarmonyPatch(typeof(Game), nameof(Game.UpdateSleeping))]
    internal static class Game_UpdateSleeping_Patch
    {
        private static bool Prefix(Game __instance)
        {
            if (!Plugin.Enabled.Value) return true;
            Sleep.Update(__instance);
            return false;
        }
    }

    /// <summary>
    /// SleepStop always runs on the server, for the world save. When the server is a hosted game whose player was
    /// awake, its AttachStop must not pull that player off a chair or a ship.
    /// </summary>
    [HarmonyPatch(typeof(Game), nameof(Game.SleepStop))]
    internal static class Game_SleepStop_Patch
    {
        private static void Prefix()
        {
            Player player = Player.m_localPlayer;
            Sleep.KeepAttached = player != null && !player.IsSleeping();
        }

        private static void Postfix()
        {
            Sleep.KeepAttached = false;
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.AttachStop))]
    internal static class Player_AttachStop_Patch
    {
        private static bool Prefix()
        {
            return !Sleep.KeepAttached;
        }
    }
}
