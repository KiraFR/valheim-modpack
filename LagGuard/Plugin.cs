using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace LagGuard
{
    /// <summary>
    /// Server-side fixes for lag caused by a struggling player, and for items piling up on the ground.
    ///
    /// Ownership:
    /// - Every object (creature, dropped item, piece, ...) is simulated by its network owner, and the dedicated server only
    ///   relays. ZDOMan.ReleaseNearbyZDOS only hands an object over when its owner leaves its active area: a player with a
    ///   bad connection or a slow computer keeps everything around them, and everyone nearby sees it lag.
    /// - Every time the game runs that pass (every 2 s), the mod rates each player: ping (Steam), server send queue
    ///   towards them, and, when they have LagGuardClient, their frame rate and a heartbeat written into their player ZDO
    ///   (a stale heartbeat means a frozen game or a clogged upload). A player bad for BadSeconds in a row is "lagging"
    ///   until good again for RecoverSeconds in a row, so a ping spike never moves anything.
    /// - The persistent objects a lagging player owns inside the active area of a healthy player are given to that player.
    ///   Vanilla then keeps them there (their new owner is in range). A lagging player alone keeps their objects: nobody
    ///   else has them loaded. Ships, carts, saddles and containers are never moved: their owner is the player using them.
    ///
    /// Cleanup:
    /// - Vanilla ItemDrop.TimedDestruction removes an item after 1 h, but only runs on loaded items, at their owner's:
    ///   items left where nobody goes back stay in the save forever. The server holds every ZDO, loaded or not.
    /// - Every CleanupInterval minutes, it destroys the dropped items older than MaxItemAge (spawn time stored in the ZDO)
    ///   with the vanilla exceptions: not within 25 m of a player, not a piece, not above 28 m inside a base (the
    ///   PlayerBase area of a workbench or other station, rebuilt from the ZDOs since unloaded pieces have no collider).
    ///   Swimming fish and items the game never despawns (m_autoDestroy off) are left alone.
    ///
    /// Runs wherever ZNet is the server (dedicated server, or the host of a game started from the menu); does nothing on
    /// clients. Players do not need the mod; LagGuardClient only adds the frame rate and heartbeat signals.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "valheim.lagguard";
        public const string PluginName = "LagGuard";
        public const string PluginVersion = "1.0.0";

        internal static ManualLogSource Log;

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> Rebalance;
        internal static ConfigEntry<int> MaxPing;
        internal static ConfigEntry<int> MinFps;
        internal static ConfigEntry<int> MaxSendQueue;
        internal static ConfigEntry<float> HeartbeatTimeout;
        internal static ConfigEntry<float> BadSeconds;
        internal static ConfigEntry<float> RecoverSeconds;
        internal static ConfigEntry<bool> Cleanup;
        internal static ConfigEntry<float> CleanupInterval;
        internal static ConfigEntry<float> MaxItemAge;
        internal static ConfigEntry<float> PlayerRange;
        internal static ConfigEntry<bool> ProtectBases;

        // Written by LagGuardClient into the player's own ZDO; the strings must match there.
        internal static readonly int FpsKey = "LagGuard.fps".GetStableHashCode();
        internal static readonly int BeatKey = "LagGuard.beat".GetStableHashCode();

        private Harmony _harmony;
        private float _cleanupTimer;

        private void Awake()
        {
            Log = Logger;

            Enabled = Config.Bind("General", "Enabled", true, "Enables or disables the mod.");

            Rebalance = Config.Bind("Ownership", "Rebalance", true,
                "Hands the objects owned by a lagging player to a healthy player standing nearby.");
            MaxPing = Config.Bind("Ownership", "MaxPing", 250,
                new ConfigDescription("Ping (ms) above which a player counts as lagging. 0 = ignore the ping.",
                    new AcceptableValueRange<int>(0, 5000)));
            MinFps = Config.Bind("Ownership", "MinFps", 15,
                new ConfigDescription(
                    "Frame rate under which a player counts as lagging. Only known for players who have LagGuardClient. 0 = ignore.",
                    new AcceptableValueRange<int>(0, 200)));
            MaxSendQueue = Config.Bind("Ownership", "MaxSendQueue", 10240,
                new ConfigDescription(
                    "Bytes waiting to be sent to a player above which they count as lagging. The game stops sending objects to a " +
                    "player past 10240. 0 = ignore.",
                    new AcceptableValueRange<int>(0, 1000000)));
            HeartbeatTimeout = Config.Bind("Ownership", "HeartbeatTimeout", 5f,
                new ConfigDescription(
                    "Seconds without news from LagGuardClient (sent every second) after which a player counts as lagging. 0 = ignore.",
                    new AcceptableValueRange<float>(0f, 60f)));
            BadSeconds = Config.Bind("Ownership", "BadSeconds", 10f,
                new ConfigDescription("Seconds a player must stay bad in a row before their objects are handed over.",
                    new AcceptableValueRange<float>(0f, 300f)));
            RecoverSeconds = Config.Bind("Ownership", "RecoverSeconds", 30f,
                new ConfigDescription("Seconds a lagging player must stay good in a row before counting as healthy again.",
                    new AcceptableValueRange<float>(0f, 600f)));

            Cleanup = Config.Bind("Cleanup", "Cleanup", true, "Periodically destroys items left on the ground for too long.");
            CleanupInterval = Config.Bind("Cleanup", "CleanupInterval", 10f,
                new ConfigDescription("Minutes between two cleanups.", new AcceptableValueRange<float>(1f, 1440f)));
            MaxItemAge = Config.Bind("Cleanup", "MaxItemAge", 30f,
                new ConfigDescription("Minutes an item may stay on the ground before it is destroyed. Vanilla: 60, loaded items only.",
                    new AcceptableValueRange<float>(1f, 10080f)));
            PlayerRange = Config.Bind("Cleanup", "PlayerRange", 25f,
                new ConfigDescription("Items closer than this (in metres) to a player are kept. Vanilla: 25.",
                    new AcceptableValueRange<float>(0f, 500f)));
            ProtectBases = Config.Bind("Cleanup", "ProtectBases", true,
                "Keeps items lying inside a base (workbench area), as the game does.");

            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll(typeof(Plugin).Assembly);

            Log.LogInfo($"{PluginName} {PluginVersion} loaded.");
        }

        private void Update()
        {
            if (!Enabled.Value || !Cleanup.Value || !IsServerReady()) return;

            _cleanupTimer += Time.unscaledDeltaTime;
            if (_cleanupTimer < CleanupInterval.Value * 60f) return;
            _cleanupTimer = 0f;

            ItemCleanup.Run();
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
        }

        internal static bool IsServerReady()
        {
            return ZNet.instance != null && ZNet.instance.IsServer() && ZDOMan.instance != null && ZNetScene.instance != null;
        }
    }

    /// <summary>Runs right after the game's own ownership pass (ZDOMan.ReleaseZDOS resets its timer when it runs).</summary>
    [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.ReleaseZDOS))]
    internal static class ZDOManReleaseZDOSPatch
    {
        private static void Postfix(ZDOMan __instance)
        {
            if (__instance.m_releaseZDOTimer != 0f) return;
            if (!Plugin.Enabled.Value || !Plugin.Rebalance.Value || !Plugin.IsServerReady()) return;

            OwnerBalance.Run(__instance);
        }
    }

    internal static class OwnerBalance
    {
        private class PeerHealth
        {
            public string Name;
            public bool Lagging;
            public float BadSince = -1f;
            public float GoodSince = -1f;
            public int LastBeat;
            public float LastBeatTime;
        }

        private static readonly Dictionary<long, PeerHealth> Health = new Dictionary<long, PeerHealth>();
        private static readonly HashSet<long> Lagging = new HashSet<long>();
        private static readonly List<ZDO> NearObjects = new List<ZDO>();
        private static readonly Dictionary<int, bool> Movable = new Dictionary<int, bool>();

        internal static void Run(ZDOMan zdoMan)
        {
            List<ZNetPeer> peers = ZNet.instance.GetPeers();
            float now = Time.time;

            Lagging.Clear();
            foreach (ZNetPeer peer in peers)
            {
                if (!peer.IsReady()) continue;
                if (UpdateHealth(peer, zdoMan, now)) Lagging.Add(peer.m_uid);
            }
            ForgetDisconnected(peers);

            if (Lagging.Count == 0) return;

            int moved = 0;
            foreach (ZNetPeer peer in peers)
            {
                if (!peer.IsReady() || Lagging.Contains(peer.m_uid)) continue;
                moved += TakeOver(zdoMan, peer.m_uid, peer.GetRefPos());
            }
            // The host of a game started from the menu is a player too.
            if (!ZNet.instance.IsDedicated() && Player.m_localPlayer != null)
                moved += TakeOver(zdoMan, ZDOMan.GetSessionID(), ZNet.instance.GetReferencePosition());

            if (moved > 0) Plugin.Log.LogInfo($"Handed {moved} object(s) owned by lagging players over to nearby players.");
        }

        /// <summary>Rates a peer and returns whether it currently counts as lagging.</summary>
        private static bool UpdateHealth(ZNetPeer peer, ZDOMan zdoMan, float now)
        {
            if (!Health.TryGetValue(peer.m_uid, out PeerHealth health))
            {
                health = new PeerHealth { LastBeatTime = now };
                Health[peer.m_uid] = health;
            }
            health.Name = peer.m_playerName;

            string reason = GetBadReason(peer, zdoMan, health, now);

            if (reason != null)
            {
                health.GoodSince = -1f;
                if (health.BadSince < 0f) health.BadSince = now;
                if (!health.Lagging && now - health.BadSince >= Plugin.BadSeconds.Value)
                {
                    health.Lagging = true;
                    Plugin.Log.LogInfo($"{health.Name} is lagging ({reason}): their objects go to nearby players.");
                }
            }
            else
            {
                health.BadSince = -1f;
                if (health.GoodSince < 0f) health.GoodSince = now;
                if (health.Lagging && now - health.GoodSince >= Plugin.RecoverSeconds.Value)
                {
                    health.Lagging = false;
                    Plugin.Log.LogInfo($"{health.Name} is no longer lagging.");
                }
            }
            return health.Lagging;
        }

        /// <summary>Why the peer looks bad right now, or null if it looks fine.</summary>
        private static string GetBadReason(ZNetPeer peer, ZDOMan zdoMan, PeerHealth health, float now)
        {
            string reason = null;

            if (Plugin.MaxPing.Value > 0)
            {
                peer.m_socket.GetConnectionQuality(out _, out _, out int ping, out _, out _);
                if (ping > Plugin.MaxPing.Value) reason = $"ping {ping} ms";
            }

            if (Plugin.MaxSendQueue.Value > 0)
            {
                int queue = peer.m_socket.GetSendQueueSize();
                if (queue > Plugin.MaxSendQueue.Value) reason = Append(reason, $"send queue {queue} bytes");
            }

            ZDO character = peer.m_characterID.IsNone() ? null : zdoMan.GetZDO(peer.m_characterID);
            if (character != null)
            {
                int beat = character.GetInt(Plugin.BeatKey);
                if (beat != health.LastBeat)
                {
                    health.LastBeat = beat;
                    health.LastBeatTime = now;
                }

                // 0: the player does not have LagGuardClient, so neither signal is known.
                if (beat != 0)
                {
                    int fps = character.GetInt(Plugin.FpsKey);
                    if (Plugin.MinFps.Value > 0 && fps > 0 && fps < Plugin.MinFps.Value) reason = Append(reason, $"{fps} fps");

                    float silence = now - health.LastBeatTime;
                    if (Plugin.HeartbeatTimeout.Value > 0f && silence > Plugin.HeartbeatTimeout.Value)
                        reason = Append(reason, $"no news for {silence:0} s");
                }
            }

            return reason;
        }

        private static string Append(string reason, string part)
        {
            return reason == null ? part : reason + ", " + part;
        }

        private static void ForgetDisconnected(List<ZNetPeer> peers)
        {
            if (Health.Count <= peers.Count) return;

            var connected = new HashSet<long>();
            foreach (ZNetPeer peer in peers) connected.Add(peer.m_uid);

            var gone = new List<long>();
            foreach (long uid in Health.Keys)
                if (!connected.Contains(uid)) gone.Add(uid);
            foreach (long uid in gone) Health.Remove(uid);
        }

        /// <summary>
        /// Gives the new owner the lagging players' objects inside its active area, as vanilla
        /// ReleaseNearbyZDOS does for objects whose owner went away.
        /// </summary>
        private static int TakeOver(ZDOMan zdoMan, long newOwner, Vector3 refPos)
        {
            Vector2s zone = ZoneSystem.GetZone(refPos);
            SimulationDistance synced = ZNet.instance.GetSyncedSimulationDistance();
            var near = new SimulationDistance(synced.NearSimulationDistance, 0, synced.IsClassic);

            NearObjects.Clear();
            zdoMan.FindSectorObjects(zone, near, NearObjects);

            int moved = 0;
            foreach (ZDO zdo in NearObjects)
            {
                if (!zdo.Persistent || !Lagging.Contains(zdo.GetOwner())) continue;
                if (!ZNetScene.InActiveArea(zdo.GetPosition(), zone) || !IsMovable(zdo.GetPrefab())) continue;

                zdo.SetOwner(newOwner);
                moved++;
            }
            return moved;
        }

        /// <summary>Objects whose owner is chosen by the game from who uses them are never moved.</summary>
        private static bool IsMovable(int prefabHash)
        {
            if (Movable.TryGetValue(prefabHash, out bool movable)) return movable;

            GameObject prefab = ZNetScene.instance.GetPrefab(prefabHash);
            movable = prefab != null &&
                      prefab.GetComponent<Ship>() == null &&
                      prefab.GetComponent<Vagon>() == null &&
                      prefab.GetComponent<Container>() == null &&
                      prefab.GetComponentInChildren<Sadle>(true) == null;
            Movable[prefabHash] = movable;
            return movable;
        }
    }

    internal static class ItemCleanup
    {
        /// <summary>What a prefab means for the cleanup; computed once per prefab.</summary>
        private class PrefabInfo
        {
            public bool Removable;
            public float BaseRadius;
            public Vector3 BaseOffset;
        }

        // Same threshold as ItemDrop.IsInsideBase: below it (under water), items in a base are not protected.
        private const float BaseMinHeight = 28f;

        private static readonly Dictionary<int, PrefabInfo> Infos = new Dictionary<int, PrefabInfo>();

        internal static void Run()
        {
            var bases = new List<Vector4>();
            var items = new List<ZDO>();

            foreach (ZDO zdo in ZDOMan.instance.m_objectsByID.Values)
            {
                PrefabInfo info = GetInfo(zdo.GetPrefab());
                if (info == null) continue;
                if (info.Removable) items.Add(zdo);
                else if (info.BaseRadius > 0f) bases.Add(ToSphere(zdo, info));
            }

            List<Vector3> players = GetPlayerPositions();
            DateTime now = ZNet.instance.GetTime();
            double maxAge = Plugin.MaxItemAge.Value * 60.0;
            float playerRangeSqr = Plugin.PlayerRange.Value * Plugin.PlayerRange.Value;
            long server = ZDOMan.GetSessionID();

            int removed = 0;
            foreach (ZDO zdo in items)
            {
                long spawnTime = zdo.GetLong(ZDOVars.s_spawnTime);
                if (spawnTime == 0L || (now - new DateTime(spawnTime)).TotalSeconds < maxAge) continue;
                if (zdo.GetBool(ZDOVars.s_piece)) continue;

                Vector3 position = zdo.GetPosition();
                if (IsNear(position, players, playerRangeSqr)) continue;
                if (Plugin.ProtectBases.Value && position.y > BaseMinHeight && IsInside(position, bases)) continue;

                // Only the owner can destroy a ZDO; the destruction is then sent to every player.
                zdo.SetOwner(server);
                ZDOMan.instance.DestroyZDO(zdo);
                removed++;
            }

            Plugin.Log.LogInfo($"Cleanup: {removed} item(s) older than {Plugin.MaxItemAge.Value:0.#} min destroyed, " +
                               $"{items.Count - removed} kept, {bases.Count} base area(s).");
        }

        private static PrefabInfo GetInfo(int prefabHash)
        {
            if (Infos.TryGetValue(prefabHash, out PrefabInfo info)) return info;

            GameObject prefab = ZNetScene.instance.GetPrefab(prefabHash);
            if (prefab != null)
            {
                info = new PrefabInfo();
                ItemDrop item = prefab.GetComponent<ItemDrop>();
                info.Removable = item != null && item.m_autoDestroy && prefab.GetComponent<Fish>() == null;
                if (!info.Removable) ReadBaseArea(prefab, info);
                if (!info.Removable && info.BaseRadius <= 0f) info = null;
            }
            Infos[prefabHash] = info;
            return info;
        }

        /// <summary>Largest PlayerBase sphere of a piece, relative to its root.</summary>
        private static void ReadBaseArea(GameObject prefab, PrefabInfo info)
        {
            foreach (EffectArea area in prefab.GetComponentsInChildren<EffectArea>(true))
            {
                if ((area.m_type & EffectArea.Type.PlayerBase) == 0) continue;
                if (!(area.GetComponent<Collider>() is SphereCollider sphere)) continue;

                Vector3 scale = sphere.transform.lossyScale;
                float radius = sphere.radius * Mathf.Max(scale.x, Mathf.Max(scale.y, scale.z));
                if (radius <= info.BaseRadius) continue;

                info.BaseRadius = radius;
                info.BaseOffset = sphere.transform.TransformPoint(sphere.center) - prefab.transform.position;
            }
        }

        private static Vector4 ToSphere(ZDO zdo, PrefabInfo info)
        {
            Vector3 center = zdo.GetPosition() + zdo.GetRotation() * info.BaseOffset;
            return new Vector4(center.x, center.y, center.z, info.BaseRadius);
        }

        private static List<Vector3> GetPlayerPositions()
        {
            var positions = new List<Vector3>();
            foreach (ZNetPeer peer in ZNet.instance.GetPeers())
            {
                ZDO character = peer.m_characterID.IsNone() ? null : ZDOMan.instance.GetZDO(peer.m_characterID);
                positions.Add(character != null ? character.GetPosition() : peer.GetRefPos());
            }
            if (Player.m_localPlayer != null) positions.Add(Player.m_localPlayer.transform.position);
            return positions;
        }

        private static bool IsNear(Vector3 position, List<Vector3> players, float rangeSqr)
        {
            foreach (Vector3 player in players)
                if ((player - position).sqrMagnitude <= rangeSqr) return true;
            return false;
        }

        private static bool IsInside(Vector3 position, List<Vector4> spheres)
        {
            foreach (Vector4 sphere in spheres)
            {
                var center = new Vector3(sphere.x, sphere.y, sphere.z);
                if ((center - position).sqrMagnitude <= sphere.w * sphere.w) return true;
            }
            return false;
        }
    }
}
