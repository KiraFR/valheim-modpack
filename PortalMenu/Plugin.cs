using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PortalMenu
{
    /// <summary>
    /// Remplace l'appairage un-à-un des portails par un choix libre de la destination : interagir avec un
    /// portail ouvre la liste de tous les portails du monde, cliquer sur une ligne téléporte.
    ///
    /// Principe : le voyage vanilla (TeleportWorld.Teleport) ne fait que lire la position du ZDO apparié puis
    /// appeler Player.TeleportTo, qui est purement local. On garde ce dernier appel et on lui passe les
    /// coordonnées choisies : aucune connexion ZDOExtraData.ConnectionType.Portal n'est touchée, la sauvegarde
    /// reste du vanilla pur et désinstaller le mod laisse des portails intacts.
    ///
    /// Multijoueur : le registre complet des portails (ZDOMan.GetPortalList) n'existe que sur le serveur, un
    /// client ne reçoit que les portails de ses secteurs proches. Le mod demande donc la liste au serveur par un
    /// RPC routé. Sans le mod sur le serveur, le panneau se replie sur les portails connus localement (tous en
    /// solo ou en hébergé, seulement les proches sur un serveur dédié) et le signale.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "valheim.portalmenu";
        public const string PluginName = "PortalMenu";
        public const string PluginVersion = "1.0.0";

        internal static ManualLogSource Log;

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> PortailsSansNom;
        internal static ConfigEntry<bool> TrierParDistance;
        internal static ConfigEntry<bool> AutoriserTousObjets;
        internal static ConfigEntry<float> DistanceMaximale;
        internal static ConfigEntry<bool> AfficherBiome;
        internal static ConfigEntry<bool> DesactiverAppairageVanilla;
        internal static ConfigEntry<float> LargeurPanneau;
        internal static ConfigEntry<float> HauteurPanneau;
        internal static ConfigEntry<float> Echelle;
        internal static ConfigEntry<string> CouleurFond;

        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;

            // Fichier généré : BepInEx/config/valheim.portalmenu.cfg
            Enabled = Config.Bind("General", "Enabled", true,
                "Active ou désactive le mod. Désactivé, les portails retrouvent leur comportement vanilla " +
                "(appairage par nom identique).");

            PortailsSansNom = Config.Bind("General", "PortailsSansNom", false,
                "Affiche aussi les portails auxquels aucun nom n'a été donné. Pratique pour retrouver un " +
                "portail oublié, encombrant si beaucoup de portails sont posés sans être nommés.");

            TrierParDistance = Config.Bind("General", "TrierParDistance", false,
                "Trie la liste par distance croissante plutôt que par nom. Le bouton de tri du panneau " +
                "permet de basculer en jeu.");

            AutoriserTousObjets = Config.Bind("General", "AutoriserTousObjets", false,
                "Autorise le passage avec le minerai et les objets normalement interdits en portail. " +
                "false = règle du jeu conservée.");

            DistanceMaximale = Config.Bind("General", "DistanceMaximale", 0f,
                new ConfigDescription(
                    "Portée maximale d'un saut, en mètres. Les portails plus lointains restent affichés mais " +
                    "grisés et non cliquables. 0 = aucune limite.",
                    new AcceptableValueRange<float>(0f, 20000f)));

            AfficherBiome = Config.Bind("General", "AfficherBiome", true,
                "Affiche le biome de chaque destination à côté de sa distance.");

            DesactiverAppairageVanilla = Config.Bind("General", "DesactiverAppairageVanilla", false,
                "Empêche le serveur d'apparier automatiquement deux portails de même nom, ce qui supprime la " +
                "lueur « connecté » devenue trompeuse. À ne passer à true que si le mod est installé sur le " +
                "serveur ET chez tous les joueurs : sans le mod, un joueur ne pourrait plus voyager du tout.");

            LargeurPanneau = Config.Bind("Affichage", "LargeurPanneau", 460f,
                new ConfigDescription("Largeur du panneau, en pixels d'interface (avant Echelle).",
                    new AcceptableValueRange<float>(300f, 1600f)));

            HauteurPanneau = Config.Bind("Affichage", "HauteurPanneau", 560f,
                new ConfigDescription("Hauteur du panneau, en pixels d'interface (avant Echelle).",
                    new AcceptableValueRange<float>(240f, 1600f)));

            Echelle = Config.Bind("Affichage", "Echelle", 1f,
                new ConfigDescription(
                    "Agrandit ou réduit tout le panneau, texte compris. Il suit déjà l'échelle d'interface " +
                    "réglée dans les options du jeu ; ceci s'y ajoute. 1.5 = moitié plus grand.",
                    new AcceptableValueRange<float>(0.5f, 3f)));

            CouleurFond = Config.Bind("Affichage", "CouleurFond", "17120CF2",
                "Couleur du fond du panneau, en hexadécimal RRGGBB ou RRGGBBAA (AA = opacité, FF = opaque).");

            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll(typeof(Plugin).Assembly);

            Log.LogInfo($"{PluginName} {PluginVersion} chargé.");
        }

        private void OnDestroy()
        {
            DestinationPanel.Destroy();
            _harmony?.UnpatchSelf();
        }

        /// <summary>
        /// Le panneau se referme de lui-même si le joueur meurt, si le portail disparaît ou s'il s'en éloigne.
        /// Passe par l'Update du plugin plutôt que par un patch de Player.Update.
        /// </summary>
        private void Update()
        {
            if (DestinationPanel.IsVisible()) DestinationPanel.CheckStillValid();
        }

        /// <summary>Lit une couleur « RRGGBB » ou « RRGGBBAA » de la config, avec repli sur une valeur sûre.</summary>
        internal static Color ParseColor(string hex, Color fallback)
        {
            if (string.IsNullOrEmpty(hex)) return fallback;
            string value = hex.Trim().TrimStart('#');
            if (value.Length == 6) value += "FF";
            if (value.Length != 8) return fallback;

            try
            {
                return new Color(
                    Convert.ToInt32(value.Substring(0, 2), 16) / 255f,
                    Convert.ToInt32(value.Substring(2, 2), 16) / 255f,
                    Convert.ToInt32(value.Substring(4, 2), 16) / 255f,
                    Convert.ToInt32(value.Substring(6, 2), 16) / 255f);
            }
            catch (Exception)
            {
                return fallback;
            }
        }
    }

    // ================================================================== modèle

    /// <summary>Un portail tel que le panneau a besoin de le connaître : identité, nom, point de sortie.</summary>
    internal struct PortalInfo
    {
        public ZDOID Id;
        public string Tag;
        public Vector3 Position;
        public Quaternion Rotation;

        public PortalInfo(ZDOID id, string tag, Vector3 position, Quaternion rotation)
        {
            Id = id;
            Tag = tag ?? "";
            Position = position;
            Rotation = rotation;
        }
    }

    // ================================================================== réseau

    /// <summary>
    /// Récupération de la liste des portails. Seul le serveur tient le registre complet
    /// (ZDOMan.m_portalObjects, alimenté secteur par secteur et envoyé aux clients dans leur rayon de vue
    /// seulement), donc le client la lui demande par RPC routé.
    /// </summary>
    internal static class PortalNetwork
    {
        private const string RpcRequest = "portalmenu.Request";
        private const string RpcList = "portalmenu.List";

        /// <summary>Dernière liste reçue du serveur.</summary>
        internal static readonly List<PortalInfo> ServerList = new List<PortalInfo>();

        /// <summary>Le serveur a répondu au moins une fois : il a le mod, la liste est donc complète.</summary>
        internal static bool ServerAnswered;

        internal static void Register()
        {
            if (ZRoutedRpc.instance == null) return;

            try
            {
                ServerList.Clear();
                ServerAnswered = false;
                ZRoutedRpc.instance.Register(RpcRequest, new Action<long>(RPC_Request));
                ZRoutedRpc.instance.Register<ZPackage>(RpcList, RPC_List);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Enregistrement des RPC impossible : {e.Message}");
            }
        }

        /// <summary>Demande la liste complète au serveur. En solo ou en hébergé, l'appel revient sur soi-même.</summary>
        internal static void Request()
        {
            if (ZRoutedRpc.instance == null) return;
            ZRoutedRpc.instance.InvokeRoutedRPC(RpcRequest);
        }

        /// <summary>Côté serveur : sérialise tous les portails du monde et les renvoie au demandeur.</summary>
        private static void RPC_Request(long sender)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer() || ZDOMan.instance == null) return;

            List<ZDO> portals = ZDOMan.instance.GetPortalList();
            var valid = new List<ZDO>(portals.Count);
            foreach (ZDO zdo in portals)
            {
                if (zdo != null && zdo.IsValid()) valid.Add(zdo);
            }

            var pkg = new ZPackage();
            pkg.Write(valid.Count);
            foreach (ZDO zdo in valid)
            {
                pkg.Write(zdo.m_uid);
                pkg.Write(zdo.GetString(ZDOVars.s_tag) ?? "");
                pkg.Write(zdo.GetPosition());
                pkg.Write(zdo.GetRotation());
            }

            ZRoutedRpc.instance.InvokeRoutedRPC(sender, RpcList, pkg);
        }

        /// <summary>Côté client : réception de la liste, le panneau se rafraîchit s'il est ouvert.</summary>
        private static void RPC_List(long sender, ZPackage pkg)
        {
            if (pkg == null) return;

            try
            {
                int count = pkg.ReadInt();
                ServerList.Clear();
                for (int i = 0; i < count; i++)
                {
                    ZDOID id = pkg.ReadZDOID();
                    string tag = pkg.ReadString();
                    Vector3 pos = pkg.ReadVector3();
                    Quaternion rot = pkg.ReadQuaternion();
                    ServerList.Add(new PortalInfo(id, tag, pos, rot));
                }
                ServerAnswered = true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Liste de portails illisible : {e.Message}");
                return;
            }

            DestinationPanel.OnPortalsUpdated();
        }

        /// <summary>
        /// Repli : les portails que cette machine connaît déjà. Tous en solo ou en hébergé (on est le serveur),
        /// seulement ceux des secteurs chargés sur un client de serveur dédié sans le mod.
        /// </summary>
        internal static List<PortalInfo> LocalSnapshot()
        {
            var list = new List<PortalInfo>();
            if (ZDOMan.instance == null) return list;

            foreach (ZDO zdo in ZDOMan.instance.GetPortalList())
            {
                if (zdo == null || !zdo.IsValid()) continue;
                list.Add(new PortalInfo(zdo.m_uid, zdo.GetString(ZDOVars.s_tag) ?? "",
                    zdo.GetPosition(), zdo.GetRotation()));
            }
            return list;
        }
    }

    // ================================================================== interface

    /// <summary>
    /// Le panneau de choix de la destination : un fond, un titre, une liste défilante de boutons, un pied de
    /// page. Construit à la main sous le canvas du jeu ; la police est empruntée à un texte existant plutôt
    /// qu'embarquée, comme dans GearSlots.
    /// </summary>
    internal static class DestinationPanel
    {
        private const string RootName = "PortalMenu_Panel";
        private const float TitreHauteur = 34f;
        private const float AvertissementHauteur = 20f;
        private const float PiedHauteur = 34f;
        private const float LigneHauteur = 34f;
        private const float Marge = 12f;

        /// <summary>Au-dessus du HUD (texte de survol, messages centraux), qui sinon passe devant le panneau.</summary>
        private const int OrdreAffichage = 5000;

        private static GameObject _root;
        private static RectTransform _content;
        private static ScrollRect _scroll;
        private static TMP_Text _title;
        private static TMP_Text _hint;
        private static TMP_Text _sortLabel;
        private static TMP_FontAsset _font;

        private static TeleportWorld _portal;
        private static ZDOID _portalId;
        private static readonly List<GameObject> _rows = new List<GameObject>();

        internal static bool IsVisible()
        {
            return _root != null && _root.activeSelf;
        }

        // -------------------------------------------------------------- cycle de vie

        internal static void Show(TeleportWorld portal)
        {
            if (portal == null || portal.m_nview == null || portal.m_nview.GetZDO() == null) return;

            Transform canvas = FindCanvas();
            if (canvas == null)
            {
                Plugin.Log.LogWarning("Canvas du jeu introuvable, panneau non affiché.");
                return;
            }

            _portal = portal;
            _portalId = portal.m_nview.GetZDO().m_uid;

            if (!Build(canvas)) return;

            _root.SetActive(true);
            _root.transform.SetAsLastSibling();

            // Réglé à chaque ouverture : Unity peut perdre overrideSorting posé sur un canvas encore inactif.
            Canvas propre = _root.GetComponent<Canvas>();
            if (propre != null)
            {
                propre.overrideSorting = true;
                propre.sortingOrder = OrdreAffichage;
            }

            PortalNetwork.Request();
            Fill();
        }

        internal static void Hide()
        {
            _portal = null;
            _portalId = ZDOID.None;
            if (_root != null) _root.SetActive(false);
        }

        internal static void Destroy()
        {
            ClearRows();
            if (_root != null) UnityEngine.Object.Destroy(_root);
            _root = null;
            _content = null;
            _scroll = null;
            _title = null;
            _hint = null;
            _sortLabel = null;
            _portal = null;
        }

        /// <summary>Referme le panneau si le contexte n'a plus de sens (mort, portail détruit, éloignement).</summary>
        internal static void CheckStillValid()
        {
            Player player = Player.m_localPlayer;
            if (player == null || player.IsDead() || player.IsTeleporting() || _portal == null)
            {
                Hide();
                return;
            }

            float range = Mathf.Max(_portal.m_activationRange * 2f, 8f);
            if (Vector3.Distance(player.transform.position, _portal.transform.position) > range) Hide();
        }

        /// <summary>La liste du serveur vient d'arriver : on redessine si le panneau est ouvert.</summary>
        internal static void OnPortalsUpdated()
        {
            if (IsVisible()) Fill();
        }

        // -------------------------------------------------------------- contenu

        private static void Fill()
        {
            ClearRows();
            if (_content == null) return;

            Player player = Player.m_localPlayer;
            Vector3 from = player != null ? player.transform.position : Vector3.zero;

            List<PortalInfo> source = PortalNetwork.ServerAnswered
                ? PortalNetwork.ServerList
                : PortalNetwork.LocalSnapshot();

            var destinations = new List<PortalInfo>();
            foreach (PortalInfo info in source)
            {
                if (info.Id == _portalId) continue;
                if (!Plugin.PortailsSansNom.Value && string.IsNullOrEmpty(info.Tag.Trim())) continue;
                destinations.Add(info);
            }

            if (Plugin.TrierParDistance.Value)
            {
                destinations.Sort((a, b) => Vector3.Distance(from, a.Position)
                    .CompareTo(Vector3.Distance(from, b.Position)));
            }
            else
            {
                destinations.Sort((a, b) => string.Compare(NomAffiche(a), NomAffiche(b),
                    StringComparison.CurrentCultureIgnoreCase));
            }

            _title.text = destinations.Count > 0 ? $"Destinations ({destinations.Count})" : "Destinations";
            _hint.text = PortalNetwork.ServerAnswered
                ? ""
                : "Serveur sans le mod : portails proches uniquement.";
            _sortLabel.text = Plugin.TrierParDistance.Value ? "Tri : distance" : "Tri : nom";

            if (destinations.Count == 0)
            {
                AddMessageRow(Plugin.PortailsSansNom.Value
                    ? "Aucun autre portail dans le monde."
                    : "Aucun autre portail nommé. Nomme tes portails, ou active PortailsSansNom.");
            }
            else
            {
                float limite = Plugin.DistanceMaximale.Value;
                foreach (PortalInfo info in destinations)
                {
                    float distance = Vector3.Distance(from, info.Position);
                    AddPortalRow(info, distance, limite <= 0f || distance <= limite);
                }
            }

            Canvas.ForceUpdateCanvases();
            if (_scroll != null) _scroll.verticalNormalizedPosition = 1f;
        }

        private static void ClearRows()
        {
            foreach (GameObject row in _rows)
            {
                if (row != null) UnityEngine.Object.Destroy(row);
            }
            _rows.Clear();
        }

        private static string NomAffiche(PortalInfo info)
        {
            string tag = info.Tag != null ? info.Tag.Trim() : "";
            return string.IsNullOrEmpty(tag) ? "(sans nom)" : tag.RemoveRichTextTags();
        }

        private static string Detail(PortalInfo info, float distance)
        {
            string distanceTexte = distance >= 1000f
                ? (distance / 1000f).ToString("0.0") + " km"
                : distance.ToString("0") + " m";

            if (!Plugin.AfficherBiome.Value || WorldGenerator.instance == null) return distanceTexte;

            Heightmap.Biome biome = WorldGenerator.instance.GetBiome(info.Position);
            string nomBiome = Localization.instance.Localize("$biome_" + biome.ToString().ToLower());
            return nomBiome + "   " + distanceTexte;
        }

        private static void AddPortalRow(PortalInfo info, float distance, bool joignable)
        {
            RectTransform rect = NewRect("row", _content);
            var fond = rect.gameObject.AddComponent<Image>();
            fond.color = new Color(1f, 1f, 1f, 0.05f);

            var layout = rect.gameObject.AddComponent<LayoutElement>();
            layout.preferredHeight = LigneHauteur;
            layout.minHeight = LigneHauteur;

            TMP_Text nom = NewText(rect, "nom", NomAffiche(info), 17f, TextAlignmentOptions.Left);
            Stretch(nom.rectTransform, 10f, 200f, 0f, 0f);
            nom.color = joignable ? new Color(0.95f, 0.91f, 0.82f) : new Color(0.55f, 0.52f, 0.48f);

            TMP_Text detail = NewText(rect, "detail", Detail(info, distance), 14f, TextAlignmentOptions.Right);
            Stretch(detail.rectTransform, 10f, 10f, 0f, 0f);
            detail.color = joignable ? new Color(0.72f, 0.68f, 0.58f) : new Color(0.45f, 0.42f, 0.4f);

            var button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = fond;
            button.interactable = joignable;

            ColorBlock colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1f, 0.92f, 0.7f, 1f);
            colors.pressedColor = new Color(1f, 0.85f, 0.5f, 1f);
            colors.disabledColor = new Color(1f, 1f, 1f, 0.35f);
            colors.fadeDuration = 0.08f;
            button.colors = colors;

            PortalInfo cible = info;
            button.onClick.AddListener(delegate { TravelTo(cible); });

            _rows.Add(rect.gameObject);
        }

        private static void AddMessageRow(string message)
        {
            RectTransform rect = NewRect("message", _content);
            var layout = rect.gameObject.AddComponent<LayoutElement>();
            layout.preferredHeight = LigneHauteur * 2f;
            layout.minHeight = LigneHauteur * 2f;

            TMP_Text texte = NewText(rect, "texte", message, 15f, TextAlignmentOptions.Center);
            Stretch(texte.rectTransform, 10f, 10f, 0f, 0f);
            texte.color = new Color(0.7f, 0.66f, 0.58f);
            texte.textWrappingMode = TextWrappingModes.Normal;
            texte.overflowMode = TextOverflowModes.Overflow;

            _rows.Add(rect.gameObject);
        }

        // -------------------------------------------------------------- voyage

        /// <summary>
        /// Reprend les garde-fous de TeleportWorld.Teleport à l'identique, puis téléporte vers les coordonnées
        /// choisies au lieu de celles du portail apparié.
        /// </summary>
        private static void TravelTo(PortalInfo target)
        {
            Player player = Player.m_localPlayer;
            TeleportWorld portal = _portal;
            if (player == null || portal == null) return;

            if (ZoneSystem.instance != null && ZoneSystem.instance.GetGlobalKey(GlobalKeys.NoPortals))
            {
                player.Message(MessageHud.MessageType.Center, "$msg_blocked");
                Hide();
                return;
            }

            if (ZoneSystem.instance != null && ZoneSystem.instance.GetGlobalKey(GlobalKeys.NoBossPortals)
                && ((RandEventSystem.instance != null && RandEventSystem.instance.GetBossEvent() != null)
                    || (ZoneSystem.instance.GetGlobalKey(GlobalKeys.activeBosses, out float bosses) && bosses > 0f)))
            {
                player.Message(MessageHud.MessageType.Center, "$msg_blockedbyboss");
                Hide();
                return;
            }

            if (!player.IsTeleportable(Plugin.AutoriserTousObjets.Value || portal.m_allowAllItems))
            {
                player.Message(MessageHud.MessageType.Center, "$msg_noteleport");
                Hide();
                return;
            }

            Vector3 sortie = target.Position
                             + target.Rotation * Vector3.forward * portal.m_exitDistance
                             + Vector3.up;

            Hide();
            player.TeleportTo(sortie, target.Rotation, distantTeleport: true);
            if (Game.instance != null) Game.instance.IncrementPlayerStat(PlayerStatType.PortalsUsed);
        }

        /// <summary>Renommer passe par le TextInput du jeu, TeleportWorld étant déjà un TextReceiver.</summary>
        private static void Rename()
        {
            TeleportWorld portal = _portal;
            if (portal == null || Player.m_localPlayer == null) return;

            if (!PrivateArea.CheckAccess(portal.transform.position))
            {
                Player.m_localPlayer.Message(MessageHud.MessageType.Center, "$piece_noaccess");
                return;
            }

            Hide();
            if (TextInput.instance != null) TextInput.instance.RequestText(portal, "$piece_portal_tag", 10);
        }

        private static void ToggleSort()
        {
            Plugin.TrierParDistance.Value = !Plugin.TrierParDistance.Value;
            Fill();
        }

        // -------------------------------------------------------------- construction

        private static bool Build(Transform canvas)
        {
            if (_root != null && _root.transform.parent == canvas)
            {
                ApplySize();
                return true;
            }

            Destroy();

            _font = FindFont(canvas);
            if (_font == null)
            {
                Plugin.Log.LogWarning("Police du jeu introuvable, panneau non affiché.");
                return false;
            }

            RectTransform root = NewRect(RootName, canvas);
            _root = root.gameObject;
            root.anchorMin = new Vector2(0.5f, 0.5f);
            root.anchorMax = new Vector2(0.5f, 0.5f);
            root.pivot = new Vector2(0.5f, 0.5f);
            root.anchoredPosition = Vector2.zero;

            // Canvas propre : dessiné au-dessus du HUD et cliquable quel que soit l'ordre des canvas du jeu.
            // Le CanvasGroup ignore ceux des parents, dont l'opacité rendait le panneau translucide.
            _root.AddComponent<Canvas>();
            _root.AddComponent<GraphicRaycaster>();
            var groupe = _root.AddComponent<CanvasGroup>();
            groupe.ignoreParentGroups = true;
            groupe.alpha = 1f;
            groupe.interactable = true;
            groupe.blocksRaycasts = true;

            var fond = _root.AddComponent<Image>();
            fond.color = Plugin.ParseColor(Plugin.CouleurFond.Value, new Color(0.09f, 0.07f, 0.05f, 0.95f));

            // Titre
            _title = NewText(root, "titre", "Destinations", 22f, TextAlignmentOptions.Left);
            RectTransform titre = _title.rectTransform;
            titre.anchorMin = new Vector2(0f, 1f);
            titre.anchorMax = new Vector2(1f, 1f);
            titre.pivot = new Vector2(0.5f, 1f);
            titre.sizeDelta = new Vector2(-2f * Marge, TitreHauteur);
            titre.anchoredPosition = new Vector2(0f, -Marge * 0.5f);
            _title.color = new Color(1f, 0.94f, 0.8f);

            // Avertissement affiché quand le serveur n'a pas le mod
            _hint = NewText(root, "avertissement", "", 13f, TextAlignmentOptions.Left);
            RectTransform hint = _hint.rectTransform;
            hint.anchorMin = new Vector2(0f, 1f);
            hint.anchorMax = new Vector2(1f, 1f);
            hint.pivot = new Vector2(0.5f, 1f);
            hint.sizeDelta = new Vector2(-2f * Marge, AvertissementHauteur);
            hint.anchoredPosition = new Vector2(0f, -Marge * 0.5f - TitreHauteur);
            _hint.color = new Color(0.9f, 0.72f, 0.4f);

            // Zone défilante
            RectTransform viewport = NewRect("viewport", root);
            Stretch(viewport, Marge, Marge, Marge * 0.5f + TitreHauteur + AvertissementHauteur + 4f,
                Marge + PiedHauteur + 8f);
            var masque = viewport.gameObject.AddComponent<Image>();
            masque.color = new Color(0f, 0f, 0f, 0.25f);
            viewport.gameObject.AddComponent<RectMask2D>();

            _content = NewRect("content", viewport);
            _content.anchorMin = new Vector2(0f, 1f);
            _content.anchorMax = new Vector2(1f, 1f);
            _content.pivot = new Vector2(0.5f, 1f);
            _content.anchoredPosition = Vector2.zero;
            _content.sizeDelta = Vector2.zero;

            var pile = _content.gameObject.AddComponent<VerticalLayoutGroup>();
            pile.spacing = 2f;
            pile.padding = new RectOffset(4, 4, 4, 4);
            pile.childForceExpandWidth = true;
            pile.childForceExpandHeight = false;
            pile.childControlWidth = true;
            pile.childControlHeight = true;

            var ajusteur = _content.gameObject.AddComponent<ContentSizeFitter>();
            ajusteur.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            _scroll = viewport.gameObject.AddComponent<ScrollRect>();
            _scroll.viewport = viewport;
            _scroll.content = _content;
            _scroll.horizontal = false;
            _scroll.vertical = true;
            _scroll.movementType = ScrollRect.MovementType.Clamped;
            _scroll.scrollSensitivity = 28f;

            // Pied de page : trois boutons de largeur égale, pour suivre la largeur configurée du panneau
            RectTransform pied = NewRect("pied", root);
            pied.anchorMin = new Vector2(0f, 0f);
            pied.anchorMax = new Vector2(1f, 0f);
            pied.pivot = new Vector2(0.5f, 0f);
            pied.sizeDelta = new Vector2(-2f * Marge, PiedHauteur);
            pied.anchoredPosition = new Vector2(0f, Marge * 0.5f);

            var rangee = pied.gameObject.AddComponent<HorizontalLayoutGroup>();
            rangee.spacing = 8f;
            rangee.childForceExpandWidth = true;
            rangee.childForceExpandHeight = true;
            rangee.childControlWidth = true;
            rangee.childControlHeight = true;

            _sortLabel = NewFooterButton(pied, "tri", "Tri : nom", ToggleSort);
            NewFooterButton(pied, "renommer", "Renommer", Rename);
            NewFooterButton(pied, "fermer", "Fermer", Hide);

            ApplySize();
            return true;
        }

        private static void ApplySize()
        {
            if (_root == null) return;
            ((RectTransform)_root.transform).sizeDelta =
                new Vector2(Plugin.LargeurPanneau.Value, Plugin.HauteurPanneau.Value);
            _root.transform.localScale = Vector3.one * Plugin.Echelle.Value;
        }

        private static TMP_Text NewFooterButton(RectTransform parent, string name, string label,
            UnityEngine.Events.UnityAction action)
        {
            RectTransform rect = NewRect(name, parent);
            rect.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;

            var fond = rect.gameObject.AddComponent<Image>();
            fond.color = new Color(1f, 1f, 1f, 0.09f);

            TMP_Text texte = NewText(rect, "libelle", label, 16f, TextAlignmentOptions.Center);
            Stretch(texte.rectTransform, 0f, 0f, 0f, 0f);
            texte.color = new Color(0.95f, 0.91f, 0.82f);

            var button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = fond;

            ColorBlock colors = button.colors;
            colors.highlightedColor = new Color(1f, 0.92f, 0.7f, 1f);
            colors.pressedColor = new Color(1f, 0.85f, 0.5f, 1f);
            colors.fadeDuration = 0.08f;
            button.colors = colors;

            button.onClick.AddListener(action);
            return texte;
        }

        private static RectTransform NewRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, worldPositionStays: false);
            rect.localScale = Vector3.one;
            return rect;
        }

        private static TMP_Text NewText(RectTransform parent, string name, string contenu, float taille,
            TextAlignmentOptions alignement)
        {
            RectTransform rect = NewRect(name, parent);
            var texte = rect.gameObject.AddComponent<TextMeshProUGUI>();
            texte.font = _font;
            texte.fontSize = taille;
            texte.text = contenu;
            texte.alignment = alignement;
            texte.raycastTarget = false;
            texte.textWrappingMode = TextWrappingModes.NoWrap;
            texte.overflowMode = TextOverflowModes.Ellipsis;
            Stretch(rect, 0f, 0f, 0f, 0f);
            return texte;
        }

        private static void Stretch(RectTransform rect, float gauche, float droite, float haut, float bas)
        {
            rect.anchorMin = new Vector2(0f, 0f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.offsetMin = new Vector2(gauche, bas);
            rect.offsetMax = new Vector2(-droite, -haut);
        }

        /// <summary>Canvas du jeu, atteint depuis une GUI toujours présente en partie.</summary>
        private static Transform FindCanvas()
        {
            Component ancre = null;
            if (StoreGui.instance != null) ancre = StoreGui.instance;
            else if (InventoryGui.instance != null) ancre = InventoryGui.instance;
            else if (Hud.instance != null) ancre = Hud.instance;
            if (ancre == null) return null;

            Canvas canvas = ancre.GetComponentInParent<Canvas>();
            return canvas != null ? canvas.transform : null;
        }

        /// <summary>
        /// Police empruntée à un texte du jeu, pour ne pas embarquer d'asset. On vise la police serif du texte de
        /// survol : prendre le premier texte venu du canvas tombait sur une police pixel illisible.
        /// </summary>
        private static TMP_FontAsset FindFont(Transform canvas)
        {
            if (Hud.instance != null && Hud.instance.m_hoverName != null && Hud.instance.m_hoverName.font != null)
                return Hud.instance.m_hoverName.font;

            if (InventoryGui.instance != null && InventoryGui.instance.m_recipeName != null
                && InventoryGui.instance.m_recipeName.font != null)
                return InventoryGui.instance.m_recipeName.font;

            // Repli : la police la plus utilisée du canvas, qui est celle du jeu et pas une police de debug.
            var usages = new Dictionary<TMP_FontAsset, int>();
            TMP_FontAsset meilleure = null;
            foreach (TMP_Text texte in canvas.GetComponentsInChildren<TMP_Text>(includeInactive: true))
            {
                if (texte == null || texte.font == null) continue;
                usages.TryGetValue(texte.font, out int n);
                usages[texte.font] = ++n;
                if (meilleure == null || n > usages[meilleure]) meilleure = texte.font;
            }
            return meilleure;
        }
    }

    // ================================================================== patchs

    /// <summary>Le jeu enregistre ses RPC routés dans Game.Start : on y ajoute les nôtres.</summary>
    [HarmonyPatch(typeof(Game), nameof(Game.Start))]
    internal static class Game_Start_Patch
    {
        private static void Postfix()
        {
            PortalNetwork.Register();
        }
    }

    /// <summary>Interagir avec un portail ouvre la liste des destinations au lieu du champ de nom.</summary>
    [HarmonyPatch(typeof(TeleportWorld), nameof(TeleportWorld.Interact))]
    internal static class TeleportWorld_Interact_Patch
    {
        private static bool Prefix(TeleportWorld __instance, bool hold, ref bool __result)
        {
            if (!Plugin.Enabled.Value) return true;

            if (hold)
            {
                __result = false;
                return false;
            }

            DestinationPanel.Show(__instance);
            __result = true;
            return false;
        }
    }

    /// <summary>Le survol annonce le choix de destination, plus l'appairage par nom.</summary>
    [HarmonyPatch(typeof(TeleportWorld), nameof(TeleportWorld.GetHoverText))]
    internal static class TeleportWorld_GetHoverText_Patch
    {
        private static bool Prefix(TeleportWorld __instance, ref string __result)
        {
            if (!Plugin.Enabled.Value) return true;

            ZDO zdo = __instance.m_nview != null ? __instance.m_nview.GetZDO() : null;
            if (zdo == null) return true;

            string tag = zdo.GetString(ZDOVars.s_tag) ?? "";
            string nom = string.IsNullOrEmpty(tag.Trim()) ? "" : tag.RemoveRichTextTags();

            __result = Localization.instance.Localize(
                "$piece_portal $piece_portal_tag:\"" + nom + "\"\n" +
                "[<color=yellow><b>$KEY_Use</b></color>] Choisir une destination");
            return false;
        }
    }

    /// <summary>
    /// Panneau ouvert : ni déplacement, ni saut, ni attaque, ni rotation de la caméra. C'est PlayerController
    /// qui lit le clavier et la souris (FixedUpdate pour les déplacements, LateUpdate pour le regard) ; le
    /// TakeInput de Player, patché plus bas, ne couvre que l'interaction et la barre d'action.
    /// </summary>
    [HarmonyPatch(typeof(PlayerController), "TakeInput")]
    internal static class PlayerController_TakeInput_Patch
    {
        private static void Postfix(ref bool __result)
        {
            if (DestinationPanel.IsVisible()) __result = false;
        }
    }

    /// <summary>Panneau ouvert : le joueur n'interagit plus et n'utilise plus sa barre d'action.</summary>
    [HarmonyPatch(typeof(Player), "TakeInput")]
    internal static class Player_TakeInput_Patch
    {
        private static void Postfix(ref bool __result)
        {
            if (DestinationPanel.IsVisible()) __result = false;
        }
    }

    /// <summary>Panneau ouvert : le curseur est libéré, la caméra ne suit plus la souris.</summary>
    [HarmonyPatch(typeof(GameCamera), nameof(GameCamera.UpdateMouseCapture))]
    internal static class GameCamera_UpdateMouseCapture_Patch
    {
        private static bool Prefix()
        {
            if (!DestinationPanel.IsVisible()) return true;

            ZCursor.LockState = CursorLockMode.None;
            ZCursor.Show();
            return false;
        }
    }

    /// <summary>Échap referme le panneau au lieu d'ouvrir le menu du jeu.</summary>
    [HarmonyPatch(typeof(Menu), "Update")]
    internal static class Menu_Update_Patch
    {
        private static bool Prefix()
        {
            if (!DestinationPanel.IsVisible() || Menu.IsVisible()) return true;

            if (ZInput.GetKeyDown(KeyCode.Escape) || ZInput.GetButtonDown("JoyButtonB"))
            {
                ZInput.ResetButtonStatus("JoyButtonB");
                DestinationPanel.Hide();
            }
            return false;
        }
    }

    /// <summary>
    /// Option : empêche le serveur d'apparier deux portails de même nom, la lueur « connecté » n'ayant plus de
    /// sens quand la destination se choisit à la main.
    /// </summary>
    [HarmonyPatch(typeof(Game), nameof(Game.ConnectPortals))]
    internal static class Game_ConnectPortals_Patch
    {
        private static bool Prefix()
        {
            return !Plugin.Enabled.Value || !Plugin.DesactiverAppairageVanilla.Value;
        }
    }
}
