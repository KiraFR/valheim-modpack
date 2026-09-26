using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ShieldOutline
{
    /// <summary>
    /// Hides the dome of the shield generator, visually only.
    ///
    /// - The dome is not a mesh: it is a full-screen image effect on the camera (ShieldDomeImageEffect) that
    ///   ray-marches every dome sent through SetShieldData. The game switches that component on only while at least
    ///   one dome exists (ToggleActiveImageEffect), so forcing its dome count to 0 turns the effect off entirely and
    ///   OnRenderImage is not even called.
    /// - Gameplay is untouched: projectile blocking and IsInsideShield read m_shieldDome's position and m_radius,
    ///   which keep being updated. The generator itself, its particles, lights and hit/start/stop effects stay.
    /// - ShowGroundRing draws a thin line where the dome's sphere meets the terrain, in the dome's colour (which
    ///   follows the fuel level), so the protected area stays readable.
    ///
    /// Client side only: each player chooses what they see.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "valheim.shieldoutline";
        public const string PluginName = "ShieldOutline";
        public const string PluginVersion = "1.0.0";

        internal static ManualLogSource Log;

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> ShowGroundRing;
        internal static ConfigEntry<float> RingWidth;
        internal static ConfigEntry<float> RingOpacity;

        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;

            Enabled = Config.Bind("General", "Enabled", true, "Hides the shield generator dome effect.");
            ShowGroundRing = Config.Bind("General", "ShowGroundRing", true,
                "Draws a thin ring on the ground where the dome meets the terrain, in the dome's colour.");
            RingWidth = Config.Bind("General", "RingWidth", 0.12f,
                new ConfigDescription("Width of the ground ring, in metres.", new AcceptableValueRange<float>(0.02f, 1f)));
            RingOpacity = Config.Bind("General", "RingOpacity", 0.8f,
                new ConfigDescription("Opacity of the ground ring (0 = invisible, 1 = opaque).", new AcceptableValueRange<float>(0f, 1f)));

            Enabled.SettingChanged += (_, __) => RefreshDomeEffect();

            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll(typeof(Plugin).Assembly);

            Log.LogInfo($"{PluginName} {PluginVersion} loaded.");
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
            RefreshDomeEffect();
        }

        /// <summary>
        /// The game only toggles the effect when a dome changes: after a config change, apply it right away.
        /// </summary>
        private static void RefreshDomeEffect()
        {
            ShieldDomeImageEffect effect = ShieldGenerator.m_shieldDomeEffect;
            if (effect) effect.ToggleActiveImageEffect(effect.m_shieldDomeData.Count);
        }
    }

    [HarmonyPatch(typeof(ShieldDomeImageEffect), nameof(ShieldDomeImageEffect.ToggleActiveImageEffect))]
    internal static class ShieldDomeImageEffect_ToggleActiveImageEffect_Patch
    {
        private static void Prefix(ref int domes)
        {
            if (Plugin.Enabled.Value) domes = 0;
        }
    }

    [HarmonyPatch(typeof(ShieldGenerator), nameof(ShieldGenerator.Start))]
    internal static class ShieldGenerator_Start_Patch
    {
        private static void Postfix(ShieldGenerator __instance)
        {
            if (__instance.m_isPlacementGhost || !__instance.m_shieldDome) return;
            if (!__instance.GetComponent<GroundRing>()) __instance.gameObject.AddComponent<GroundRing>();
        }
    }

    /// <summary>
    /// Line around the generator following the intersection of the dome's sphere with the terrain.
    /// </summary>
    internal class GroundRing : MonoBehaviour
    {
        private const int Segments = 96;
        private const float HeightOffset = 0.1f;
        private const float RefreshInterval = 2f;

        private static Material s_material;

        private ShieldGenerator _generator;
        private LineRenderer _line;
        private readonly Vector3[] _points = new Vector3[Segments];
        private float _builtRadius = -1f;
        private float _nextRefresh;

        private void Awake()
        {
            _generator = GetComponent<ShieldGenerator>();

            if (!s_material) s_material = new Material(Shader.Find("Sprites/Default"));

            // On a child object: the generator already has renderers of its own.
            var go = new GameObject("ShieldOutline_Ring");
            go.transform.SetParent(transform, false);
            _line = go.AddComponent<LineRenderer>();
            _line.sharedMaterial = s_material;
            _line.useWorldSpace = true;
            _line.loop = true;
            _line.positionCount = Segments;
            _line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _line.receiveShadows = false;
            _line.enabled = false;
        }

        private void Update()
        {
            float radius = _generator.m_radius;
            bool visible = Plugin.Enabled.Value && Plugin.ShowGroundRing.Value && radius > 0.5f
                           && _generator.m_shieldDome && ZoneSystem.instance;
            _line.enabled = visible;
            if (!visible) return;

            // The radius grows and shrinks smoothly with fuel; the terrain can be dug: rebuild on both.
            if (Mathf.Abs(radius - _builtRadius) > 0.05f || Time.time >= _nextRefresh)
            {
                BuildPoints(radius);
                _builtRadius = radius;
                _nextRefresh = Time.time + RefreshInterval;
            }

            Color color = ShieldDomeImageEffect.GetDomeColor(_generator.m_lastFuel);
            color.a = Plugin.RingOpacity.Value;
            _line.startColor = color;
            _line.endColor = color;
            _line.widthMultiplier = Plugin.RingWidth.Value;
        }

        private void BuildPoints(float radius)
        {
            Vector3 center = _generator.m_shieldDome.transform.position;
            for (int i = 0; i < Segments; i++)
            {
                float angle = i * Mathf.PI * 2f / Segments;
                var dir = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));

                // The sphere meets the ground at a horizontal distance that depends on the ground's height:
                // converge on it in a few steps (the terrain rarely changes much over a few metres).
                float horizontal = radius;
                float groundY = center.y;
                for (int step = 0; step < 3; step++)
                {
                    groundY = ZoneSystem.instance.GetGroundHeight(center + dir * horizontal);
                    float dy = groundY - center.y;
                    horizontal = Mathf.Sqrt(Mathf.Max(0f, radius * radius - dy * dy));
                }

                Vector3 p = center + dir * horizontal;
                p.y = ZoneSystem.instance.GetGroundHeight(p) + HeightOffset;
                _points[i] = p;
            }
            _line.SetPositions(_points);
        }

        private void OnDestroy()
        {
            if (_line) Destroy(_line.gameObject);
        }
    }
}
