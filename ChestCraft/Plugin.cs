using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ChestCraft
{
    /// <summary>
    /// Fabriquer, améliorer et construire en puisant directement dans les coffres aux alentours.
    ///
    /// - Les matériaux des coffres proches comptent comme s'ils étaient dans le sac : le panneau
    ///   d'artisanat affiche le total, le bouton Fabriquer s'active, et le craft retire ce qui manque
    ///   dans les coffres.
    /// - Idem pour le marteau (construction) et pour les appareils (fondoir, four à charbon, moulin, rouet,
    ///   raffinerie d'eitr, fermenteur, feux, balistes), chacun désactivable séparément. Les grils et les
    ///   fours à pain sont hors jeu par défaut : leur ingrédient serait imprévisible. La touche de choix
    ///   permet de les réactiver un par un, ou General.Cuisson d'un coup.
    /// - Le jeu ne centralise pas la lecture de l'inventaire : tous les chemins finissent sur des méthodes
    ///   feuilles de Inventory (CountItems, HaveItem, GetAmmoItem, et les quatre RemoveItem). Ce sont
    ///   celles-là qui sont patchées, et seulement pendant une "portée" ouverte par les points d'entrée
    ///   du craft, de la construction et des appareils. Hors de cette portée les patches ne font rien.
    /// - La portée est refermée par un Finalizer Harmony et non par un Postfix : un Postfix ne s'exécute
    ///   pas si la méthode d'origine lève, ce qui laisserait la portée ouverte pour le reste de la partie.
    /// - Les appareils désignent leur objet par référence, pas par nom. Trois patches redirigent le retrait
    ///   vers le coffre qui détient l'objet ; sans eux l'objet serait consommé sans quitter le coffre.
    /// - Les coffres chargés s'enregistrent eux-mêmes via un patch sur Container.Awake ; le mod ne fait
    ///   aucune requête physique, il filtre cette liste par distance toutes les 0,25 s.
    ///
    /// Multijoueur : un coffre est un ZDO, écrire dedans sans en être propriétaire serait perdu. Avant
    /// chaque retrait le mod prend la propriété (ClaimOwnership) puis force la sauvegarde (Container.Save).
    /// Les coffres ouverts par un autre joueur sont ignorés par défaut pour éviter les retraits croisés.
    /// Seul le joueur qui fabrique a besoin du mod ; le serveur et les autres clients n'en ont pas besoin.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "valheim.chestcraft";
        public const string PluginName = "ChestCraft";
        public const string PluginVersion = "1.0.0";

        internal static ManualLogSource Log;
        internal static Plugin Instance;

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> Rayon;
        internal static ConfigEntry<bool> Fabrication;
        internal static ConfigEntry<bool> Construction;
        internal static ConfigEntry<bool> Appareils;
        internal static ConfigEntry<bool> Cuisson;
        internal static ConfigEntry<bool> RemplirDUnCoup;
        internal static ConfigEntry<bool> PrioriteCoffres;
        internal static ConfigEntry<bool> IgnorerCoffresOuverts;
        internal static ConfigEntry<bool> IgnorerChariots;
        internal static ConfigEntry<string> CoffresExclus;
        internal static ConfigEntry<KeyboardShortcut> ToucheChoix;
        internal static ConfigEntry<KeyboardShortcut> ToucheRemplir;
        internal static ConfigEntry<string> AppareilsChoix;

        /// <summary>Valeur de choix signifiant « ne rien prendre dans les coffres pour cet appareil ».</summary>
        internal const string ChoixAucun = "-";

        /// <summary>Valeur de choix signifiant « le premier ingrédient trouvé ».</summary>
        internal const string ChoixAuto = "*";

        /// <summary>Intervalle de rafraîchissement de la liste des coffres proches et du cache de comptage.</summary>
        private const float ScanInterval = 0.25f;

        /// <summary>Tous les coffres instanciés, alimenté par le patch sur Container.Awake.</summary>
        private static readonly HashSet<Container> AllContainers = new HashSet<Container>();

        /// <summary>Coffres proches retenus au dernier scan.</summary>
        private static readonly List<Container> NearbyContainers = new List<Container>();

        /// <summary>Comptages déjà calculés depuis le dernier scan, clé "nom|qualité|worldLevel".</summary>
        private static readonly Dictionary<string, int> CountCache = new Dictionary<string, int>();

        private static float _lastScan = -999f;

        /// <summary>Empreinte du contenu des coffres proches, recalculée à chaque scan.</summary>
        private static int _signature;

        /// <summary>Dernière empreinte pour laquelle le panneau d'artisanat a été rafraîchi.</summary>
        private static int _refreshedSignature;

        private static bool _hasSignature;

        /// <summary>Profondeur de la portée craft/construction : les patches sur Inventory n'agissent que si &gt; 0.</summary>
        private static int _depth;

        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;
            Instance = this;

            Enabled = Config.Bind("General", "Enabled", true, "Active ou désactive le mod.");

            Rayon = Config.Bind("General", "Rayon", 20f,
                new ConfigDescription(
                    "Distance en mètres autour du joueur dans laquelle les coffres sont utilisés. " +
                    "Les coffres non chargés (zone trop éloignée) ne comptent jamais, quelle que soit la valeur.",
                    new AcceptableValueRange<float>(1f, 200f)));

            Fabrication = Config.Bind("General", "Fabrication", true,
                "Utilise les coffres pour fabriquer et améliorer à l'établi, à la forge, etc.");

            Construction = Config.Bind("General", "Construction", true,
                "Utilise les coffres pour construire au marteau (et pour la cultivateur, le ciseau, etc.).");

            Appareils = Config.Bind("General", "Appareils", true,
                "Utilise les coffres pour alimenter les appareils : gril (cuisson de la nourriture au-dessus " +
                "du feu), four à pain, fondoir, four à charbon, haut fourneau, moulin, rouet, raffinerie " +
                "d'eitr, fermenteur, feux et torches (bois), balistes (munitions). " +
                "Couvre aussi bien la matière première que le carburant.");

            Cuisson = Config.Bind("General", "Cuisson", false,
                "Défaut des grils et des fours à pain, les seuls appareils où l'ingrédient pris serait " +
                "imprévisible (viande de cerf ou de sanglier, tourte ou pain). Désactivé, ils restent " +
                "vanilla et ne se servent que dans le sac. La touche de choix reste prioritaire : régler " +
                "un gril en jeu écrase ce défaut pour lui, et le réglage est retenu.");

            RemplirDUnCoup = Config.Bind("General", "RemplirDUnCoup", true,
                "Autorise le remplissage au maximum en maintenant Controls.ToucheRemplir pendant " +
                "l'interaction. Concerne le charbon et le minerai des fondoirs, le carburant des grils " +
                "et le bois des feux. Fonctionne aussi bien depuis le sac que depuis les coffres. " +
                "Désactivé, chaque appui ajoute une unité comme en vanilla.");

            PrioriteCoffres = Config.Bind("General", "PrioriteCoffres", false,
                "Prend d'abord dans les coffres et complète avec le sac. " +
                "Par défaut c'est l'inverse : le sac est vidé en premier.");

            IgnorerCoffresOuverts = Config.Bind("General", "IgnorerCoffresOuverts", true,
                "Ignore les coffres qu'un autre joueur est en train de consulter, pour éviter que deux " +
                "retraits simultanés s'écrasent. Sans effet en solo.");

            IgnorerChariots = Config.Bind("General", "IgnorerChariots", false,
                "Ignore les chariots et les bateaux : seuls les coffres posés au sol sont utilisés.");

            ToucheChoix = Config.Bind("Controls", "ToucheChoix", new KeyboardShortcut(KeyCode.R),
                "Touche qui fait défiler l'ingrédient pris dans les coffres, en visant un appareil. " +
                "Le cycle est : Automatique, puis chaque ingrédient que l'appareil sait convertir, puis Rien. " +
                "Le choix est retenu par type d'appareil et survit au redémarrage.");

            ToucheRemplir = Config.Bind("Controls", "ToucheRemplir", new KeyboardShortcut(KeyCode.LeftShift),
                "Touche à maintenir pendant l'interaction pour remplir l'appareil jusqu'à son maximum. " +
                "Sans elle, un appui ajoute une seule unité, comme en vanilla. " +
                "Shift est la course du jeu, sans effet à l'arrêt devant un appareil.");

            AppareilsChoix = Config.Bind("Objets", "AppareilsChoix", "",
                "Choix mémorisés, écrit par le mod quand tu utilises la touche de choix. " +
                "Format : appareil:ingrédient séparés par des virgules, ex : piece_cookingstation:DeerMeat, smelter:-. " +
                $"« {ChoixAucun} » signifie que l'appareil ne puise jamais dans les coffres, " +
                $"« {ChoixAuto} » le premier ingrédient trouvé. " +
                "Une entrée absente laisse le défaut, réglé par General.Cuisson pour les grils et les fours.");

            CoffresExclus = Config.Bind("Objets", "CoffresExclus", "",
                "Noms de prefab de conteneurs jamais utilisés, séparés par des virgules " +
                "(ex : piece_chest_private, Karve, piece_cartographytable).");

            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll(typeof(Plugin).Assembly);

            Log.LogInfo($"{PluginName} {PluginVersion} chargé.");
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
            AllContainers.Clear();
            NearbyContainers.Clear();
            CountCache.Clear();
        }

        // ------------------------------------------------------------------------------------------------
        // Portée : ouverte par les points d'entrée craft/construction, fermée par leur Postfix
        // ------------------------------------------------------------------------------------------------

        /// <summary>Vrai quand les patches sur Inventory doivent inclure les coffres.</summary>
        internal static bool Active => Enabled.Value && _depth > 0 && Player.m_localPlayer != null;

        /// <summary>Ouvre la portée si <paramref name="wanted"/>, ou si une portée est déjà ouverte (appel imbriqué).</summary>
        internal static bool Open(bool wanted)
        {
            if (!Enabled.Value) return false;
            if (!wanted && _depth == 0) return false;
            _depth++;
            return true;
        }

        internal static void Close(bool opened)
        {
            if (opened && _depth > 0) _depth--;
        }

        /// <summary>Seul l'inventaire du joueur local est complété par les coffres.</summary>
        internal static bool IsPlayerInventory(Inventory inventory)
        {
            Player player = Player.m_localPlayer;
            return player != null && inventory == player.GetInventory();
        }

        // ------------------------------------------------------------------------------------------------
        // Coffres proches
        // ------------------------------------------------------------------------------------------------

        internal static void Register(Container container)
        {
            if (container != null) AllContainers.Add(container);
        }

        /// <summary>Découpe une liste "a, b, c" en gardant l'ordre et en ignorant les entrées vides.</summary>
        private static List<string> ParseList(string raw)
        {
            var list = new List<string>();
            foreach (string entry in (raw ?? "").Split(','))
            {
                string trimmed = entry.Trim();
                if (trimmed.Length > 0) list.Add(trimmed);
            }
            return list;
        }

        internal static HashSet<string> ParseExclusions()
        {
            return new HashSet<string>(ParseList(CoffresExclus.Value), StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>Nom de prefab, sans le suffixe "(Clone)" ajouté par Unity à l'instanciation.</summary>
        internal static string PrefabName(Component component)
        {
            string name = component.gameObject.name;
            int clone = name.IndexOf("(Clone)", StringComparison.Ordinal);
            return clone >= 0 ? name.Substring(0, clone) : name;
        }

        // ------------------------------------------------------------------------------------------------
        // Choix de l'ingrédient, par type d'appareil
        // ------------------------------------------------------------------------------------------------

        private static string _choicesRaw;
        private static Dictionary<string, string> _choicesCache;

        /// <summary>
        /// Choix courants. Lu à chaque appel de portée, donc plusieurs fois par frame : le résultat est
        /// mémorisé tant que la chaîne de config ne change pas, pour ne pas reparser en boucle.
        /// </summary>
        private static Dictionary<string, string> Choices()
        {
            string raw = AppareilsChoix.Value ?? "";
            if (_choicesCache != null && _choicesRaw == raw) return _choicesCache;

            _choicesCache = ParseChoices();
            _choicesRaw = raw;
            return _choicesCache;
        }

        private static Dictionary<string, string> ParseChoices()
        {
            var choices = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string entry in ParseList(AppareilsChoix.Value))
            {
                int separator = entry.IndexOf(':');
                if (separator <= 0 || separator == entry.Length - 1)
                {
                    Log.LogWarning($"Choix ignoré, format attendu appareil:ingrédient : \"{entry}\"");
                    continue;
                }
                choices[entry.Substring(0, separator).Trim()] = entry.Substring(separator + 1).Trim();
            }
            return choices;
        }

        /// <summary>Écrire dans la ConfigEntry suffit à persister : BepInEx sauvegarde à l'affectation.</summary>
        private static void SaveChoices(Dictionary<string, string> choices)
        {
            AppareilsChoix.Value = string.Join(", ", choices.Select(kv => $"{kv.Key}:{kv.Value}").ToArray());
        }

        /// <summary>Ingrédient retenu pour ce type d'appareil, ou null pour le mode automatique.</summary>
        /// <summary>
        /// Réglage courant de l'appareil : toujours ChoixAuto, ChoixAucun, ou un nom de prefab d'ingrédient.
        /// Sans entrée explicite on retombe sur le défaut, qui est Rien pour les grils et fours tant que
        /// Cuisson est désactivé : on ne sait pas ce qui va être cuit, autant ne rien prendre tout seul.
        /// </summary>
        internal static string ChoiceFor(Component station)
        {
            if (station == null) return ChoixAuto;
            if (IsHardBlocked(station)) return ChoixAucun;
            return Choices().TryGetValue(PrefabName(station), out string choice) ? choice : ChoixAuto;
        }

        /// <summary>
        /// Cuisson désactivé coupe les grils et les fours à pain sans appel : un choix enregistré à la
        /// touche R ne le contourne pas. Autrement le réglage ne servirait à rien sur un appareil déjà
        /// réglé une fois, ce qui est précisément le cas qu'on veut couvrir.
        /// </summary>
        internal static bool IsHardBlocked(Component station)
        {
            return station is CookingStation && !Cuisson.Value;
        }

        /// <summary>L'appareil est réglé sur Rien : il ne doit rien puiser dans les coffres, carburant compris.</summary>
        internal static bool IsBlocked(Component station)
        {
            return ChoiceFor(station) == ChoixAucun;
        }

        /// <summary>
        /// Ingrédients que cet appareil sait convertir. Le feu et la baliste n'en ont pas : leur cycle se
        /// réduit à Automatique et Rien, ce qui suffit puisque leur consommable est unique.
        /// </summary>
        internal static List<ItemDrop> InputsOf(Component station)
        {
            var inputs = new List<ItemDrop>();
            switch (station)
            {
                case CookingStation cooking:
                    inputs.AddRange(cooking.m_conversion.Select(c => c.m_from));
                    break;
                case Smelter smelter:
                    inputs.AddRange(smelter.m_conversion.Select(c => c.m_from));
                    break;
                case Fermenter fermenter:
                    inputs.AddRange(fermenter.m_conversion.Select(c => c.m_from));
                    break;
            }
            inputs.RemoveAll(drop => drop == null);
            return inputs;
        }

        /// <summary>
        /// Choisit quel ingrédient prendre dans les coffres parmi ceux que l'appareil sait convertir.
        /// En automatique on garde l'ordre de l'appareil, qui est celui du jeu. Sinon seul l'ingrédient
        /// retenu peut sortir d'un coffre ; les autres restent accessibles depuis le sac, comme en vanilla.
        /// </summary>
        internal static ItemDrop.ItemData PickFromContainers(Component station)
        {
            string choice = ChoiceFor(station);
            if (choice == ChoixAucun) return null;

            foreach (ItemDrop drop in InputsOf(station))
            {
                if (choice != ChoixAuto && !string.Equals(choice, drop.name, StringComparison.OrdinalIgnoreCase)) continue;

                ItemDrop.ItemData found = FindByNameInContainers(drop.m_itemData.m_shared.m_name);
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>Libellé affiché pour le réglage courant d'un appareil.</summary>
        internal static string ChoiceLabel(Component station)
        {
            string choice = ChoiceFor(station);
            if (choice == ChoixAuto) return "Automatique";
            if (choice == ChoixAucun) return "Rien";

            ItemDrop drop = InputsOf(station).Find(
                d => string.Equals(choice, d.name, StringComparison.OrdinalIgnoreCase));
            return drop != null ? Localization.instance.Localize(drop.m_itemData.m_shared.m_name) : choice;
        }

        /// <summary>
        /// Fait avancer le choix d'un cran : Automatique, chaque ingrédient dans l'ordre de l'appareil,
        /// puis Rien, puis retour à Automatique.
        /// </summary>
        internal static void CycleChoice(Component station)
        {
            List<ItemDrop> inputs = InputsOf(station);
            var cycle = new List<string> { ChoixAuto };
            cycle.AddRange(inputs.Select(d => d.name));
            cycle.Add(ChoixAucun);

            string current = ChoiceFor(station);
            int index = cycle.FindIndex(c => string.Equals(c, current, StringComparison.OrdinalIgnoreCase));

            // Le choix est toujours écrit en clair, jamais effacé : une entrée absente signifie
            // « défaut », qui n'est pas Automatique pour tous les appareils.
            var choices = new Dictionary<string, string>(Choices(), StringComparer.OrdinalIgnoreCase);
            choices[PrefabName(station)] = cycle[(index + 1) % cycle.Count];
            SaveChoices(choices);
        }

        /// <summary>Un autre joueur consulte ce coffre : le ZDO le signale mais ce n'est pas nous.</summary>
        private static bool OpenedBySomeoneElse(Container container)
        {
            ZDO zdo = container.m_nview.GetZDO();
            return zdo != null && zdo.GetInt(ZDOVars.s_inUse) == 1 && !container.IsInUse();
        }

        private static bool AccessAllowed(Container container, long playerID)
        {
            if (container.m_checkGuardStone &&
                !PrivateArea.CheckAccess(container.transform.position, 0f, flash: false))
            {
                return false;
            }

            switch (container.m_privacy)
            {
                case Container.PrivacySetting.Public:
                    return true;
                case Container.PrivacySetting.Private:
                    return container.m_piece != null && container.m_piece.GetCreator() == playerID;
                default:
                    return false;
            }
        }

        /// <summary>Liste des coffres utilisables, recalculée au plus toutes les <see cref="ScanInterval"/> secondes.</summary>
        internal static List<Container> Nearby()
        {
            if (Time.time - _lastScan < ScanInterval) return NearbyContainers;
            _lastScan = Time.time;
            Rescan();
            return NearbyContainers;
        }

        private static void Rescan()
        {
            NearbyContainers.Clear();
            CountCache.Clear();

            Player player = Player.m_localPlayer;
            if (player == null) return;

            Vector3 center = player.transform.position;
            float rayon = Rayon.Value * Rayon.Value;
            long playerID = player.GetPlayerID();
            HashSet<string> exclus = ParseExclusions();
            bool ignorerOuverts = IgnorerCoffresOuverts.Value;
            bool ignorerChariots = IgnorerChariots.Value;

            AllContainers.RemoveWhere(c => c == null);

            foreach (Container container in AllContainers)
            {
                if (container.m_nview == null || !container.m_nview.IsValid()) continue;
                if (container.GetInventory() == null) continue;
                if ((container.transform.position - center).sqrMagnitude > rayon) continue;
                if (ignorerChariots && (container.m_wagon != null || container.m_rootObjectOverride != null)) continue;
                if (ignorerOuverts && OpenedBySomeoneElse(container)) continue;
                if (!AccessAllowed(container, playerID)) continue;
                if (exclus.Count > 0 && exclus.Contains(PrefabName(container))) continue;

                NearbyContainers.Add(container);
            }

            _signature = ComputeSignature();
            if (!_hasSignature)
            {
                _hasSignature = true;
                _refreshedSignature = _signature;
            }
        }

        /// <summary>
        /// Empreinte du contenu de tous les coffres retenus. Sert uniquement à détecter un changement,
        /// pas à identifier quoi que ce soit : une collision ferait seulement rater un rafraîchissement.
        /// </summary>
        private static int ComputeSignature()
        {
            int hash = NearbyContainers.Count;
            foreach (Container container in NearbyContainers)
            {
                foreach (ItemDrop.ItemData item in container.GetInventory().m_inventory)
                {
                    hash = hash * 31 + item.m_shared.m_name.GetHashCode();
                    hash = hash * 31 + item.m_stack;
                    hash = hash * 31 + item.m_quality;
                }
            }
            return hash;
        }

        // ------------------------------------------------------------------------------------------------
        // Remplissage en un appui
        // ------------------------------------------------------------------------------------------------

        /// <summary>
        /// Test « la touche est maintenue », sans passer par KeyboardShortcut.IsPressed().
        ///
        /// IsPressed() exige qu'AUCUNE autre touche du clavier ne soit enfoncée. Or ce test tombe pendant
        /// que le joueur tient sa touche d'interaction : celle-ci fait échouer la condition à tous les
        /// coups, et le modificateur n'était donc jamais reconnu.
        /// </summary>
        private static bool RemplirDemande()
        {
            KeyboardShortcut touche = ToucheRemplir.Value;
            if (touche.MainKey == KeyCode.None || !Input.GetKey(touche.MainKey)) return false;

            foreach (KeyCode modificateur in touche.Modifiers)
            {
                if (!Input.GetKey(modificateur)) return false;
            }
            return true;
        }

        private static bool _filling;

        /// <summary>Vrai pendant un remplissage : sert à avaler les messages répétés du jeu.</summary>
        internal static bool Filling => _filling;

        /// <summary>
        /// Répète l'ajout que le jeu vient de faire jusqu'à ce que l'appareil refuse, puis résume en un
        /// seul message.
        ///
        /// Deux précautions. La propriété du ZDO est prise d'abord : InvokeRoutedRPC ne s'exécute
        /// immédiatement que si on est le destinataire, sinon l'ajout partirait sur le réseau et la
        /// quantité lue à l'itération suivante serait encore l'ancienne, donc la boucle ne s'arrêterait
        /// jamais d'elle-même. Et le nombre d'ajouts est plafonné par la capacité de l'appareil, au cas où.
        /// </summary>
        internal static void FillToMax(ZNetView nview, Humanoid user, string label, int capacite, Func<bool> ajouter)
        {
            if (!RemplirDUnCoup.Value || _filling) return;
            if (!RemplirDemande()) return;
            if (nview != null && nview.IsValid() && !nview.IsOwner()) nview.ClaimOwnership();

            int ajoutes = 1; // le premier ajout est celui que le jeu vient de faire
            _filling = true;
            try
            {
                while (ajoutes < Math.Max(1, capacite) && ajouter()) ajoutes++;
            }
            catch (Exception e)
            {
                Log.LogWarning($"Remplissage interrompu : {e.Message}");
            }
            finally
            {
                _filling = false;
            }

            if (user != null) user.Message(MessageHud.MessageType.Center, $"{label} x{ajoutes}");
        }

        /// <summary>Nom lisible de l'appareil : celui de la pièce construite, pas celui du prefab.</summary>
        internal static string StationName(Component station)
        {
            Piece piece = station.GetComponentInParent<Piece>();
            return piece != null ? Localization.instance.Localize(piece.m_name) : PrefabName(station);
        }

        /// <summary>Ligne ajoutée au texte de survol d'un appareil pour montrer et rappeler le réglage.</summary>
        internal static string AppendChoice(Component station, string text)
        {
            if (!Enabled.Value || !Appareils.Value || string.IsNullOrEmpty(text)) return text;
            if (IsHardBlocked(station)) return text;
            return text + "\n[<color=yellow><b>" + ToucheChoix.Value.MainKey +
                   "</b></color>] Coffres : " + ChoiceLabel(station);
        }

        /// <summary>Nom de touche présentable : l'enum Unity dit "LeftShift" là où le joueur lit "Shift".</summary>
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

        /// <summary>Rappel de la touche de remplissage, sur les appareils qui empilent plusieurs unités.</summary>
        internal static string AppendFill(Component station, string text)
        {
            if (!Enabled.Value || !RemplirDUnCoup.Value || string.IsNullOrEmpty(text)) return text;
            if (!(station is Smelter || station is CookingStation || station is Fireplace)) return text;

            string utiliser = Localization.instance.Localize("$KEY_Use");
            return text + "\n[<color=yellow><b>" + NomTouche(ToucheRemplir.Value.MainKey) + " + " + utiliser +
                   "</b></color>] Tout ajouter";
        }

        /// <summary>Appareil auquel appartient cet interrupteur, seulement pour ceux qui ajoutent quelque chose.</summary>
        internal static Component StationOfSwitch(Switch sw)
        {
            CookingStation cooking = sw.GetComponentInParent<CookingStation>();
            if (cooking != null)
            {
                return sw == cooking.m_addFoodSwitch || sw == cooking.m_addFuelSwitch ? cooking : null;
            }

            Smelter smelter = sw.GetComponentInParent<Smelter>();
            if (smelter != null)
            {
                return sw == smelter.m_addOreSwitch || sw == smelter.m_addWoodSwitch ? smelter : null;
            }

            Fermenter fermenter = sw.GetComponentInParent<Fermenter>();
            if (fermenter != null)
            {
                return sw == fermenter.m_addSwitch ? fermenter : null;
            }

            return null;
        }

        /// <summary>Appareil actuellement visé par le joueur, ou null.</summary>
        private static Component HoveredStation()
        {
            GameObject hover = Player.m_localPlayer.GetHoverObject();
            if (hover == null) return null;

            CookingStation cooking = hover.GetComponentInParent<CookingStation>();
            if (cooking != null) return cooking;

            Smelter smelter = hover.GetComponentInParent<Smelter>();
            if (smelter != null) return smelter;

            Fermenter fermenter = hover.GetComponentInParent<Fermenter>();
            if (fermenter != null) return fermenter;

            Fireplace fireplace = hover.GetComponentInParent<Fireplace>();
            if (fireplace != null) return fireplace;

            Turret turret = hover.GetComponentInParent<Turret>();
            if (turret != null) return turret;

            return null;
        }

        /// <summary>Mêmes verrous que RowTogether : la touche ne part pas pendant qu'on tape ou qu'un menu est ouvert.</summary>
        private static bool CanUseInput()
        {
            if (Console.IsVisible() || Menu.IsVisible() || TextInput.IsVisible()) return false;
            if (InventoryGui.IsVisible() || Minimap.IsOpen()) return false;
            if (Chat.instance != null && Chat.instance.HasFocus()) return false;
            return true;
        }

        /// <summary>Touche de choix : fait avancer le réglage de l'appareil visé et le confirme à l'écran.</summary>
        private static void HandleChoiceKey()
        {
            if (!Appareils.Value || !ToucheChoix.Value.IsDown() || !CanUseInput()) return;

            Component station = HoveredStation();
            if (station == null) return;

            if (IsHardBlocked(station))
            {
                Player.m_localPlayer.Message(MessageHud.MessageType.Center,
                    $"{StationName(station)} — coffres désactivés (General.Cuisson)");
                return;
            }

            CycleChoice(station);
            Player.m_localPlayer.Message(MessageHud.MessageType.Center,
                $"{StationName(station)} — coffres : {ChoiceLabel(station)}");
        }

        /// <summary>
        /// Le jeu ne reconstruit la liste des recettes que sur événement (ouverture du panneau, craft,
        /// changement d'inventaire). Un coffre qui se remplit pendant que le panneau est ouvert ne
        /// déclenche rien, donc une recette devenue fabricable resterait grisée. Les quantités affichées,
        /// elles, sont recalculées chaque frame par UpdateRecipe et étaient déjà à jour.
        ///
        /// Appelé depuis Update, donc hors de toute portée : pas de réentrance dans les patches.
        /// UpdateCraftingPanel est ce que le jeu appelle lui-même après chaque craft, et il conserve la
        /// recette sélectionnée.
        /// </summary>
        private void Update()
        {
            if (!Enabled.Value || Player.m_localPlayer == null) return;

            HandleChoiceKey();

            if (InventoryGui.instance == null || !InventoryGui.IsVisible()) return;
            if (ZoneSystem.instance == null) return;

            Nearby();
            if (!_hasSignature || _signature == _refreshedSignature) return;

            _refreshedSignature = _signature;
            try
            {
                InventoryGui.instance.UpdateCraftingPanel();
            }
            catch (Exception e)
            {
                Log.LogWarning($"Rafraîchissement du panneau impossible : {e.Message}");
            }
        }

        /// <summary>Force un recalcul au prochain accès (après un retrait, les comptages sont périmés).</summary>
        internal static void Invalidate()
        {
            CountCache.Clear();
        }

        // ------------------------------------------------------------------------------------------------
        // Comptage et retrait
        // ------------------------------------------------------------------------------------------------

        /// <summary>Comptage vanilla (Inventory.CountItems) appliqué à un inventaire donné, sans repasser par les patches.</summary>
        internal static int CountIn(Inventory inventory, string name, int quality, bool matchWorldLevel)
        {
            int total = 0;
            foreach (ItemDrop.ItemData item in inventory.m_inventory)
            {
                if ((name == null || item.m_shared.m_name == name) &&
                    (quality < 0 || quality == item.m_quality) &&
                    (!matchWorldLevel || item.m_worldLevel >= Game.m_worldLevel))
                {
                    total += item.m_stack;
                }
            }
            return total;
        }

        /// <summary>Total présent dans les coffres proches, mis en cache jusqu'au prochain scan.</summary>
        internal static int CountInContainers(string name, int quality, bool matchWorldLevel)
        {
            if (name == null) return 0;

            List<Container> containers = Nearby();
            if (containers.Count == 0) return 0;

            string key = name + "|" + quality + "|" + (matchWorldLevel ? 1 : 0);
            if (CountCache.TryGetValue(key, out int cached)) return cached;

            int total = 0;
            foreach (Container container in containers)
            {
                total += CountIn(container.GetInventory(), name, quality, matchWorldLevel);
            }

            CountCache[key] = total;
            return total;
        }

        /// <summary>Premier objet correspondant trouvé dans les coffres proches, ou null.</summary>
        internal static ItemDrop.ItemData FindInContainers(string name, int quality, int minAmount)
        {
            foreach (Container container in Nearby())
            {
                if (CountIn(container.GetInventory(), name, quality, matchWorldLevel: true) < minAmount) continue;
                foreach (ItemDrop.ItemData item in container.GetInventory().m_inventory)
                {
                    if (item.m_shared.m_name == name && (quality < 0 || item.m_quality == quality)) return item;
                }
            }
            return null;
        }

        /// <summary>
        /// Retire jusqu'à <paramref name="amount"/> exemplaires dans les coffres proches et renvoie la
        /// quantité réellement retirée. Prend la propriété du ZDO avant d'écrire, sinon le serveur
        /// écraserait la modification à la prochaine synchronisation.
        /// </summary>
        internal static int RemoveFromContainers(string name, int amount, int quality, bool worldLevelBased)
        {
            if (amount <= 0 || name == null) return 0;

            int removed = 0;
            foreach (Container container in Nearby())
            {
                if (removed >= amount) break;

                Inventory inventory = container.GetInventory();
                int available = CountIn(inventory, name, quality, worldLevelBased);
                if (available <= 0) continue;

                int take = Math.Min(available, amount - removed);
                if (WriteTo(container, inv => inv.RemoveItem(name, take, quality, worldLevelBased)))
                {
                    removed += take;
                }
            }

            return removed;
        }

        /// <summary>
        /// Prend la propriété du ZDO, applique l'écriture, puis force la sauvegarde. Sans ClaimOwnership
        /// la modification serait écrasée à la prochaine synchronisation venant du propriétaire réel.
        /// </summary>
        private static bool WriteTo(Container container, Action<Inventory> write)
        {
            try
            {
                if (!container.m_nview.IsOwner()) container.m_nview.ClaimOwnership();
                write(container.GetInventory());
                container.Save();
                Invalidate();
                return true;
            }
            catch (Exception e)
            {
                Log.LogWarning($"Écriture impossible dans {PrefabName(container)} : {e.Message}");
                return false;
            }
        }

        /// <summary>Coffre proche qui détient physiquement cet objet, ou null si l'objet vient du sac.</summary>
        internal static Container OwnerOf(ItemDrop.ItemData item)
        {
            if (item == null) return null;
            foreach (Container container in Nearby())
            {
                if (container.GetInventory().m_inventory.Contains(item)) return container;
            }
            return null;
        }

        /// <summary>
        /// Retrait d'un objet désigné par identité, redirigé vers le coffre qui le détient.
        /// Renvoie false si l'objet n'appartient à aucun coffre proche : c'est alors au jeu de le retirer.
        /// </summary>
        private static bool RemoveExact(ItemDrop.ItemData item, Action<Inventory> write)
        {
            Container container = OwnerOf(item);
            return container != null && WriteTo(container, write);
        }

        internal static bool RemoveOneExact(ItemDrop.ItemData item)
        {
            return RemoveExact(item, inv => inv.RemoveOneItem(item));
        }

        internal static bool RemoveStackExact(ItemDrop.ItemData item)
        {
            return RemoveExact(item, inv => inv.RemoveItem(item));
        }

        internal static bool RemoveAmountExact(ItemDrop.ItemData item, int amount)
        {
            return RemoveExact(item, inv => inv.RemoveItem(item, amount));
        }

        /// <summary>Équivalent de Inventory.GetItem(nom) appliqué aux coffres proches.</summary>
        internal static ItemDrop.ItemData FindByNameInContainers(string name)
        {
            if (name == null) return null;
            foreach (Container container in Nearby())
            {
                foreach (ItemDrop.ItemData item in container.GetInventory().m_inventory)
                {
                    if (item.m_shared.m_name == name && item.m_worldLevel >= Game.m_worldLevel) return item;
                }
            }
            return null;
        }

        /// <summary>Équivalent de Inventory.GetAmmoItem appliqué aux coffres proches (balistes, tourelles).</summary>
        internal static ItemDrop.ItemData FindAmmoInContainers(string ammoName, string matchPrefabName)
        {
            foreach (Container container in Nearby())
            {
                foreach (ItemDrop.ItemData item in container.GetInventory().m_inventory)
                {
                    bool isAmmo = item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Ammo ||
                                  item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.AmmoNonEquipable ||
                                  item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Consumable;

                    if (isAmmo && item.m_shared.m_ammoType == ammoName &&
                        (matchPrefabName == null || (item.m_dropPrefab != null && item.m_dropPrefab.name == matchPrefabName)))
                    {
                        return item;
                    }
                }
            }
            return null;
        }

        // ------------------------------------------------------------------------------------------------
        // Diagnostic
        // ------------------------------------------------------------------------------------------------

        internal static string DescribeNearby()
        {
            var sb = new StringBuilder();
            Player player = Player.m_localPlayer;
            if (player == null) return "Pas de joueur local.";

            _lastScan = -999f;
            List<Container> containers = Nearby();
            Vector3 center = player.transform.position;

            foreach (Container container in containers.OrderBy(c => (c.transform.position - center).sqrMagnitude))
            {
                Inventory inventory = container.GetInventory();
                float distance = Vector3.Distance(container.transform.position, center);
                sb.AppendLine($"{PrefabName(container),-28} {distance,5:0.0} m  " +
                              $"{inventory.NrOfItems(),3} pile(s)  propriétaire : {(container.m_nview.IsOwner() ? "moi" : "autre")}");
            }

            sb.AppendLine($"{containers.Count} coffre(s) dans un rayon de {Rayon.Value:0.#} m " +
                          $"sur {AllContainers.Count} chargé(s).");
            return sb.ToString();
        }
    }

    // ----------------------------------------------------------------------------------------------------
    // Enregistrement des coffres
    // ----------------------------------------------------------------------------------------------------

    /// <summary>Chaque conteneur instancié s'inscrit lui-même : pas de requête physique à faire ensuite.</summary>
    [HarmonyPatch(typeof(Container), nameof(Container.Awake))]
    internal static class Container_Awake_Patch
    {
        private static void Postfix(Container __instance)
        {
            Plugin.Register(__instance);
        }
    }

    // ----------------------------------------------------------------------------------------------------
    // Points d'entrée : ouverture de la portée
    // ----------------------------------------------------------------------------------------------------

    /// <summary>Recette : état du bouton Fabriquer, et vérification refaite au moment du craft.</summary>
    [HarmonyPatch(typeof(Player), nameof(Player.HaveRequirements),
        new[] { typeof(Recipe), typeof(bool), typeof(int), typeof(int) })]
    internal static class Player_HaveRequirementsRecipe_Patch
    {
        private static void Prefix(out bool __state)
        {
            __state = Plugin.Open(Plugin.Fabrication.Value);
        }

        private static void Finalizer(bool __state)
        {
            Plugin.Close(__state);
        }
    }

    /// <summary>Pièce de construction : grisage dans le menu du marteau et test avant la pose.</summary>
    [HarmonyPatch(typeof(Player), nameof(Player.HaveRequirements),
        new[] { typeof(Piece), typeof(Player.RequirementMode) })]
    internal static class Player_HaveRequirementsPiece_Patch
    {
        private static void Prefix(out bool __state)
        {
            __state = Plugin.Open(Plugin.Construction.Value);
        }

        private static void Finalizer(bool __state)
        {
            Plugin.Close(__state);
        }
    }

    /// <summary>Consommation des matériaux, à la fois pour le craft et pour la pose d'une pièce.</summary>
    [HarmonyPatch(typeof(Player), nameof(Player.ConsumeResources))]
    internal static class Player_ConsumeResources_Patch
    {
        private static void Prefix(out bool __state)
        {
            // Pendant un craft la portée est déjà ouverte par DoCrafting : Open la conserve.
            __state = Plugin.Open(Plugin.Construction.Value);
        }

        private static void Finalizer(bool __state)
        {
            Plugin.Close(__state);
        }
    }

    /// <summary>Ligne "matériau x quantité" du panneau d'artisanat et du menu de construction.</summary>
    [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.SetupRequirement))]
    internal static class InventoryGui_SetupRequirement_Patch
    {
        private static void Prefix(bool craft, out bool __state)
        {
            __state = Plugin.Open(craft ? Plugin.Fabrication.Value : Plugin.Construction.Value);
        }

        private static void Finalizer(bool __state)
        {
            Plugin.Close(__state);
        }
    }

    /// <summary>Le craft lui-même : couvre la consommation et le cas "un seul ingrédient au choix".</summary>
    [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.DoCrafting))]
    internal static class InventoryGui_DoCrafting_Patch
    {
        private static void Prefix(out bool __state)
        {
            __state = Plugin.Open(Plugin.Fabrication.Value);
        }

        private static void Finalizer(bool __state)
        {
            Plugin.Close(__state);
        }
    }

    /// <summary>
    /// Recettes à ingrédient unique au choix (Recipe.m_requireOnlyOneIngredient) : le jeu cherche
    /// l'ingrédient dans le sac uniquement. S'il n'y est pas, on en désigne un pris dans les coffres ;
    /// le retrait qui suit passe par Inventory.RemoveItem, donc par le patch plus bas.
    /// </summary>
    [HarmonyPatch(typeof(Player), nameof(Player.GetFirstRequiredItem))]
    internal static class Player_GetFirstRequiredItem_Patch
    {
        private static void Prefix(out bool __state)
        {
            __state = Plugin.Open(Plugin.Fabrication.Value);
        }

        private static void Postfix(Player __instance, Recipe recipe, int qualityLevel, int craftMultiplier,
            ref ItemDrop.ItemData __result, ref int amount, ref int extraAmount, bool __state)
        {
            if (!__state || __result != null || !Plugin.Active) return;

            CraftingStation station = __instance.GetCurrentCraftingStation();
            foreach (Piece.Requirement requirement in recipe.m_resources)
            {
                if ((station != null && station.m_upgrader != requirement.m_upgraderResource) ||
                    (station == null && requirement.m_upgraderResource) ||
                    !requirement.m_resItem)
                {
                    continue;
                }

                int need = requirement.GetAmount(qualityLevel) * craftMultiplier;
                if (need <= 0) continue;

                string name = requirement.m_resItem.m_itemData.m_shared.m_name;
                for (int quality = 0; quality <= requirement.m_resItem.m_itemData.m_shared.m_maxQuality; quality++)
                {
                    ItemDrop.ItemData found = Plugin.FindInContainers(name, quality, need);
                    if (found == null) continue;

                    amount = need;
                    extraAmount = requirement.m_extraAmountOnlyOneIngredient;
                    __result = found;
                    return;
                }
            }
        }

        private static void Finalizer(bool __state)
        {
            Plugin.Close(__state);
        }
    }

    // ----------------------------------------------------------------------------------------------------
    // Méthodes feuilles : c'est ici que les coffres s'ajoutent au sac
    // ----------------------------------------------------------------------------------------------------

    [HarmonyPatch(typeof(Inventory), nameof(Inventory.CountItems),
        new[] { typeof(string), typeof(int), typeof(bool) })]
    internal static class Inventory_CountItems_Patch
    {
        private static void Postfix(Inventory __instance, string name, int quality, bool matchWorldLevel,
            ref int __result)
        {
            if (!Plugin.Active || !Plugin.IsPlayerInventory(__instance)) return;
            __result += Plugin.CountInContainers(name, quality, matchWorldLevel);
        }
    }

    [HarmonyPatch(typeof(Inventory), nameof(Inventory.HaveItem),
        new[] { typeof(string), typeof(bool) })]
    internal static class Inventory_HaveItem_Patch
    {
        private static void Postfix(Inventory __instance, string name, bool matchWorldLevel, ref bool __result)
        {
            if (__result || !Plugin.Active || !Plugin.IsPlayerInventory(__instance)) return;
            __result = Plugin.CountInContainers(name, -1, matchWorldLevel) > 0;
        }
    }

    /// <summary>
    /// Retrait par nom : le sac d'abord, les coffres pour le reste (ou l'inverse selon PrioriteCoffres).
    /// Le Prefix baisse la quantité demandée au jeu de ce qui a été pris ailleurs.
    /// </summary>
    [HarmonyPatch(typeof(Inventory), nameof(Inventory.RemoveItem),
        new[] { typeof(string), typeof(int), typeof(int), typeof(bool) })]
    internal static class Inventory_RemoveItem_Patch
    {
        private static void Prefix(Inventory __instance, string name, ref int amount, int itemQuality,
            bool worldLevelBased, out int __state)
        {
            __state = 0;
            if (!Plugin.Active || !Plugin.IsPlayerInventory(__instance)) return;

            if (Plugin.PrioriteCoffres.Value)
            {
                amount -= Plugin.RemoveFromContainers(name, amount, itemQuality, worldLevelBased);
            }
            else
            {
                // Ce que le sac ne couvre pas sera pris dans les coffres après coup.
                __state = amount - Plugin.CountIn(__instance, name, itemQuality, worldLevelBased);
            }
        }

        private static void Postfix(Inventory __instance, string name, int itemQuality, bool worldLevelBased,
            int __state)
        {
            if (__state <= 0) return;
            if (!Plugin.Active || !Plugin.IsPlayerInventory(__instance)) return;
            Plugin.RemoveFromContainers(name, __state, itemQuality, worldLevelBased);
        }
    }

    // ----------------------------------------------------------------------------------------------------
    // Appareils : gril, four, fondoir, moulin, rouet, fermenteur, feu, baliste
    // ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Ces appareils ne fabriquent pas, ils convertissent : ils prennent l'objet directement dans le sac
    /// par leur propre chemin, sans passer par Player.HaveRequirements ni ConsumeResources. Un seul patch
    /// à cibles multiples ouvre la portée sur chacun de leurs points d'entrée, y compris les méthodes
    /// d'infobulle (CanUseItems, TryGetItems) pour que l'invite affichée corresponde à ce qui est possible.
    /// </summary>
    [HarmonyPatch]
    internal static class Stations_Scope_Patch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(CookingStation), nameof(CookingStation.OnInteract));
            yield return AccessTools.Method(typeof(CookingStation), nameof(CookingStation.UseItem));
            yield return AccessTools.Method(typeof(CookingStation), nameof(CookingStation.OnAddFuelSwitch));
            yield return AccessTools.Method(typeof(CookingStation), nameof(CookingStation.CanUseItems));
            yield return AccessTools.Method(typeof(CookingStation), nameof(CookingStation.TryGetItems));

            yield return AccessTools.Method(typeof(Smelter), nameof(Smelter.OnAddOre));
            yield return AccessTools.Method(typeof(Smelter), nameof(Smelter.OnAddFuel));
            yield return AccessTools.Method(typeof(Smelter), nameof(Smelter.CanUseItems));
            yield return AccessTools.Method(typeof(Smelter), nameof(Smelter.TryGetItems));

            yield return AccessTools.Method(typeof(Fermenter), nameof(Fermenter.Interact));
            yield return AccessTools.Method(typeof(Fermenter), nameof(Fermenter.UseItem));

            yield return AccessTools.Method(typeof(Fireplace), nameof(Fireplace.Interact));
            yield return AccessTools.Method(typeof(Fireplace), nameof(Fireplace.UseItem));
            yield return AccessTools.Method(typeof(Fireplace), nameof(Fireplace.CanUseItems));
            yield return AccessTools.Method(typeof(Fireplace), nameof(Fireplace.TryGetItems));

            yield return AccessTools.Method(typeof(Turret), nameof(Turret.UseItem));
        }

        private static void Prefix(MonoBehaviour __instance, out bool __state)
        {
            __state = Plugin.Open(Plugin.Appareils.Value && !Plugin.IsBlocked(__instance));
        }

        private static void Finalizer(bool __state)
        {
            Plugin.Close(__state);
        }
    }

    /// <summary>
    /// Gril et four : l'aliment à cuire. Volontairement séparé de FindIncompatibleItem, qui reste limité
    /// au sac. Sinon un objet interdit rangé dans un coffre bloquerait la cuisson.
    /// </summary>
    [HarmonyPatch(typeof(CookingStation), nameof(CookingStation.FindCookableItem))]
    internal static class CookingStation_FindCookableItem_Patch
    {
        private static void Postfix(CookingStation __instance, ref ItemDrop.ItemData __result)
        {
            if (__result != null || !Plugin.Active) return;
            __result = Plugin.PickFromContainers(__instance);
        }
    }

    /// <summary>Fondoir, four à charbon, moulin, rouet, raffinerie d'eitr : la matière première.</summary>
    [HarmonyPatch(typeof(Smelter), nameof(Smelter.FindCookableItem))]
    internal static class Smelter_FindCookableItem_Patch
    {
        private static void Postfix(Smelter __instance, ref ItemDrop.ItemData __result)
        {
            if (__result != null || !Plugin.Active) return;
            __result = Plugin.PickFromContainers(__instance);
        }
    }

    /// <summary>Fermenteur : le tonneau d'hydromel à faire fermenter.</summary>
    [HarmonyPatch(typeof(Fermenter), nameof(Fermenter.FindCookableItem))]
    internal static class Fermenter_FindCookableItem_Patch
    {
        private static void Postfix(Fermenter __instance, ref ItemDrop.ItemData __result)
        {
            if (__result != null || !Plugin.Active) return;
            __result = Plugin.PickFromContainers(__instance);
        }
    }

    /// <summary>
    /// Rappel du réglage sur le texte de survol. Deux chemins existent : les appareils sans interrupteur
    /// construisent eux-mêmes leur texte, les autres délèguent à Switch. CookingStation.GetHoverText rend
    /// une chaîne vide quand un interrupteur existe, d'où le garde sur le texte vide côté AppendChoice.
    /// </summary>
    [HarmonyPatch(typeof(CookingStation), nameof(CookingStation.GetHoverText))]
    internal static class CookingStation_GetHoverText_Patch
    {
        private static void Postfix(CookingStation __instance, ref string __result)
        {
            __result = Plugin.AppendFill(__instance, Plugin.AppendChoice(__instance, __result));
        }
    }

    [HarmonyPatch(typeof(Fermenter), nameof(Fermenter.GetHoverText))]
    internal static class Fermenter_GetHoverText_Patch
    {
        private static void Postfix(Fermenter __instance, ref string __result)
        {
            __result = Plugin.AppendChoice(__instance, __result);
        }
    }

    /// <summary>Feux et torches : ils construisent leur texte de survol eux-mêmes.</summary>
    [HarmonyPatch(typeof(Fireplace), nameof(Fireplace.GetHoverText))]
    internal static class Fireplace_GetHoverText_Patch
    {
        private static void Postfix(Fireplace __instance, ref string __result)
        {
            __result = Plugin.AppendFill(__instance, __result);
        }
    }

    /// <summary>Interrupteurs d'ajout : ceux du fondoir, du four et du fermenteur.</summary>
    [HarmonyPatch(typeof(Switch), nameof(Switch.GetHoverText))]
    internal static class Switch_GetHoverText_Patch
    {
        private static void Postfix(Switch __instance, ref string __result)
        {
            Component station = Plugin.StationOfSwitch(__instance);
            if (station != null) __result = Plugin.AppendFill(station, Plugin.AppendChoice(station, __result));
        }
    }

    /// <summary>Munitions des balistes : Turret.FindAmmoItem passe par là.</summary>
    [HarmonyPatch(typeof(Inventory), nameof(Inventory.GetAmmoItem))]
    internal static class Inventory_GetAmmoItem_Patch
    {
        private static void Postfix(Inventory __instance, string ammoName, string matchPrefabName,
            ref ItemDrop.ItemData __result)
        {
            if (__result != null || !Plugin.Active || !Plugin.IsPlayerInventory(__instance)) return;
            __result = Plugin.FindAmmoInContainers(ammoName, matchPrefabName);
        }
    }

    // ----------------------------------------------------------------------------------------------------
    // Retrait par identité : l'objet désigné appartient à un coffre, pas au sac
    // ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Les appareils retirent l'objet qu'ils ont trouvé, par référence et non par nom. Quand cette
    /// référence vient d'un coffre, la retirer du sac échouerait sans rien dire et l'objet serait dupliqué :
    /// consommé par l'appareil, toujours présent dans le coffre. Ces trois patches redirigent le retrait.
    /// </summary>
    [HarmonyPatch(typeof(Inventory), nameof(Inventory.RemoveOneItem))]
    internal static class Inventory_RemoveOneItem_Patch
    {
        private static bool Prefix(Inventory __instance, ItemDrop.ItemData item, ref bool __result)
        {
            if (!Plugin.Active || !Plugin.IsPlayerInventory(__instance)) return true;
            if (__instance.m_inventory.Contains(item)) return true;
            if (!Plugin.RemoveOneExact(item)) return true;

            __result = true;
            return false;
        }
    }

    [HarmonyPatch(typeof(Inventory), nameof(Inventory.RemoveItem), new[] { typeof(ItemDrop.ItemData) })]
    internal static class Inventory_RemoveItemStack_Patch
    {
        private static bool Prefix(Inventory __instance, ItemDrop.ItemData item, ref bool __result)
        {
            if (!Plugin.Active || !Plugin.IsPlayerInventory(__instance)) return true;
            if (__instance.m_inventory.Contains(item)) return true;
            if (!Plugin.RemoveStackExact(item)) return true;

            __result = true;
            return false;
        }
    }

    [HarmonyPatch(typeof(Inventory), nameof(Inventory.RemoveItem), new[] { typeof(ItemDrop.ItemData), typeof(int) })]
    internal static class Inventory_RemoveItemAmount_Patch
    {
        private static bool Prefix(Inventory __instance, ItemDrop.ItemData item, int amount, ref bool __result)
        {
            if (!Plugin.Active || !Plugin.IsPlayerInventory(__instance)) return true;
            if (__instance.m_inventory.Contains(item)) return true;
            if (!Plugin.RemoveAmountExact(item, amount)) return true;

            __result = true;
            return false;
        }
    }

    // ----------------------------------------------------------------------------------------------------
    // Remplissage en un appui
    // ----------------------------------------------------------------------------------------------------

    /// <summary>Le jeu répète son message à chaque unité ajoutée : on l'avale et on résume à la fin.</summary>
    [HarmonyPatch(typeof(Player), nameof(Player.Message))]
    internal static class Player_Message_Patch
    {
        private static bool Prefix()
        {
            return !Plugin.Filling;
        }
    }

    /// <summary>Charbon et bois des fondoirs, fours à charbon, hauts fourneaux.</summary>
    [HarmonyPatch(typeof(Smelter), nameof(Smelter.OnAddFuel))]
    internal static class Smelter_OnAddFuel_Fill_Patch
    {
        private static void Postfix(Smelter __instance, Switch sw, Humanoid user, bool __result)
        {
            if (!__result) return;
            Plugin.FillToMax(__instance.m_nview, user,
                Localization.instance.Localize(__instance.m_fuelItem.m_itemData.m_shared.m_name),
                __instance.m_maxFuel, () => __instance.OnAddFuel(sw, user, null));
        }
    }

    /// <summary>Minerai des fondoirs, orge des moulins, lin des rouets.</summary>
    [HarmonyPatch(typeof(Smelter), nameof(Smelter.OnAddOre))]
    internal static class Smelter_OnAddOre_Fill_Patch
    {
        private static void Postfix(Smelter __instance, Switch sw, Humanoid user, bool __result)
        {
            if (!__result) return;
            Plugin.FillToMax(__instance.m_nview, user, Plugin.StationName(__instance),
                __instance.m_maxOre, () => __instance.OnAddOre(sw, user, null));
        }
    }

    /// <summary>Carburant des grils et des fours à pain.</summary>
    [HarmonyPatch(typeof(CookingStation), nameof(CookingStation.OnAddFuelSwitch))]
    internal static class CookingStation_OnAddFuel_Fill_Patch
    {
        private static void Postfix(CookingStation __instance, Switch sw, Humanoid user, bool __result)
        {
            if (!__result) return;
            Plugin.FillToMax(__instance.m_nview, user,
                Localization.instance.Localize(__instance.m_fuelItem.m_itemData.m_shared.m_name),
                __instance.m_maxFuel, () => __instance.OnAddFuelSwitch(sw, user, null));
        }
    }

    /// <summary>
    /// Bois des feux et des torches. Interact sert aussi à allumer et éteindre : le Prefix repère ce
    /// cas-là pour ne pas le confondre avec un ajout. La répétition passe alt à true, ce qui est
    /// exactement la condition qui saute la bascule allumé/éteint.
    /// </summary>
    [HarmonyPatch(typeof(Fireplace), nameof(Fireplace.Interact))]
    internal static class Fireplace_Interact_Fill_Patch
    {
        private static void Prefix(Fireplace __instance, bool hold, bool alt, out bool __state)
        {
            float fuel = __instance.m_nview != null && __instance.m_nview.IsValid()
                ? __instance.m_nview.GetZDO().GetFloat(ZDOVars.s_fuel)
                : 0f;
            __state = __instance.m_canTurnOff && !hold && !alt && fuel > 0f;
        }

        private static void Postfix(Fireplace __instance, Humanoid user, bool __result, bool __state)
        {
            if (!__result || __state) return;
            Plugin.FillToMax(__instance.m_nview, user,
                Localization.instance.Localize(__instance.m_fuelItem.m_itemData.m_shared.m_name),
                Mathf.CeilToInt(__instance.m_maxFuel), () => __instance.Interact(user, hold: false, alt: true));
        }
    }

    // ----------------------------------------------------------------------------------------------------
    // Console
    // ----------------------------------------------------------------------------------------------------

    /// <summary>Commandes console chestcraft_list et chestcraft_reload.</summary>
    [HarmonyPatch(typeof(Terminal), nameof(Terminal.InitTerminal))]
    internal static class Terminal_InitTerminal_Patch
    {
        private static bool _registered;

        private static void Postfix()
        {
            if (_registered) return;
            _registered = true;

            new Terminal.ConsoleCommand("chestcraft_list",
                "Liste les coffres pris en compte autour du joueur, avec leur distance et leur propriétaire.",
                delegate (Terminal.ConsoleEventArgs args)
                {
                    string text = Plugin.DescribeNearby();
                    Plugin.Log.LogInfo("chestcraft_list :\n" + text);
                    foreach (string line in text.Split('\n'))
                    {
                        if (line.Trim().Length > 0) args.Context.AddString(line);
                    }
                });

            new Terminal.ConsoleCommand("chestcraft_reload",
                "Recharge la config ChestCraft depuis le fichier.",
                delegate (Terminal.ConsoleEventArgs args)
                {
                    Plugin.Instance.Config.Reload();
                    Plugin.Invalidate();
                    args.Context.AddString("Config ChestCraft rechargée.");
                });
        }
    }
}
