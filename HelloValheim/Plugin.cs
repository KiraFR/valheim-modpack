using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace HelloValheim
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "valheim.hellovalheim";
        public const string PluginName = "HelloValheim";
        public const string PluginVersion = "1.0.0";

        internal static ManualLogSource Log;
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<string> WelcomeMessage;

        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;

            // Fichier généré : BepInEx/config/valheim.hellovalheim.cfg
            Enabled = Config.Bind("General", "Enabled", true,
                "Active ou désactive le message de bienvenue.");
            WelcomeMessage = Config.Bind("General", "WelcomeMessage", "Bienvenue dans Valheim, $name !",
                "Message affiché au spawn. $name est remplacé par le nom du joueur.");

            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll(typeof(Plugin).Assembly);

            Log.LogInfo($"{PluginName} {PluginVersion} chargé.");
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.OnSpawned))]
    internal static class Player_OnSpawned_Patch
    {
        private static void Postfix(Player __instance)
        {
            if (!Plugin.Enabled.Value) return;
            if (__instance != Player.m_localPlayer) return;

            string msg = Plugin.WelcomeMessage.Value.Replace("$name", __instance.GetPlayerName());
            __instance.Message(MessageHud.MessageType.Center, msg);
            Plugin.Log.LogInfo($"Message de bienvenue affiché pour {__instance.GetPlayerName()}.");
        }
    }
}
