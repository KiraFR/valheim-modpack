using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Compass
{
    /// <summary>
    /// Compass bar at the top of the screen: a horizontal strip that scrolls with the camera, showing N, NE, E... and
    /// the degrees in between, with the exact heading under the centre mark.
    ///
    /// - North is the top of the game's map: Minimap.WorldToMapPoint maps world +Z to the map's vertical axis and turns
    ///   the player marker by -eulerAngles.y, so a yaw of 0 faces north (+Z) and 90 faces east (+X).
    /// - The heading is the camera's yaw, not the character's: it follows where the player looks, also while steering
    ///   a ship or looking around in third person. eulerAngles.y is used instead of forward, which shrinks to nothing
    ///   when looking straight up or down.
    /// - Purely local display: no patch, no network, nothing needed from the server or the other players.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "valheim.compass";
        public const string PluginName = "Compass";
        public const string PluginVersion = "1.0.0";

        internal static ManualLogSource Log;

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> Width;
        internal static ConfigEntry<float> PositionY;
        internal static ConfigEntry<float> VisibleAngle;
        internal static ConfigEntry<bool> ShowNumbers;
        internal static ConfigEntry<bool> ShowHeading;
        internal static ConfigEntry<float> BackgroundOpacity;

        private void Awake()
        {
            Log = Logger;

            Enabled = Config.Bind("General", "Enabled", true, "Enables or disables the mod.");

            Width = Config.Bind("Display", "Width", 500f,
                new ConfigDescription("Width of the compass bar, in pixels at 100% GUI scale.",
                    new AcceptableValueRange<float>(200f, 1600f)));
            PositionY = Config.Bind("Display", "PositionY", 10f,
                "Distance between the top edge of the screen and the bar, in pixels at 100% GUI scale.");
            VisibleAngle = Config.Bind("Display", "VisibleAngle", 180f,
                new ConfigDescription("Angle covered by the bar from its left edge to its right edge, in degrees " +
                    "(180 = from your left to your right).",
                    new AcceptableValueRange<float>(60f, 360f)));
            ShowNumbers = Config.Bind("Display", "ShowNumbers", true,
                "Shows the degrees every 15° between the cardinal and intercardinal points.");
            ShowHeading = Config.Bind("Display", "ShowHeading", true,
                "Shows the exact heading in degrees under the centre of the bar.");
            BackgroundOpacity = Config.Bind("Display", "BackgroundOpacity", 0.35f,
                new ConfigDescription("Opacity of the dark background behind the bar (0 = none).",
                    new AcceptableValueRange<float>(0f, 1f)));

            Log.LogInfo($"{PluginName} {PluginVersion} loaded.");
        }

        private void Update()
        {
            CompassBar.Update();
        }

        private void OnDestroy()
        {
            CompassBar.Destroy();
        }
    }

    /// <summary>
    /// The bar, built in uGUI on the game's canvas like TeamHealth's list: one mark every 5° made of a tick and, every
    /// 15°, a label. Marks are created once and only moved along the bar each frame, clipped by a RectMask2D.
    /// </summary>
    internal static class CompassBar
    {
        private const string BarName = "Compass_Bar";

        private const int Step = 5;
        private const float BarHeight = 32f;
        private const float HeadingWidth = 52f;
        private const float HeadingHeight = 20f;

        /// <summary>Marks fade out over this last share of each half of the bar, instead of being cut at the edge.</summary>
        private const float FadeShare = 0.25f;

        private static readonly Color CardinalColor = new Color(0.95f, 0.91f, 0.82f);
        private static readonly Color NorthColor = new Color(1f, 0.55f, 0.25f);
        private static readonly Color IntercardinalColor = new Color(0.85f, 0.81f, 0.72f);
        private static readonly Color NumberColor = new Color(0.7f, 0.7f, 0.7f);
        private static readonly Color TickColor = new Color(0.8f, 0.8f, 0.8f);
        private static readonly Color CentreColor = new Color(1f, 0.8f, 0.3f);

        private sealed class Mark
        {
            public int Angle;
            public RectTransform Root;
            public Graphic Tick;
            public TMP_Text Label;
            public bool IsNumber;
        }

        private static readonly List<Mark> Marks = new List<Mark>();

        private static RectTransform _bar;
        private static RectTransform _strip;
        private static Image _background;
        private static RectTransform _headingBox;
        private static Image _headingBackground;
        private static TMP_Text _heading;
        private static TMP_Text _textStyle;

        private static int _shownHeading = -1;

        internal static void Update()
        {
            Camera camera = Utils.GetMainCamera();
            if (!IsShown() || camera == null)
            {
                Hide();
                return;
            }
            if (!BuildBar()) return;

            _bar.gameObject.SetActive(true);
            ApplySettings();

            float heading = Mathf.Repeat(camera.transform.eulerAngles.y, 360f);
            float halfAngle = Plugin.VisibleAngle.Value / 2f;
            float halfWidth = Plugin.Width.Value / 2f;

            foreach (Mark mark in Marks)
            {
                // Signed angle from the heading to the mark, -180..180: negative marks are on the left.
                float delta = Mathf.DeltaAngle(heading, mark.Angle);
                float distance = Mathf.Abs(delta) / halfAngle;
                bool visible = distance <= 1f;
                if (mark.Root.gameObject.activeSelf != visible) mark.Root.gameObject.SetActive(visible);
                if (!visible) continue;
                if (mark.IsNumber && mark.Label.gameObject.activeSelf != Plugin.ShowNumbers.Value)
                    mark.Label.gameObject.SetActive(Plugin.ShowNumbers.Value);

                mark.Root.anchoredPosition = new Vector2(delta / halfAngle * halfWidth, 0f);
                // CanvasRenderer alpha does not rebuild the mesh, unlike changing Graphic.color every frame.
                float alpha = Mathf.Clamp01((1f - distance) / FadeShare);
                mark.Tick.canvasRenderer.SetAlpha(alpha);
                if (mark.Label != null) mark.Label.canvasRenderer.SetAlpha(alpha);
            }

            _headingBox.gameObject.SetActive(Plugin.ShowHeading.Value);
            int rounded = Mathf.RoundToInt(heading) % 360;
            if (Plugin.ShowHeading.Value && rounded != _shownHeading)
            {
                _shownHeading = rounded;
                _heading.text = $"{rounded}°";
            }
        }

        internal static void Destroy()
        {
            if (_bar != null) Object.Destroy(_bar.gameObject);
            _bar = null;
            Marks.Clear();
        }

        /// <summary>Hidden with the HUD (vanilla toggle, cutscenes) and behind the inventory, the large map and the pause menu.</summary>
        private static bool IsShown()
        {
            Player local = Player.m_localPlayer;
            return Plugin.Enabled.Value && local != null && Hud.instance != null
                && !Hud.IsUserHidden() && !local.InCutscene()
                && !InventoryGui.IsVisible() && !Minimap.IsOpen() && !Menu.IsVisible();
        }

        private static void Hide()
        {
            if (_bar != null) _bar.gameObject.SetActive(false);
        }

        /// <summary>Settings edited at runtime (for example with a configuration manager) apply without a restart.</summary>
        private static void ApplySettings()
        {
            _bar.anchoredPosition = new Vector2(0f, -Plugin.PositionY.Value);
            _bar.sizeDelta = new Vector2(Plugin.Width.Value, BarHeight);
            Color background = Color.black;
            background.a = Plugin.BackgroundOpacity.Value;
            _background.color = background;
            _headingBackground.color = background;
        }

        // -------------------------------------------------------------- UI

        /// <summary>Built on first use, and again after the game destroyed the canvas (back to the main menu).</summary>
        private static bool BuildBar()
        {
            if (_bar != null) return true;
            Marks.Clear();
            _shownHeading = -1;

            Transform canvas = FindCanvas();
            _textStyle = canvas != null ? FindTextStyle(canvas) : null;
            if (_textStyle == null) return false;

            var go = new GameObject(BarName, typeof(RectTransform));
            _bar = (RectTransform)go.transform;
            _bar.SetParent(canvas, false);
            // Anchored on the middle of the top edge, hanging down from it.
            _bar.anchorMin = new Vector2(0.5f, 1f);
            _bar.anchorMax = new Vector2(0.5f, 1f);
            _bar.pivot = new Vector2(0.5f, 1f);

            // Display only: clicks go through to the game.
            var group = go.AddComponent<CanvasGroup>();
            group.interactable = false;
            group.blocksRaycasts = false;

            _background = NewImage(_bar, Color.black);

            _strip = NewRect("strip", _bar);
            Stretch(_strip);
            _strip.gameObject.AddComponent<RectMask2D>();
            for (int angle = 0; angle < 360; angle += Step) Marks.Add(NewMark(angle));

            // Centre mark, over the strip and not clipped by it.
            RectTransform centre = NewRect("centre", _bar);
            centre.anchorMin = new Vector2(0.5f, 0f);
            centre.anchorMax = new Vector2(0.5f, 1f);
            centre.pivot = new Vector2(0.5f, 0.5f);
            centre.sizeDelta = new Vector2(2f, 6f);
            NewImage(centre, CentreColor);

            _headingBox = NewRect("heading", _bar);
            _headingBox.anchorMin = new Vector2(0.5f, 0f);
            _headingBox.anchorMax = new Vector2(0.5f, 0f);
            _headingBox.pivot = new Vector2(0.5f, 1f);
            _headingBox.anchoredPosition = new Vector2(0f, -3f);
            _headingBox.sizeDelta = new Vector2(HeadingWidth, HeadingHeight);
            _headingBackground = NewImage(_headingBox, Color.black);
            _heading = NewText(_headingBox, "value", 15f, CentreColor);
            Stretch(_heading.rectTransform);

            ApplySettings();
            return true;
        }

        /// <summary>
        /// A mark is a zero-width column the height of the bar, placed at its angle; the tick hangs from the bottom and
        /// the label, if any, sits above it. Cardinal points get the longest tick and the largest letter.
        /// </summary>
        private static Mark NewMark(int angle)
        {
            RectTransform root = NewRect(angle.ToString(), _strip);
            root.anchorMin = new Vector2(0.5f, 0f);
            root.anchorMax = new Vector2(0.5f, 1f);
            root.pivot = new Vector2(0.5f, 0.5f);
            root.sizeDelta = Vector2.zero;

            bool cardinal = angle % 90 == 0;
            bool intercardinal = !cardinal && angle % 45 == 0;
            bool number = !cardinal && !intercardinal && angle % 15 == 0;

            float tickHeight = cardinal ? 9f : intercardinal ? 7f : number ? 6f : 4f;
            RectTransform tickRect = NewRect("tick", root);
            tickRect.anchorMin = new Vector2(0.5f, 0f);
            tickRect.anchorMax = new Vector2(0.5f, 0f);
            tickRect.pivot = new Vector2(0.5f, 0f);
            tickRect.sizeDelta = new Vector2(cardinal ? 2f : 1f, tickHeight);
            Image tick = NewImage(tickRect, angle == 0 ? NorthColor : TickColor);

            TMP_Text label = null;
            if (cardinal || intercardinal || number)
            {
                string text = cardinal || intercardinal ? PointName(angle) : angle.ToString();
                float size = cardinal ? 20f : intercardinal ? 15f : 12f;
                Color color = angle == 0 ? NorthColor : cardinal ? CardinalColor : intercardinal ? IntercardinalColor : NumberColor;
                label = NewText(root, "label", size, color);
                label.text = text;
                RectTransform rect = label.rectTransform;
                rect.anchorMin = new Vector2(0.5f, 0f);
                rect.anchorMax = new Vector2(0.5f, 1f);
                rect.pivot = new Vector2(0.5f, 0.5f);
                rect.sizeDelta = new Vector2(40f, 0f);
                // Centred in the space left above the ticks.
                rect.offsetMin = new Vector2(-20f, tickHeight);
                rect.offsetMax = new Vector2(20f, 0f);
            }

            return new Mark { Angle = angle, Root = root, Tick = tick, Label = label, IsNumber = number };
        }

        private static string PointName(int angle)
        {
            switch (angle)
            {
                case 0: return "N";
                case 45: return "NE";
                case 90: return "E";
                case 135: return "SE";
                case 180: return "S";
                case 225: return "SW";
                case 270: return "W";
                default: return "NW";
            }
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

        /// <summary>Same font and material as the game's hover text, so the labels keep its outline over bright skies.</summary>
        private static TMP_Text NewText(RectTransform parent, string name, float size, Color color)
        {
            RectTransform rect = NewRect(name, parent);
            var text = rect.gameObject.AddComponent<TextMeshProUGUI>();
            text.font = _textStyle.font;
            text.fontSharedMaterial = _textStyle.fontSharedMaterial;
            text.fontSize = size;
            text.color = color;
            text.alignment = TextAlignmentOptions.Center;
            text.raycastTarget = false;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.overflowMode = TextOverflowModes.Overflow;
            return text;
        }

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
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
        /// A game text to copy the font from, so no asset is shipped. Aims at the serif font of the hover text: the first
        /// text found in the canvas can be an unreadable pixel font.
        /// </summary>
        private static TMP_Text FindTextStyle(Transform canvas)
        {
            if (Hud.instance != null && Hud.instance.m_hoverName != null && Hud.instance.m_hoverName.font != null)
                return Hud.instance.m_hoverName;

            if (InventoryGui.instance != null && InventoryGui.instance.m_recipeName != null
                && InventoryGui.instance.m_recipeName.font != null)
                return InventoryGui.instance.m_recipeName;

            // Fallback: a text using the most used font in the canvas, which is the game's and not a debug font.
            var uses = new Dictionary<TMP_FontAsset, int>();
            TMP_Text best = null;
            int bestCount = 0;
            foreach (TMP_Text text in canvas.GetComponentsInChildren<TMP_Text>(includeInactive: true))
            {
                if (text.font == null) continue;
                uses.TryGetValue(text.font, out int count);
                uses[text.font] = ++count;
                if (count > bestCount)
                {
                    best = text;
                    bestCount = count;
                }
            }
            return best;
        }
    }
}
