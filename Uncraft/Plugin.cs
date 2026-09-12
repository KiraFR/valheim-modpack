using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Uncraft
{
    /// <summary>
    /// Décrafter les objets fabricables dans leur atelier pour récupérer les matériaux.
    ///
    /// - Un onglet "Décrafter" est ajouté à côté de "Fabriquer" / "Améliorer" dans le panneau d'artisanat,
    ///   visible seulement près d'un atelier.
    /// - La liste montre les objets de l'inventaire dont la recette se fabrique à l'atelier courant (ou à la
    ///   main). Le panneau de droite affiche les matériaux rendus, le bouton lance le décraft avec la barre
    ///   de progression vanilla.
    /// - Matériaux rendus = coût de fabrication + coûts d'amélioration jusqu'à la qualité de l'objet,
    ///   multipliés par Ratio. Les recettes "un seul ingrédient au choix" ne sont pas décraftables
    ///   (impossible de savoir quel ingrédient rendre).
    /// - Ce qui ne tient pas dans l'inventaire est déposé au sol devant le joueur.
    ///
    /// Multijoueur : tout se passe dans l'inventaire du joueur local, synchronisé par le jeu lui-même.
    /// Seuls les joueurs qui veulent décrafter ont besoin du mod.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "valheim.uncraft";
        public const string PluginName = "Uncraft";
        public const string PluginVersion = "1.0.0";

        internal static ManualLogSource Log;

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> Ratio;
        internal static ConfigEntry<bool> ExigerNiveauAtelier;
        internal static ConfigEntry<bool> AutoriserRecettesSansAtelier;
        internal static ConfigEntry<bool> SeulementObjetsFabriques;
        internal static ConfigEntry<bool> DecrafterToutePile;
        internal static ConfigEntry<string> ObjetsExclus;

        /// <summary>Bouton d'onglet "Décrafter", cloné depuis l'onglet "Améliorer" au démarrage de l'interface.</summary>
        internal static Button TabButton;

        /// <summary>Décraft en attente : lancé au clic, exécuté quand la barre de progression vanilla se termine.</summary>
        internal static Recipe PendingRecipe;
        internal static ItemDrop.ItemData PendingItem;

        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;

            Enabled = Config.Bind("General", "Enabled", true, "Active ou désactive le mod (l'onglet disparaît si désactivé).");

            Ratio = Config.Bind("General", "Ratio", 1.0f,
                new ConfigDescription(
                    "Part des matériaux rendus au décraft. 1.0 = tout, 0.5 = la moitié (arrondi à l'inférieur, " +
                    "un matériau dont le résultat tombe à 0 n'est pas rendu).",
                    new AcceptableValueRange<float>(0f, 1f)));

            ExigerNiveauAtelier = Config.Bind("General", "ExigerNiveauAtelier", true,
                "Exige le même niveau d'atelier que pour fabriquer l'objet à sa qualité actuelle " +
                "(un objet qualité 3 demande le niveau requis pour l'amélioration 3).");

            AutoriserRecettesSansAtelier = Config.Bind("General", "AutoriserRecettesSansAtelier", true,
                "Les objets fabricables à la main (torche, marteau, massue...) peuvent être décraftés dans n'importe quel atelier.");

            SeulementObjetsFabriques = Config.Bind("General", "SeulementObjetsFabriques", false,
                "N'autorise que les objets fabriqués par un joueur (refuse le butin trouvé ou acheté).");

            DecrafterToutePile = Config.Bind("General", "DecrafterToutePile", false,
                "Décrafte toute la pile d'un coup (par lots de la recette, ex : 20 flèches par lot). " +
                "Sinon un seul lot par clic.");

            ObjetsExclus = Config.Bind("Objets", "Exclus", "",
                "Noms de prefab jamais décraftables, séparés par des virgules (ex : Bronze, BlackMetal). " +
                "Les noms sont ceux de BepInEx/config/valheim.stackmax.objets.txt si StackMax est installé.");

            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll(typeof(Plugin).Assembly);

            Log.LogInfo($"{PluginName} {PluginVersion} chargé.");
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
        }

        // ------------------------------------------------------------------------------------------------
        // Logique de décraft
        // ------------------------------------------------------------------------------------------------

        internal sealed class Refund
        {
            public ItemDrop Item;
            public int Amount;
        }

        internal static bool InUncraftTab(InventoryGui gui)
        {
            return Enabled.Value && TabButton != null && TabButton.gameObject.activeSelf && !TabButton.interactable;
        }

        internal static HashSet<string> ParseExclusions()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string s in (ObjetsExclus.Value ?? "").Split(','))
            {
                string t = s.Trim();
                if (t.Length > 0) set.Add(t);
            }
            return set;
        }

        /// <summary>Recette activée qui produit cet objet, ou null.</summary>
        internal static Recipe FindRecipe(ItemDrop.ItemData item)
        {
            if (item == null || ObjectDB.instance == null) return null;
            Recipe recipe = ObjectDB.instance.GetRecipe(item);
            if (recipe == null || !recipe.m_enabled || recipe.m_item == null) return null;
            return recipe;
        }

        /// <summary>Nombre de lots décraftés en un clic (un lot = m_amount objets).</summary>
        internal static int Batches(Recipe recipe, ItemDrop.ItemData item)
        {
            int perBatch = Mathf.Max(1, recipe.m_amount);
            int available = item.m_stack / perBatch;
            if (available <= 0) return 0;
            return DecrafterToutePile.Value ? available : 1;
        }

        /// <summary>
        /// Matériaux rendus : pour chaque ressource, coût de fabrication (niveau 1) plus coûts d'amélioration
        /// de 2 jusqu'à la qualité de l'objet, fois le nombre de lots, fois Ratio.
        /// </summary>
        internal static List<Refund> ComputeRefunds(Recipe recipe, int quality, int batches)
        {
            var list = new List<Refund>();
            foreach (Piece.Requirement req in recipe.m_resources)
            {
                if (req.m_resItem == null || req.m_upgraderResource) continue;
                int total = 0;
                for (int q = 1; q <= Mathf.Max(1, quality); q++) total += req.GetAmount(q);
                int refund = Mathf.FloorToInt(total * batches * Ratio.Value);
                if (refund > 0) list.Add(new Refund { Item = req.m_resItem, Amount = refund });
            }
            return list;
        }

        /// <summary>L'atelier courant permet-il de décrafter cette recette (même atelier, ou recette à la main) ?</summary>
        internal static bool StationMatches(Recipe recipe, CraftingStation current)
        {
            CraftingStation required = recipe.m_craftingStation;
            if (required == null) return AutoriserRecettesSansAtelier.Value;
            return current != null && current.m_name == required.m_name;
        }

        internal static bool CanUncraft(Player player, Recipe recipe, ItemDrop.ItemData item, out string reason)
        {
            reason = "";
            if (!Enabled.Value || player == null || recipe == null || item == null) { reason = "Décraft indisponible"; return false; }

            if (recipe.m_requireOnlyOneIngredient)
            {
                reason = "Recette à ingrédient variable : impossible de savoir quoi rendre";
                return false;
            }
            if (ParseExclusions().Contains(recipe.m_item.name))
            {
                reason = "Objet exclu par la configuration";
                return false;
            }
            if (SeulementObjetsFabriques.Value && item.m_crafterID == 0)
            {
                reason = "Cet objet n'a pas été fabriqué par un joueur";
                return false;
            }
            if (Batches(recipe, item) <= 0)
            {
                reason = $"Il faut au moins {recipe.m_amount} exemplaires (un lot de fabrication)";
                return false;
            }
            if (player.IsItemEquiped(item))
            {
                reason = "Déséquipez l'objet d'abord";
                return false;
            }

            CraftingStation current = player.GetCurrentCraftingStation();
            if (!StationMatches(recipe, current))
            {
                reason = recipe.m_craftingStation != null
                    ? "Nécessite : " + Localization.instance.Localize(recipe.m_craftingStation.m_name)
                    : "Recette sans atelier : décraft désactivé par la configuration";
                return false;
            }
            if (recipe.m_craftingStation != null && ExigerNiveauAtelier.Value)
            {
                int required = recipe.GetRequiredStationLevel(item.m_quality);
                if (current.GetLevel() < required)
                {
                    reason = $"Niveau d'atelier {required} requis";
                    return false;
                }
            }
            if (ComputeRefunds(recipe, item.m_quality, 1).Count == 0)
            {
                reason = "Aucun matériau à récupérer";
                return false;
            }
            return true;
        }

        /// <summary>Retire les objets, rend les matériaux, joue les effets et rafraîchit le panneau.</summary>
        internal static void DoUncraft(InventoryGui gui, Player player, Recipe recipe, ItemDrop.ItemData item)
        {
            Inventory inv = player.GetInventory();
            if (!inv.ContainsItem(item)) return;

            if (!CanUncraft(player, recipe, item, out string reason))
            {
                player.Message(MessageHud.MessageType.Center, reason);
                return;
            }

            int batches = Batches(recipe, item);
            int removed = batches * Mathf.Max(1, recipe.m_amount);
            List<Refund> refunds = ComputeRefunds(recipe, item.m_quality, batches);
            string itemName = Localization.instance.Localize(item.m_shared.m_name);

            inv.RemoveItem(item, removed);

            var summary = new StringBuilder();
            int dropped = 0;
            foreach (Refund refund in refunds)
            {
                GameObject prefab = refund.Item.gameObject;
                int maxStack = Mathf.Max(1, refund.Item.m_itemData.m_shared.m_maxStackSize);
                int left = refund.Amount;
                while (left > 0)
                {
                    int chunk = Mathf.Min(left, maxStack);
                    if (inv.CanAddItem(prefab, chunk))
                    {
                        inv.AddItem(prefab, chunk);
                    }
                    else
                    {
                        // Inventaire plein : on dépose au sol devant le joueur plutôt que de perdre les matériaux.
                        ItemDrop.ItemData data = refund.Item.m_itemData.Clone();
                        data.m_dropPrefab = prefab;
                        Vector3 pos = player.transform.position + player.transform.forward + Vector3.up;
                        ItemDrop.DropItem(data, chunk, pos, player.transform.rotation);
                        dropped += chunk;
                    }
                    left -= chunk;
                }
                if (summary.Length > 0) summary.Append(", ");
                summary.Append(refund.Amount).Append(' ').Append(Localization.instance.Localize(refund.Item.m_itemData.m_shared.m_name));
            }

            CraftingStation station = player.GetCurrentCraftingStation();
            if (station != null) station.m_craftItemDoneEffects.Create(player.transform.position, Quaternion.identity);
            else gui.m_craftItemDoneEffects.Create(player.transform.position, Quaternion.identity);

            string msg = $"Décrafté : {itemName} x{removed} → {summary}";
            if (dropped > 0) msg += $" ({dropped} déposé(s) au sol, inventaire plein)";
            player.Message(MessageHud.MessageType.TopLeft, msg);
            Log.LogInfo(msg);

            gui.UpdateCraftingPanel();
        }

        // ------------------------------------------------------------------------------------------------
        // Interface
        // ------------------------------------------------------------------------------------------------

        internal static void CreateTab(InventoryGui gui)
        {
            Button source = gui.m_tabUpgrade;
            if (source == null) { Log.LogWarning("Onglet Améliorer introuvable, onglet Décrafter non créé."); return; }

            GameObject go = UnityEngine.Object.Instantiate(source.gameObject, source.transform.parent);
            go.name = "TabUncraft";
            TabButton = go.GetComponent<Button>();

            // Remplace l'événement entier : les écouteurs persistants copiés de l'onglet Améliorer
            // (OnTabUpgradePressed) ne peuvent pas être retirés par RemoveAllListeners.
            TabButton.onClick = new Button.ButtonClickedEvent();
            TabButton.onClick.AddListener(() => OnTabUncraftPressed(gui));

            foreach (TMP_Text t in go.GetComponentsInChildren<TMP_Text>(true)) t.text = "Décrafter";
            foreach (Text t in go.GetComponentsInChildren<Text>(true)) t.text = "Décrafter";

            RectTransform rt = go.transform as RectTransform;
            RectTransform srcRt = source.transform as RectTransform;
            if (rt != null && srcRt != null)
            {
                rt.anchoredPosition = srcRt.anchoredPosition + new Vector2(srcRt.rect.width + 4f, 0f);
            }

            TabButton.interactable = true;
            go.SetActive(false);
            Log.LogInfo("Onglet Décrafter créé.");
        }

        internal static void OnTabUncraftPressed(InventoryGui gui)
        {
            gui.SetActiveGroup(gui.m_uiGroups[3]);
            gui.m_tabCraft.interactable = true;
            gui.m_tabUpgrade.interactable = true;
            TabButton.interactable = false;
            gui.UpdateCraftingPanel();
        }

        /// <summary>Remplace la liste de recettes par les objets décraftables de l'inventaire.</summary>
        internal static void BuildUncraftList(InventoryGui gui)
        {
            Player player = Player.m_localPlayer;
            foreach (InventoryGui.RecipeDataPair pair in gui.m_availableRecipes)
            {
                UnityEngine.Object.Destroy(pair.InterfaceElement);
            }
            gui.m_availableRecipes.Clear();

            if (player != null)
            {
                CraftingStation current = player.GetCurrentCraftingStation();
                var rows = new List<Tuple<string, Recipe, ItemDrop.ItemData>>();
                foreach (ItemDrop.ItemData item in player.GetInventory().GetAllItems())
                {
                    Recipe recipe = FindRecipe(item);
                    if (recipe == null || !StationMatches(recipe, current)) continue;
                    rows.Add(Tuple.Create(Localization.instance.Localize(item.m_shared.m_name), recipe, item));
                }
                foreach (var row in rows.OrderBy(r => r.Item1).ThenBy(r => r.Item3.m_quality))
                {
                    bool can = CanUncraft(player, row.Item2, row.Item3, out _);
                    gui.AddRecipeToList(player, row.Item2, row.Item3, can);
                }
            }

            float height = Mathf.Max(gui.m_recipeListBaseSize, gui.m_availableRecipes.Count * gui.m_recipeListSpace);
            gui.m_recipeListRoot.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, height);
        }

        /// <summary>Affiche un matériau rendu dans un emplacement de prérequis du panneau.</summary>
        internal static void SetupRefund(Transform root, Refund refund)
        {
            Image icon = root.Find("res_icon").GetComponent<Image>();
            TMP_Text name = root.Find("res_name").GetComponent<TMP_Text>();
            TMP_Text amount = root.Find("res_amount").GetComponent<TMP_Text>();
            string label = Localization.instance.Localize(refund.Item.m_itemData.m_shared.m_name);

            icon.gameObject.SetActive(true);
            name.gameObject.SetActive(true);
            amount.gameObject.SetActive(true);
            icon.sprite = refund.Item.m_itemData.GetIcon();
            icon.color = Color.white;
            name.text = label;
            amount.text = "+" + refund.Amount;
            amount.color = new Color(0.6f, 1f, 0.6f);
            UITooltip tooltip = root.GetComponent<UITooltip>();
            if (tooltip != null) tooltip.m_text = label;
        }

        /// <summary>Repeint le panneau de droite pour l'objet sélectionné en mode décraft.</summary>
        internal static void UpdateUncraftPanel(InventoryGui gui, Player player)
        {
            Recipe recipe = gui.m_selectedRecipe.Recipe;
            ItemDrop.ItemData item = gui.m_selectedRecipe.ItemData;
            if (recipe == null || item == null) return;

            bool can = CanUncraft(player, recipe, item, out string reason);
            int batches = Mathf.Max(1, Batches(recipe, item));
            int removed = batches * Mathf.Max(1, recipe.m_amount);
            string name = Localization.instance.Localize(item.m_shared.m_name);

            gui.m_recipeName.text = removed > 1 ? $"{name} x{removed}" : name;
            gui.m_recipeDecription.text = Localization.instance.Localize(
                ItemDrop.ItemData.GetTooltip(item, item.m_quality, true, Game.m_worldLevel, removed));
            gui.m_variantButton.gameObject.SetActive(false);

            gui.m_itemCraftType.gameObject.SetActive(true);
            gui.m_itemCraftType.text = can
                ? $"Décrafter : rend {Mathf.RoundToInt(Ratio.Value * 100f)}% des matériaux"
                : reason;

            List<Refund> refunds = ComputeRefunds(recipe, item.m_quality, batches);
            GameObject[] slots = gui.m_recipeRequirementList;
            int offset = 0;
            if (refunds.Count > slots.Length)
            {
                int pages = Mathf.CeilToInt(refunds.Count / (float)slots.Length);
                offset = (int)Time.fixedTime % pages * slots.Length;
            }
            int shown = 0;
            for (int k = offset; k < refunds.Count && shown < slots.Length; k++, shown++)
            {
                SetupRefund(slots[shown].transform, refunds[k]);
            }
            for (; shown < slots.Length; shown++)
            {
                InventoryGui.HideRequirement(slots[shown].transform);
            }

            CraftingStation current = player.GetCurrentCraftingStation();
            if (recipe.m_craftingStation != null && ExigerNiveauAtelier.Value)
            {
                int required = recipe.GetRequiredStationLevel(item.m_quality);
                gui.m_minStationLevelIcon.gameObject.SetActive(true);
                gui.m_minStationLevelText.text = required.ToString();
                bool ok = current != null && current.GetLevel() >= required;
                gui.m_minStationLevelText.color = ok || Mathf.Sin(Time.time * 10f) <= 0f ? gui.m_minStationLevelBasecolor : Color.red;
            }
            else
            {
                gui.m_minStationLevelIcon.gameObject.SetActive(false);
            }

            gui.m_craftButton.interactable = can;
            TMP_Text buttonText = gui.m_craftButton.GetComponentInChildren<TMP_Text>();
            if (buttonText != null) buttonText.text = "Décrafter";
            UITooltip buttonTooltip = gui.m_craftButton.GetComponent<UITooltip>();
            if (buttonTooltip != null) buttonTooltip.m_text = can ? "" : reason;
        }
    }

    /// <summary>Création de l'onglet au démarrage de l'interface d'inventaire.</summary>
    [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.Awake))]
    internal static class InventoryGui_Awake_Patch
    {
        private static void Postfix(InventoryGui __instance)
        {
            try { Plugin.CreateTab(__instance); }
            catch (Exception e) { Plugin.Log.LogError("Création de l'onglet Décrafter échouée : " + e); }
        }
    }

    /// <summary>Les onglets vanilla rendent l'onglet Décrafter cliquable à nouveau.</summary>
    [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.OnTabCraftPressed))]
    internal static class InventoryGui_OnTabCraftPressed_Patch
    {
        private static void Prefix()
        {
            if (Plugin.TabButton != null) Plugin.TabButton.interactable = true;
        }
    }

    [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.OnTabUpgradePressed))]
    internal static class InventoryGui_OnTabUpgradePressed_Patch
    {
        private static void Prefix()
        {
            if (Plugin.TabButton != null) Plugin.TabButton.interactable = true;
        }
    }

    /// <summary>Visibilité de l'onglet : seulement près d'un atelier. Retour à Fabriquer si l'atelier disparaît.</summary>
    [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.UpdateCraftingPanel))]
    internal static class InventoryGui_UpdateCraftingPanel_Patch
    {
        private static void Prefix(InventoryGui __instance)
        {
            if (Plugin.TabButton == null) return;
            Player player = Player.m_localPlayer;
            bool show = Plugin.Enabled.Value && player != null && player.GetCurrentCraftingStation() != null;
            Plugin.TabButton.gameObject.SetActive(show);
            if (!show && !Plugin.TabButton.interactable)
            {
                Plugin.TabButton.interactable = true;
                __instance.m_tabCraft.interactable = false;
                __instance.m_tabUpgrade.interactable = true;
            }
        }
    }

    /// <summary>En mode décraft, la liste de gauche montre les objets décraftables de l'inventaire.</summary>
    [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.UpdateRecipeList))]
    internal static class InventoryGui_UpdateRecipeList_Patch
    {
        private static bool Prefix(InventoryGui __instance)
        {
            if (!Plugin.InUncraftTab(__instance)) return true;
            Plugin.BuildUncraftList(__instance);
            return false;
        }
    }

    /// <summary>En mode décraft, le panneau de droite montre les matériaux rendus et le bouton Décrafter.</summary>
    [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.UpdateRecipe))]
    internal static class InventoryGui_UpdateRecipe_Patch
    {
        private static void Postfix(InventoryGui __instance, Player player)
        {
            if (!Plugin.InUncraftTab(__instance)) return;
            Plugin.UpdateUncraftPanel(__instance, player);
        }
    }

    /// <summary>Clic sur le bouton : lance la barre de progression vanilla avec un décraft en attente.</summary>
    [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.OnCraftPressed))]
    internal static class InventoryGui_OnCraftPressed_Patch
    {
        private static bool Prefix(InventoryGui __instance)
        {
            if (!Plugin.InUncraftTab(__instance)) return true;

            Recipe recipe = __instance.m_selectedRecipe.Recipe;
            ItemDrop.ItemData item = __instance.m_selectedRecipe.ItemData;
            Player player = Player.m_localPlayer;
            if (recipe == null || item == null || player == null) return false;

            if (!Plugin.CanUncraft(player, recipe, item, out string reason))
            {
                player.Message(MessageHud.MessageType.Center, reason);
                return false;
            }

            __instance.SetActiveGroup(__instance.m_uiGroups[3]);
            Plugin.PendingRecipe = recipe;
            Plugin.PendingItem = item;
            __instance.m_craftRecipe = recipe;
            __instance.m_craftUpgradeItem = item;
            __instance.m_multiCrafting = false;
            __instance.m_craftTimer = 0f;

            CraftingStation station = player.GetCurrentCraftingStation();
            if (station != null) station.m_craftItemEffects.Create(player.transform.position, Quaternion.identity);
            else __instance.m_craftItemEffects.Create(player.transform.position, Quaternion.identity);
            return false;
        }
    }

    /// <summary>Fin de la barre de progression : exécute le décraft à la place de la fabrication.</summary>
    [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.DoCrafting))]
    internal static class InventoryGui_DoCrafting_Patch
    {
        private static bool Prefix(InventoryGui __instance, Player player)
        {
            if (Plugin.PendingRecipe == null) return true;
            Recipe recipe = Plugin.PendingRecipe;
            ItemDrop.ItemData item = Plugin.PendingItem;
            Plugin.PendingRecipe = null;
            Plugin.PendingItem = null;
            try { Plugin.DoUncraft(__instance, player, recipe, item); }
            catch (Exception e) { Plugin.Log.LogError("Décraft échoué : " + e); }
            return false;
        }
    }

    /// <summary>Annulation ou fermeture : plus de décraft en attente.</summary>
    [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.OnCraftCancelPressed))]
    internal static class InventoryGui_OnCraftCancelPressed_Patch
    {
        private static void Postfix()
        {
            Plugin.PendingRecipe = null;
            Plugin.PendingItem = null;
        }
    }

    [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.Hide))]
    internal static class InventoryGui_Hide_Patch
    {
        private static void Postfix()
        {
            Plugin.PendingRecipe = null;
            Plugin.PendingItem = null;
        }
    }
}
