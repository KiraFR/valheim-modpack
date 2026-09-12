using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace QuickBrew
{
    /// <summary>
    /// Réduit la durée de fermentation des tonneaux (hydromels, potions).
    ///
    /// - Le tonneau ne stocke que son heure de départ (ZDOVars.s_startTime) ; chaque client compare
    ///   « maintenant - départ » à m_fermentationDuration pour savoir si c'est prêt.
    /// - Plutôt que de changer m_fermentationDuration localement (un propriétaire vanilla refuserait alors
    ///   de soutirer), le mod antidate l'heure de départ dans le ZDO. L'état est répliqué par le jeu : tout
    ///   le monde, avec ou sans le mod, voit le tonneau prêt au même moment.
    /// - La valeur antidatée est gardée dans le ZDO sous StartDecaleKey. Quand s_startTime ne lui correspond
    ///   plus (nouveau remplissage, tonneau rempli avant l'installation du mod, chrono remis à zéro faute de
    ///   toit), le décalage est réappliqué.
    /// - AfficherTempsRestant ajoute le temps restant au survol du tonneau.
    ///
    /// Multijoueur : seul le propriétaire réseau du tonneau (un joueur à proximité) écrit le ZDO. Si ce
    /// propriétaire n'a pas le mod, le tonneau fermente à la vitesse vanilla, sans incohérence. Pour un effet
    /// garanti, installer le mod chez tous les joueurs ; inutile sur le serveur dédié.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "valheim.quickbrew";
        public const string PluginName = "QuickBrew";
        public const string PluginVersion = "1.0.0";

        internal static ManualLogSource Log;

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> Multiplicateur;
        internal static ConfigEntry<bool> AfficherTempsRestant;

        /// <summary>Dernière valeur de s_startTime écrite par le mod : si elle diffère, le décalage reste à faire.</summary>
        internal static readonly int StartDecaleKey = "QuickBrew.startTimeDecale".GetStableHashCode();

        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;

            Enabled = Config.Bind("General", "Enabled", true, "Active ou désactive le mod.");

            Multiplicateur = Config.Bind("General", "Multiplicateur", 0.025f,
                new ConfigDescription(
                    "Part de la durée vanilla à attendre. Vanilla : 2400 s (40 min). 0.025 = 1 min, 0.25 = 10 min, 0.5 = 20 min, " +
                    "0 = prêt quasi immédiatement, 1 = vanilla. Ne s'applique qu'aux tonneaux remplis après le changement.",
                    new AcceptableValueRange<float>(0f, 1f)));

            AfficherTempsRestant = Config.Bind("General", "AfficherTempsRestant", true,
                "Affiche le temps restant avant que le tonneau soit prêt, au survol.");

            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll(typeof(Plugin).Assembly);

            Log.LogInfo($"{PluginName} {PluginVersion} chargé.");
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
        }

        /// <summary>
        /// Antidate l'heure de départ du tonneau pour qu'il soit prêt après Multiplicateur × durée vanilla.
        /// Ne fait rien hors propriétaire, tonneau vide, ou décalage déjà appliqué à ce départ.
        /// </summary>
        internal static void AppliquerDecalage(Fermenter fermenter)
        {
            if (!Enabled.Value) return;

            ZNetView nview = fermenter.m_nview;
            if (nview == null || !nview.IsValid() || !nview.IsOwner()) return;

            ZDO zdo = nview.GetZDO();
            long depart = zdo.GetLong(ZDOVars.s_startTime, 0L);
            if (depart == 0L || zdo.GetInt(ZDOVars.s_content) == 0) return;
            if (depart == zdo.GetLong(StartDecaleKey, 0L)) return;

            // Au moins 1 s de fermentation : GetStatus exige un temps strictement supérieur à la durée.
            double cible = Math.Max(1.0, fermenter.m_fermentationDuration * Multiplicateur.Value);
            double avance = fermenter.m_fermentationDuration - cible;

            long nouveauDepart = avance > 0.0 ? depart - TimeSpan.FromSeconds(avance).Ticks : depart;
            if (nouveauDepart != depart) zdo.Set(ZDOVars.s_startTime, nouveauDepart);
            zdo.Set(StartDecaleKey, nouveauDepart);
        }

        internal static string FormatDuree(double secondes)
        {
            int total = Math.Max(0, (int)Math.Ceiling(secondes));
            int min = total / 60;
            int sec = total % 60;
            return min > 0 ? $"{min} min {sec:00} s" : $"{sec} s";
        }
    }

    /// <summary>Remplissage : décalage immédiat chez le propriétaire, qui reçoit le RPC.</summary>
    [HarmonyPatch(typeof(Fermenter), nameof(Fermenter.RPC_AddItem))]
    internal static class Fermenter_RPC_AddItem_Patch
    {
        private static void Postfix(Fermenter __instance)
        {
            Plugin.AppliquerDecalage(__instance);
        }
    }

    /// <summary>
    /// Toutes les 2 s : rattrape les tonneaux remplis avant l'installation, ceux dont le chrono a été remis à
    /// zéro (UpdateCover appelle ResetFermentationTimer quand le toit manque), et les changements de propriétaire.
    /// </summary>
    [HarmonyPatch(typeof(Fermenter), nameof(Fermenter.SlowUpdate))]
    internal static class Fermenter_SlowUpdate_Patch
    {
        private static void Postfix(Fermenter __instance)
        {
            Plugin.AppliquerDecalage(__instance);
        }
    }

    /// <summary>Survol : temps restant, lu depuis le ZDO répliqué, donc juste même sans être propriétaire.</summary>
    [HarmonyPatch(typeof(Fermenter), nameof(Fermenter.GetHoverText))]
    internal static class Fermenter_GetHoverText_Patch
    {
        private static void Postfix(Fermenter __instance, ref string __result)
        {
            if (!Plugin.Enabled.Value || !Plugin.AfficherTempsRestant.Value) return;
            if (__instance.m_nview == null || !__instance.m_nview.IsValid()) return;
            if (__instance.GetStatus() != Fermenter.Status.Fermenting) return;
            if (__instance.m_exposed || !__instance.m_hasRoof) return;
            if (!PrivateArea.CheckAccess(__instance.transform.position, 0f, false)) return;

            double restant = __instance.m_fermentationDuration - __instance.GetFermentationTime();
            __result += "\nPrêt dans " + Plugin.FormatDuree(restant);
        }
    }
}
