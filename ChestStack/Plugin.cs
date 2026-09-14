using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ChestStack
{
    /// <summary>
    /// Stores the whole bag into the surrounding chests with a single key press.
    ///
    /// - The game already has the mechanism: Inventory.StackAll only moves an item if the destination
    ///   already holds one of the same kind. The mod reinvents nothing, it calls Container.StackAll() on
    ///   every chest in range instead of the single open chest.
    /// - Container.StackAll() goes through an RPC round trip that requests ownership of the ZDO, refuses if
    ///   another player is browsing the chest and checks access to private chests. It is for that safety
    ///   that this path is taken rather than calling Inventory.StackAll directly, as the game's "Place
    ///   stacks" button does. Bonus: RPC_StackResponse already plays the deposit visual effect on every
    ///   chest that received something, so the on-screen feedback comes for free.
    /// - Consequence: storing does not happen during the key press but later, in RPC_StackResponse.
    ///   The mod waits for the responses (at most ResponseDelay seconds) before showing its summary, and
    ///   meanwhile suppresses the messages each chest wants to show on its own behalf.
    /// - Three ways to trigger, all leading to the same grouped store. The mod's key, which requires
    ///   nothing to be open. The "Place stacks" button of the chest screen. And holding the interact key
    ///   on a chest, which in vanilla already stores then closes. In the last two cases the chest the
    ///   player is looking at has no priority: it becomes one candidate among the others, and an item goes
    ///   to the neighbour if the neighbour is the one already holding some. ExtendGameControls restores
    ///   the vanilla behaviour of both game controls.
    ///
    /// Protections: worn equipment is already spared by StackAll itself, which tests IsItemEquiped.
    /// The hotbar, everything that feeds and meads are added through a Postfix on that same IsItemEquiped, which
    /// answers "equipped" for these items. It is the only per-item test StackAll consults, so the filter
    /// is exact down to the stack, without rewriting StackAll or touching how the inventory is read.
    /// "Everything that feeds" uses the game's own definition (m_food, m_foodStamina, m_foodEitr), the one
    /// that decides whether to show the nutrition panel: raw meat is a material with no nutritional value, so
    /// it does go to the chest. The filtering window is closed by a Finalizer and not by a Postfix, because a
    /// Postfix does not run if the original method throws, which would make food invisible to the rest of
    /// the game for the whole session.
    ///
    /// Multiplayer: only the player who stores needs the mod. Absolute rule: never modify a chest that we
    /// do not own yet, because Container only saves on the owner's side and an unsaved modification is
    /// erased on the next reload. The chest's response often arrives before ownership itself; the deposit
    /// is then postponed until ownership arrives, and abandoned without moving anything if it does not
    /// (details in Container_RPC_StackResponse_Patch). Each chest only reloads its local copy once per
    /// second, so a Load() is forced right before writing.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "valheim.cheststack";
        public const string PluginName = "ChestStack";
        public const string PluginVersion = "1.0.4";

        internal static ManualLogSource Log;

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> Radius;
        internal static ConfigEntry<bool> ProtectHotbar;
        internal static ConfigEntry<bool> ProtectFood;
        internal static ConfigEntry<bool> ProtectMeads;
        internal static ConfigEntry<bool> ExtendGameControls;
        internal static ConfigEntry<bool> ProtectOutsideMod;
        internal static ConfigEntry<float> ResponseDelay;
        internal static ConfigEntry<KeyboardShortcut> StoreKey;

        private Harmony _harmony;

        /// <summary>Every instantiated container, fed by the patch on Container.Awake.</summary>
        private static readonly HashSet<Container> AllContainers = new HashSet<Container>();

        // State of the store in progress. Storing is spread over several frames: N requests are sent,
        // then we wait for N responses or for the guard delay to expire.
        private static bool _storeInProgress;
        private static int _expectedResponses;
        private static int _receivedResponses;
        private static int _storedItems;
        private static int _filledChests;
        private static int _abandoned;
        private static float _waitDeadline;

        /// <summary>
        /// Chests that granted the store but whose ownership has not reached us yet.
        /// Nothing is moved into them while they are here. See Container_RPC_StackResponse_Patch.
        /// </summary>
        private static readonly List<Container> AwaitingOwnership = new List<Container>();

        /// <summary>True while a StackAll whose source is the local player's bag is running.</summary>
        private static bool _filterActive;

        internal static bool StoreInProgress => _storeInProgress;
        internal static bool FilterActive => _filterActive;

        private void Awake()
        {
            Log = Logger;

            // Generated file: BepInEx/config/valheim.cheststack.cfg
            Enabled = Config.Bind("General", "Enabled", true,
                "Enables or disables storing into the surrounding chests.");

            Radius = Config.Bind("General", "Radius", 200f,
                new ConfigDescription(
                    "Maximum distance, in meters, of the chests involved in storing. Chests outside the area " +
                    "the game has loaded around you are never included, whatever this value.",
                    new AcceptableValueRange<float>(2f, 500f)));
            MigrateKey(Radius, "General", "Rayon");

            ProtectHotbar = Config.Bind("General", "ProtectHotbar", true,
                "Leaves the first row of the bag in place, the one bound to keys 1 to 8. Worn equipment is " +
                "already spared by the game itself, there is nothing to configure for it.");
            MigrateKey(ProtectHotbar, "General", "ProtegerBarreAction");

            ProtectFood = Config.Bind("General", "ProtectFood", true,
                "Leaves in place everything that feeds, according to the game's definition (health, stamina " +
                "or eitr). Raw meat and fish are materials with no nutritional value: they go to the chest.");
            MigrateKey(ProtectFood, "General", "ProtegerNourriture");

            ProtectMeads = Config.Bind("General", "ProtectMeads", true,
                "Leaves meads and every other drinkable potion in place (health, stamina, eitr, resistances). " +
                "Mead bases still waiting for the fermenter are materials: they go to the chest.");

            ExtendGameControls = Config.Bind("General", "ExtendGameControls", true,
                "Makes the game's controls act on the whole neighbourhood instead of the single open chest: " +
                "the \"Place stacks\" button and holding the interact key then store into every chest in " +
                "range. When false, they go back to vanilla and only the mod's key stores.");
            MigrateKey(ExtendGameControls, "General", "EtendreControlesJeu");

            ProtectOutsideMod = Config.Bind("General", "ProtectOutsideMod", true,
                "Only has an effect when ExtendGameControls is false: still applies the protections above " +
                "to the game's controls that stayed vanilla.");
            MigrateKey(ProtectOutsideMod, "General", "ProtegerHorsDuMod");

            ResponseDelay = Config.Bind("General", "ResponseDelay", 2f,
                new ConfigDescription(
                    "Waiting time, in seconds, before showing the summary if a chest never responds. " +
                    "Raise it on a remote server with high latency.",
                    new AcceptableValueRange<float>(0.2f, 10f)));
            MigrateKey(ResponseDelay, "General", "DelaiReponse");

            StoreKey = Config.Bind("Controls", "StoreKey",
                new KeyboardShortcut(KeyCode.R, KeyCode.LeftShift),
                "Key that stores the bag into the surrounding chests. It only responds while aiming at a chest " +
                "or with a chest open, which avoids triggering it in the middle of a run. The modifier " +
                "also avoids clashing with ChestCraft's R key.");
            MigrateKey(StoreKey, "Controls", "ToucheRanger");

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

        private void Update()
        {
            if (!Enabled.Value) return;

            if (_storeInProgress)
            {
                TrackStore();
                return;
            }

            if (!StoreKey.Value.IsDown() || !InputFree()) return;

            Container targeted = TargetedChest();
            if (targeted == null) return;

            StartStore(targeted);
        }

        /// <summary>
        /// The chest under the crosshair, or the one whose screen is open. Acts as a guard for the key: the
        /// gesture is "I store here", not "I store wherever I am", which rules out an accidental press while running.
        /// </summary>
        private static Container TargetedChest()
        {
            Player player = Player.m_localPlayer;
            if (player == null) return null;

            if (InventoryGui.instance != null && InventoryGui.instance.m_currentContainer != null)
                return InventoryGui.instance.m_currentContainer;

            GameObject hovered = player.GetHoverObject();
            return hovered != null ? hovered.GetComponentInParent<Container>() : null;
        }

        // ------------------------------------------------------------------------------------------------
        // Storing
        // ------------------------------------------------------------------------------------------------

        /// <summary>
        /// Same locks as RowTogether, minus the one on the inventory: storing from an open chest is precisely
        /// the most common gesture, so the key must respond there.
        /// </summary>
        private static bool InputFree()
        {
            if (Console.IsVisible() || Menu.IsVisible() || TextInput.IsVisible()) return false;
            if (Minimap.IsOpen()) return false;
            if (Chat.instance != null && Chat.instance.HasFocus()) return false;
            return true;
        }

        /// <summary>
        /// Starts a grouped store. <paramref name="origin"/> is the chest the player was looking at when
        /// triggering, added to the list even if it holds nothing matching: it is a chest like the others,
        /// with no priority over its neighbours.
        /// Returns false if nothing was started, in which case the vanilla caller must take over.
        /// </summary>
        internal static bool StartStore(Container origin = null)
        {
            if (_storeInProgress) return false;

            Player player = Player.m_localPlayer;
            if (player == null || player.IsTeleporting()) return false;

            List<Container> chests = ChestsInRange(player);
            if (origin != null && origin.m_nview != null && origin.m_nview.IsValid() &&
                origin.GetInventory() != null && !chests.Contains(origin))
            {
                chests.Add(origin);
            }

            if (chests.Count == 0)
            {
                player.Message(MessageHud.MessageType.Center, "No chest in range");
                return false;
            }

            _storedItems = 0;
            _filledChests = 0;
            _receivedResponses = 0;
            _abandoned = 0;
            AwaitingOwnership.Clear();
            _expectedResponses = chests.Count;
            _waitDeadline = Time.time + Mathf.Max(0.1f, ResponseDelay.Value);
            _storeInProgress = true;

            foreach (Container chest in chests) chest.StackAll();

            Log.LogInfo($"Store requested from {chests.Count} chest(s) within a radius of {Radius.Value:0.#} m.");
            return true;
        }

        /// <summary>
        /// Deposits into the chests whose ownership just arrived, then concludes as soon as every chest has
        /// been processed, or when the guard delay expires. A chest still pending at that point is abandoned
        /// without anything having been moved into it: the items stay in the bag, nothing can be lost.
        /// </summary>
        private static void TrackStore()
        {
            ProcessPending();

            if (_receivedResponses < _expectedResponses && Time.time < _waitDeadline) return;

            _abandoned += AwaitingOwnership.Count;
            AwaitingOwnership.Clear();
            _storeInProgress = false;

            if (_abandoned > 0)
            {
                Log.LogWarning($"{_abandoned} chest(s) abandoned: ownership not received within " +
                               $"{ResponseDelay.Value:0.#} s, nothing was moved into them.");
            }

            Log.LogInfo($"Store finished: {_storedItems} item(s), {_filledChests} chest(s), " +
                        $"{_receivedResponses}/{_expectedResponses} processed, {_abandoned} abandoned.");

            Player player = Player.m_localPlayer;
            if (player == null) return;

            string result = _storedItems > 0
                ? $"{_storedItems} item(s) stored in {_filledChests} chest(s)"
                : "Nothing to store in the chests in range";
            if (_abandoned > 0) result += $"\n{_abandoned} chest(s) unreachable, try again";

            player.Message(MessageHud.MessageType.Center, result);
        }

        private static void ProcessPending()
        {
            for (int i = AwaitingOwnership.Count - 1; i >= 0; i--)
            {
                Container chest = AwaitingOwnership[i];

                if (chest == null || chest.m_nview == null || !chest.m_nview.IsValid())
                {
                    AwaitingOwnership.RemoveAt(i);
                    _receivedResponses++;
                    _abandoned++;
                    continue;
                }

                if (!chest.m_nview.IsOwner()) continue;

                AwaitingOwnership.RemoveAt(i);
                Deposit(chest);
            }
        }

        /// <summary>
        /// What RPC_StackResponse does when the store is granted, replayed once ownership is actually
        /// acquired. message must stay true: when false, StackAll no longer measures the difference and returns
        /// the chest's total content instead of the number of items deposited, which would skew the summary.
        /// The message it emits is suppressed during the store anyway.
        /// </summary>
        private static void Deposit(Container chest)
        {
            _receivedResponses++;

            Player player = Player.m_localPlayer;
            if (player == null) return;

            chest.Load();
            if (chest.GetInventory().StackAll(player.GetInventory(), message: true) > 0 &&
                InventoryGui.instance != null)
            {
                InventoryGui.instance.m_moveItemEffects.Create(chest.transform.position, Quaternion.identity);
            }
        }

        /// <summary>
        /// Chests selected for this store.
        ///
        /// The "chest in use" test is not repeated here: RPC_RequestStack does it on the owner's side, the
        /// only one who knows the answer, and its refusal is already handled as a response.
        ///
        /// Both access checks, however, are repeated. RPC_RequestStack only checks the chest's privacy, not
        /// the ward: in vanilla it is Container.Interact that stops the player in front of a guarded chest,
        /// and this mod does not go through Interact. Without this filter we could deposit into a chest we
        /// are not even allowed to open.
        /// </summary>
        private static List<Container> ChestsInRange(Player player)
        {
            var selected = new List<Container>();
            Vector3 center = player.transform.position;
            float radiusSquared = Radius.Value * Radius.Value;
            long playerID = CurrentPlayerId();

            AllContainers.RemoveWhere(c => c == null);

            foreach (Container chest in AllContainers)
            {
                if (chest.m_nview == null || !chest.m_nview.IsValid()) continue;
                if (chest.GetInventory() == null) continue;
                if ((chest.transform.position - center).sqrMagnitude > radiusSquared) continue;
                if (!AccessAllowed(chest, playerID)) continue;

                selected.Add(chest);
            }

            return selected;
        }

        /// <summary>
        /// The two refusals the player can face in front of a chest: another player's ward, and the
        /// privacy of the chest itself. Used for filtering as well as for display, so as not to offer a
        /// store that would be refused.
        /// </summary>
        internal static bool AccessAllowed(Container chest, long playerID)
        {
            if (chest.m_checkGuardStone &&
                !PrivateArea.CheckAccess(chest.transform.position, 0f, false)) return false;
            return chest.CheckAccess(playerID);
        }

        internal static long CurrentPlayerId()
        {
            return Game.instance != null ? Game.instance.GetPlayerProfile().GetPlayerID() : 0L;
        }

        // ------------------------------------------------------------------------------------------------
        // Key label
        // ------------------------------------------------------------------------------------------------

        private static KeyboardShortcut _keyCache;
        private static string _labelCache;

        /// <summary>
        /// "Shift+R" rather than BepInEx's "R + LeftShift". Only recomputed when the setting changes,
        /// because hovering a chest requests this text every frame.
        /// </summary>
        internal static string StoreKeyLabel()
        {
            KeyboardShortcut current = StoreKey.Value;
            if (_labelCache != null && current.Equals(_keyCache)) return _labelCache;

            string label = "";
            foreach (KeyCode modifier in current.Modifiers) label += KeyName(modifier) + "+";
            label += KeyName(current.MainKey);

            _keyCache = current;
            _labelCache = label;
            return _labelCache;
        }

        private static string KeyName(KeyCode key)
        {
            switch (key)
            {
                case KeyCode.LeftShift:
                case KeyCode.RightShift: return "Shift";
                case KeyCode.LeftControl:
                case KeyCode.RightControl: return "Ctrl";
                case KeyCode.LeftAlt:
                case KeyCode.RightAlt: return "Alt";
                default: return key.ToString();
            }
        }

        // ------------------------------------------------------------------------------------------------
        // Patch hooks
        // ------------------------------------------------------------------------------------------------

        internal static void Register(Container chest)
        {
            if (chest != null) AllContainers.Add(chest);
        }

        internal static void CountResponse()
        {
            if (_storeInProgress) _receivedResponses++;
        }

        internal static void AwaitOwnership(Container chest)
        {
            if (!AwaitingOwnership.Contains(chest)) AwaitingOwnership.Add(chest);
        }

        internal static void RecordResult(int items)
        {
            if (!_storeInProgress || items <= 0) return;
            _storedItems += items;
            _filledChests++;
        }

        /// <summary>
        /// Opens the filtering window if the source really is the local player's bag. Outside a store
        /// triggered by the mod, ProtectOutsideMod decides whether the game's controls benefit from it too.
        /// Returns the state to hand back to the Finalizer.
        /// </summary>
        internal static bool OpenFilter(Inventory source)
        {
            if (!Enabled.Value) return false;

            Player player = Player.m_localPlayer;
            if (player == null || source != player.GetInventory()) return false;
            if (!_storeInProgress && !ProtectOutsideMod.Value) return false;

            _filterActive = true;
            return true;
        }

        internal static void CloseFilter()
        {
            _filterActive = false;
        }

        /// <summary>
        /// What automatic storing must not take away. Worn equipment is not listed here:
        /// StackAll already tests it on its own side.
        /// </summary>
        internal static bool IsProtected(ItemDrop.ItemData item)
        {
            if (item == null) return false;
            if (ProtectHotbar.Value && item.m_gridPos.y == 0) return true;
            if (ProtectFood.Value && IsEdible(item)) return true;
            if (ProtectMeads.Value && IsMead(item)) return true;
            return false;
        }

        /// <summary>
        /// A consumable whose effect is a status effect rather than food: meads and potions. They have no food
        /// value, so IsEdible misses them. Mead bases are materials, so they fail the type test and get stored.
        /// </summary>
        private static bool IsMead(ItemDrop.ItemData item)
        {
            ItemDrop.ItemData.SharedData shared = item.m_shared;
            if (shared == null) return false;
            return shared.m_itemType == ItemDrop.ItemData.ItemType.Consumable && shared.m_consumeStatusEffect != null;
        }

        /// <summary>
        /// The game's own definition, the one that decides whether to show the nutrition panel in the tooltip.
        /// Raw meat is a material with no nutritional value: it is therefore not protected.
        /// </summary>
        private static bool IsEdible(ItemDrop.ItemData item)
        {
            ItemDrop.ItemData.SharedData shared = item.m_shared;
            if (shared == null) return false;
            return shared.m_food > 0f || shared.m_foodStamina > 0f || shared.m_foodEitr > 0f;
        }

        /// <summary>
        /// Each chest wants to announce its own result in the center of the screen. With fifteen chests that
        /// would be fifteen stacked messages; the mod keeps only one, its own, shown once the store concludes.
        /// </summary>
        internal static bool ShouldSuppress(string msg)
        {
            if (!_storeInProgress || msg == null) return false;
            return msg.StartsWith("$msg_stackall", System.StringComparison.Ordinal) || msg == "$msg_inuse";
        }
    }

    // ----------------------------------------------------------------------------------------------------
    // Chest registration
    // ----------------------------------------------------------------------------------------------------

    /// <summary>Every instantiated container registers itself: no physics query needed afterwards.</summary>
    [HarmonyPatch(typeof(Container), nameof(Container.Awake))]
    internal static class Container_Awake_Patch
    {
        private static void Postfix(Container __instance)
        {
            Plugin.Register(__instance);
        }
    }

    // ----------------------------------------------------------------------------------------------------
    // Filtering window and counting
    // ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// The only place where the player's bag empties into a chest. It is wrapped to enable the protections
    /// and to record how many items actually left.
    /// </summary>
    [HarmonyPatch(typeof(Inventory), nameof(Inventory.StackAll))]
    internal static class Inventory_StackAll_Patch
    {
        private static void Prefix(Inventory fromInventory, out bool __state)
        {
            __state = Plugin.OpenFilter(fromInventory);
        }

        private static void Postfix(int __result)
        {
            Plugin.RecordResult(__result);
        }

        /// <summary>Finalizer and not Postfix: an exception must not leave the filter open for the session.</summary>
        private static void Finalizer(bool __state)
        {
            if (__state) Plugin.CloseFilter();
        }
    }

    // ----------------------------------------------------------------------------------------------------
    // Protections
    // ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// StackAll consults IsItemEquiped item by item to decide not to move it. Answering
    /// "equipped" for the hotbar and food is enough to spare them, without rewriting StackAll.
    /// </summary>
    [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.IsItemEquiped))]
    internal static class Humanoid_IsItemEquiped_Patch
    {
        private static void Postfix(Humanoid __instance, ItemDrop.ItemData item, ref bool __result)
        {
            if (__result || !Plugin.FilterActive) return;
            if (__instance != Player.m_localPlayer) return;
            if (Plugin.IsProtected(item)) __result = true;
        }
    }

    // ----------------------------------------------------------------------------------------------------
    // Chest responses
    // ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Every chest responds here, and this is where version 1.0.0 lost items.
    ///
    /// In RPC_RequestStack, the chest's owner does three things in this order: ForceSendZDO,
    /// SetOwner, then sends this response. But ForceSendZDO only queues the ZDO: it is only sent on the
    /// next pass of ZDOMan.SendZDOToPeers2, every 0.05 s, whereas the response goes out immediately on the
    /// socket. When the chest belongs to the server or to another player, the response therefore arrives
    /// before the ZDO that makes us the owner. StackAll then removed the items from the bag and added them
    /// to the chest's local copy, but Container.OnContainerChanged refused to save since IsOwner() was still
    /// false. On the next reload the local copy was overwritten by the ZDO, and the items had vanished on
    /// both sides. If instead the player touched the chest again once owner, the local copy was saved and
    /// the items reappeared: hence the "sometimes".
    ///
    /// The game is not exposed to this, because its held key only stores into an already open chest, hence
    /// already owned. The mod immediately stores into neighbours never opened. A player who already owns
    /// the chests around them never sees the problem, which explains why it only affects one player.
    ///
    /// Fix: as long as ownership is not there, the original response is skipped and the chest is put on
    /// hold. TrackStore replays the deposit as soon as IsOwner() becomes true, or abandons it at the guard delay.
    /// </summary>
    [HarmonyPatch(typeof(Container), nameof(Container.RPC_StackResponse))]
    internal static class Container_RPC_StackResponse_Patch
    {
        private static bool Prefix(Container __instance, bool granted)
        {
            if (!Plugin.StoreInProgress) return true;

            if (!granted || __instance.m_nview == null || !__instance.m_nview.IsValid())
            {
                Plugin.CountResponse();
                return true;
            }

            if (!__instance.m_nview.IsOwner())
            {
                Plugin.AwaitOwnership(__instance);
                return false;
            }

            Plugin.CountResponse();
            __instance.Load();
            return true;
        }
    }

    // ----------------------------------------------------------------------------------------------------
    // Hover display
    // ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Shows the key below the game's two lines, in the same form as them. Nothing is shown if the
    /// chest would refuse the store, so as not to promise a gesture that would fail.
    /// </summary>
    [HarmonyPatch(typeof(Container), nameof(Container.GetHoverText))]
    internal static class Container_GetHoverText_Patch
    {
        private static void Postfix(Container __instance, ref string __result)
        {
            if (!Plugin.Enabled.Value || string.IsNullOrEmpty(__result)) return;
            if (!Plugin.AccessAllowed(__instance, Plugin.CurrentPlayerId())) return;

            __result += $"\n[<color=yellow><b>{Plugin.StoreKeyLabel()}</b></color>] " +
                        "Store in nearby chests";
        }
    }

    // ----------------------------------------------------------------------------------------------------
    // Extending the game's controls
    // ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Entry point of holding the interact key on an open chest. The store is redirected here to the whole
    /// neighbourhood, the targeted chest being only one candidate among the others.
    /// The mod calls this itself for every chest: StoreInProgress then lets the original call through,
    /// otherwise the redirection would call itself endlessly.
    /// </summary>
    [HarmonyPatch(typeof(Container), nameof(Container.StackAll))]
    internal static class Container_StackAll_Patch
    {
        private static bool Prefix(Container __instance)
        {
            if (!Plugin.Enabled.Value || !Plugin.ExtendGameControls.Value) return true;
            if (Plugin.StoreInProgress) return true;
            return !Plugin.StartStore(__instance);
        }
    }

    /// <summary>
    /// The "Place stacks" button of the chest screen. It does not call Container.StackAll but
    /// Inventory.StackAll directly, without requesting ownership of the ZDO: it therefore needs its own
    /// redirection, which along the way makes it take the safer network path.
    /// </summary>
    [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.OnStackAll))]
    internal static class InventoryGui_OnStackAll_Patch
    {
        private static bool Prefix(InventoryGui __instance)
        {
            if (!Plugin.Enabled.Value || !Plugin.ExtendGameControls.Value) return true;
            if (__instance.m_currentContainer == null) return true;
            if (Player.m_localPlayer == null || Player.m_localPlayer.IsTeleporting()) return true;

            __instance.SetupDragItem(null, null, 1);
            return !Plugin.StartStore(__instance.m_currentContainer);
        }
    }

    // ----------------------------------------------------------------------------------------------------
    // Messages
    // ----------------------------------------------------------------------------------------------------

    /// <summary>Suppresses the chests' individual announcements during a grouped store.</summary>
    [HarmonyPatch(typeof(Player), nameof(Player.Message))]
    internal static class Player_Message_Patch
    {
        private static bool Prefix(string msg)
        {
            return !Plugin.ShouldSuppress(msg);
        }
    }
}
