using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace RowTogether
{
    /// <summary>
    /// Seated passengers of a ship row along with the helmsman.
    ///
    /// - No key, no state to synchronise: a passenger counts as a rower as soon as they are
    ///   seated (the "sit" emote, or any animation tagged "sitting").
    /// - The effect only applies while the helmsman rows (Ship.Speed.Slow or Back); under sail, nothing.
    /// - Each rower adds their bonus to the thrust: the effect is incremental, with no notion of side.
    /// - The helmsman (at the helm) does not row, they ARE the base thrust (x1 multiplier).
    /// - LimitToSailSpeed caps rowing at the ship's sailing speed (see MaxMultiplier).
    ///
    /// Physics is only computed by the ship's network owner. That is NOT necessarily the
    /// helmsman: Ship.UpdateOwner hands ownership to any player on board as soon as the
    /// current owner is no longer there. The mod must therefore be installed by every player
    /// likely to board; it is useless on a dedicated server, which only owns an object when
    /// no player is nearby. The animator of remote players is replicated by
    /// ZSyncAnimation, so IsSitting() is reliable on their local replicas.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "valheim.rowtogether";
        public const string PluginName = "RowTogether";
        public const string PluginVersion = "3.0.1";

        internal static ManualLogSource Log;
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> BonusPerRower;
        internal static ConfigEntry<int> MaxRowers;
        internal static ConfigEntry<bool> LimitToSailSpeed;
        internal static ConfigEntry<bool> ShowMessages;

        private Harmony _harmony;
        private int _lastRowers = -1;
        private bool _wasRowing;

        private void Awake()
        {
            Log = Logger;

            Enabled = Config.Bind("General", "Enabled", true, "Enables or disables the mod.");

            BonusPerRower = Config.Bind("Rowing", "BonusPerRower", 0.5f,
                new ConfigDescription(
                    "Rowing thrust bonus per seated passenger. Each rower adds this bonus to the base thrust, " +
                    "the side of the ship does not matter. 0.5 = 1 rower x1.5, 2 rowers x2, 4 rowers x3. " +
                    "The helmsman does not count: they are the base thrust (x1).",
                    new AcceptableValueRange<float>(0f, 5f)));

            MaxRowers = Config.Bind("Rowing", "MaxRowers", 0,
                new ConfigDescription("Maximum number of rowers taken into account (0 = unlimited).",
                    new AcceptableValueRange<int>(0, 20)));

            LimitToSailSpeed = Config.Bind("Rowing", "LimitToSailSpeed", true,
                "Prevents rowing from exceeding the speed THIS ship would reach under sail with the best wind. " +
                "The cap is computed per ship (a raft stays slow, a longship stays fast), so adding " +
                "rowers beyond it no longer helps. Set to false for no limit at all.");

            ShowMessages = Config.Bind("Feedback", "ShowMessages", true,
                "Shows on-screen messages (number of rowers for the helmsman, starting to row for passengers).");

            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll(typeof(Plugin).Assembly);

            Log.LogInfo($"{PluginName} {PluginVersion} loaded.");
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
        }

        /// <summary>Visual feedback only: the mechanics live in Ship_CustomFixedUpdate_Patch.</summary>
        private void Update()
        {
            if (!Enabled.Value || !ShowMessages.Value) return;
            Player local = Player.m_localPlayer;
            if (local == null) return;

            Ship ship = Ship.GetLocalShip();
            if (ship == null || !ship.IsPlayerInBoat(local))
            {
                _lastRowers = -1;
                _wasRowing = false;
                return;
            }

            bool helmsRowing = IsRowingSpeed(ship);

            // Helmsman: number of active rowers, when it changes.
            if (IsHelmsman(ship, local))
            {
                int rowers = helmsRowing ? Analyze(ship).Rowers : 0;
                if (rowers != _lastRowers)
                {
                    _lastRowers = rowers;
                    if (rowers > 0) Msg(local, Describe(Analyze(ship)));
                }
                _wasRowing = false;
                return;
            }

            // Passenger: notify when they start / stop contributing.
            _lastRowers = -1;
            bool rowing = helmsRowing && IsRower(ship, local);
            if (rowing != _wasRowing)
            {
                _wasRowing = rowing;
                Msg(local, rowing ? "You are rowing. " + Describe(Analyze(ship)) : "You stop rowing.");
            }
        }

        /// <summary>"2 rowers: row x2" — the label shared by the helmsman and the rowers.</summary>
        private static string Describe(Crew crew)
        {
            string plural = crew.Rowers > 1 ? "s" : "";
            string txt = $"{crew.Rowers} rower{plural}: row x{crew.Multiplier:0.0#}";
            if (crew.Capped) txt += " (capped at sailing speed)";
            return txt;
        }

        // ----- Crew -----

        internal static bool IsRowingSpeed(Ship ship)
        {
            return ship.m_speed == Ship.Speed.Slow || ship.m_speed == Ship.Speed.Back;
        }

        internal static bool IsHelmsman(Ship ship, Player p)
        {
            if (ship.m_shipControlls == null) return false;
            return ship.m_shipControlls.GetUser() == p.GetPlayerID();
        }

        /// <summary>
        /// A passenger rows if they are seated and not at the helm. IsSitting() compares the animator's
        /// current state to the "sitting" tag, and that animator is replicated on every client.
        /// </summary>
        internal static bool IsRower(Ship ship, Player p)
        {
            if (p == null || p.IsDead()) return false;
            if (IsHelmsman(ship, p)) return false;
            return p.IsSitting();
        }

        internal struct Crew
        {
            public int Rowers;
            public float Multiplier;
            /// <summary>True if LimitToSailSpeed trimmed the multiplier.</summary>
            public bool Capped;
        }

        internal static Crew Analyze(Ship ship)
        {
            Crew crew = default;
            List<Player> players = ship.m_players;
            for (int i = 0; i < players.Count; i++)
            {
                if (IsRower(ship, players[i])) crew.Rowers++;
            }

            if (MaxRowers.Value > 0) crew.Rowers = Mathf.Min(crew.Rowers, MaxRowers.Value);
            crew.Multiplier = 1f + BonusPerRower.Value * crew.Rowers;

            if (LimitToSailSpeed.Value)
            {
                float max = MaxMultiplier(ship);
                if (crew.Multiplier > max)
                {
                    crew.Multiplier = max;
                    crew.Capped = true;
                }
            }
            return crew;
        }

        // ----- "Sailing speed" cap -----

        /// <summary>
        /// Maximum forward thrust factor of the sail, over all wind directions, as a fraction of
        /// m_sailForceFactor. Ship.GetWindAngleFactor is 0.7 with a tailwind and 1.0 with a crosswind,
        /// but the crosswind loses in projection onto the ship's axis: the real maximum is at
        /// ~65° from the heading, i.e. ~0.737. A pure downwind run is not the optimum in Valheim.
        /// </summary>
        private const float BestSailForwardFactor = 0.737f;

        /// <summary>
        /// Multiplier beyond which rowing would go faster than sailing.
        ///
        /// Sail and oars push the ship in the same physics loop and suffer the same
        /// quadratic drag (m_dampingForward). With equal acceleration per physics step, the
        /// equilibrium speed is therefore equal: equalising the accelerations is enough, without
        /// having to model the drag. Sail: BestSailForwardFactor * m_sailForceFactor per step.
        /// Oars: m_backwardForce * fixedDeltaTime per step.
        ///
        /// The values come from the ship's prefab, so the cap adapts to each hull.
        /// Never below 1: the mod must not make rowing slower than vanilla.
        ///
        /// Hulls without a sail (m_sailForceFactor = 0, the Trailership's case): the comparison is
        /// meaningless, the cap would bring them back to x1 and cancel the mod. They are not capped,
        /// only BonusPerRower and MaxRowers govern them.
        /// </summary>
        internal static float MaxMultiplier(Ship ship)
        {
            if (ship.m_sailForceFactor <= 0f) return float.MaxValue;

            float rowAccel = ship.m_backwardForce * Time.fixedDeltaTime;
            if (rowAccel <= 0f) return float.MaxValue;

            return Mathf.Max(1f, BestSailForwardFactor * ship.m_sailForceFactor / rowAccel);
        }

        // ----- Utilities -----

        private static void Msg(Player p, string text)
        {
            if (!ShowMessages.Value || p == null) return;
            p.Message(MessageHud.MessageType.TopLeft, text);
        }
    }

    /// <summary>
    /// On world load: lists in the log the rowing cap computed for every hull in the game.
    /// The m_backwardForce and m_sailForceFactor values live in the Unity prefabs, so they are invisible
    /// outside runtime: this summary makes it possible to check the caps without going to sea.
    /// </summary>
    [HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.Awake))]
    internal static class ZNetScene_Awake_Patch
    {
        private static void Postfix(ZNetScene __instance)
        {
            if (!Plugin.Enabled.Value) return;

            foreach (GameObject prefab in __instance.m_prefabs)
            {
                if (prefab == null) continue;
                Ship ship = prefab.GetComponent<Ship>();
                if (ship == null) continue;

                float max = Plugin.MaxMultiplier(ship);
                string cap = float.IsInfinity(max) || max >= float.MaxValue
                    ? "no sail, uncapped"
                    : $"max rowing x{max:0.00}";

                Plugin.Log.LogInfo(
                    $"Ship {prefab.name}: {cap} " +
                    $"(m_backwardForce {ship.m_backwardForce}, m_sailForceFactor {ship.m_sailForceFactor})");
            }
        }
    }

    /// <summary>
    /// Around the ship's physics update: m_backwardForce is inflated during the call,
    /// then the original value is restored. Only applies while the helmsman rows.
    /// </summary>
    [HarmonyPatch(typeof(Ship), nameof(Ship.CustomFixedUpdate))]
    internal static class Ship_CustomFixedUpdate_Patch
    {
        private static void Prefix(Ship __instance, out float __state)
        {
            __state = __instance.m_backwardForce;

            if (!Plugin.Enabled.Value) return;
            if (!Plugin.IsRowingSpeed(__instance)) return;
            if (__instance.m_nview != null && !__instance.m_nview.IsOwner()) return; // only the owner computes physics

            __instance.m_backwardForce = __state * Plugin.Analyze(__instance).Multiplier;
        }

        private static void Postfix(Ship __instance, float __state)
        {
            __instance.m_backwardForce = __state;
        }
    }
}
