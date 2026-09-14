using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TeamHealth
{
    /// <summary>
    /// Lists the other players with their health bar on the left of the screen.
    ///
    /// - Health comes from state the game already replicates: Character.GetHealth() and GetMaxHealth() read
    ///   ZDOVars.s_health and s_maxHealth from the player's ZDO, kept up to date by that player's own client. No RPC and
    ///   no custom state, so only the players who want the list need the mod.
    /// - Rows follow ZNet.GetPlayerList(), the list the server sends to every client, so every connected player is
    ///   listed. A player's Player object, and therefore their health, only exists on this machine while they are in
    ///   the zones loaded around us; further away the row says "Too far", with their distance when they share
    ///   their position on the map.
    /// - While a player respawns, their character ID in that list is ZDOID.None: the row says "Dead".
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "valheim.teamhealth";
        public const string PluginName = "TeamHealth";
        public const string PluginVersion = "1.0.0";

        internal static ManualLogSource Log;

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> ShowNumbers;
        internal static ConfigEntry<bool> ShowFarPlayers;
        internal static ConfigEntry<float> PositionX;
        internal static ConfigEntry<float> PositionY;
        internal static ConfigEntry<float> Width;

        private void Awake()
        {
            Log = Logger;

            Enabled = Config.Bind("General", "Enabled", true, "Enables or disables the mod.");

            ShowNumbers = Config.Bind("Display", "ShowNumbers", true,
                "Shows current and max health next to each name.");
            ShowFarPlayers = Config.Bind("Display", "ShowFarPlayers", true,
                "Also lists the players too far away for their health to be known, with their distance when they share " +
                "their position on the map.");
            PositionX = Config.Bind("Display", "PositionX", 20f,
                "Distance between the left edge of the screen and the list, in pixels at 100% GUI scale.");
            PositionY = Config.Bind("Display", "PositionY", 0f,
                "Offset of the centre of the list from the middle of the screen, in pixels at 100% GUI scale (positive = up).");
            Width = Config.Bind("Display", "Width", 220f,
                new ConfigDescription("Width of the list, in pixels at 100% GUI scale.",
                    new AcceptableValueRange<float>(120f, 600f)));

            Log.LogInfo($"{PluginName} {PluginVersion} loaded.");
        }

        private void Update()
        {
            HealthList.Update();
        }

        private void OnDestroy()
        {
            HealthList.Destroy();
        }
    }

    /// <summary>
    /// The list, built in uGUI on the game's canvas like VoiceChat's speaker list: one row per player with the name,
    /// a value on the right (health, distance or "Dead") and a health bar underneath.
    /// </summary>
    internal static class HealthList
    {
        private const string ListName = "TeamHealth_List";

        /// <summary>Health changes are read ten times per second; showing or hiding the list is checked every frame.</summary>
        private const float RefreshInterval = 0.1f;

        private const float RowHeight = 38f;
        private const float BarHeight = 7f;
        private const float Padding = 6f;

        /// <summary>Below this share of max health the bar turns to LowColor.</summary>
        private const float LowHealth = 0.3f;

        private static readonly Color NameColor = new Color(0.95f, 0.91f, 0.82f);
        private static readonly Color MutedColor = new Color(0.6f, 0.6f, 0.6f);
        private static readonly Color HealthColor = new Color(0.78f, 0.13f, 0.08f);
        private static readonly Color LowColor = new Color(1f, 0.45f, 0.1f);
        private static readonly Color RowColor = new Color(0f, 0f, 0f, 0.35f);
        private static readonly Color BarBackColor = new Color(0f, 0f, 0f, 0.6f);

        private sealed class Row
        {
            public GameObject Root;
            public TMP_Text Name;
            public TMP_Text Value;
            public GameObject Bar;
            public RectTransform Fill;
            public Image FillImage;
        }

        private struct Entry
        {
            public string Name;
            public string Value;
            public bool Muted;
            public bool HasBar;
            public float Ratio;
        }

        private static readonly List<Row> Rows = new List<Row>();
        private static readonly List<Entry> Entries = new List<Entry>();

        private static RectTransform _list;
        private static TMP_FontAsset _font;
        private static float _nextRefresh;

        internal static void Update()
        {
            if (!IsShown())
            {
                Hide();
                _nextRefresh = 0f;
                return;
            }
            if (Time.unscaledTime < _nextRefresh) return;
            _nextRefresh = Time.unscaledTime + RefreshInterval;

            Collect();
            if (Entries.Count == 0)
            {
                Hide();
                return;
            }
            if (!BuildList()) return;

            _list.gameObject.SetActive(true);
            _list.anchoredPosition = new Vector2(Plugin.PositionX.Value, Plugin.PositionY.Value);
            _list.sizeDelta = new Vector2(Plugin.Width.Value, _list.sizeDelta.y);
            while (Rows.Count < Entries.Count) Rows.Add(NewRow());
            for (int i = 0; i < Rows.Count; i++)
            {
                bool used = i < Entries.Count;
                Rows[i].Root.SetActive(used);
                if (used) Fill(Rows[i], Entries[i]);
            }
        }

        internal static void Destroy()
        {
            if (_list != null) UnityEngine.Object.Destroy(_list.gameObject);
            _list = null;
            Rows.Clear();
        }

        /// <summary>Hidden with the HUD (vanilla toggle, cutscenes) and behind the inventory, the large map and the pause menu.</summary>
        private static bool IsShown()
        {
            Player local = Player.m_localPlayer;
            return Plugin.Enabled.Value && local != null && ZNet.instance != null
                && !Hud.IsUserHidden() && !local.InCutscene()
                && !InventoryGui.IsVisible() && !Minimap.IsOpen() && !Menu.IsVisible();
        }

        private static void Hide()
        {
            if (_list != null) _list.gameObject.SetActive(false);
        }

        // -------------------------------------------------------------- data

        private static void Collect()
        {
            Entries.Clear();
            Player local = Player.m_localPlayer;
            ZDOID self = local.GetZDOID();
            string selfName = local.GetPlayerName();

            foreach (ZNet.PlayerInfo info in ZNet.instance.GetPlayerList())
            {
                if (info.m_characterID.Equals(self)) continue;

                if (info.m_characterID.IsNone())
                {
                    // Our own entry still carries no character for a moment after we respawn, until the next list.
                    if (info.m_name == selfName) continue;
                    Entries.Add(new Entry { Name = info.m_name, Value = "Dead", Muted = true, HasBar = true, Ratio = 0f });
                    continue;
                }

                Player player = FindPlayer(info.m_characterID);
                if (player != null)
                {
                    float max = Mathf.Max(1f, player.GetMaxHealth());
                    float health = Mathf.Clamp(player.GetHealth(), 0f, max);
                    bool dead = player.IsDead() || health <= 0f;
                    string value = dead ? "Dead"
                        : Plugin.ShowNumbers.Value ? $"{Mathf.CeilToInt(health)} / {Mathf.CeilToInt(max)}" : "";
                    Entries.Add(new Entry { Name = info.m_name, Value = value, Muted = dead, HasBar = true, Ratio = dead ? 0f : health / max });
                }
                else if (Plugin.ShowFarPlayers.Value)
                {
                    string value = info.m_publicPosition
                        ? $"Too far ({FormatDistance(Vector3.Distance(local.transform.position, info.m_position))})"
                        : "Too far";
                    Entries.Add(new Entry { Name = info.m_name, Value = value, Muted = true, HasBar = false });
                }
            }

            // Sorted by name so rows don't swap places as players come into range.
            Entries.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        }

        private static Player FindPlayer(ZDOID id)
        {
            foreach (Player player in Player.GetAllPlayers())
            {
                if (player != null && player.GetZDOID().Equals(id)) return player;
            }
            return null;
        }

        private static string FormatDistance(float meters)
        {
            return meters < 1000f ? $"{Mathf.RoundToInt(meters)} m" : $"{meters / 1000f:0.0} km";
        }

        // -------------------------------------------------------------- UI

        private static void Fill(Row row, Entry entry)
        {
            row.Name.text = entry.Name;
            row.Name.color = entry.Muted ? MutedColor : NameColor;
            row.Value.text = entry.Value;
            row.Value.color = entry.Muted ? MutedColor : NameColor;
            // The value keeps its full width ("Too far (1.2 km)" is longer than "150 / 150"), the name takes the rest.
            float valueWidth = entry.Value.Length > 0 ? row.Value.GetPreferredValues(entry.Value).x + 8f : 0f;
            row.Name.rectTransform.offsetMax = new Vector2(-(Padding + valueWidth), -2f);
            row.Bar.SetActive(entry.HasBar);
            row.Fill.anchorMax = new Vector2(entry.Ratio, 1f);
            row.FillImage.color = entry.Ratio < LowHealth ? LowColor : HealthColor;
        }

        /// <summary>Built on first use, and again after the game destroyed the canvas (back to the main menu).</summary>
        private static bool BuildList()
        {
            if (_list != null) return true;
            Rows.Clear();

            Transform canvas = FindCanvas();
            _font = canvas != null ? FindFont(canvas) : null;
            if (_font == null) return false;

            var go = new GameObject(ListName, typeof(RectTransform));
            _list = (RectTransform)go.transform;
            _list.SetParent(canvas, false);
            // Anchored on the middle of the left edge and centred on it: the list grows both ways as players join.
            _list.anchorMin = new Vector2(0f, 0.5f);
            _list.anchorMax = new Vector2(0f, 0.5f);
            _list.pivot = new Vector2(0f, 0.5f);
            _list.sizeDelta = new Vector2(Plugin.Width.Value, 0f);

            // Display only: clicks go through to the game.
            var group = go.AddComponent<CanvasGroup>();
            group.interactable = false;
            group.blocksRaycasts = false;

            var stack = go.AddComponent<VerticalLayoutGroup>();
            stack.spacing = 4f;
            stack.childControlWidth = true;
            stack.childControlHeight = true;
            stack.childForceExpandWidth = true;
            stack.childForceExpandHeight = false;
            go.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            return true;
        }

        private static Row NewRow()
        {
            RectTransform root = NewRect("row", _list);
            root.gameObject.AddComponent<LayoutElement>().preferredHeight = RowHeight;
            NewImage(root, RowColor);

            // Name and value share the line above the bar; Fill narrows the name (ellipsis) to leave room for the value.
            float textBottom = Padding + BarHeight + 2f;
            TMP_Text name = NewText(root, "name", 16f, TextAlignmentOptions.Left);
            Stretch(name.rectTransform, Padding, Padding, 2f, textBottom);

            TMP_Text value = NewText(root, "value", 15f, TextAlignmentOptions.Right);
            Stretch(value.rectTransform, Padding, Padding, 2f, textBottom);

            RectTransform bar = NewRect("bar", root);
            Stretch(bar, Padding, Padding, RowHeight - Padding - BarHeight, Padding);
            NewImage(bar, BarBackColor);

            RectTransform fill = NewRect("fill", bar);
            Stretch(fill, 0f, 0f, 0f, 0f);
            Image fillImage = NewImage(fill, HealthColor);

            return new Row { Root = root.gameObject, Name = name, Value = value, Bar = bar.gameObject, Fill = fill, FillImage = fillImage };
        }

        private static RectTransform NewRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, worldPositionStays: false);
            rect.localScale = Vector3.one;
            return rect;
        }

        private static Image NewImage(RectTransform rect, Color color)
        {
            var image = rect.gameObject.AddComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
            return image;
        }

        private static TMP_Text NewText(RectTransform parent, string name, float size, TextAlignmentOptions alignment)
        {
            RectTransform rect = NewRect(name, parent);
            var text = rect.gameObject.AddComponent<TextMeshProUGUI>();
            text.font = _font;
            text.fontSize = size;
            text.alignment = alignment;
            text.raycastTarget = false;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.overflowMode = TextOverflowModes.Ellipsis;
            return text;
        }

        private static void Stretch(RectTransform rect, float left, float right, float top, float bottom)
        {
            rect.anchorMin = new Vector2(0f, 0f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.offsetMin = new Vector2(left, bottom);
            rect.offsetMax = new Vector2(-right, -top);
        }

        /// <summary>The game's canvas, reached through a GUI that always exists in a session.</summary>
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
        /// Font borrowed from a game text, so no asset is shipped. Aims at the serif font of the hover text: the first
        /// text found in the canvas can be an unreadable pixel font.
        /// </summary>
        private static TMP_FontAsset FindFont(Transform canvas)
        {
            if (Hud.instance != null && Hud.instance.m_hoverName != null && Hud.instance.m_hoverName.font != null)
                return Hud.instance.m_hoverName.font;

            if (InventoryGui.instance != null && InventoryGui.instance.m_recipeName != null
                && InventoryGui.instance.m_recipeName.font != null)
                return InventoryGui.instance.m_recipeName.font;

            // Fallback: the most used font in the canvas, which is the game's and not a debug font.
            var uses = new Dictionary<TMP_FontAsset, int>();
            TMP_FontAsset best = null;
            int bestCount = 0;
            foreach (TMP_Text text in canvas.GetComponentsInChildren<TMP_Text>(includeInactive: true))
            {
                if (text.font == null) continue;
                uses.TryGetValue(text.font, out int count);
                uses[text.font] = ++count;
                if (count > bestCount)
                {
                    best = text.font;
                    bestCount = count;
                }
            }
            return best;
        }
    }
}
