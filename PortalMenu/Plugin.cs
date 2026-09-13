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
    /// Replaces the one-to-one pairing of portals with a free choice of destination: interacting with a
    /// portal opens the list of every portal in the world, clicking a row teleports.
    ///
    /// How it works: vanilla travel (TeleportWorld.Teleport) only reads the position of the paired ZDO, then
    /// calls Player.TeleportTo, which is purely local. We keep that last call and pass it the chosen
    /// coordinates: no ZDOExtraData.ConnectionType.Portal connection is touched, the save stays pure vanilla
    /// and uninstalling the mod leaves portals intact.
    ///
    /// Multiplayer: the full portal registry (ZDOMan.GetPortalList) only exists on the server, a client only
    /// receives the portals of its nearby sectors. The mod therefore asks the server for the list through a
    /// routed RPC. Without the mod on the server, the panel falls back to the locally known portals (all of them
    /// in single player or when hosting, only nearby ones on a dedicated server) and says so.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "valheim.portalmenu";
        public const string PluginName = "PortalMenu";
        public const string PluginVersion = "1.0.1";

        internal static ManualLogSource Log;

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> UnnamedPortals;
        internal static ConfigEntry<bool> SortByDistance;
        internal static ConfigEntry<bool> AllowAllItems;
        internal static ConfigEntry<float> MaxDistance;
        internal static ConfigEntry<bool> ShowBiome;
        internal static ConfigEntry<bool> DisableVanillaPairing;
        internal static ConfigEntry<float> PanelWidth;
        internal static ConfigEntry<float> PanelHeight;
        internal static ConfigEntry<float> Scale;
        internal static ConfigEntry<string> BackgroundColor;

        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;

            // Generated file: BepInEx/config/valheim.portalmenu.cfg
            Enabled = Config.Bind("General", "Enabled", true,
                "Enables or disables the mod. When disabled, portals get their vanilla behaviour back " +
                "(pairing by identical name).");

            UnnamedPortals = Config.Bind("General", "UnnamedPortals", false,
                "Also lists portals that were never given a name. Handy to find a forgotten " +
                "portal, cluttered if many portals are placed without being named.");
            MigrateKey(UnnamedPortals, "General", "PortailsSansNom");

            SortByDistance = Config.Bind("General", "SortByDistance", false,
                "Sorts the list by increasing distance instead of by name. The panel's sort button " +
                "toggles it in game.");
            MigrateKey(SortByDistance, "General", "TrierParDistance");

            AllowAllItems = Config.Bind("General", "AllowAllItems", false,
                "Allows travelling with ore and the items normally forbidden through portals. " +
                "false = game rule kept.");
            MigrateKey(AllowAllItems, "General", "AutoriserTousObjets");

            MaxDistance = Config.Bind("General", "MaxDistance", 0f,
                new ConfigDescription(
                    "Maximum range of a jump, in metres. Farther portals stay listed but " +
                    "greyed out and not clickable. 0 = no limit.",
                    new AcceptableValueRange<float>(0f, 20000f)));
            MigrateKey(MaxDistance, "General", "DistanceMaximale");

            ShowBiome = Config.Bind("General", "ShowBiome", true,
                "Shows the biome of each destination next to its distance.");
            MigrateKey(ShowBiome, "General", "AfficherBiome");

            DisableVanillaPairing = Config.Bind("General", "DisableVanillaPairing", false,
                "Prevents the server from automatically pairing two portals with the same name, which removes the " +
                "now misleading \"connected\" glow. Only set to true if the mod is installed on the " +
                "server AND for every player: without the mod, a player could no longer travel at all.");
            MigrateKey(DisableVanillaPairing, "General", "DesactiverAppairageVanilla");

            PanelWidth = Config.Bind("Display", "PanelWidth", 460f,
                new ConfigDescription("Panel width, in UI pixels (before Scale).",
                    new AcceptableValueRange<float>(300f, 1600f)));
            MigrateKey(PanelWidth, "Affichage", "LargeurPanneau");

            PanelHeight = Config.Bind("Display", "PanelHeight", 560f,
                new ConfigDescription("Panel height, in UI pixels (before Scale).",
                    new AcceptableValueRange<float>(240f, 1600f)));
            MigrateKey(PanelHeight, "Affichage", "HauteurPanneau");

            Scale = Config.Bind("Display", "Scale", 1f,
                new ConfigDescription(
                    "Enlarges or shrinks the whole panel, text included. It already follows the UI scale " +
                    "set in the game options; this is applied on top. 1.5 = half again as large.",
                    new AcceptableValueRange<float>(0.5f, 3f)));
            MigrateKey(Scale, "Affichage", "Echelle");

            BackgroundColor = Config.Bind("Display", "BackgroundColor", "17120CF2",
                "Panel background colour, in hexadecimal RRGGBB or RRGGBBAA (AA = opacity, FF = opaque).");
            MigrateKey(BackgroundColor, "Affichage", "CouleurFond");

            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll(typeof(Plugin).Assembly);

            Log.LogInfo($"{PluginName} {PluginVersion} loaded.");
        }

        /// <summary>
        /// Keeps the value of a config key renamed when the mod was translated to English: if the old key is still in the
        /// .cfg (BepInEx keeps unbound keys as orphans), its value moves to the new entry and the old line is dropped.
        /// </summary>
        private void MigrateKey(ConfigEntryBase entry, string oldSection, string oldKey)
        {
            var old = new ConfigDefinition(oldSection, oldKey);
            if (!Config.OrphanedEntries.TryGetValue(old, out string value)) return;

            entry.SetSerializedValue(value);
            Config.OrphanedEntries.Remove(old);
            Config.Save();
            Log.LogInfo($"Config key [{oldSection}] {oldKey} migrated to [{entry.Definition.Section}] {entry.Definition.Key}.");
        }

        private void OnDestroy()
        {
            DestinationPanel.Destroy();
            _harmony?.UnpatchSelf();
        }

        /// <summary>
        /// The panel closes by itself if the player dies, if the portal disappears or if the player walks away from it.
        /// Goes through the plugin's Update rather than a patch of Player.Update.
        /// </summary>
        private void Update()
        {
            if (DestinationPanel.IsVisible()) DestinationPanel.CheckStillValid();
        }

        /// <summary>Reads a "RRGGBB" or "RRGGBBAA" colour from the config, falling back to a safe value.</summary>
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

    // ================================================================== model

    /// <summary>A portal as the panel needs to know it: identity, name, exit point.</summary>
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

    // ================================================================== network

    /// <summary>
    /// Fetching the portal list. Only the server holds the full registry
    /// (ZDOMan.m_portalObjects, filled sector by sector and sent to clients within their view range
    /// only), so the client asks it for the list through a routed RPC.
    /// </summary>
    internal static class PortalNetwork
    {
        private const string RpcRequest = "portalmenu.Request";
        private const string RpcList = "portalmenu.List";

        /// <summary>Last list received from the server.</summary>
        internal static readonly List<PortalInfo> ServerList = new List<PortalInfo>();

        /// <summary>The server answered at least once: it has the mod, so the list is complete.</summary>
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
                Plugin.Log.LogWarning($"Could not register the RPCs: {e.Message}");
            }
        }

        /// <summary>Asks the server for the full list. In single player or when hosting, the call comes back to ourselves.</summary>
        internal static void Request()
        {
            if (ZRoutedRpc.instance == null) return;
            ZRoutedRpc.instance.InvokeRoutedRPC(RpcRequest);
        }

        /// <summary>Server side: serializes every portal in the world and sends them back to the requester.</summary>
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

        /// <summary>Client side: the list is received, the panel refreshes if it is open.</summary>
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
                Plugin.Log.LogWarning($"Unreadable portal list: {e.Message}");
                return;
            }

            DestinationPanel.OnPortalsUpdated();
        }

        /// <summary>
        /// Fallback: the portals this machine already knows. All of them in single player or when hosting (we are the
        /// server), only those of the loaded sectors on a client of a dedicated server without the mod.
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

    // ================================================================== UI

    /// <summary>
    /// The destination selection panel: a background, a title, a scrolling list of buttons, a footer.
    /// Built by hand under the game's canvas; the font is borrowed from an existing text rather than
    /// embedded, as in GearSlots.
    /// </summary>
    internal static class DestinationPanel
    {
        private const string RootName = "PortalMenu_Panel";
        private const float TitleHeight = 34f;
        private const float WarningHeight = 20f;
        private const float FooterHeight = 34f;
        private const float RowHeight = 34f;
        private const float Margin = 12f;

        /// <summary>Above the HUD (hover text, center messages), which otherwise draws in front of the panel.</summary>
        private const int SortingOrder = 5000;

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

        // -------------------------------------------------------------- lifecycle

        internal static void Show(TeleportWorld portal)
        {
            if (portal == null || portal.m_nview == null || portal.m_nview.GetZDO() == null) return;

            Transform canvas = FindCanvas();
            if (canvas == null)
            {
                Plugin.Log.LogWarning("Game canvas not found, panel not shown.");
                return;
            }

            _portal = portal;
            _portalId = portal.m_nview.GetZDO().m_uid;

            if (!Build(canvas)) return;

            _root.SetActive(true);
            _root.transform.SetAsLastSibling();

            // Set on every opening: Unity can lose overrideSorting when it is set on a canvas that is still inactive.
            Canvas ownCanvas = _root.GetComponent<Canvas>();
            if (ownCanvas != null)
            {
                ownCanvas.overrideSorting = true;
                ownCanvas.sortingOrder = SortingOrder;
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

        /// <summary>Closes the panel if the context no longer makes sense (death, portal destroyed, walked away).</summary>
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

        /// <summary>The server list has just arrived: redraw if the panel is open.</summary>
        internal static void OnPortalsUpdated()
        {
            if (IsVisible()) Fill();
        }

        // -------------------------------------------------------------- content

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
                if (!Plugin.UnnamedPortals.Value && string.IsNullOrEmpty(info.Tag.Trim())) continue;
                destinations.Add(info);
            }

            if (Plugin.SortByDistance.Value)
            {
                destinations.Sort((a, b) => Vector3.Distance(from, a.Position)
                    .CompareTo(Vector3.Distance(from, b.Position)));
            }
            else
            {
                destinations.Sort((a, b) => string.Compare(DisplayName(a), DisplayName(b),
                    StringComparison.CurrentCultureIgnoreCase));
            }

            _title.text = destinations.Count > 0 ? $"Destinations ({destinations.Count})" : "Destinations";
            _hint.text = PortalNetwork.ServerAnswered
                ? ""
                : "Server without the mod: nearby portals only.";
            _sortLabel.text = Plugin.SortByDistance.Value ? "Sort: distance" : "Sort: name";

            if (destinations.Count == 0)
            {
                AddMessageRow(Plugin.UnnamedPortals.Value
                    ? "No other portal in the world."
                    : "No other named portal. Name your portals, or enable UnnamedPortals.");
            }
            else
            {
                float limit = Plugin.MaxDistance.Value;
                foreach (PortalInfo info in destinations)
                {
                    float distance = Vector3.Distance(from, info.Position);
                    AddPortalRow(info, distance, limit <= 0f || distance <= limit);
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

        private static string DisplayName(PortalInfo info)
        {
            string tag = info.Tag != null ? info.Tag.Trim() : "";
            return string.IsNullOrEmpty(tag) ? "(unnamed)" : tag.RemoveRichTextTags();
        }

        private static string Detail(PortalInfo info, float distance)
        {
            string distanceText = distance >= 1000f
                ? (distance / 1000f).ToString("0.0") + " km"
                : distance.ToString("0") + " m";

            if (!Plugin.ShowBiome.Value || WorldGenerator.instance == null) return distanceText;

            Heightmap.Biome biome = WorldGenerator.instance.GetBiome(info.Position);
            string biomeName = Localization.instance.Localize("$biome_" + biome.ToString().ToLower());
            return biomeName + "   " + distanceText;
        }

        private static void AddPortalRow(PortalInfo info, float distance, bool reachable)
        {
            RectTransform rect = NewRect("row", _content);
            var background = rect.gameObject.AddComponent<Image>();
            background.color = new Color(1f, 1f, 1f, 0.05f);

            var layout = rect.gameObject.AddComponent<LayoutElement>();
            layout.preferredHeight = RowHeight;
            layout.minHeight = RowHeight;

            TMP_Text name = NewText(rect, "name", DisplayName(info), 17f, TextAlignmentOptions.Left);
            Stretch(name.rectTransform, 10f, 200f, 0f, 0f);
            name.color = reachable ? new Color(0.95f, 0.91f, 0.82f) : new Color(0.55f, 0.52f, 0.48f);

            TMP_Text detail = NewText(rect, "detail", Detail(info, distance), 14f, TextAlignmentOptions.Right);
            Stretch(detail.rectTransform, 10f, 10f, 0f, 0f);
            detail.color = reachable ? new Color(0.72f, 0.68f, 0.58f) : new Color(0.45f, 0.42f, 0.4f);

            var button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = background;
            button.interactable = reachable;

            ColorBlock colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1f, 0.92f, 0.7f, 1f);
            colors.pressedColor = new Color(1f, 0.85f, 0.5f, 1f);
            colors.disabledColor = new Color(1f, 1f, 1f, 0.35f);
            colors.fadeDuration = 0.08f;
            button.colors = colors;

            PortalInfo target = info;
            button.onClick.AddListener(delegate { TravelTo(target); });

            _rows.Add(rect.gameObject);
        }

        private static void AddMessageRow(string message)
        {
            RectTransform rect = NewRect("message", _content);
            var layout = rect.gameObject.AddComponent<LayoutElement>();
            layout.preferredHeight = RowHeight * 2f;
            layout.minHeight = RowHeight * 2f;

            TMP_Text text = NewText(rect, "text", message, 15f, TextAlignmentOptions.Center);
            Stretch(text.rectTransform, 10f, 10f, 0f, 0f);
            text.color = new Color(0.7f, 0.66f, 0.58f);
            text.textWrappingMode = TextWrappingModes.Normal;
            text.overflowMode = TextOverflowModes.Overflow;

            _rows.Add(rect.gameObject);
        }

        // -------------------------------------------------------------- travel

        /// <summary>
        /// Applies the same safeguards as TeleportWorld.Teleport, then teleports to the chosen coordinates
        /// instead of those of the paired portal.
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

            if (!player.IsTeleportable(Plugin.AllowAllItems.Value || portal.m_allowAllItems))
            {
                player.Message(MessageHud.MessageType.Center, "$msg_noteleport");
                Hide();
                return;
            }

            Vector3 exit = target.Position
                           + target.Rotation * Vector3.forward * portal.m_exitDistance
                           + Vector3.up;

            Hide();
            player.TeleportTo(exit, target.Rotation, distantTeleport: true);
            if (Game.instance != null) Game.instance.IncrementPlayerStat(PlayerStatType.PortalsUsed);
        }

        /// <summary>Renaming goes through the game's TextInput, TeleportWorld already being a TextReceiver.</summary>
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
            Plugin.SortByDistance.Value = !Plugin.SortByDistance.Value;
            Fill();
        }

        // -------------------------------------------------------------- building

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
                Plugin.Log.LogWarning("Game font not found, panel not shown.");
                return false;
            }

            RectTransform root = NewRect(RootName, canvas);
            _root = root.gameObject;
            root.anchorMin = new Vector2(0.5f, 0.5f);
            root.anchorMax = new Vector2(0.5f, 0.5f);
            root.pivot = new Vector2(0.5f, 0.5f);
            root.anchoredPosition = Vector2.zero;

            // Own canvas: drawn above the HUD and clickable whatever the order of the game's canvases.
            // The CanvasGroup ignores those of the parents, whose opacity made the panel translucent.
            _root.AddComponent<Canvas>();
            _root.AddComponent<GraphicRaycaster>();
            var group = _root.AddComponent<CanvasGroup>();
            group.ignoreParentGroups = true;
            group.alpha = 1f;
            group.interactable = true;
            group.blocksRaycasts = true;

            var background = _root.AddComponent<Image>();
            background.color = Plugin.ParseColor(Plugin.BackgroundColor.Value, new Color(0.09f, 0.07f, 0.05f, 0.95f));

            // Title
            _title = NewText(root, "title", "Destinations", 22f, TextAlignmentOptions.Left);
            RectTransform title = _title.rectTransform;
            title.anchorMin = new Vector2(0f, 1f);
            title.anchorMax = new Vector2(1f, 1f);
            title.pivot = new Vector2(0.5f, 1f);
            title.sizeDelta = new Vector2(-2f * Margin, TitleHeight);
            title.anchoredPosition = new Vector2(0f, -Margin * 0.5f);
            _title.color = new Color(1f, 0.94f, 0.8f);

            // Warning shown when the server does not have the mod
            _hint = NewText(root, "warning", "", 13f, TextAlignmentOptions.Left);
            RectTransform hint = _hint.rectTransform;
            hint.anchorMin = new Vector2(0f, 1f);
            hint.anchorMax = new Vector2(1f, 1f);
            hint.pivot = new Vector2(0.5f, 1f);
            hint.sizeDelta = new Vector2(-2f * Margin, WarningHeight);
            hint.anchoredPosition = new Vector2(0f, -Margin * 0.5f - TitleHeight);
            _hint.color = new Color(0.9f, 0.72f, 0.4f);

            // Scrolling area
            RectTransform viewport = NewRect("viewport", root);
            Stretch(viewport, Margin, Margin, Margin * 0.5f + TitleHeight + WarningHeight + 4f,
                Margin + FooterHeight + 8f);
            var mask = viewport.gameObject.AddComponent<Image>();
            mask.color = new Color(0f, 0f, 0f, 0.25f);
            viewport.gameObject.AddComponent<RectMask2D>();

            _content = NewRect("content", viewport);
            _content.anchorMin = new Vector2(0f, 1f);
            _content.anchorMax = new Vector2(1f, 1f);
            _content.pivot = new Vector2(0.5f, 1f);
            _content.anchoredPosition = Vector2.zero;
            _content.sizeDelta = Vector2.zero;

            var stack = _content.gameObject.AddComponent<VerticalLayoutGroup>();
            stack.spacing = 2f;
            stack.padding = new RectOffset(4, 4, 4, 4);
            stack.childForceExpandWidth = true;
            stack.childForceExpandHeight = false;
            stack.childControlWidth = true;
            stack.childControlHeight = true;

            var fitter = _content.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            _scroll = viewport.gameObject.AddComponent<ScrollRect>();
            _scroll.viewport = viewport;
            _scroll.content = _content;
            _scroll.horizontal = false;
            _scroll.vertical = true;
            _scroll.movementType = ScrollRect.MovementType.Clamped;
            _scroll.scrollSensitivity = 28f;

            // Footer: three buttons of equal width, to follow the configured panel width
            RectTransform footer = NewRect("footer", root);
            footer.anchorMin = new Vector2(0f, 0f);
            footer.anchorMax = new Vector2(1f, 0f);
            footer.pivot = new Vector2(0.5f, 0f);
            footer.sizeDelta = new Vector2(-2f * Margin, FooterHeight);
            footer.anchoredPosition = new Vector2(0f, Margin * 0.5f);

            var buttonRow = footer.gameObject.AddComponent<HorizontalLayoutGroup>();
            buttonRow.spacing = 8f;
            buttonRow.childForceExpandWidth = true;
            buttonRow.childForceExpandHeight = true;
            buttonRow.childControlWidth = true;
            buttonRow.childControlHeight = true;

            _sortLabel = NewFooterButton(footer, "sort", "Sort: name", ToggleSort);
            NewFooterButton(footer, "rename", "Rename", Rename);
            NewFooterButton(footer, "close", "Close", Hide);

            ApplySize();
            return true;
        }

        private static void ApplySize()
        {
            if (_root == null) return;
            ((RectTransform)_root.transform).sizeDelta =
                new Vector2(Plugin.PanelWidth.Value, Plugin.PanelHeight.Value);
            _root.transform.localScale = Vector3.one * Plugin.Scale.Value;
        }

        private static TMP_Text NewFooterButton(RectTransform parent, string name, string label,
            UnityEngine.Events.UnityAction action)
        {
            RectTransform rect = NewRect(name, parent);
            rect.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;

            var background = rect.gameObject.AddComponent<Image>();
            background.color = new Color(1f, 1f, 1f, 0.09f);

            TMP_Text text = NewText(rect, "label", label, 16f, TextAlignmentOptions.Center);
            Stretch(text.rectTransform, 0f, 0f, 0f, 0f);
            text.color = new Color(0.95f, 0.91f, 0.82f);

            var button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = background;

            ColorBlock colors = button.colors;
            colors.highlightedColor = new Color(1f, 0.92f, 0.7f, 1f);
            colors.pressedColor = new Color(1f, 0.85f, 0.5f, 1f);
            colors.fadeDuration = 0.08f;
            button.colors = colors;

            button.onClick.AddListener(action);
            return text;
        }

        private static RectTransform NewRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, worldPositionStays: false);
            rect.localScale = Vector3.one;
            return rect;
        }

        private static TMP_Text NewText(RectTransform parent, string name, string content, float size,
            TextAlignmentOptions alignment)
        {
            RectTransform rect = NewRect(name, parent);
            var text = rect.gameObject.AddComponent<TextMeshProUGUI>();
            text.font = _font;
            text.fontSize = size;
            text.text = content;
            text.alignment = alignment;
            text.raycastTarget = false;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.overflowMode = TextOverflowModes.Ellipsis;
            Stretch(rect, 0f, 0f, 0f, 0f);
            return text;
        }

        private static void Stretch(RectTransform rect, float left, float right, float top, float bottom)
        {
            rect.anchorMin = new Vector2(0f, 0f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.offsetMin = new Vector2(left, bottom);
            rect.offsetMax = new Vector2(-right, -top);
        }

        /// <summary>The game's canvas, reached from a GUI that is always present during a game session.</summary>
        private static Transform FindCanvas()
        {
            Component anchor = null;
            if (StoreGui.instance != null) anchor = StoreGui.instance;
            else if (InventoryGui.instance != null) anchor = InventoryGui.instance;
            else if (Hud.instance != null) anchor = Hud.instance;
            if (anchor == null) return null;

            Canvas canvas = anchor.GetComponentInParent<Canvas>();
            return canvas != null ? canvas.transform : null;
        }

        /// <summary>
        /// Font borrowed from a game text, to avoid embedding an asset. We target the serif font of the hover
        /// text: taking the first text found in the canvas ended up on an unreadable pixel font.
        /// </summary>
        private static TMP_FontAsset FindFont(Transform canvas)
        {
            if (Hud.instance != null && Hud.instance.m_hoverName != null && Hud.instance.m_hoverName.font != null)
                return Hud.instance.m_hoverName.font;

            if (InventoryGui.instance != null && InventoryGui.instance.m_recipeName != null
                && InventoryGui.instance.m_recipeName.font != null)
                return InventoryGui.instance.m_recipeName.font;

            // Fallback: the most used font in the canvas, which is the game's and not a debug font.
            var usages = new Dictionary<TMP_FontAsset, int>();
            TMP_FontAsset best = null;
            foreach (TMP_Text text in canvas.GetComponentsInChildren<TMP_Text>(includeInactive: true))
            {
                if (text == null || text.font == null) continue;
                usages.TryGetValue(text.font, out int n);
                usages[text.font] = ++n;
                if (best == null || n > usages[best]) best = text.font;
            }
            return best;
        }
    }

    // ================================================================== patches

    /// <summary>The game registers its routed RPCs in Game.Start: we add ours there.</summary>
    [HarmonyPatch(typeof(Game), nameof(Game.Start))]
    internal static class Game_Start_Patch
    {
        private static void Postfix()
        {
            PortalNetwork.Register();
        }
    }

    /// <summary>Interacting with a portal opens the destination list instead of the name field.</summary>
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

    /// <summary>The hover text announces the destination choice, no longer the pairing by name.</summary>
    [HarmonyPatch(typeof(TeleportWorld), nameof(TeleportWorld.GetHoverText))]
    internal static class TeleportWorld_GetHoverText_Patch
    {
        private static bool Prefix(TeleportWorld __instance, ref string __result)
        {
            if (!Plugin.Enabled.Value) return true;

            ZDO zdo = __instance.m_nview != null ? __instance.m_nview.GetZDO() : null;
            if (zdo == null) return true;

            string tag = zdo.GetString(ZDOVars.s_tag) ?? "";
            string name = string.IsNullOrEmpty(tag.Trim()) ? "" : tag.RemoveRichTextTags();

            __result = Localization.instance.Localize(
                "$piece_portal $piece_portal_tag:\"" + name + "\"\n" +
                "[<color=yellow><b>$KEY_Use</b></color>] Choose a destination");
            return false;
        }
    }

    /// <summary>
    /// Panel open: no movement, no jumping, no attacking, no camera rotation. PlayerController is the one
    /// reading keyboard and mouse (FixedUpdate for movement, LateUpdate for looking around); Player's
    /// TakeInput, patched below, only covers interaction and the hotbar.
    /// </summary>
    [HarmonyPatch(typeof(PlayerController), "TakeInput")]
    internal static class PlayerController_TakeInput_Patch
    {
        private static void Postfix(ref bool __result)
        {
            if (DestinationPanel.IsVisible()) __result = false;
        }
    }

    /// <summary>Panel open: the player no longer interacts nor uses the hotbar.</summary>
    [HarmonyPatch(typeof(Player), "TakeInput")]
    internal static class Player_TakeInput_Patch
    {
        private static void Postfix(ref bool __result)
        {
            if (DestinationPanel.IsVisible()) __result = false;
        }
    }

    /// <summary>Panel open: the cursor is released, the camera no longer follows the mouse.</summary>
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

    /// <summary>Escape closes the panel instead of opening the game menu.</summary>
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
    /// Option: prevents the server from pairing two portals with the same name, the "connected" glow no longer
    /// making sense when the destination is chosen by hand.
    /// </summary>
    [HarmonyPatch(typeof(Game), nameof(Game.ConnectPortals))]
    internal static class Game_ConnectPortals_Patch
    {
        private static bool Prefix()
        {
            return !Plugin.Enabled.Value || !Plugin.DisableVanillaPairing.Value;
        }
    }
}
