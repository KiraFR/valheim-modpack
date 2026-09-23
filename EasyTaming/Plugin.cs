using System;
using System.Collections.Generic;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace EasyTaming
{
    /// <summary>
    /// Taming quality of life.
    ///
    /// - Hover details (client only): time left before the creature is tamed, how long it stays fed, love points or
    ///   time before birth, time before a young one grows up. Everything is read from the replicated ZDO
    ///   (s_tameTimeLeft, s_tameLastFeeding, s_lovePoints, s_pregnant, s_spawnTime), so it is right without being the owner.
    /// - Group command (client only): a key that makes every commandable tamed creature nearby follow the player, or stay
    ///   if some already follow them. It sends the vanilla "Command" RPC, so a vanilla owner handles it.
    /// - Taming, food search, breeding and growth (owner only): TamingUpdate, FindClosestConsumableItem, Procreate and
    ///   GrowUpdate all start with m_nview.IsOwner(). The config of the creature's owner decides: install the mod for
    ///   every player with the same config, and on the dedicated server, which permanently owns the creatures around
    ///   the world spawn (like QuickBrew's barrels).
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "valheim.easytaming";
        public const string PluginName = "EasyTaming";
        public const string PluginVersion = "1.0.0";

        internal static ManualLogSource Log;

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> ShowDetails;
        internal static ConfigEntry<KeyboardShortcut> GroupCommandKey;
        internal static ConfigEntry<float> GroupCommandRange;
        internal static ConfigEntry<float> TamingTimeMultiplier;
        internal static ConfigEntry<bool> TameWhileAlerted;
        internal static ConfigEntry<float> FoodSearchRange;
        internal static ConfigEntry<int> MaxCreatures;
        internal static ConfigEntry<float> PregnancyTimeMultiplier;
        internal static ConfigEntry<float> GrowTimeMultiplier;

        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;

            Enabled = Config.Bind("General", "Enabled", true, "Enables or disables the mod.");

            ShowDetails = Config.Bind("Info", "ShowDetails", true,
                "Shows on hover: time left before taming, how long the creature stays fed, love points or time before " +
                "birth, and time before a young one grows up.");

            GroupCommandKey = Config.Bind("Command", "GroupCommandKey", new KeyboardShortcut(KeyCode.Y),
                "Makes every commandable tamed creature nearby (wolves...) follow you. If some already follow you, " +
                "they all stay instead. Creatures following another player are left alone.");
            GroupCommandRange = Config.Bind("Command", "GroupCommandRange", 20f,
                new ConfigDescription("Radius of the group command, in metres.", new AcceptableValueRange<float>(1f, 100f)));

            TamingTimeMultiplier = Config.Bind("Taming", "TamingTimeMultiplier", 0.25f,
                new ConfigDescription(
                    "Share of the vanilla taming time to wait. Vanilla: 1800 s (30 min) of calm, fed time for most " +
                    "creatures. 0.25 = 7.5 min, 0.1 = 3 min, 1 = vanilla. Also applies to creatures already being tamed.",
                    new AcceptableValueRange<float>(0.01f, 1f)));
            TameWhileAlerted = Config.Bind("Taming", "TameWhileAlerted", false,
                "Keeps taming going while the creature is alerted (frightened, or attacking you). Vanilla pauses it.");
            FoodSearchRange = Config.Bind("Taming", "FoodSearchRange", 15f,
                new ConfigDescription(
                    "Distance in metres at which creatures spot food lying on the ground. Vanilla: 5 m for most " +
                    "creatures. A creature whose own range is larger keeps it. They still need a path to the food.",
                    new AcceptableValueRange<float>(0f, 50f)));

            MaxCreatures = Config.Bind("Breeding", "MaxCreatures", 0,
                new ConfigDescription(
                    "How many creatures of a kind (adults and young) may live within 10 m of one another before they stop " +
                    "breeding. 0 = the creature's vanilla value (4 for most).",
                    new AcceptableValueRange<int>(0, 100)));
            PregnancyTimeMultiplier = Config.Bind("Breeding", "PregnancyTimeMultiplier", 1f,
                new ConfigDescription("Share of the vanilla pregnancy duration to wait. 0.5 = twice as fast, 1 = vanilla.",
                    new AcceptableValueRange<float>(0f, 10f)));
            GrowTimeMultiplier = Config.Bind("Breeding", "GrowTimeMultiplier", 1f,
                new ConfigDescription("Share of the vanilla time a young one takes to grow up. 0.5 = twice as fast, 1 = vanilla.",
                    new AcceptableValueRange<float>(0f, 10f)));

            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll(typeof(Plugin).Assembly);

            Log.LogInfo($"{PluginName} {PluginVersion} loaded.");
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
        }

        private void Update()
        {
            if (!Enabled.Value || !GroupCommandKey.Value.IsDown() || !InputFree()) return;

            Player player = Player.m_localPlayer;
            if (player == null || player.IsDead()) return;
            GroupCommand(player);
        }

        internal static bool InputFree()
        {
            if (Console.IsVisible() || Menu.IsVisible() || TextInput.IsVisible()) return false;
            if (InventoryGui.IsVisible() || Minimap.IsOpen()) return false;
            if (Chat.instance != null && Chat.instance.HasFocus()) return false;
            return true;
        }

        /// <summary>
        /// Stay if at least one nearby creature follows the player, follow otherwise. s_follow (the followed player's
        /// name, written by the owner) is read rather than MonsterAI.GetFollowTarget(), which is only set on the owner.
        /// Command() toggles on the owner, so it is only sent to creatures that should change state.
        /// </summary>
        private static void GroupCommand(Player player)
        {
            string me = player.GetPlayerName();
            float range = GroupCommandRange.Value;
            var nearby = new List<Tameable>();
            foreach (Character character in Character.GetAllCharacters())
            {
                if (character.IsPlayer() || !character.IsTamed() || character.IsDead()) continue;
                if (Vector3.Distance(character.transform.position, player.transform.position) > range) continue;

                Tameable tameable = character.GetComponent<Tameable>();
                if (tameable == null || !tameable.m_commandable || !tameable.m_nview.IsValid()) continue;
                nearby.Add(tameable);
            }

            bool anyFollowing = false;
            foreach (Tameable tameable in nearby)
            {
                if (FollowedName(tameable) == me) anyFollowing = true;
            }

            int count = 0;
            foreach (Tameable tameable in nearby)
            {
                string followed = FollowedName(tameable);
                if (anyFollowing ? followed == me : followed.Length == 0)
                {
                    tameable.Command(player, message: false);
                    count++;
                }
            }

            string text;
            if (count == 0) text = "No tamed creature to command nearby";
            else if (anyFollowing) text = count == 1 ? "1 creature stays here" : $"{count} creatures stay here";
            else text = count == 1 ? "1 creature follows you" : $"{count} creatures follow you";
            player.Message(MessageHud.MessageType.Center, text);
        }

        private static string FollowedName(Tameable tameable)
        {
            return tameable.m_nview.GetZDO().GetString(ZDOVars.s_follow);
        }

        internal static double SecondsSince(long ticks)
        {
            return (ZNet.instance.GetTime() - new DateTime(ticks)).TotalSeconds;
        }

        internal static string FormatDuration(double seconds)
        {
            int total = Math.Max(0, (int)Math.Ceiling(seconds));
            int hours = total / 3600;
            int min = total % 3600 / 60;
            int sec = total % 60;
            if (hours > 0) return $"{hours} h {min:00} min";
            return min > 0 ? $"{min} min {sec:00} s" : $"{sec} s";
        }
    }

    /// <summary>
    /// Hover: extra lines under the name line. Character.GetHoverText delegates to Tameable.GetHoverText, so both
    /// paths pass here. Durations assume the viewer's config matches the owner's.
    /// </summary>
    [HarmonyPatch(typeof(Tameable), nameof(Tameable.GetHoverText))]
    internal static class Tameable_GetHoverText_Patch
    {
        private static void Postfix(Tameable __instance, ref string __result)
        {
            if (!Plugin.Enabled.Value || !Plugin.ShowDetails.Value || string.IsNullOrEmpty(__result)) return;
            if (__instance.m_character == null || !__instance.m_nview.IsValid()) return;

            ZDO zdo = __instance.m_nview.GetZDO();
            var lines = new StringBuilder();
            bool tamed = __instance.IsTamed();

            if (!tamed)
            {
                double left = __instance.GetRemainingTime() * Plugin.TamingTimeMultiplier.Value;
                lines.Append("\nTamed in ").Append(Plugin.FormatDuration(left)).Append(" while fed and calm");
            }

            long lastFeeding = zdo.GetLong(ZDOVars.s_tameLastFeeding, 0L);
            if (lastFeeding != 0L)
            {
                double fedLeft = __instance.m_fedDuration - Plugin.SecondsSince(lastFeeding);
                if (fedLeft > 0) lines.Append("\nFed for ").Append(Plugin.FormatDuration(fedLeft));
            }

            Procreation procreation = __instance.GetComponent<Procreation>();
            if (tamed && procreation != null)
            {
                long pregnant = zdo.GetLong(ZDOVars.s_pregnant, 0L);
                if (pregnant != 0L)
                {
                    double birthLeft = procreation.m_pregnancyDuration * Plugin.PregnancyTimeMultiplier.Value -
                                       Plugin.SecondsSince(pregnant);
                    lines.Append(birthLeft > 0 ? "\nPregnant, birth in " + Plugin.FormatDuration(birthLeft) : "\nBirth imminent");
                }
                else
                {
                    lines.Append($"\nLove {procreation.GetLovePoints()}/{procreation.m_requiredLovePoints}");
                }
            }

            Growup growup = __instance.GetComponent<Growup>();
            long spawnTime = zdo.GetLong(ZDOVars.s_spawnTime, 0L);
            if (growup != null && spawnTime != 0L)
            {
                double growLeft = growup.m_growTime * Plugin.GrowTimeMultiplier.Value - Plugin.SecondsSince(spawnTime);
                lines.Append(growLeft > 0 ? "\nGrows up in " + Plugin.FormatDuration(growLeft) : "\nGrowing up");
            }

            if (lines.Length == 0) return;
            int firstBreak = __result.IndexOf('\n');
            __result = firstBreak < 0 ? __result + lines : __result.Insert(firstBreak, lines.ToString());
        }
    }

    /// <summary>Taming speed: scales the 3 s removed from s_tameTimeLeft on every TamingUpdate (owner only).</summary>
    [HarmonyPatch(typeof(Tameable), nameof(Tameable.DecreaseRemainingTime))]
    internal static class Tameable_DecreaseRemainingTime_Patch
    {
        private static void Prefix(ref float time)
        {
            if (!Plugin.Enabled.Value) return;
            time /= Mathf.Max(0.01f, Plugin.TamingTimeMultiplier.Value);
        }
    }

    /// <summary>TameWhileAlerted: the vanilla TamingUpdate without its !IsAlerted() condition.</summary>
    [HarmonyPatch(typeof(Tameable), nameof(Tameable.TamingUpdate))]
    internal static class Tameable_TamingUpdate_Patch
    {
        private static bool Prefix(Tameable __instance)
        {
            if (!Plugin.Enabled.Value || !Plugin.TameWhileAlerted.Value) return true;

            Tameable t = __instance;
            if (t.m_nview.IsValid() && t.m_nview.IsOwner() && !t.IsTamed() && !t.IsHungry() && t.m_monsterAI != null)
            {
                t.m_monsterAI.SetDespawnInDay(false);
                t.m_monsterAI.SetEventCreature(false);
                t.DecreaseRemainingTime(3f);
                if (t.GetRemainingTime() <= 0f) t.Tame();
                else t.m_sootheEffect.Create(t.transform.position, t.transform.rotation);
            }
            return false;
        }
    }

    /// <summary>Food search: widens the radius creatures scan for food lying on the ground (owner only).</summary>
    [HarmonyPatch(typeof(MonsterAI), nameof(MonsterAI.FindClosestConsumableItem))]
    internal static class MonsterAI_FindClosestConsumableItem_Patch
    {
        private static void Prefix(MonsterAI __instance, ref float maxRange)
        {
            if (!Plugin.Enabled.Value || __instance.m_tamable == null) return;
            maxRange = Mathf.Max(maxRange, Plugin.FoodSearchRange.Value);
        }
    }

    /// <summary>Breeding: overrides the creature cap and pregnancy duration for one Procreate call (owner only).</summary>
    [HarmonyPatch(typeof(Procreation), nameof(Procreation.Procreate))]
    internal static class Procreation_Procreate_Patch
    {
        internal struct Saved
        {
            public int MaxCreatures;
            public float PregnancyDuration;
        }

        private static void Prefix(Procreation __instance, out Saved __state)
        {
            __state = new Saved { MaxCreatures = __instance.m_maxCreatures, PregnancyDuration = __instance.m_pregnancyDuration };
            if (!Plugin.Enabled.Value) return;

            if (Plugin.MaxCreatures.Value > 0) __instance.m_maxCreatures = Plugin.MaxCreatures.Value;
            __instance.m_pregnancyDuration *= Plugin.PregnancyTimeMultiplier.Value;
        }

        private static void Postfix(Procreation __instance, Saved __state)
        {
            __instance.m_maxCreatures = __state.MaxCreatures;
            __instance.m_pregnancyDuration = __state.PregnancyDuration;
        }
    }

    /// <summary>Growth: scales how long a young one takes to grow up, for one GrowUpdate call (owner only).</summary>
    [HarmonyPatch(typeof(Growup), nameof(Growup.GrowUpdate))]
    internal static class Growup_GrowUpdate_Patch
    {
        private static void Prefix(Growup __instance, out float __state)
        {
            __state = __instance.m_growTime;
            if (Plugin.Enabled.Value) __instance.m_growTime *= Plugin.GrowTimeMultiplier.Value;
        }

        private static void Postfix(Growup __instance, float __state)
        {
            __instance.m_growTime = __state;
        }
    }
}
