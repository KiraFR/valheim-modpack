using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace GearSlots
{
    /// <summary>
    /// Adds dedicated equipment slots (head, chest, legs, shoulders, utility, trinket)
    /// shown as a panel to the right of the inventory.
    ///
    /// How it works: the game can already resize the bag (Player.SetInventorySize, 4 to 9 rows, remembered
    /// in the "invrows" profile key). We add one row, remove it from the visible grid and move its slots
    /// into a separate panel. Items therefore stay real inventory items: no custom serialization, saving and
    /// multiplayer unchanged.
    ///
    /// Each slot of the reserved row accepts only one item type, and an item placed in its slot is
    /// automatically equipped (removed from the slot = unequipped).
    ///
    /// Multiplayer: purely client-side mod, nothing to install on the server. When uninstalled the bag keeps
    /// its 5 rows and the equipment shows up again in a normal row: nothing is lost.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "valheim.gearslots";
        public const string PluginName = "GearSlots";
        public const string PluginVersion = "1.0.1";

        internal static ManualLogSource Log;
        internal static Plugin Instance;

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<int> BagRows;
        internal static ConfigEntry<bool> AutoEquip;
        internal static ConfigEntry<bool> AutoSlot;
        internal static ConfigEntry<bool> ShowLabels;
        internal static ConfigEntry<bool> ShowBackground;
        internal static ConfigEntry<string> BackgroundColor;
        internal static ConfigEntry<float> OffsetX;
        internal static ConfigEntry<float> OffsetY;
        internal static ConfigEntry<float> ColumnSpacing;

        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;
            Instance = this;

            // Generated file: BepInEx/config/valheim.gearslots.cfg
            Enabled = Config.Bind("General", "Enabled", true,
                "Enables or disables the mod. Takes effect after restarting the game.");

            BagRows = Config.Bind("General", "BagRows", 4,
                new ConfigDescription(
                    "Number of rows of the bag itself, excluding the equipment slots. 4 = vanilla size. " +
                    "The equipment row is added on top (4 -> internal inventory of 5 rows). " +
                    "Warning: lowering this value drops the items of the removed rows on the ground.",
                    new AcceptableValueRange<int>(1, 8)));
            MigrateKey(BagRows, "General", "RangeesSac");

            AutoEquip = Config.Bind("General", "AutoEquip", true,
                "An item placed in its slot is equipped automatically, and removing it unequips it.");
            MigrateKey(AutoEquip, "General", "EquipementAuto");

            AutoSlot = Config.Bind("General", "AutoSlot", true,
                "Equipping a piece from the bag automatically moves it into its dedicated slot.");
            MigrateKey(AutoSlot, "General", "RangementAuto");

            ShowLabels = Config.Bind("Display", "ShowLabels", true,
                "Shows the name of each slot above it.");
            MigrateKey(ShowLabels, "Affichage", "AfficherLibelles");

            ShowBackground = Config.Bind("Display", "ShowBackground", true,
                "Shows a solid background behind the equipment panel.");
            MigrateKey(ShowBackground, "Affichage", "AfficherFond");

            BackgroundColor = Config.Bind("Display", "BackgroundColor", "2A1F16FA",
                "Background color, in hexadecimal RRGGBB or RRGGBBAA (AA = opacity, FF = opaque). " +
                "Defaults to a dark brown close to the inventory's wood.");
            MigrateKey(BackgroundColor, "Affichage", "CouleurFond");

            OffsetX = Config.Bind("Display", "OffsetX", 130f,
                new ConfigDescription(
                    "Horizontal offset of the panel, in pixels, from the right edge of the inventory panel.",
                    new AcceptableValueRange<float>(-600f, 600f)));
            MigrateKey(OffsetX, "Affichage", "DecalageX");

            OffsetY = Config.Bind("Display", "OffsetY", 0f,
                new ConfigDescription("Vertical offset of the panel, in pixels.",
                    new AcceptableValueRange<float>(-600f, 600f)));
            MigrateKey(OffsetY, "Affichage", "DecalageY");

            ColumnSpacing = Config.Bind("Display", "ColumnSpacing", 14f,
                new ConfigDescription("Horizontal space between the two slot columns, in pixels.",
                    new AcceptableValueRange<float>(0f, 200f)));
            MigrateKey(ColumnSpacing, "Affichage", "EcartColonnes");

            Config.SettingChanged += OnSettingChanged;

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
            Config.SettingChanged -= OnSettingChanged;
            GearPanel.Destroy();
            _harmony?.UnpatchSelf();
        }

        /// <summary>The requested number of rows changed: the inventory is resized on the fly.</summary>
        private void OnSettingChanged(object sender, SettingChangedEventArgs e)
        {
            if (e.ChangedSetting != BagRows) return;
            GearPanel.ApplyInventorySize(Player.m_localPlayer);
        }

        /// <summary>
        /// Equipment / slots synchronization. Runs from the plugin's Update rather than from a patch
        /// on Player.Update: same frequency, no patch on a very hot game method.
        /// </summary>
        private void Update()
        {
            if (!Enabled.Value || !AutoEquip.Value) return;
            GearPanel.SyncEquipment(Player.m_localPlayer);
        }
    }

    /// <summary>Definition of a slot: its cell in the reserved row and its place in the panel.</summary>
    internal sealed class SlotDef
    {
        public readonly int GridX;
        public readonly string Label;
        public readonly int Column;
        public readonly int Row;
        public readonly ItemDrop.ItemData.ItemType[] Types;

        public SlotDef(int gridX, string label, int column, int row, params ItemDrop.ItemData.ItemType[] types)
        {
            GridX = gridX;
            Label = label;
            Column = column;
            Row = row;
            Types = types;
        }
    }

    /// <summary>Slot table, panel layout and synchronization with the worn equipment.</summary>
    internal static class GearPanel
    {
        /// <summary>Number of inventory rows reserved for the slots. One is enough (6 cells out of 8).</summary>
        internal const int ReservedRows = 1;

        private const string RootName = "GearSlots_Panel";
        private const string LabelName = "GearSlots_Label";
        private const float Padding = 12f;
        private const float LabelBand = 20f;
        private const float RowGap = 8f;

        /// <summary>
        /// Position of each slot. GridX is the column in the inventory's reserved row,
        /// Column/Row the visual position in the panel (two columns, as in the reference screenshot).
        /// </summary>
        internal static readonly SlotDef[] Slots =
        {
            new SlotDef(0, "Head",      0, 0, ItemDrop.ItemData.ItemType.Helmet),
            new SlotDef(1, "Chest",     0, 1, ItemDrop.ItemData.ItemType.Chest),
            new SlotDef(2, "Legs",      0, 2, ItemDrop.ItemData.ItemType.Legs),
            new SlotDef(3, "Shoulders", 1, 0, ItemDrop.ItemData.ItemType.Shoulder),
            new SlotDef(4, "Utility",   1, 1, ItemDrop.ItemData.ItemType.Utility),
            new SlotDef(5, "Trinket",   1, 2, ItemDrop.ItemData.ItemType.Trinket),
        };

        private static RectTransform _root;
        private static Image _background;

        /// <summary>Original parent of the cells, to give them back to the grid if the panel goes away.</summary>
        private static RectTransform _gridRoot;

        private static readonly Color DefaultBackground = new Color(0.165f, 0.122f, 0.086f, 0.98f);
        private static string _colorText;
        private static Color _color = DefaultBackground;

        /// <summary>Configured background color, parsed again only when the config text changes.</summary>
        private static Color BackgroundColor()
        {
            string raw = Plugin.BackgroundColor.Value;
            if (raw == _colorText) return _color;

            _colorText = raw;
            string html = string.IsNullOrEmpty(raw) ? "" : (raw[0] == '#' ? raw : "#" + raw);
            if (!ColorUtility.TryParseHtmlString(html, out _color))
            {
                _color = DefaultBackground;
                Plugin.Log.LogWarning($"Invalid BackgroundColor: \"{raw}\". Expected format RRGGBB or RRGGBBAA.");
            }
            return _color;
        }

        /// <summary>Last item seen in each slot, so that only changes are acted upon.</summary>
        private static readonly ItemDrop.ItemData[] _lastInSlot = new ItemDrop.ItemData[Slots.Length];

        /// <summary>Safeguard: equipping/unequipping triggers our own patches, we do not want recursion.</summary>
        private static bool _busy;

        // ---------------------------------------------------------------- helpers

        internal static bool IsPlayerInventory(Inventory inventory)
        {
            Player player = Player.m_localPlayer;
            return player != null && ReferenceEquals(inventory, player.m_inventory);
        }

        /// <summary>Index of the reserved row, or -1 if the inventory is too small.</summary>
        internal static int ReservedRow(Inventory inventory)
        {
            int row = inventory.GetHeight() - ReservedRows;
            return row >= 1 ? row : -1;
        }

        internal static SlotDef FindSlot(int gridX)
        {
            foreach (SlotDef slot in Slots)
            {
                if (slot.GridX == gridX) return slot;
            }
            return null;
        }

        internal static SlotDef FindSlotForType(ItemDrop.ItemData.ItemType type)
        {
            foreach (SlotDef slot in Slots)
            {
                foreach (ItemDrop.ItemData.ItemType accepted in slot.Types)
                {
                    if (accepted == type) return slot;
                }
            }
            return null;
        }

        /// <summary>Does the slot of column gridX accept this item?</summary>
        internal static bool Accepts(int gridX, ItemDrop.ItemData item)
        {
            SlotDef slot = FindSlot(gridX);
            if (slot == null || item == null) return false;
            foreach (ItemDrop.ItemData.ItemType accepted in slot.Types)
            {
                if (accepted == item.m_shared.m_itemType) return true;
            }
            return false;
        }

        /// <summary>First free cell of the bag, outside the reserved row.</summary>
        internal static bool TryFindBagSlot(Inventory inventory, out Vector2i pos)
        {
            int rows = inventory.GetHeight() - ReservedRows;
            for (int y = 0; y < rows; y++)
            {
                for (int x = 0; x < inventory.GetWidth(); x++)
                {
                    if (inventory.GetItemAt(x, y) == null)
                    {
                        pos = new Vector2i(x, y);
                        return true;
                    }
                }
            }
            pos = new Vector2i(-1, -1);
            return false;
        }

        /// <summary>Free cells of the bag alone, equipment row excluded.</summary>
        internal static int CountBagEmptySlots(Inventory inventory)
        {
            int rows = inventory.GetHeight() - ReservedRows;
            int used = 0;
            foreach (ItemDrop.ItemData item in inventory.GetAllItems())
            {
                if (item.m_gridPos.y < rows) used++;
            }
            return rows * inventory.GetWidth() - used;
        }

        // ---------------------------------------------------------------- inventory size

        /// <summary>Sets the player's inventory to BagRows + reserved row, then cleans up the reserved row.</summary>
        internal static void ApplyInventorySize(Player player)
        {
            if (player == null || !Plugin.Enabled.Value) return;

            int target = Mathf.Clamp(Plugin.BagRows.Value + ReservedRows, 2, 9);
            if (player.m_inventory.GetHeight() != target)
            {
                // SetInventorySize also updates the panel and remembers the size in the profile.
                player.SetInventorySize(target);
                Plugin.Log.LogInfo($"Inventory set to {target} rows ({Plugin.BagRows.Value} bag + {ReservedRows} equipment).");
            }
            else if (InventoryGui.instance != null)
            {
                InventoryGui.instance.SetInventorySize(target);
            }

            ResetCache();
            SweepReservedRow(player);
            TidyEquipped(player);
        }

        /// <summary>
        /// Moves the pieces already worn into their slot. Without this, an existing character would keep
        /// their armor in the bag until they re-equip it by hand.
        /// </summary>
        internal static void TidyEquipped(Player player)
        {
            if (!Plugin.AutoSlot.Value) return;
            foreach (ItemDrop.ItemData item in player.m_inventory.GetEquippedItems())
            {
                MoveToSlot(player, item);
            }
        }

        /// <summary>
        /// Moves out of the reserved row any item that does not belong there: cells without a defined slot
        /// (columns 6 and 7) or an item of the wrong type, left over from an old config or a vanilla game.
        /// </summary>
        internal static void SweepReservedRow(Player player)
        {
            Inventory inventory = player.m_inventory;
            int row = ReservedRow(inventory);
            if (row < 0) return;

            for (int x = 0; x < inventory.GetWidth(); x++)
            {
                ItemDrop.ItemData item = inventory.GetItemAt(x, row);
                if (item == null || Accepts(x, item)) continue;

                if (TryFindBagSlot(inventory, out Vector2i free))
                {
                    item.m_gridPos = free;
                    Plugin.Log.LogInfo($"\"{item.m_shared.m_name}\" moved out of the equipment row.");
                }
                else
                {
                    player.DropItem(inventory, item, item.m_stack);
                    Plugin.Log.LogWarning($"Bag full: \"{item.m_shared.m_name}\" dropped on the ground from the equipment row.");
                }
            }
            inventory.m_onChanged?.Invoke();
        }

        // ---------------------------------------------------------------- equipment

        internal static void ResetCache()
        {
            for (int i = 0; i < _lastInSlot.Length; i++) _lastInSlot[i] = null;
        }

        /// <summary>Equips what just arrived in a slot, unequips what just left one.</summary>
        internal static void SyncEquipment(Player player)
        {
            if (_busy || player == null || player.IsDead() || player.IsTeleporting()) return;

            Inventory inventory = player.m_inventory;
            int row = ReservedRow(inventory);
            if (row < 0) return;

            _busy = true;
            try
            {
                for (int i = 0; i < Slots.Length; i++)
                {
                    ItemDrop.ItemData current = inventory.GetItemAt(Slots[i].GridX, row);
                    ItemDrop.ItemData previous = _lastInSlot[i];
                    if (ReferenceEquals(current, previous)) continue;

                    if (previous != null && player.IsItemEquiped(previous))
                    {
                        player.UnequipItem(previous, triggerEquipEffects: false);
                    }
                    if (current != null && !player.IsItemEquiped(current))
                    {
                        player.EquipItem(current, triggerEquipEffects: false);
                    }
                    _lastInSlot[i] = current;
                }
            }
            finally
            {
                _busy = false;
            }
        }

        /// <summary>Moves a freshly equipped piece into its slot, swapping with the occupant.</summary>
        internal static void MoveToSlot(Player player, ItemDrop.ItemData item)
        {
            if (_busy || player == null || item == null || player.IsDead()) return;

            Inventory inventory = player.m_inventory;
            if (!inventory.ContainsItem(item)) return;

            int row = ReservedRow(inventory);
            if (row < 0) return;

            SlotDef slot = FindSlotForType(item.m_shared.m_itemType);
            if (slot == null) return;
            if (item.m_gridPos.x == slot.GridX && item.m_gridPos.y == row) return;

            _busy = true;
            try
            {
                ItemDrop.ItemData occupant = inventory.GetItemAt(slot.GridX, row);
                if (occupant != null)
                {
                    // The item's old position is only suitable if it is outside the reserved row.
                    Vector2i destination = item.m_gridPos;
                    if (destination.y == row && !TryFindBagSlot(inventory, out destination)) return;

                    if (player.IsItemEquiped(occupant)) player.UnequipItem(occupant, triggerEquipEffects: false);
                    occupant.m_gridPos = destination;
                }

                item.m_gridPos = new Vector2i(slot.GridX, row);
                inventory.m_onChanged?.Invoke();
            }
            finally
            {
                _busy = false;
            }
        }

        /// <summary>Moves a just-unequipped piece out of the panel, so that an occupied slot is always worn.</summary>
        internal static void MoveOutOfSlot(Player player, ItemDrop.ItemData item)
        {
            if (_busy || player == null || item == null || player.IsDead()) return;

            Inventory inventory = player.m_inventory;
            if (!inventory.ContainsItem(item)) return;

            int row = ReservedRow(inventory);
            if (row < 0 || item.m_gridPos.y != row) return;
            if (!TryFindBagSlot(inventory, out Vector2i free)) return;

            _busy = true;
            try
            {
                item.m_gridPos = free;
                for (int i = 0; i < Slots.Length; i++)
                {
                    if (ReferenceEquals(_lastInSlot[i], item)) _lastInSlot[i] = null;
                }
                inventory.m_onChanged?.Invoke();
            }
            finally
            {
                _busy = false;
            }
        }

        // ---------------------------------------------------------------- panel

        /// <summary>
        /// Destroys the panel without taking the cells with it: they are the grid's live InventoryElements,
        /// which the game keeps referencing in InventoryGrid.m_elements.
        /// </summary>
        internal static void Destroy()
        {
            if (_root != null)
            {
                for (int i = _root.childCount - 1; i >= 0; i--)
                {
                    Transform child = _root.GetChild(i);
                    if (_gridRoot != null) child.SetParent(_gridRoot, worldPositionStays: false);
                    else child.SetParent(null, worldPositionStays: false);
                }
                UnityEngine.Object.Destroy(_root.gameObject);
            }
            _root = null;
            _background = null;
        }

        /// <summary>
        /// Moves the cells of the reserved row into a panel to the right of the inventory, and hides the
        /// columns without a slot. The InventoryElements keep their logical position: drag and drop,
        /// tooltips and the "equipped" border keep working as is.
        /// </summary>
        internal static void LayoutPanel(InventoryGrid grid, Inventory inventory)
        {
            int row = ReservedRow(inventory);
            if (row < 0) return;

            int width = inventory.GetWidth();
            int first = row * width;
            if (grid.m_elements.Count < first + width) return;

            InventoryGui gui = InventoryGui.instance;
            if (gui == null || gui.m_player == null) return;

            float space = grid.m_elementSpace;
            float gap = Plugin.ColumnSpacing.Value;
            float pitch = LabelBand + space + RowGap;

            int columns = 1, rows = 1;
            foreach (SlotDef slot in Slots)
            {
                if (slot.Column + 1 > columns) columns = slot.Column + 1;
                if (slot.Row + 1 > rows) rows = slot.Row + 1;
            }

            _gridRoot = grid.m_gridRoot;
            EnsureRoot(gui);
            _root.sizeDelta = new Vector2(
                Padding * 2f + columns * space + (columns - 1) * gap,
                Padding * 2f + rows * pitch - RowGap);
            _root.anchoredPosition = new Vector2(Plugin.OffsetX.Value, Plugin.OffsetY.Value);
            if (_background != null)
            {
                _background.enabled = Plugin.ShowBackground.Value;
                _background.color = BackgroundColor();
            }

            for (int x = 0; x < width; x++)
            {
                InventoryElement element = grid.m_elements[first + x];
                SlotDef slot = FindSlot(x);

                if (slot == null)
                {
                    // Columns without a slot: hidden, and SweepReservedRow guarantees they stay empty.
                    if (element.gameObject.activeSelf) element.gameObject.SetActive(false);
                    continue;
                }

                if (!element.gameObject.activeSelf) element.gameObject.SetActive(true);

                RectTransform rect = (RectTransform)element.transform;
                if (rect.parent != _root)
                {
                    rect.SetParent(_root, worldPositionStays: false);
                    rect.anchorMin = new Vector2(0f, 1f);
                    rect.anchorMax = new Vector2(0f, 1f);
                    rect.pivot = new Vector2(0.5f, 0.5f);
                }

                rect.anchoredPosition = new Vector2(
                    Padding + space * 0.5f + slot.Column * (space + gap),
                    -(Padding + LabelBand + space * 0.5f + slot.Row * pitch));

                UpdateLabel(element, slot, space);
            }

            // The grid must only span the bag rows: undo the enlargement made by UpdateGui.
            grid.m_gridRoot.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, row * space);
        }

        private static void EnsureRoot(InventoryGui gui)
        {
            if (_root != null && _root.parent == gui.m_player) return;

            Destroy();

            var go = new GameObject(RootName, typeof(RectTransform));
            _root = (RectTransform)go.transform;
            _root.SetParent(gui.m_player, worldPositionStays: false);
            _root.anchorMin = new Vector2(1f, 0.5f);
            _root.anchorMax = new Vector2(1f, 0.5f);
            _root.pivot = new Vector2(0f, 0.5f);
            _root.localScale = Vector3.one;

            _background = go.AddComponent<Image>();
            _background.color = BackgroundColor();
            _background.raycastTarget = false;
        }

        /// <summary>
        /// Label above the cell. We clone the "binding" object (the hotbar shortcut number)
        /// to inherit the game's font and style without shipping any asset.
        /// </summary>
        private static void UpdateLabel(InventoryElement element, SlotDef slot, float space)
        {
            Transform existing = element.transform.Find(LabelName);
            if (existing == null)
            {
                Transform binding = element.transform.Find("binding");
                if (binding == null) return;

                GameObject clone = UnityEngine.Object.Instantiate(binding.gameObject, element.transform);
                clone.name = LabelName;
                existing = clone.transform;

                var rect = (RectTransform)existing;
                rect.anchorMin = new Vector2(0.5f, 1f);
                rect.anchorMax = new Vector2(0.5f, 1f);
                rect.pivot = new Vector2(0.5f, 0f);
                rect.anchoredPosition = new Vector2(0f, 3f);
                rect.sizeDelta = new Vector2(space * 1.6f, LabelBand);

                TMP_Text text = clone.GetComponent<TMP_Text>();
                if (text != null)
                {
                    text.text = slot.Label;
                    text.alignment = TextAlignmentOptions.Center;
                    text.color = new Color(0.92f, 0.87f, 0.76f, 1f);
                    text.raycastTarget = false;
                }
            }

            TMP_Text label = existing.GetComponent<TMP_Text>();
            if (label != null) label.enabled = Plugin.ShowLabels.Value;
        }
    }

    // ================================================================== patches

    /// <summary>On spawn, the game applies the size remembered in the profile: we enforce ours right after.</summary>
    [HarmonyPatch(typeof(Player), nameof(Player.OnSpawned))]
    internal static class Player_OnSpawned_Patch
    {
        private static void Postfix(Player __instance)
        {
            if (!Plugin.Enabled.Value) return;
            if (__instance != Player.m_localPlayer) return;
            GearPanel.ApplyInventorySize(__instance);
        }
    }

    /// <summary>The inventory panel must not grow for the reserved row.</summary>
    [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.SetInventorySize))]
    internal static class InventoryGui_SetInventorySize_Patch
    {
        private static void Postfix(InventoryGui __instance, int rows)
        {
            if (!Plugin.Enabled.Value) return;
            float height = __instance.m_playerHeight
                           + (rows - GearPanel.ReservedRows - 4) * __instance.m_invGridHeight;
            __instance.m_player.sizeDelta = new Vector2(__instance.m_player.sizeDelta.x, height);
        }
    }

    /// <summary>After each refresh of the player's grid, the panel cells are repositioned.</summary>
    [HarmonyPatch(typeof(InventoryGrid), nameof(InventoryGrid.UpdateInventory))]
    internal static class InventoryGrid_UpdateInventory_Patch
    {
        private static void Postfix(InventoryGrid __instance, Inventory inventory)
        {
            if (!Plugin.Enabled.Value || inventory == null) return;
            if (InventoryGui.instance == null || __instance != InventoryGui.instance.m_playerGrid) return;
            if (!GearPanel.IsPlayerInventory(inventory)) return;

            try
            {
                GearPanel.LayoutPanel(__instance, inventory);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Could not lay out the equipment panel: {e}");
            }
        }
    }

    /// <summary>A slot only accepts its item type: dropping anything else is blocked.</summary>
    [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.OnSelectedItem))]
    internal static class InventoryGui_OnSelectedItem_Patch
    {
        private static bool Prefix(InventoryGui __instance, InventoryGrid grid, Vector2i pos)
        {
            if (!Plugin.Enabled.Value) return true;
            if (__instance.m_dragGo == null || __instance.m_dragItem == null) return true;
            if (grid == null || grid != __instance.m_playerGrid) return true;

            Inventory inventory = grid.GetInventory();
            if (inventory == null || !GearPanel.IsPlayerInventory(inventory)) return true;
            if (pos.y != GearPanel.ReservedRow(inventory)) return true;
            if (GearPanel.Accepts(pos.x, __instance.m_dragItem)) return true;

            SlotDef slot = GearPanel.FindSlot(pos.x);
            Player.m_localPlayer.Message(MessageHud.MessageType.Center,
                slot != null ? $"Reserved slot: {slot.Label}" : "Unused slot");
            return false;
        }
    }

    /// <summary>Automatic bag filling (pickup, transfer from a chest) skips the reserved row.</summary>
    [HarmonyPatch(typeof(Inventory), nameof(Inventory.FindEmptySlot))]
    internal static class Inventory_FindEmptySlot_Patch
    {
        private static bool Prefix(Inventory __instance, bool topFirst, ref Vector2i __result)
        {
            if (!Plugin.Enabled.Value || !GearPanel.IsPlayerInventory(__instance)) return true;

            int rows = __instance.GetHeight() - GearPanel.ReservedRows;
            int width = __instance.GetWidth();

            if (topFirst)
            {
                for (int y = 0; y < rows; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        if (__instance.GetItemAt(x, y) == null)
                        {
                            __result = new Vector2i(x, y);
                            return false;
                        }
                    }
                }
            }
            else
            {
                for (int y = rows - 1; y >= 0; y--)
                {
                    for (int x = 0; x < width; x++)
                    {
                        if (__instance.GetItemAt(x, y) == null)
                        {
                            __result = new Vector2i(x, y);
                            return false;
                        }
                    }
                }
            }

            __result = new Vector2i(-1, -1);
            return false;
        }
    }

    /// <summary>Equipment cells do not count as free space in the bag.</summary>
    [HarmonyPatch(typeof(Inventory), nameof(Inventory.GetEmptySlots))]
    internal static class Inventory_GetEmptySlots_Patch
    {
        private static bool Prefix(Inventory __instance, ref int __result)
        {
            if (!Plugin.Enabled.Value || !GearPanel.IsPlayerInventory(__instance)) return true;
            __result = GearPanel.CountBagEmptySlots(__instance);
            return false;
        }
    }

    /// <summary>Same for the "is there a free cell left?" check.</summary>
    [HarmonyPatch(typeof(Inventory), nameof(Inventory.HaveEmptySlot))]
    internal static class Inventory_HaveEmptySlot_Patch
    {
        private static bool Prefix(Inventory __instance, ref bool __result)
        {
            if (!Plugin.Enabled.Value || !GearPanel.IsPlayerInventory(__instance)) return true;
            __result = GearPanel.CountBagEmptySlots(__instance) > 0;
            return false;
        }
    }

    /// <summary>A piece equipped from the bag goes to its dedicated slot.</summary>
    [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.EquipItem))]
    internal static class Humanoid_EquipItem_Patch
    {
        private static void Postfix(Humanoid __instance, ItemDrop.ItemData item, bool __result)
        {
            if (!__result || !Plugin.Enabled.Value || !Plugin.AutoSlot.Value) return;
            if (!(__instance is Player player) || player != Player.m_localPlayer) return;
            GearPanel.MoveToSlot(player, item);
        }
    }

    /// <summary>An unequipped piece leaves its slot: an occupied slot is always worn.</summary>
    [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.UnequipItem))]
    internal static class Humanoid_UnequipItem_Patch
    {
        private static void Postfix(Humanoid __instance, ItemDrop.ItemData item)
        {
            if (!Plugin.Enabled.Value || !Plugin.AutoEquip.Value) return;
            if (!(__instance is Player player) || player != Player.m_localPlayer) return;
            GearPanel.MoveOutOfSlot(player, item);
        }
    }
}
