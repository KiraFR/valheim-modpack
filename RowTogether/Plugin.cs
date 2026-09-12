using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace RowTogether
{
    /// <summary>
    /// Les passagers assis d'un bateau rament avec le barreur.
    ///
    /// - Aucune touche, aucun état à synchroniser : un passager compte comme rameur dès qu'il est
    ///   assis (emote « s'asseoir », ou n'importe quelle animation taguée « sitting »).
    /// - L'effet ne joue que quand le barreur rame (Ship.Speed.Slow ou Back) ; à la voile, rien.
    /// - Chaque rameur ajoute son bonus à la poussée : l'effet est incrémental, sans notion de côté.
    /// - Le barreur (à la barre) ne rame pas, il EST la poussée de base (multiplicateur x1).
    /// - LimitToSailSpeed plafonne la rame à la vitesse voile du bateau (voir MaxMultiplier).
    ///
    /// La physique n'est calculée que par le propriétaire réseau du bateau. Ce n'est PAS forcément
    /// le barreur : Ship.UpdateOwner transfère la propriété à n'importe quel joueur à bord dès que
    /// le propriétaire courant n'y est plus. Le mod doit donc être installé chez tous les joueurs
    /// susceptibles de monter à bord ; il est inutile sur un serveur dédié, qui ne possède un objet
    /// que quand aucun joueur n'est à proximité. L'animateur des joueurs distants est répliqué par
    /// ZSyncAnimation, donc IsSitting() est fiable sur leurs répliques locales.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "valheim.rowtogether";
        public const string PluginName = "RowTogether";
        public const string PluginVersion = "3.0.0";

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

            Enabled = Config.Bind("General", "Enabled", true, "Active ou désactive le mod.");

            BonusPerRower = Config.Bind("Rowing", "BonusPerRower", 0.5f,
                new ConfigDescription(
                    "Bonus de poussée à la rame par passager assis. Chaque rameur ajoute ce bonus à la poussée " +
                    "de base, le côté du bateau n'a aucune importance. 0.5 = 1 rameur x1.5, 2 rameurs x2, 4 rameurs x3. " +
                    "Le barreur ne compte pas : c'est lui la poussée de base (x1).",
                    new AcceptableValueRange<float>(0f, 5f)));

            MaxRowers = Config.Bind("Rowing", "MaxRowers", 0,
                new ConfigDescription("Nombre maximum de rameurs pris en compte (0 = illimité).",
                    new AcceptableValueRange<int>(0, 20)));

            LimitToSailSpeed = Config.Bind("Rowing", "LimitToSailSpeed", true,
                "Empêche la rame de dépasser la vitesse que CE bateau atteindrait à la voile au meilleur vent. " +
                "Le plafond est calculé par bateau (un radeau reste lent, un drakkar reste rapide), donc ajouter " +
                "des rameurs au-delà ne sert plus à rien. Mettre à false pour n'avoir aucune limite.");

            ShowMessages = Config.Bind("Feedback", "ShowMessages", true,
                "Affiche des messages à l'écran (nombre de rameurs pour le barreur, prise de rame pour les passagers).");

            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll(typeof(Plugin).Assembly);

            Log.LogInfo($"{PluginName} {PluginVersion} chargé.");
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
        }

        /// <summary>Retour visuel uniquement : la mécanique vit dans Ship_CustomFixedUpdate_Patch.</summary>
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

            // Barreur : nombre de rameurs actifs, quand il change.
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

            // Passager : prévenir quand il commence / arrête de contribuer.
            _lastRowers = -1;
            bool rowing = helmsRowing && IsRower(ship, local);
            if (rowing != _wasRowing)
            {
                _wasRowing = rowing;
                Msg(local, rowing ? "Vous ramez. " + Describe(Analyze(ship)) : "Vous arrêtez de ramer.");
            }
        }

        /// <summary>"2 rameurs : rame x2" — le libellé partagé par le barreur et les rameurs.</summary>
        private static string Describe(Crew crew)
        {
            string plural = crew.Rowers > 1 ? "s" : "";
            string txt = $"{crew.Rowers} rameur{plural} : rame x{crew.Multiplier:0.0#}";
            if (crew.Capped) txt += " (plafonné à la vitesse voile)";
            return txt;
        }

        // ----- Équipage -----

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
        /// Un passager rame s'il est assis et n'est pas à la barre. IsSitting() compare l'état courant
        /// de l'animateur au tag « sitting », et cet animateur est répliqué sur tous les clients.
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
            /// <summary>Vrai si LimitToSailSpeed a rogné le multiplicateur.</summary>
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

        // ----- Plafond « vitesse voile » -----

        /// <summary>
        /// Facteur de poussée avant maximal de la voile, tous vents confondus, en fraction de
        /// m_sailForceFactor. Ship.GetWindAngleFactor vaut 0.7 vent arrière et 1.0 vent de travers,
        /// mais le vent de travers perd en projection sur l'axe du bateau : le maximum réel est à
        /// ~65° du cap, soit ~0.737. Le portant pur n'est pas l'optimum dans Valheim.
        /// </summary>
        private const float BestSailForwardFactor = 0.737f;

        /// <summary>
        /// Multiplicateur au-delà duquel la rame irait plus vite que la voile.
        ///
        /// Voile et rame poussent le bateau dans la même boucle physique et subissent la même
        /// traînée quadratique (m_dampingForward). À accélération par pas de physique égale, la
        /// vitesse d'équilibre est donc égale : il suffit d'égaliser les accélérations, sans avoir
        /// à modéliser la traînée. Voile : BestSailForwardFactor * m_sailForceFactor par pas.
        /// Rame : m_backwardForce * fixedDeltaTime par pas.
        ///
        /// Les valeurs viennent du prefab du bateau, donc le plafond s'adapte à chaque coque.
        /// Jamais en dessous de 1 : le mod ne doit pas rendre la rame plus lente que le vanilla.
        ///
        /// Coques sans voile (m_sailForceFactor = 0, cas du Trailership) : la comparaison n'a
        /// aucun sens, le plafond les ramènerait à x1 et annulerait le mod. On ne les plafonne pas,
        /// seuls BonusPerRower et MaxRowers les gouvernent.
        /// </summary>
        internal static float MaxMultiplier(Ship ship)
        {
            if (ship.m_sailForceFactor <= 0f) return float.MaxValue;

            float rowAccel = ship.m_backwardForce * Time.fixedDeltaTime;
            if (rowAccel <= 0f) return float.MaxValue;

            return Mathf.Max(1f, BestSailForwardFactor * ship.m_sailForceFactor / rowAccel);
        }

        // ----- Utilitaires -----

        private static void Msg(Player p, string text)
        {
            if (!ShowMessages.Value || p == null) return;
            p.Message(MessageHud.MessageType.TopLeft, text);
        }
    }

    /// <summary>
    /// Au chargement du monde : liste dans le log le plafond de rame calculé pour chaque coque du jeu.
    /// Les valeurs m_backwardForce et m_sailForceFactor vivent dans les prefabs Unity, donc invisibles
    /// hors exécution : ce récapitulatif permet de vérifier les plafonds sans partir en mer.
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
                    ? "sans voile, non plafonné"
                    : $"rame max x{max:0.00}";

                Plugin.Log.LogInfo(
                    $"Bateau {prefab.name} : {cap} " +
                    $"(m_backwardForce {ship.m_backwardForce}, m_sailForceFactor {ship.m_sailForceFactor})");
            }
        }
    }

    /// <summary>
    /// Autour de la mise à jour physique du bateau : on gonfle m_backwardForce pendant l'appel,
    /// puis on restaure la valeur d'origine. Ne s'applique que quand le barreur rame.
    /// </summary>
    [HarmonyPatch(typeof(Ship), nameof(Ship.CustomFixedUpdate))]
    internal static class Ship_CustomFixedUpdate_Patch
    {
        private static void Prefix(Ship __instance, out float __state)
        {
            __state = __instance.m_backwardForce;

            if (!Plugin.Enabled.Value) return;
            if (!Plugin.IsRowingSpeed(__instance)) return;
            if (__instance.m_nview != null && !__instance.m_nview.IsOwner()) return; // seul le propriétaire calcule la physique

            __instance.m_backwardForce = __state * Plugin.Analyze(__instance).Multiplier;
        }

        private static void Postfix(Ship __instance, float __state)
        {
            __instance.m_backwardForce = __state;
        }
    }
}
