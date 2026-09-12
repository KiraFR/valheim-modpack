using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ChestStack
{
    /// <summary>
    /// Ranger tout le sac dans les coffres alentour en un appui.
    ///
    /// - Le jeu possède déjà la mécanique : Inventory.StackAll ne déplace un objet que si la destination
    ///   en contient déjà un exemplaire. Le mod ne réinvente rien, il appelle Container.StackAll() sur
    ///   chaque coffre à portée au lieu du seul coffre ouvert.
    /// - Container.StackAll() passe par un aller-retour RPC qui demande la propriété du ZDO, refuse si un
    ///   autre joueur fouille le coffre et vérifie l'accès des coffres privés. C'est pour cette sécurité
    ///   qu'on emprunte ce chemin plutôt que d'appeler Inventory.StackAll en direct, comme le fait le
    ///   bouton « Empiler tout » du jeu. Bonus : RPC_StackResponse joue déjà l'effet visuel de dépôt sur
    ///   chaque coffre qui a reçu quelque chose, donc le retour à l'écran est gratuit.
    /// - Conséquence : le rangement n'a pas lieu pendant l'appui mais plus tard, dans RPC_StackResponse.
    ///   Le mod attend les réponses (au plus DelaiReponse secondes) avant d'afficher son récapitulatif, et
    ///   étouffe entre-temps les messages que chaque coffre veut afficher pour son propre compte.
    /// - Trois façons de déclencher, toutes menant au même rangement groupé. La touche du mod, qui n'exige
    ///   rien d'ouvert. Le bouton « Objets similaires » de l'écran des coffres. Et le maintien de la touche
    ///   d'interaction sur un coffre, qui en vanilla range déjà puis referme. Dans les deux derniers cas le
    ///   coffre que le joueur a sous les yeux n'a aucune priorité : il devient un candidat parmi les autres,
    ///   et un objet file chez le voisin si c'est le voisin qui en détient déjà. EtendreControlesJeu rend
    ///   leur comportement vanilla aux deux contrôles du jeu.
    ///
    /// Protections : l'équipement porté est déjà épargné par StackAll lui-même, qui teste IsItemEquiped.
    /// La barre d'action et tout ce qui nourrit s'y ajoutent par un Postfix sur ce même IsItemEquiped, qui
    /// répond « équipé » pour ces objets. C'est le seul test par objet que StackAll consulte, donc le filtre
    /// est exact à la pile près, sans réécrire StackAll ni toucher à la lecture de l'inventaire.
    /// « Ce qui nourrit » reprend la définition du jeu (m_food, m_foodStamina, m_foodEitr), celle qui décide
    /// d'afficher l'encart nutrition : la viande crue est un matériau sans valeur nutritive, elle part donc
    /// bien au coffre. La fenêtre de filtrage est refermée par un Finalizer et non par un Postfix, car un
    /// Postfix ne s'exécute pas si la méthode d'origine lève, ce qui rendrait la nourriture invisible au
    /// reste du jeu pour toute la partie.
    ///
    /// Multijoueur : seul le joueur qui range a besoin du mod. Règle absolue : ne jamais modifier un coffre
    /// dont on n'est pas encore propriétaire, car Container ne sauvegarde que chez le propriétaire et une
    /// modification non sauvegardée est effacée au rechargement suivant. La réponse du coffre arrive souvent
    /// avant la propriété elle-même ; le dépôt est alors reporté jusqu'à ce qu'elle arrive, et abandonné sans
    /// rien déplacer si elle n'arrive pas (détail dans Container_RPC_StackResponse_Patch). Chaque coffre ne
    /// recharge sa copie locale qu'une fois par seconde, donc un Load() est forcé juste avant d'écrire.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "valheim.cheststack";
        public const string PluginName = "ChestStack";
        public const string PluginVersion = "1.0.1";

        internal static ManualLogSource Log;

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> Rayon;
        internal static ConfigEntry<bool> ProtegerBarreAction;
        internal static ConfigEntry<bool> ProtegerNourriture;
        internal static ConfigEntry<bool> EtendreControlesJeu;
        internal static ConfigEntry<bool> ProtegerHorsDuMod;
        internal static ConfigEntry<float> DelaiReponse;
        internal static ConfigEntry<KeyboardShortcut> ToucheRanger;

        private Harmony _harmony;

        /// <summary>Tous les conteneurs instanciés, alimenté par le patch sur Container.Awake.</summary>
        private static readonly HashSet<Container> AllContainers = new HashSet<Container>();

        // État du rangement en cours. Le rangement est étalé sur plusieurs frames : on lance N demandes,
        // puis on attend N réponses ou l'expiration du délai de garde.
        private static bool _triEnCours;
        private static int _reponsesAttendues;
        private static int _reponsesRecues;
        private static int _objetsRanges;
        private static int _coffresRemplis;
        private static int _abandons;
        private static float _finAttente;

        /// <summary>
        /// Coffres qui ont accordé le rangement mais dont la propriété n'est pas encore arrivée chez nous.
        /// Rien n'y est déplacé tant qu'ils sont ici. Voir Container_RPC_StackResponse_Patch.
        /// </summary>
        private static readonly List<Container> EnAttenteDePropriete = new List<Container>();

        /// <summary>Vrai pendant l'exécution d'un StackAll dont la source est le sac du joueur local.</summary>
        private static bool _filtrageActif;

        internal static bool TriEnCours => _triEnCours;
        internal static bool FiltrageActif => _filtrageActif;

        private void Awake()
        {
            Log = Logger;

            // Fichier généré : BepInEx/config/valheim.cheststack.cfg
            Enabled = Config.Bind("General", "Enabled", true,
                "Active ou désactive le rangement dans les coffres alentour.");

            Rayon = Config.Bind("General", "Rayon", 20f,
                new ConfigDescription(
                    "Distance maximale, en mètres, des coffres concernés par le rangement. Même valeur par " +
                    "défaut que ChestCraft, pour que « les coffres qui comptent » désignent le même entrepôt.",
                    new AcceptableValueRange<float>(2f, 100f)));

            ProtegerBarreAction = Config.Bind("General", "ProtegerBarreAction", true,
                "Laisse en place la première rangée du sac, celle des touches 1 à 8. L'équipement porté est " +
                "déjà épargné par le jeu lui-même, il n'y a rien à régler pour lui.");

            ProtegerNourriture = Config.Bind("General", "ProtegerNourriture", true,
                "Laisse en place tout ce qui nourrit, selon la définition du jeu (santé, endurance ou eitr). " +
                "La viande et le poisson crus sont des matériaux sans valeur nutritive : ils partent au coffre.");

            EtendreControlesJeu = Config.Bind("General", "EtendreControlesJeu", true,
                "Fait porter les contrôles du jeu sur tout le voisinage au lieu du seul coffre ouvert : le " +
                "bouton « Objets similaires » et le maintien de la touche d'interaction rangent alors dans " +
                "tous les coffres à portée. Sur false, ils redeviennent vanilla et seule la touche du mod range.");

            ProtegerHorsDuMod = Config.Bind("General", "ProtegerHorsDuMod", true,
                "N'a d'effet que si EtendreControlesJeu est sur false : applique quand même les protections " +
                "ci-dessus aux contrôles du jeu restés vanilla.");

            DelaiReponse = Config.Bind("General", "DelaiReponse", 2f,
                new ConfigDescription(
                    "Temps d'attente, en secondes, avant d'afficher le récapitulatif si un coffre ne répond " +
                    "jamais. À monter sur un serveur distant à forte latence.",
                    new AcceptableValueRange<float>(0.2f, 10f)));

            ToucheRanger = Config.Bind("Controls", "ToucheRanger",
                new KeyboardShortcut(KeyCode.R, KeyCode.LeftShift),
                "Touche qui range le sac dans les coffres alentour. Elle ne répond qu'en visant un coffre ou " +
                "en ayant un coffre ouvert, ce qui évite un déclenchement en pleine course. Le modificateur " +
                "évite aussi de croiser la touche R de ChestCraft.");

            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll(typeof(Plugin).Assembly);

            Log.LogInfo($"{PluginName} {PluginVersion} chargé.");
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
        }

        private void Update()
        {
            if (!Enabled.Value) return;

            if (_triEnCours)
            {
                SuivreTri();
                return;
            }

            if (!ToucheRanger.Value.IsDown() || !SaisieLibre()) return;

            Container vise = CoffreVise();
            if (vise == null) return;

            LancerTri(vise);
        }

        /// <summary>
        /// Le coffre sous le réticule, ou celui dont l'écran est ouvert. Sert de garde à la touche : le geste
        /// est « je range ici », pas « je range où que je sois », ce qui écarte l'appui accidentel en courant.
        /// </summary>
        private static Container CoffreVise()
        {
            Player player = Player.m_localPlayer;
            if (player == null) return null;

            if (InventoryGui.instance != null && InventoryGui.instance.m_currentContainer != null)
                return InventoryGui.instance.m_currentContainer;

            GameObject survole = player.GetHoverObject();
            return survole != null ? survole.GetComponentInParent<Container>() : null;
        }

        // ------------------------------------------------------------------------------------------------
        // Rangement
        // ------------------------------------------------------------------------------------------------

        /// <summary>
        /// Mêmes verrous que RowTogether, moins celui sur l'inventaire : ranger depuis un coffre ouvert est
        /// justement le geste le plus courant, la touche doit donc y répondre.
        /// </summary>
        private static bool SaisieLibre()
        {
            if (Console.IsVisible() || Menu.IsVisible() || TextInput.IsVisible()) return false;
            if (Minimap.IsOpen()) return false;
            if (Chat.instance != null && Chat.instance.HasFocus()) return false;
            return true;
        }

        /// <summary>
        /// Démarre un rangement groupé. <paramref name="origine"/> est le coffre que le joueur avait sous les
        /// yeux quand il a déclenché, ajouté à la liste même s'il n'a rien qui corresponde : c'est un coffre
        /// comme les autres, il n'a aucune priorité sur ses voisins.
        /// Renvoie false si rien n'a été lancé, auquel cas l'appelant vanilla doit reprendre la main.
        /// </summary>
        internal static bool LancerTri(Container origine = null)
        {
            if (_triEnCours) return false;

            Player player = Player.m_localPlayer;
            if (player == null || player.IsTeleporting()) return false;

            List<Container> coffres = CoffresAPortee(player);
            if (origine != null && origine.m_nview != null && origine.m_nview.IsValid() &&
                origine.GetInventory() != null && !coffres.Contains(origine))
            {
                coffres.Add(origine);
            }

            if (coffres.Count == 0)
            {
                player.Message(MessageHud.MessageType.Center, "Aucun coffre à portée");
                return false;
            }

            _objetsRanges = 0;
            _coffresRemplis = 0;
            _reponsesRecues = 0;
            _abandons = 0;
            EnAttenteDePropriete.Clear();
            _reponsesAttendues = coffres.Count;
            _finAttente = Time.time + Mathf.Max(0.1f, DelaiReponse.Value);
            _triEnCours = true;

            foreach (Container coffre in coffres) coffre.StackAll();

            Log.LogInfo($"Rangement demandé à {coffres.Count} coffre(s) dans un rayon de {Rayon.Value:0.#} m.");
            return true;
        }

        /// <summary>
        /// Dépose dans les coffres dont la propriété vient d'arriver, puis conclut dès que tous les coffres ont
        /// été traités, ou au bout du délai de garde. Un coffre encore en attente à ce moment-là est abandonné
        /// sans que rien n'y ait été déplacé : les objets restent dans le sac, rien ne peut se perdre.
        /// </summary>
        private static void SuivreTri()
        {
            TraiterAttentes();

            if (_reponsesRecues < _reponsesAttendues && Time.time < _finAttente) return;

            _abandons += EnAttenteDePropriete.Count;
            EnAttenteDePropriete.Clear();
            _triEnCours = false;

            if (_abandons > 0)
            {
                Log.LogWarning($"{_abandons} coffre(s) abandonné(s) : propriété non reçue dans le délai de " +
                               $"{DelaiReponse.Value:0.#} s, rien n'y a été déplacé.");
            }

            Log.LogInfo($"Rangement terminé : {_objetsRanges} objet(s), {_coffresRemplis} coffre(s), " +
                        $"{_reponsesRecues}/{_reponsesAttendues} traité(s), {_abandons} abandon(s).");

            Player player = Player.m_localPlayer;
            if (player == null) return;

            string resultat = _objetsRanges > 0
                ? $"{_objetsRanges} objet(s) rangé(s) dans {_coffresRemplis} coffre(s)"
                : "Rien à ranger dans les coffres à portée";
            if (_abandons > 0) resultat += $"\n{_abandons} coffre(s) injoignable(s), réessaie";

            player.Message(MessageHud.MessageType.Center, resultat);
        }

        private static void TraiterAttentes()
        {
            for (int i = EnAttenteDePropriete.Count - 1; i >= 0; i--)
            {
                Container coffre = EnAttenteDePropriete[i];

                if (coffre == null || coffre.m_nview == null || !coffre.m_nview.IsValid())
                {
                    EnAttenteDePropriete.RemoveAt(i);
                    _reponsesRecues++;
                    _abandons++;
                    continue;
                }

                if (!coffre.m_nview.IsOwner()) continue;

                EnAttenteDePropriete.RemoveAt(i);
                Deposer(coffre);
            }
        }

        /// <summary>
        /// Ce que RPC_StackResponse fait quand le rangement est accordé, rejoué une fois la propriété réellement
        /// acquise. message doit rester à true : sur false, StackAll ne mesure plus l'écart et renvoie le
        /// contenu total du coffre au lieu du nombre d'objets déposés, ce qui fausserait le récapitulatif.
        /// Le message qu'il émet est de toute façon étouffé pendant le tri.
        /// </summary>
        private static void Deposer(Container coffre)
        {
            _reponsesRecues++;

            Player player = Player.m_localPlayer;
            if (player == null) return;

            coffre.Load();
            if (coffre.GetInventory().StackAll(player.GetInventory(), message: true) > 0 &&
                InventoryGui.instance != null)
            {
                InventoryGui.instance.m_moveItemEffects.Create(coffre.transform.position, Quaternion.identity);
            }
        }

        /// <summary>
        /// Coffres retenus pour ce rangement.
        ///
        /// Le test « coffre en cours d'utilisation » n'est pas refait ici : RPC_RequestStack le fait côté
        /// propriétaire, qui seul connaît la réponse, et son refus est déjà traité comme une réponse.
        ///
        /// Les deux contrôles d'accès, eux, sont bien refaits. RPC_RequestStack ne vérifie que la
        /// confidentialité du coffre, pas le cercle protecteur : en vanilla c'est Container.Interact qui
        /// arrête le joueur devant un coffre gardé, et ce mod ne passe pas par Interact. Sans ce filtre on
        /// pourrait déposer dans un coffre qu'on n'a même pas le droit d'ouvrir.
        /// </summary>
        private static List<Container> CoffresAPortee(Player player)
        {
            var retenus = new List<Container>();
            Vector3 centre = player.transform.position;
            float rayonCarre = Rayon.Value * Rayon.Value;
            long playerID = JoueurCourant();

            AllContainers.RemoveWhere(c => c == null);

            foreach (Container coffre in AllContainers)
            {
                if (coffre.m_nview == null || !coffre.m_nview.IsValid()) continue;
                if (coffre.GetInventory() == null) continue;
                if ((coffre.transform.position - centre).sqrMagnitude > rayonCarre) continue;
                if (!AccesAutorise(coffre, playerID)) continue;

                retenus.Add(coffre);
            }

            return retenus;
        }

        /// <summary>
        /// Les deux refus que le joueur peut se voir opposer devant un coffre : le cercle protecteur d'un
        /// autre joueur, et la confidentialité du coffre lui-même. Sert au filtrage comme à l'affichage,
        /// pour ne pas proposer un rangement qui serait refusé.
        /// </summary>
        internal static bool AccesAutorise(Container coffre, long playerID)
        {
            if (coffre.m_checkGuardStone &&
                !PrivateArea.CheckAccess(coffre.transform.position, 0f, false)) return false;
            return coffre.CheckAccess(playerID);
        }

        internal static long JoueurCourant()
        {
            return Game.instance != null ? Game.instance.GetPlayerProfile().GetPlayerID() : 0L;
        }

        // ------------------------------------------------------------------------------------------------
        // Libellé de la touche
        // ------------------------------------------------------------------------------------------------

        private static KeyboardShortcut _toucheCache;
        private static string _libelleCache;

        /// <summary>
        /// « Shift+R » plutôt que le « R + LeftShift » de BepInEx. Recalculé seulement quand le réglage
        /// change, car le survol d'un coffre rappelle ce texte à chaque image.
        /// </summary>
        internal static string LibelleToucheRanger()
        {
            KeyboardShortcut actuelle = ToucheRanger.Value;
            if (_libelleCache != null && actuelle.Equals(_toucheCache)) return _libelleCache;

            string libelle = "";
            foreach (KeyCode modificateur in actuelle.Modifiers) libelle += NomTouche(modificateur) + "+";
            libelle += NomTouche(actuelle.MainKey);

            _toucheCache = actuelle;
            _libelleCache = libelle;
            return _libelleCache;
        }

        private static string NomTouche(KeyCode touche)
        {
            switch (touche)
            {
                case KeyCode.LeftShift:
                case KeyCode.RightShift: return "Shift";
                case KeyCode.LeftControl:
                case KeyCode.RightControl: return "Ctrl";
                case KeyCode.LeftAlt:
                case KeyCode.RightAlt: return "Alt";
                default: return touche.ToString();
            }
        }

        // ------------------------------------------------------------------------------------------------
        // Points d'accroche des patches
        // ------------------------------------------------------------------------------------------------

        internal static void Register(Container coffre)
        {
            if (coffre != null) AllContainers.Add(coffre);
        }

        internal static void CompterReponse()
        {
            if (_triEnCours) _reponsesRecues++;
        }

        internal static void MettreEnAttente(Container coffre)
        {
            if (!EnAttenteDePropriete.Contains(coffre)) EnAttenteDePropriete.Add(coffre);
        }

        internal static void EnregistrerResultat(int objets)
        {
            if (!_triEnCours || objets <= 0) return;
            _objetsRanges += objets;
            _coffresRemplis++;
        }

        /// <summary>
        /// Ouvre la fenêtre de filtrage si la source est bien le sac du joueur local. Hors d'un rangement
        /// déclenché par le mod, ProtegerHorsDuMod décide si les contrôles du jeu en bénéficient aussi.
        /// Renvoie l'état à restituer au Finalizer.
        /// </summary>
        internal static bool OuvrirFiltrage(Inventory source)
        {
            if (!Enabled.Value) return false;

            Player player = Player.m_localPlayer;
            if (player == null || source != player.GetInventory()) return false;
            if (!_triEnCours && !ProtegerHorsDuMod.Value) return false;

            _filtrageActif = true;
            return true;
        }

        internal static void FermerFiltrage()
        {
            _filtrageActif = false;
        }

        /// <summary>
        /// Ce que le rangement automatique ne doit pas emporter. L'équipement porté n'y figure pas :
        /// StackAll le teste déjà de son côté.
        /// </summary>
        internal static bool EstProtege(ItemDrop.ItemData item)
        {
            if (item == null) return false;
            if (ProtegerBarreAction.Value && item.m_gridPos.y == 0) return true;
            if (ProtegerNourriture.Value && EstComestible(item)) return true;
            return false;
        }

        /// <summary>
        /// Définition du jeu lui-même, celle qui décide d'afficher l'encart nutrition dans l'infobulle.
        /// La viande crue est un matériau sans valeur nutritive : elle n'est donc pas protégée.
        /// </summary>
        private static bool EstComestible(ItemDrop.ItemData item)
        {
            ItemDrop.ItemData.SharedData partage = item.m_shared;
            if (partage == null) return false;
            return partage.m_food > 0f || partage.m_foodStamina > 0f || partage.m_foodEitr > 0f;
        }

        /// <summary>
        /// Chaque coffre veut annoncer son propre résultat au centre de l'écran. Sur quinze coffres ce serait
        /// quinze messages empilés ; le mod n'en garde qu'un, le sien, affiché une fois le tri conclu.
        /// </summary>
        internal static bool DoitEtouffer(string msg)
        {
            if (!_triEnCours || msg == null) return false;
            return msg.StartsWith("$msg_stackall", System.StringComparison.Ordinal) || msg == "$msg_inuse";
        }
    }

    // ----------------------------------------------------------------------------------------------------
    // Enregistrement des coffres
    // ----------------------------------------------------------------------------------------------------

    /// <summary>Chaque conteneur instancié s'inscrit lui-même : aucune requête physique à faire ensuite.</summary>
    [HarmonyPatch(typeof(Container), nameof(Container.Awake))]
    internal static class Container_Awake_Patch
    {
        private static void Postfix(Container __instance)
        {
            Plugin.Register(__instance);
        }
    }

    // ----------------------------------------------------------------------------------------------------
    // Fenêtre de filtrage et comptage
    // ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Le seul endroit où le sac du joueur se vide vers un coffre. On l'encadre pour activer les protections
    /// et pour relever combien d'objets sont effectivement partis.
    /// </summary>
    [HarmonyPatch(typeof(Inventory), nameof(Inventory.StackAll))]
    internal static class Inventory_StackAll_Patch
    {
        private static void Prefix(Inventory fromInventory, out bool __state)
        {
            __state = Plugin.OuvrirFiltrage(fromInventory);
        }

        private static void Postfix(int __result)
        {
            Plugin.EnregistrerResultat(__result);
        }

        /// <summary>Finalizer et non Postfix : une exception ne doit pas laisser le filtre ouvert pour la partie.</summary>
        private static void Finalizer(bool __state)
        {
            if (__state) Plugin.FermerFiltrage();
        }
    }

    // ----------------------------------------------------------------------------------------------------
    // Protections
    // ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// StackAll consulte IsItemEquiped objet par objet pour décider de ne pas le déplacer. Répondre
    /// « équipé » pour la barre d'action et la nourriture suffit à les épargner, sans réécrire StackAll.
    /// </summary>
    [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.IsItemEquiped))]
    internal static class Humanoid_IsItemEquiped_Patch
    {
        private static void Postfix(Humanoid __instance, ItemDrop.ItemData item, ref bool __result)
        {
            if (__result || !Plugin.FiltrageActif) return;
            if (__instance != Player.m_localPlayer) return;
            if (Plugin.EstProtege(item)) __result = true;
        }
    }

    // ----------------------------------------------------------------------------------------------------
    // Réponses des coffres
    // ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Chaque coffre répond ici, et c'est ici que la version 1.0.0 perdait des objets.
    ///
    /// Dans RPC_RequestStack, le propriétaire du coffre fait trois choses dans cet ordre : ForceSendZDO,
    /// SetOwner, puis l'envoi de cette réponse. Or ForceSendZDO ne fait que mettre le ZDO en file : il ne
    /// part qu'au prochain passage de ZDOMan.SendZDOToPeers2, toutes les 0,05 s, alors que la réponse part
    /// immédiatement sur le socket. Quand le coffre appartient au serveur ou à un autre joueur, la réponse
    /// arrive donc avant le ZDO qui nous désigne propriétaire. StackAll retirait alors les objets du sac et
    /// les ajoutait à la copie locale du coffre, mais Container.OnContainerChanged refusait de sauvegarder
    /// puisque IsOwner() était encore faux. Au rechargement suivant la copie locale était écrasée par le ZDO,
    /// et les objets avaient disparu des deux côtés. Si au contraire le joueur retouchait le coffre une fois
    /// propriétaire, la copie locale était sauvegardée et les objets réapparaissaient : d'où le « des fois ».
    ///
    /// Le jeu n'y est pas exposé, car son maintien de touche ne range que dans un coffre déjà ouvert, donc
    /// déjà possédé. Le mod range tout de suite dans des voisins jamais ouverts. Un joueur déjà propriétaire
    /// des coffres autour de lui ne voit jamais le problème, ce qui explique qu'il ne touche qu'un joueur.
    ///
    /// Correctif : tant que la propriété n'est pas là, la réponse d'origine est sautée et le coffre mis en
    /// attente. SuivreTri rejoue le dépôt dès que IsOwner() devient vrai, ou l'abandonne au délai de garde.
    /// </summary>
    [HarmonyPatch(typeof(Container), nameof(Container.RPC_StackResponse))]
    internal static class Container_RPC_StackResponse_Patch
    {
        private static bool Prefix(Container __instance, bool granted)
        {
            if (!Plugin.TriEnCours) return true;

            if (!granted || __instance.m_nview == null || !__instance.m_nview.IsValid())
            {
                Plugin.CompterReponse();
                return true;
            }

            if (!__instance.m_nview.IsOwner())
            {
                Plugin.MettreEnAttente(__instance);
                return false;
            }

            Plugin.CompterReponse();
            __instance.Load();
            return true;
        }
    }

    // ----------------------------------------------------------------------------------------------------
    // Affichage au survol
    // ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Annonce la touche sous les deux lignes du jeu, dans la même forme qu'elles. Rien n'est affiché si le
    /// coffre refuserait le rangement, pour ne pas promettre un geste qui échouerait.
    /// </summary>
    [HarmonyPatch(typeof(Container), nameof(Container.GetHoverText))]
    internal static class Container_GetHoverText_Patch
    {
        private static void Postfix(Container __instance, ref string __result)
        {
            if (!Plugin.Enabled.Value || string.IsNullOrEmpty(__result)) return;
            if (!Plugin.AccesAutorise(__instance, Plugin.JoueurCourant())) return;

            __result += $"\n[<color=yellow><b>{Plugin.LibelleToucheRanger()}</b></color>] " +
                        "Ranger dans les coffres alentour";
        }
    }

    // ----------------------------------------------------------------------------------------------------
    // Élargissement des contrôles du jeu
    // ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Point de passage du maintien de la touche d'interaction sur un coffre ouvert. On y détourne le
    /// rangement vers tout le voisinage, le coffre visé n'étant plus qu'un candidat parmi les autres.
    /// Le mod s'appelle lui-même ici pour chaque coffre : TriEnCours laisse alors filer l'appel d'origine,
    /// sans quoi le détournement se rappellerait sans fin.
    /// </summary>
    [HarmonyPatch(typeof(Container), nameof(Container.StackAll))]
    internal static class Container_StackAll_Patch
    {
        private static bool Prefix(Container __instance)
        {
            if (!Plugin.Enabled.Value || !Plugin.EtendreControlesJeu.Value) return true;
            if (Plugin.TriEnCours) return true;
            return !Plugin.LancerTri(__instance);
        }
    }

    /// <summary>
    /// Le bouton « Objets similaires » de l'écran des coffres. Il n'appelle pas Container.StackAll mais
    /// Inventory.StackAll en direct, sans demander la propriété du ZDO : il lui faut donc son propre
    /// détournement, qui au passage lui fait emprunter le chemin réseau plus sûr.
    /// </summary>
    [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.OnStackAll))]
    internal static class InventoryGui_OnStackAll_Patch
    {
        private static bool Prefix(InventoryGui __instance)
        {
            if (!Plugin.Enabled.Value || !Plugin.EtendreControlesJeu.Value) return true;
            if (__instance.m_currentContainer == null) return true;
            if (Player.m_localPlayer == null || Player.m_localPlayer.IsTeleporting()) return true;

            __instance.SetupDragItem(null, null, 1);
            return !Plugin.LancerTri(__instance.m_currentContainer);
        }
    }

    // ----------------------------------------------------------------------------------------------------
    // Messages
    // ----------------------------------------------------------------------------------------------------

    /// <summary>Étouffe les annonces individuelles des coffres pendant un rangement groupé.</summary>
    [HarmonyPatch(typeof(Player), nameof(Player.Message))]
    internal static class Player_Message_Patch
    {
        private static bool Prefix(string msg)
        {
            return !Plugin.DoitEtouffer(msg);
        }
    }
}
