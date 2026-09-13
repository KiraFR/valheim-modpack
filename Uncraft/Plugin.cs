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
    /// Uncraft craftable items at their crafting station to get the materials back.
    ///
    /// - An "Uncraft" tab is added next to "Craft" / "Upgrade" in the crafting panel,
    ///   visible only near a crafting station.
    /// - The list shows the inventory items whose recipe is crafted at the current station (or by
    ///   hand). The right panel shows the materials returned, the button starts the uncraft with the vanilla
    ///   progress bar.
    /// - Materials returned = crafting cost + upgrade costs up to the item's quality,
    ///   multiplied by Ratio. "Any one ingredient" recipes cannot be uncrafted
    ///   (no way to know which ingredient to return).
    /// - Whatever does not fit in the inventory is dropped on the ground in front of the player.
    ///
    /// Multiplayer: everything happens in the local player's inventory, synchronized by the game itself.
    /// Only players who want to uncraft need the mod.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "valheim.uncraft";
        public const string PluginName = "Uncraft";
        public const string PluginVersion = "1.0.1";

        internal static ManualLogSource Log;

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> Ratio;
        internal static ConfigEntry<bool> RequireStationLevel;
        internal static ConfigEntry<bool> AllowRecipesWithoutStation;
        internal static ConfigEntry<bool> OnlyCraftedItems;
        internal static ConfigEntry<bool> UncraftWholeStack;
        internal static ConfigEntry<string> ExcludedItems;

        /// <summary>"Uncraft" tab button, cloned from the "Upgrade" tab when the UI starts.</summary>
        internal static Button TabButton;

        /// <summary>Pending uncraft: started on click, executed when the vanilla progress bar completes.</summary>
        internal static Recipe PendingRecipe;
        internal static ItemDrop.ItemData PendingItem;

        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;

            Enabled = Config.Bind("General", "Enabled", true, "Enables or disables the mod (the tab disappears when disabled).");

            Ratio = Config.Bind("General", "Ratio", 1.0f,
                new ConfigDescription(
                    "Share of materials returned when uncrafting. 1.0 = everything, 0.5 = half (rounded down, " +
                    "a material whose result drops to 0 is not returned).",
                    new AcceptableValueRange<float>(0f, 1f)));

            RequireStationLevel = Config.Bind("General", "RequireStationLevel", true,
                "Requires the same station level as crafting the item at its current quality " +
                "(a quality 3 item needs the level required for upgrade 3).");
            MigrateKey(RequireStationLevel, "General", "ExigerNiveauAtelier");

            AllowRecipesWithoutStation = Config.Bind("General", "AllowRecipesWithoutStation", true,
                "Items craftable by hand (torch, hammer, club...) can be uncrafted at any crafting station.");
            MigrateKey(AllowRecipesWithoutStation, "General", "AutoriserRecettesSansAtelier");

            OnlyCraftedItems = Config.Bind("General", "OnlyCraftedItems", false,
                "Only allows items crafted by a player (refuses loot that was found or bought).");
            MigrateKey(OnlyCraftedItems, "General", "SeulementObjetsFabriques");

            UncraftWholeStack = Config.Bind("General", "UncraftWholeStack", false,
                "Uncrafts the whole stack at once (in recipe batches, e.g. 20 arrows per batch). " +
                "Otherwise a single batch per click.");
            MigrateKey(UncraftWholeStack, "General", "DecrafterToutePile");

            ExcludedItems = Config.Bind("Items", "Excluded", "",
                "Prefab names that can never be uncrafted, comma separated (e.g. Bronze, BlackMetal). " +
                "The names are the ones in BepInEx/config/valheim.stackmax.items.txt if StackMax is installed.");
            MigrateKey(ExcludedItems, "Objets", "Exclus");

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
            _harmony?.UnpatchSelf();
        }

        // ------------------------------------------------------------------------------------------------
        // Uncraft logic
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
            foreach (string s in (ExcludedItems.Value ?? "").Split(','))
            {
                string t = s.Trim();
                if (t.Length > 0) set.Add(t);
            }
            return set;
        }

        /// <summary>Enabled recipe that produces this item, or null.</summary>
        internal static Recipe FindRecipe(ItemDrop.ItemData item)
        {
            if (item == null || ObjectDB.instance == null) return null;
            Recipe recipe = ObjectDB.instance.GetRecipe(item);
            if (recipe == null || !recipe.m_enabled || recipe.m_item == null) return null;
            return recipe;
        }

        /// <summary>Number of batches uncrafted in one click (one batch = m_amount items).</summary>
        internal static int Batches(Recipe recipe, ItemDrop.ItemData item)
        {
            int perBatch = Mathf.Max(1, recipe.m_amount);
            int available = item.m_stack / perBatch;
            if (available <= 0) return 0;
            return UncraftWholeStack.Value ? available : 1;
        }

        /// <summary>
        /// Materials returned: for each resource, crafting cost (level 1) plus upgrade costs
        /// from 2 up to the item's quality, times the number of batches, times Ratio.
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

        /// <summary>Does the current station allow uncrafting this recipe (same station, or hand recipe)?</summary>
        internal static bool StationMatches(Recipe recipe, CraftingStation current)
        {
            CraftingStation required = recipe.m_craftingStation;
            if (required == null) return AllowRecipesWithoutStation.Value;
            return current != null && current.m_name == required.m_name;
        }

        internal static bool CanUncraft(Player player, Recipe recipe, ItemDrop.ItemData item, out string reason)
        {
            reason = "";
            if (!Enabled.Value || player == null || recipe == null || item == null) { reason = "Uncraft unavailable"; return false; }

            if (recipe.m_requireOnlyOneIngredient)
            {
                reason = "Variable ingredient recipe: no way to know what to return";
                return false;
            }
            if (ParseExclusions().Contains(recipe.m_item.name))
            {
                reason = "Item excluded by the configuration";
                return false;
            }
            if (OnlyCraftedItems.Value && item.m_crafterID == 0)
            {
                reason = "This item was not crafted by a player";
                return false;
            }
            if (Batches(recipe, item) <= 0)
            {
                reason = $"At least {recipe.m_amount} copies are needed (one crafting batch)";
                return false;
            }
            if (player.IsItemEquiped(item))
            {
                reason = "Unequip the item first";
                return false;
            }

            CraftingStation current = player.GetCurrentCraftingStation();
            if (!StationMatches(recipe, current))
            {
                reason = recipe.m_craftingStation != null
                    ? "Requires: " + Localization.instance.Localize(recipe.m_craftingStation.m_name)
                    : "Recipe without station: uncraft disabled by the configuration";
                return false;
            }
            if (recipe.m_craftingStation != null && RequireStationLevel.Value)
            {
                int required = recipe.GetRequiredStationLevel(item.m_quality);
                if (current.GetLevel() < required)
                {
                    reason = $"Station level {required} required";
                    return false;
                }
            }
            if (ComputeRefunds(recipe, item.m_quality, 1).Count == 0)
            {
                reason = "No materials to recover";
                return false;
            }
            return true;
        }

        /// <summary>Removes the items, returns the materials, plays the effects and refreshes the panel.</summary>
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
                        // Inventory full: drop on the ground in front of the player rather than losing the materials.
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

            string msg = $"Uncrafted: {itemName} x{removed} → {summary}";
            if (dropped > 0) msg += $" ({dropped} dropped on the ground, inventory full)";
            player.Message(MessageHud.MessageType.TopLeft, msg);
            Log.LogInfo(msg);

            gui.UpdateCraftingPanel();
        }

        // ------------------------------------------------------------------------------------------------
        // User interface
        // ------------------------------------------------------------------------------------------------

        internal static void CreateTab(InventoryGui gui)
        {
            Button source = gui.m_tabUpgrade;
            if (source == null) { Log.LogWarning("Upgrade tab not found, Uncraft tab not created."); return; }

            GameObject go = UnityEngine.Object.Instantiate(source.gameObject, source.transform.parent);
            go.name = "TabUncraft";
            TabButton = go.GetComponent<Button>();

            // Replace the whole event: the persistent listeners copied from the Upgrade tab
            // (OnTabUpgradePressed) cannot be removed by RemoveAllListeners.
            TabButton.onClick = new Button.ButtonClickedEvent();
            TabButton.onClick.AddListener(() => OnTabUncraftPressed(gui));

            foreach (TMP_Text t in go.GetComponentsInChildren<TMP_Text>(true)) t.text = "Uncraft";
            foreach (Text t in go.GetComponentsInChildren<Text>(true)) t.text = "Uncraft";

            RectTransform rt = go.transform as RectTransform;
            RectTransform srcRt = source.transform as RectTransform;
            if (rt != null && srcRt != null)
            {
                rt.anchoredPosition = srcRt.anchoredPosition + new Vector2(srcRt.rect.width + 4f, 0f);
            }

            TabButton.interactable = true;
            go.SetActive(false);
            Log.LogInfo("Uncraft tab created.");
        }

        internal static void OnTabUncraftPressed(InventoryGui gui)
        {
            gui.SetActiveGroup(gui.m_uiGroups[3]);
            gui.m_tabCraft.interactable = true;
            gui.m_tabUpgrade.interactable = true;
            TabButton.interactable = false;
            gui.UpdateCraftingPanel();
        }

        /// <summary>Replaces the recipe list with the uncraftable items from the inventory.</summary>
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

        /// <summary>Shows a returned material in one of the panel's requirement slots.</summary>
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

        /// <summary>Redraws the right panel for the item selected in uncraft mode.</summary>
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
                ? $"Uncraft: returns {Mathf.RoundToInt(Ratio.Value * 100f)}% of the materials"
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
            if (recipe.m_craftingStation != null && RequireStationLevel.Value)
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
            if (buttonText != null) buttonText.text = "Uncraft";
            UITooltip buttonTooltip = gui.m_craftButton.GetComponent<UITooltip>();
            if (buttonTooltip != null) buttonTooltip.m_text = can ? "" : reason;
        }
    }

    /// <summary>Creates the tab when the inventory UI starts.</summary>
    [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.Awake))]
    internal static class InventoryGui_Awake_Patch
    {
        private static void Postfix(InventoryGui __instance)
        {
            try { Plugin.CreateTab(__instance); }
            catch (Exception e) { Plugin.Log.LogError("Failed to create the Uncraft tab: " + e); }
        }
    }

    /// <summary>The vanilla tabs make the Uncraft tab clickable again.</summary>
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

    /// <summary>Tab visibility: only near a crafting station. Back to Craft if the station goes away.</summary>
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

    /// <summary>In uncraft mode, the left list shows the uncraftable items from the inventory.</summary>
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

    /// <summary>In uncraft mode, the right panel shows the materials returned and the Uncraft button.</summary>
    [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.UpdateRecipe))]
    internal static class InventoryGui_UpdateRecipe_Patch
    {
        private static void Postfix(InventoryGui __instance, Player player)
        {
            if (!Plugin.InUncraftTab(__instance)) return;
            Plugin.UpdateUncraftPanel(__instance, player);
        }
    }

    /// <summary>Button click: starts the vanilla progress bar with a pending uncraft.</summary>
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

    /// <summary>End of the progress bar: runs the uncraft instead of the crafting.</summary>
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
            catch (Exception e) { Plugin.Log.LogError("Uncraft failed: " + e); }
            return false;
        }
    }

    /// <summary>Cancel or close: no more pending uncraft.</summary>
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
