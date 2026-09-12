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
    /// Ajoute des emplacements d'équipement dédiés (tête, torse, jambes, épaules, utilitaire, babiole)
    /// affichés en panneau à droite de l'inventaire.
    ///
    /// Principe : le jeu sait déjà redimensionner le sac (Player.SetInventorySize, 4 à 9 rangées, mémorisé
    /// dans la clé de profil "invrows"). On ajoute une rangée, on la retire de la grille visible et on
    /// repositionne ses cases dans un panneau à part. Les objets restent donc de vrais objets de l'inventaire :
    /// aucune sérialisation maison, sauvegarde et multijoueur inchangés.
    ///
    /// Chaque case de la rangée réservée n'accepte qu'un type d'objet, et un objet posé dans son emplacement
    /// est automatiquement équipé (retiré de l'emplacement = déséquipé).
    ///
    /// Multijoueur : mod purement client, rien à installer sur le serveur. À la désinstallation le sac garde
    /// ses 5 rangées et l'équipement réapparaît dans une rangée normale : rien n'est perdu.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "valheim.gearslots";
        public const string PluginName = "GearSlots";
        public const string PluginVersion = "1.0.0";

        internal static ManualLogSource Log;
        internal static Plugin Instance;

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<int> RangeesSac;
        internal static ConfigEntry<bool> EquipementAuto;
        internal static ConfigEntry<bool> RangementAuto;
        internal static ConfigEntry<bool> AfficherLibelles;
        internal static ConfigEntry<bool> AfficherFond;
        internal static ConfigEntry<string> CouleurFond;
        internal static ConfigEntry<float> DecalageX;
        internal static ConfigEntry<float> DecalageY;
        internal static ConfigEntry<float> EcartColonnes;

        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;
            Instance = this;

            // Fichier généré : BepInEx/config/valheim.gearslots.cfg
            Enabled = Config.Bind("General", "Enabled", true,
                "Active ou désactive le mod. Changement pris en compte au redémarrage du jeu.");

            RangeesSac = Config.Bind("General", "RangeesSac", 4,
                new ConfigDescription(
                    "Nombre de rangées du sac lui-même, hors emplacements d'équipement. 4 = taille vanilla. " +
                    "La rangée d'équipement s'ajoute par-dessus (4 -> inventaire interne de 5 rangées). " +
                    "Attention : réduire cette valeur fait tomber au sol les objets des rangées supprimées.",
                    new AcceptableValueRange<int>(1, 8)));

            EquipementAuto = Config.Bind("General", "EquipementAuto", true,
                "Un objet posé dans son emplacement est équipé automatiquement, et le retirer le déséquipe.");

            RangementAuto = Config.Bind("General", "RangementAuto", true,
                "Équiper une pièce depuis le sac la range automatiquement dans son emplacement dédié.");

            AfficherLibelles = Config.Bind("Affichage", "AfficherLibelles", true,
                "Affiche le nom de chaque emplacement au-dessus de sa case.");

            AfficherFond = Config.Bind("Affichage", "AfficherFond", true,
                "Affiche un fond plein derrière le panneau d'équipement.");

            CouleurFond = Config.Bind("Affichage", "CouleurFond", "2A1F16FA",
                "Couleur du fond, en hexadécimal RRGGBB ou RRGGBBAA (AA = opacité, FF = opaque). " +
                "Par défaut un brun sombre proche du bois de l'inventaire.");

            DecalageX = Config.Bind("Affichage", "DecalageX", 130f,
                new ConfigDescription(
                    "Décalage horizontal du panneau, en pixels, depuis le bord droit du panneau d'inventaire.",
                    new AcceptableValueRange<float>(-600f, 600f)));

            DecalageY = Config.Bind("Affichage", "DecalageY", 0f,
                new ConfigDescription("Décalage vertical du panneau, en pixels.",
                    new AcceptableValueRange<float>(-600f, 600f)));

            EcartColonnes = Config.Bind("Affichage", "EcartColonnes", 14f,
                new ConfigDescription("Espace horizontal entre les deux colonnes d'emplacements, en pixels.",
                    new AcceptableValueRange<float>(0f, 200f)));

            Config.SettingChanged += OnSettingChanged;

            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll(typeof(Plugin).Assembly);

            Log.LogInfo($"{PluginName} {PluginVersion} chargé.");
        }

        private void OnDestroy()
        {
            Config.SettingChanged -= OnSettingChanged;
            GearPanel.Destroy();
            _harmony?.UnpatchSelf();
        }

        /// <summary>Le nombre de rangées demandé change : on redimensionne l'inventaire à chaud.</summary>
        private void OnSettingChanged(object sender, SettingChangedEventArgs e)
        {
            if (e.ChangedSetting != RangeesSac) return;
            GearPanel.ApplyInventorySize(Player.m_localPlayer);
        }

        /// <summary>
        /// Synchronisation équipement / emplacements. Passe par l'Update du plugin plutôt que par un patch
        /// de Player.Update : même fréquence, aucun patch sur une méthode très chaude du jeu.
        /// </summary>
        private void Update()
        {
            if (!Enabled.Value || !EquipementAuto.Value) return;
            GearPanel.SyncEquipment(Player.m_localPlayer);
        }
    }

    /// <summary>Définition d'un emplacement : sa case dans la rangée réservée et sa place dans le panneau.</summary>
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

    /// <summary>Table des emplacements, mise en page du panneau et synchronisation avec l'équipement porté.</summary>
    internal static class GearPanel
    {
        /// <summary>Nombre de rangées de l'inventaire réservées aux emplacements. Une seule suffit (6 cases sur 8).</summary>
        internal const int ReservedRows = 1;

        private const string RootName = "GearSlots_Panel";
        private const string LabelName = "GearSlots_Label";
        private const float Padding = 12f;
        private const float LabelBand = 20f;
        private const float RowGap = 8f;

        /// <summary>
        /// Position de chaque emplacement. GridX est la colonne dans la rangée réservée de l'inventaire,
        /// Column/Row la position visuelle dans le panneau (deux colonnes, comme sur la capture de référence).
        /// </summary>
        internal static readonly SlotDef[] Slots =
        {
            new SlotDef(0, "Tête",       0, 0, ItemDrop.ItemData.ItemType.Helmet),
            new SlotDef(1, "Torse",      0, 1, ItemDrop.ItemData.ItemType.Chest),
            new SlotDef(2, "Jambes",     0, 2, ItemDrop.ItemData.ItemType.Legs),
            new SlotDef(3, "Épaules",    1, 0, ItemDrop.ItemData.ItemType.Shoulder),
            new SlotDef(4, "Utilitaire", 1, 1, ItemDrop.ItemData.ItemType.Utility),
            new SlotDef(5, "Babiole",    1, 2, ItemDrop.ItemData.ItemType.Trinket),
        };

        private static RectTransform _root;
        private static Image _background;

        /// <summary>Parent d'origine des cases, pour les rendre à la grille si le panneau disparaît.</summary>
        private static RectTransform _gridRoot;

        private static readonly Color DefaultBackground = new Color(0.165f, 0.122f, 0.086f, 0.98f);
        private static string _colorText;
        private static Color _color = DefaultBackground;

        /// <summary>Couleur de fond configurée, relue seulement quand le texte de la config change.</summary>
        private static Color BackgroundColor()
        {
            string raw = Plugin.CouleurFond.Value;
            if (raw == _colorText) return _color;

            _colorText = raw;
            string html = string.IsNullOrEmpty(raw) ? "" : (raw[0] == '#' ? raw : "#" + raw);
            if (!ColorUtility.TryParseHtmlString(html, out _color))
            {
                _color = DefaultBackground;
                Plugin.Log.LogWarning($"CouleurFond invalide : \"{raw}\". Format attendu RRGGBB ou RRGGBBAA.");
            }
            return _color;
        }

        /// <summary>Dernier objet vu dans chaque emplacement, pour n'agir que sur les changements.</summary>
        private static readonly ItemDrop.ItemData[] _lastInSlot = new ItemDrop.ItemData[Slots.Length];

        /// <summary>Garde-fou : équiper/déséquiper déclenche nos propres patchs, on ne veut pas de récursion.</summary>
        private static bool _busy;

        // ---------------------------------------------------------------- outils

        internal static bool IsPlayerInventory(Inventory inventory)
        {
            Player player = Player.m_localPlayer;
            return player != null && ReferenceEquals(inventory, player.m_inventory);
        }

        /// <summary>Index de la rangée réservée, ou -1 si l'inventaire est trop petit.</summary>
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

        /// <summary>L'emplacement de la colonne gridX accepte-t-il cet objet ?</summary>
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

        /// <summary>Première case libre du sac, hors rangée réservée.</summary>
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

        /// <summary>Cases libres du sac seul, rangée d'équipement exclue.</summary>
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

        // ---------------------------------------------------------------- taille de l'inventaire

        /// <summary>Porte l'inventaire du joueur à RangeesSac + rangée réservée, puis nettoie la rangée réservée.</summary>
        internal static void ApplyInventorySize(Player player)
        {
            if (player == null || !Plugin.Enabled.Value) return;

            int target = Mathf.Clamp(Plugin.RangeesSac.Value + ReservedRows, 2, 9);
            if (player.m_inventory.GetHeight() != target)
            {
                // SetInventorySize met aussi à jour le panneau et mémorise la taille dans le profil.
                player.SetInventorySize(target);
                Plugin.Log.LogInfo($"Inventaire porté à {target} rangées ({Plugin.RangeesSac.Value} de sac + {ReservedRows} d'équipement).");
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
        /// Range dans leur emplacement les pièces déjà portées. Sans ça, un personnage existant garderait
        /// son armure dans le sac jusqu'à ce qu'il la rééquipe à la main.
        /// </summary>
        internal static void TidyEquipped(Player player)
        {
            if (!Plugin.RangementAuto.Value) return;
            foreach (ItemDrop.ItemData item in player.m_inventory.GetEquippedItems())
            {
                MoveToSlot(player, item);
            }
        }

        /// <summary>
        /// Sort de la rangée réservée tout objet qui n'a rien à y faire : cases sans emplacement défini
        /// (colonnes 6 et 7) ou objet du mauvais type, hérité d'une ancienne config ou d'une partie vanilla.
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
                    Plugin.Log.LogInfo($"\"{item.m_shared.m_name}\" déplacé hors de la rangée d'équipement.");
                }
                else
                {
                    player.DropItem(inventory, item, item.m_stack);
                    Plugin.Log.LogWarning($"Sac plein : \"{item.m_shared.m_name}\" lâché au sol depuis la rangée d'équipement.");
                }
            }
            inventory.m_onChanged?.Invoke();
        }

        // ---------------------------------------------------------------- équipement

        internal static void ResetCache()
        {
            for (int i = 0; i < _lastInSlot.Length; i++) _lastInSlot[i] = null;
        }

        /// <summary>Équipe ce qui vient d'arriver dans un emplacement, déséquipe ce qui vient d'en sortir.</summary>
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

        /// <summary>Range une pièce fraîchement équipée dans son emplacement, en échangeant avec l'occupant.</summary>
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
                    // L'ancienne position de l'objet ne convient que si elle est hors rangée réservée.
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

        /// <summary>Sort du panneau une pièce qu'on vient de déséquiper, pour qu'un emplacement occupé soit toujours porté.</summary>
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

        // ---------------------------------------------------------------- panneau

        /// <summary>
        /// Détruit le panneau sans emporter les cases : ce sont les InventoryElement vivants de la grille,
        /// que le jeu continue de référencer dans InventoryGrid.m_elements.
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
        /// Déplace les cases de la rangée réservée dans un panneau à droite de l'inventaire, et masque les
        /// colonnes sans emplacement. Les InventoryElement gardent leur position logique : le drag & drop,
        /// les tooltips et le liseré « équipé » continuent de fonctionner tels quels.
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
            float gap = Plugin.EcartColonnes.Value;
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
            _root.anchoredPosition = new Vector2(Plugin.DecalageX.Value, Plugin.DecalageY.Value);
            if (_background != null)
            {
                _background.enabled = Plugin.AfficherFond.Value;
                _background.color = BackgroundColor();
            }

            for (int x = 0; x < width; x++)
            {
                InventoryElement element = grid.m_elements[first + x];
                SlotDef slot = FindSlot(x);

                if (slot == null)
                {
                    // Colonnes sans emplacement : masquées, et SweepReservedRow garantit qu'elles restent vides.
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

            // La grille ne doit occuper que les rangées du sac : on annule l'agrandissement fait par UpdateGui.
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
        /// Libellé au-dessus de la case. On clone l'objet "binding" (le numéro de raccourci de la barre rapide)
        /// pour hériter de la police et du style du jeu sans embarquer d'asset.
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
            if (label != null) label.enabled = Plugin.AfficherLibelles.Value;
        }
    }

    // ================================================================== patchs

    /// <summary>Au spawn, le jeu applique la taille mémorisée dans le profil : on impose la nôtre juste après.</summary>
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

    /// <summary>Le panneau d'inventaire ne doit pas s'agrandir pour la rangée réservée.</summary>
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

    /// <summary>Après chaque rafraîchissement de la grille du joueur, on replace les cases du panneau.</summary>
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
                Plugin.Log.LogError($"Mise en page du panneau d'équipement impossible : {e}");
            }
        }
    }

    /// <summary>Un emplacement n'accepte que son type d'objet : on bloque le dépôt sinon.</summary>
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
                slot != null ? $"Emplacement réservé : {slot.Label}" : "Emplacement inutilisé");
            return false;
        }
    }

    /// <summary>Le remplissage automatique du sac (ramassage, transfert depuis un coffre) ignore la rangée réservée.</summary>
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

    /// <summary>Les cases d'équipement ne comptent pas comme de la place libre dans le sac.</summary>
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

    /// <summary>Idem pour le test « reste-t-il une case libre ? ».</summary>
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

    /// <summary>Une pièce équipée depuis le sac rejoint son emplacement dédié.</summary>
    [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.EquipItem))]
    internal static class Humanoid_EquipItem_Patch
    {
        private static void Postfix(Humanoid __instance, ItemDrop.ItemData item, bool __result)
        {
            if (!__result || !Plugin.Enabled.Value || !Plugin.RangementAuto.Value) return;
            if (!(__instance is Player player) || player != Player.m_localPlayer) return;
            GearPanel.MoveToSlot(player, item);
        }
    }

    /// <summary>Une pièce déséquipée quitte son emplacement : un emplacement occupé est toujours porté.</summary>
    [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.UnequipItem))]
    internal static class Humanoid_UnequipItem_Patch
    {
        private static void Postfix(Humanoid __instance, ItemDrop.ItemData item)
        {
            if (!Plugin.Enabled.Value || !Plugin.EquipementAuto.Value) return;
            if (!(__instance is Player player) || player != Player.m_localPlayer) return;
            GearPanel.MoveOutOfSlot(player, item);
        }
    }
}
