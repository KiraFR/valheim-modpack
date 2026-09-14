using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace SkillCarry
{
    /// <summary>
    /// Raises the player's max carry weight according to their skill levels.
    ///
    /// - Player.GetMaxCarryWeight is the only source of the limit: IsEncumbered, the auto pickup check and the
    ///   weight shown in the inventory all read it, so a single Postfix covers every case.
    /// - Each skill has its own bonus in [Skills], reached at level 100 and scaled linearly below it through
    ///   Player.GetSkillFactor (level / 100, clamped). That factor goes through SEMan.ModifySkillLevel, so a
    ///   temporary skill boost also raises the weight while it lasts.
    /// - The vanilla limit is (m_maxCarryWeight + status effects such as Megingjord) x Game.m_carryWeightRate, the
    ///   world modifier. The bonus is multiplied by the same rate, so a world with a lower carry weight shrinks it too.
    ///
    /// Multiplayer: encumbrance is only evaluated by the player's own client (the owner of their Player object), and
    /// nothing is written to a ZDO. Client only: neither the server nor the other players need the mod.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "valheim.skillcarry";
        public const string PluginName = "SkillCarry";
        public const string PluginVersion = "1.0.0";

        internal static ManualLogSource Log;

        internal static ConfigEntry<bool> Enabled;

        /// <summary>Bonus at level 100 per skill, in the order of the Skills.SkillType enum.</summary>
        internal static readonly List<KeyValuePair<Skills.SkillType, ConfigEntry<float>>> SkillBonuses =
            new List<KeyValuePair<Skills.SkillType, ConfigEntry<float>>>();

        /// <summary>
        /// Default bonuses: physical skills only. Raw strength (mining, felling, bare hands) weighs the most, then
        /// running, body (blocking, swimming) and agility (jumping, dodging), then melee weapons. Up to +625 with all
        /// of them at 100. Ranged weapons, magic, sneaking, crafts, fishing and riding stay at 0.
        /// </summary>
        private static readonly Dictionary<Skills.SkillType, float> DefaultBonuses = new Dictionary<Skills.SkillType, float>
        {
            { Skills.SkillType.Pickaxes, 75f },
            { Skills.SkillType.WoodCutting, 75f },
            { Skills.SkillType.Unarmed, 75f },
            { Skills.SkillType.Run, 50f },
            { Skills.SkillType.Blocking, 50f },
            { Skills.SkillType.Swim, 50f },
            { Skills.SkillType.Jump, 50f },
            { Skills.SkillType.Dodge, 50f },
            { Skills.SkillType.Swords, 25f },
            { Skills.SkillType.Knives, 25f },
            { Skills.SkillType.Clubs, 25f },
            { Skills.SkillType.Polearms, 25f },
            { Skills.SkillType.Spears, 25f },
            { Skills.SkillType.Axes, 25f },
        };

        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;

            Enabled = Config.Bind("General", "Enabled", true, "Enables or disables the mod.");

            foreach (Skills.SkillType skill in Enum.GetValues(typeof(Skills.SkillType)))
            {
                if (skill == Skills.SkillType.None || skill == Skills.SkillType.All) continue;

                DefaultBonuses.TryGetValue(skill, out float defaultBonus);
                ConfigEntry<float> entry = Config.Bind("Skills", skill.ToString(), defaultBonus,
                    new ConfigDescription(
                        $"Carry weight added at {skill} level 100, scaled linearly below (level 50 gives half). " +
                        "0 = this skill gives nothing. Vanilla base weight: 300.",
                        new AcceptableValueRange<float>(0f, 1000f)));
                SkillBonuses.Add(new KeyValuePair<Skills.SkillType, ConfigEntry<float>>(skill, entry));
            }

            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll(typeof(Plugin).Assembly);

            Log.LogInfo($"{PluginName} {PluginVersion} loaded.");
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
        }

        /// <summary>Skills already reported as missing from the player prefab, so the warning is logged once.</summary>
        private static readonly HashSet<Skills.SkillType> ReportedUndefined = new HashSet<Skills.SkillType>();

        /// <summary>Sum of every skill bonus for this player, before the world carry weight rate.</summary>
        internal static float GetSkillBonus(Player player)
        {
            float bonus = 0f;
            foreach (KeyValuePair<Skills.SkillType, ConfigEntry<float>> pair in SkillBonuses)
            {
                float max = pair.Value.Value;
                if (max <= 0f) continue;

                // Some enum values have no SkillDef on the player prefab. Reading their level would make
                // Skills.GetSkill store a Skill with a null m_info, and Skills.Save would then throw on it,
                // breaking the character save.
                if (player.m_skills.GetSkillDef(pair.Key) == null)
                {
                    if (ReportedUndefined.Add(pair.Key))
                        Log.LogWarning($"Skill {pair.Key} does not exist in this game version, its bonus is ignored.");
                    continue;
                }

                bonus += max * player.GetSkillFactor(pair.Key);
            }
            return bonus;
        }
    }

    /// <summary>Adds the skill bonus to the limit, scaled by the world carry weight rate like the vanilla value.</summary>
    [HarmonyPatch(typeof(Player), nameof(Player.GetMaxCarryWeight))]
    internal static class Player_GetMaxCarryWeight_Patch
    {
        private static void Postfix(Player __instance, ref float __result)
        {
            if (!Plugin.Enabled.Value) return;
            // Only the local player's skills are loaded; other players' Player objects hold empty skill data.
            if (__instance != Player.m_localPlayer || __instance.m_skills == null) return;

            __result += Plugin.GetSkillBonus(__instance) * Game.m_carryWeightRate;
        }
    }
}
