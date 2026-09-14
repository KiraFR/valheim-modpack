using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace FieldPlanter
{
    /// <summary>
    /// Plants a row or a grid of crops in one click with the cultivator.
    ///
    /// - Only pieces carrying a Plant are affected (crops, tree saplings, BerryFarm bushes). Vines, which attach to a
    ///   wall, and the other cultivator tools keep the vanilla single placement.
    /// - The first spot is the vanilla ghost: vanilla places it with all its own checks and costs. The row starts
    ///   there and runs away from the player along the camera's horizontal direction, snapped to SnapAngle so rows
    ///   line up with each other; the grid adds rows to the right.
    /// - Spacing: Plant.HaveGrowSpace fails as soon as any collider of its space mask lies within m_growRadius of the
    ///   plant. The step is therefore m_growRadius plus the horizontal reach of the colliders of the plant and of
    ///   what it grows into (scaled by m_maxScale), plus ExtraSpacing: a neighbour that ripens first must not leave
    ///   the rest of the row stuck on "not enough space".
    /// - Spots are computed in an UpdatePlacementGhost postfix. The game runs that method every LateUpdate and again
    ///   at the start of TryPlacePiece, before the main plant exists, so the preview and the placement read the
    ///   same list.
    /// - Each extra spot repeats the checks of UpdatePlacementGhost and Plant.UpdateHealth that apply to a plant
    ///   (ground, cultivated soil, biome, heat and cold, slope, water, no-build locations, wards, free space).
    ///   Failing spots are skipped rather than cancelling the whole row.
    /// - Costs are vanilla, per plant: seeds, tool durability and skill raise, plus stamina (GetBuildStamina, scaled by
    ///   the world's stamina rate) when FreeStamina is off; by default only the main plant, placed by vanilla, uses it.
    ///   Spots are filled in order (row by row, starting with the ghost's row) until one of
    ///   them runs out, and a message names the missing resource and how many plants were left out.
    /// - Preview ghosts are copies of the vanilla ghost instantiated under an inactive holder, so none of their
    ///   scripts ever wake up (no ZDO, no entry in Piece.s_allPieces), then stripped of every script and collider.
    ///   Through MaterialMan, like the vanilla ghost, a copy turns red when its spot cannot take a plant and yellow
    ///   when a resource runs out before it.
    ///
    /// Client only: every plant is created by the planting player through Player.PlacePiece, like a vanilla plant.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "valheim.fieldplanter";
        public const string PluginName = "FieldPlanter";
        public const string PluginVersion = "1.0.0";

        internal static ManualLogSource Log;

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<PlantMode> Mode;
        internal static ConfigEntry<int> RowLength;
        internal static ConfigEntry<int> GridWidth;
        internal static ConfigEntry<KeyboardShortcut> ModeKey;
        internal static ConfigEntry<KeyCode> LengthModifier;
        internal static ConfigEntry<KeyCode> WidthModifier;
        internal static ConfigEntry<float> ExtraSpacing;
        internal static ConfigEntry<float> SpacingOverride;
        internal static ConfigEntry<float> SnapAngle;
        internal static ConfigEntry<bool> FreeStamina;

        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;

            Enabled = Config.Bind("General", "Enabled", true, "Enables or disables the mod.");

            Mode = Config.Bind("General", "Mode", PlantMode.Row,
                "Single = vanilla, one plant per click. Row = RowLength plants in a line. Grid = RowLength x GridWidth. " +
                "Changed in game with ModeKey.");

            RowLength = Config.Bind("General", "RowLength", 5,
                new ConfigDescription("Plants per row. Changed in game with LengthModifier + mouse wheel.",
                    new AcceptableValueRange<int>(1, Field.MaxSide)));

            GridWidth = Config.Bind("General", "GridWidth", 3,
                new ConfigDescription("Rows in Grid mode. Changed in game with WidthModifier + mouse wheel.",
                    new AcceptableValueRange<int>(1, Field.MaxSide)));

            ModeKey = Config.Bind("Controls", "ModeKey", new KeyboardShortcut(KeyCode.N),
                "Cycles Single / Row / Grid while a plant is selected in the cultivator.");

            LengthModifier = Config.Bind("Controls", "LengthModifier", KeyCode.LeftShift,
                "Hold with the mouse wheel to change the row length (the ghost does not turn meanwhile).");

            WidthModifier = Config.Bind("Controls", "WidthModifier", KeyCode.LeftAlt,
                "Hold with the mouse wheel to change the number of rows in Grid mode.");

            ExtraSpacing = Config.Bind("Spacing", "ExtraSpacing", 0.1f,
                new ConfigDescription(
                    "Metres added to the computed spacing (grow radius + collider size). 0 = as tight as the plants allow.",
                    new AcceptableValueRange<float>(0f, 2f)));

            SpacingOverride = Config.Bind("Spacing", "SpacingOverride", 0f,
                new ConfigDescription(
                    "Fixed distance between plants in metres, for every plant. 0 = computed per plant. Too small a value " +
                    "leaves plants stuck on \"not enough space\".",
                    new AcceptableValueRange<float>(0f, 5f)));

            SnapAngle = Config.Bind("Spacing", "SnapAngle", 45f,
                new ConfigDescription(
                    "The row direction follows the camera, rounded to this angle in degrees so rows stay parallel. 0 = free.",
                    new AcceptableValueRange<float>(0f, 90f)));

            FreeStamina = Config.Bind("Costs", "FreeStamina", true,
                "true = the extra plants of a row or grid cost no stamina (the main plant keeps its vanilla cost). " +
                "false = each plant costs stamina like a vanilla placement, and the field stops when the bar runs out.");

            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll(typeof(Plugin).Assembly);

            Log.LogInfo($"{PluginName} {PluginVersion} loaded.");
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
            Preview.Clear();
        }

        internal static bool InputFree()
        {
            if (Console.IsVisible() || Menu.IsVisible() || TextInput.IsVisible()) return false;
            if (InventoryGui.IsVisible() || Minimap.IsOpen()) return false;
            if (Chat.instance != null && Chat.instance.HasFocus()) return false;
            return true;
        }

        internal static void ShowLayout(Player player)
        {
            string text;
            switch (Mode.Value)
            {
                case PlantMode.Single: text = "Planting: one at a time"; break;
                case PlantMode.Row: text = $"Planting: row of {RowLength.Value}"; break;
                default: text = $"Planting: {RowLength.Value} x {GridWidth.Value} grid"; break;
            }
            player.Message(MessageHud.MessageType.Center, text);
        }
    }

    internal enum PlantMode
    {
        Single,
        Row,
        Grid
    }

    internal enum SpotState
    {
        Plant,
        /// <summary>The spot itself cannot take a plant (soil, biome, space, ward...).</summary>
        Blocked,
        NoSeeds,
        NoStamina,
        NoDurability
    }

    internal struct Spot
    {
        public Vector3 Position;
        public SpotState State;
    }

    /// <summary>The extra spots of the current layout, beside the vanilla ghost, and the rules deciding which get a plant.</summary>
    internal static class Field
    {
        internal const int MaxSide = 10;

        /// <summary>How far above and below the ghost's height the ground is searched at each spot.</summary>
        private const float ProbeUp = 2f;
        private const float ProbeDown = 4f;

        internal static readonly List<Spot> Spots = new List<Spot>();

        /// <summary>Piece the spots were computed for: TryPlacePiece must place that very piece.</summary>
        private static Piece s_spotsPiece;

        /// <summary>Token of the requirement that limits the field (e.g. "$item_carrotseeds"), for the left-out message.</summary>
        private static string s_scarceItem = "";

        private static int s_spaceMask;
        private static readonly Collider[] s_hit = new Collider[1];
        private static readonly Dictionary<string, float> s_reach = new Dictionary<string, float>();

        /// <summary>Same layers as Plant.HaveGrowSpace.</summary>
        private static int SpaceMask
        {
            get
            {
                if (s_spaceMask == 0)
                    s_spaceMask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "piece_nonsolid");
                return s_spaceMask;
            }
        }

        /// <summary>The selected piece's Plant when the mod handles it, otherwise null.</summary>
        internal static Plant RowPlant(Player player)
        {
            if (player == null || !player.InPlaceMode()) return null;
            Piece piece = player.GetSelectedPiece();
            if (piece == null) return null;
            Plant plant = piece.GetComponent<Plant>();
            return plant != null && plant.m_attachDistance <= 0f ? plant : null;
        }

        internal static void Compute(Player player)
        {
            Spots.Clear();
            s_spotsPiece = null;
            if (!Plugin.Enabled.Value || Plugin.Mode.Value == PlantMode.Single) return;

            GameObject ghost = player.m_placementGhost;
            if (ghost == null || !ghost.activeSelf) return;
            Plant plant = RowPlant(player);
            ItemDrop.ItemData tool = player.GetRightItem();
            if (plant == null || tool == null) return;

            int length = Plugin.RowLength.Value;
            int width = Plugin.Mode.Value == PlantMode.Grid ? Plugin.GridWidth.Value : 1;
            if (length * width <= 1) return;

            Piece piece = plant.GetComponent<Piece>();
            Vector3 origin = ghost.transform.position;

            // Vanilla lifts the ghost so its collider rests on the hit point: extra plants keep the same height above ground
            float lift = FindGround(player, origin, origin.y, out RaycastHit originHit) ? origin.y - originHit.point.y : 0f;

            Quaternion facing = RowFacing(player);
            Vector3 forward = facing * Vector3.forward;
            Vector3 right = facing * Vector3.right;
            float step = Step(plant);

            for (int w = 0; w < width; w++)
            {
                for (int l = 0; l < length; l++)
                {
                    if (w == 0 && l == 0) continue;
                    Vector3 flat = origin + forward * (step * l) + right * (step * w);
                    bool valid = CheckSpot(player, piece, plant, flat, origin.y, lift, out Vector3 position);
                    Spots.Add(new Spot { Position = position, State = valid ? SpotState.Plant : SpotState.Blocked });
                }
            }

            Limits(player, piece, tool, out int seeds, out int stamina, out int durability);
            int affordable = 0;
            for (int i = 0; i < Spots.Count; i++)
            {
                if (Spots[i].State != SpotState.Plant) continue;
                SpotState state = CostState(affordable++, seeds, stamina, durability);
                if (state == SpotState.Plant) continue;
                Spot spot = Spots[i];
                spot.State = state;
                Spots[i] = spot;
            }

            s_spotsPiece = piece;
        }

        internal static bool IsOutOfCost(SpotState state)
        {
            return state == SpotState.NoSeeds || state == SpotState.NoStamina || state == SpotState.NoDurability;
        }

        /// <summary>State of the valid spot <paramref name="index"/> (counted in filling order), naming the scarcest resource.</summary>
        private static SpotState CostState(int index, int seeds, int stamina, int durability)
        {
            int limit = Mathf.Min(seeds, Mathf.Min(stamina, durability));
            if (index < limit) return SpotState.Plant;
            if (limit == seeds) return SpotState.NoSeeds;
            return limit == stamina ? SpotState.NoStamina : SpotState.NoDurability;
        }

        /// <summary>
        /// Called after vanilla placed the main plant, before it takes that plant's costs. Places the spots computed by the
        /// UpdatePlacementGhost call at the start of TryPlacePiece, then takes their costs the way UpdatePlacement does.
        /// </summary>
        internal static void PlaceExtras(Player player, Piece piece)
        {
            if (!Plugin.Enabled.Value || piece == null || piece != s_spotsPiece) return;
            ItemDrop.ItemData tool = player.GetRightItem();
            if (tool == null) return;

            bool cheated = (player.m_inventory.ItemCheated(piece.m_resources) || player.NoCostCheat()) &&
                           !PlayerProfile.s_bypassCheatChecks;
            Quaternion ghostRotation = player.m_placementGhost != null ? player.m_placementGhost.transform.rotation : Quaternion.identity;

            int placed = 0;
            int leftOut = 0;
            SpotState reason = SpotState.Plant;
            foreach (Spot spot in Spots)
            {
                if (IsOutOfCost(spot.State))
                {
                    leftOut++;
                    reason = spot.State;
                }
                if (spot.State != SpotState.Plant) continue;

                Quaternion rotation = piece.m_randomInitBuildRotation
                    ? Quaternion.Euler(0f, Random.Range(0, 16) * player.m_placeRotationDegrees, 0f)
                    : ghostRotation;

                Game.instance.IncrementPlayerStat(PlayerStatType.Builds, 1f, cheated);
                Game.instance.GetPlayerProfile().IncrementStatBuildPiecePlaced(piece.m_name, 1f, cheated);
                player.PlacePiece(piece, spot.Position, rotation, doAttack: false, cheated);
                placed++;
            }
            Spots.Clear();
            s_spotsPiece = null;
            if (leftOut > 0)
                player.Message(MessageHud.MessageType.Center, $"{leftOut} {(leftOut == 1 ? "plant" : "plants")} left out: {Shortage(reason)}");
            if (placed == 0) return;

            if (!ZoneSystem.instance.GetGlobalKey(piece.FreeBuildKey()))
                player.ConsumeResources(piece.m_resources, 0, -1, placed);

            if (!Plugin.FreeStamina.Value) player.UseStamina(player.GetBuildStamina() * placed);

            Skills.SkillType skill = player.m_buildPieces.m_skill;
            if (skill != Skills.SkillType.None)
            {
                for (int i = 0; i < placed; i++)
                {
                    if (player.m_buildRemoveDebt > 0) player.m_buildRemoveDebt--;
                    else player.RaiseSkill(skill);
                }
            }

            if (tool.m_shared.m_useDurability)
                tool.m_durability -= player.GetPlaceDurability(tool) * Game.m_durabilityRate * placed;
        }

        /// <summary>
        /// How many extra plants each resource pays for, after the main one, with the same thresholds as UpdatePlacement
        /// (HaveRequirements, then HaveStamina before each placement). Each limit is capped at the number of spots.
        /// </summary>
        private static void Limits(Player player, Piece piece, ItemDrop.ItemData tool, out int seeds, out int stamina,
            out int durability)
        {
            int wanted = Spots.Count;

            seeds = wanted;
            s_scarceItem = "";
            if (!player.m_noPlacementCost && !ZoneSystem.instance.GetGlobalKey(piece.FreeBuildKey()))
            {
                foreach (Piece.Requirement requirement in piece.m_resources)
                {
                    if (requirement.m_resItem == null || requirement.m_amount <= 0) continue;
                    string item = requirement.m_resItem.m_itemData.m_shared.m_name;
                    int paid = Mathf.Max(0, (player.m_inventory.CountItems(item) - requirement.m_amount) / requirement.m_amount);
                    if (paid >= seeds) continue;
                    seeds = paid;
                    s_scarceItem = item;
                }
            }

            // UseStamina scales the cost by the world's stamina rate, HaveStamina compares the unscaled attack stamina
            float cost = player.GetBuildStamina() * Game.m_staminaRate;
            float needed = tool.m_shared.m_attack.m_attackStamina;
            float staminaLeft = player.GetStamina() - cost;
            stamina = 0;
            while (stamina < wanted && (Plugin.FreeStamina.Value || cost <= 0f || staminaLeft > needed))
            {
                staminaLeft -= cost;
                stamina++;
            }

            durability = wanted;
            if (tool.m_shared.m_useDurability)
            {
                float wear = player.GetPlaceDurability(tool) * Game.m_durabilityRate;
                float durabilityLeft = tool.m_durability - wear;
                durability = 0;
                while (durability < wanted && (wear <= 0f || durabilityLeft > 0f))
                {
                    durabilityLeft -= wear;
                    durability++;
                }
            }
        }

        private static string Shortage(SpotState reason)
        {
            switch (reason)
            {
                case SpotState.NoSeeds: return "not enough " + Localization.instance.Localize(s_scarceItem);
                case SpotState.NoStamina: return "not enough stamina";
                default: return "the tool is too worn";
            }
        }

        private static Quaternion RowFacing(Player player)
        {
            Vector3 look = GameCamera.instance != null ? GameCamera.instance.transform.forward : player.transform.forward;
            look.y = 0f;
            // Looking straight down gives no usable heading: the body's facing takes over
            if (look.sqrMagnitude < 0.01f) look = player.transform.forward;

            float yaw = Mathf.Atan2(look.x, look.z) * Mathf.Rad2Deg;
            float snap = Plugin.SnapAngle.Value;
            if (snap > 0f) yaw = Mathf.Round(yaw / snap) * snap;
            return Quaternion.Euler(0f, yaw, 0f);
        }

        private static bool FindGround(Player player, Vector3 at, float referenceY, out RaycastHit hit)
        {
            Vector3 from = new Vector3(at.x, referenceY + ProbeUp, at.z);
            return Physics.Raycast(from, Vector3.down, out hit, ProbeUp + ProbeDown, player.m_placeRayMask);
        }

        private static bool CheckSpot(Player player, Piece piece, Plant plant, Vector3 flat, float referenceY, float lift,
            out Vector3 position)
        {
            position = flat;
            if (!FindGround(player, flat, referenceY, out RaycastHit hit)) return false;
            Vector3 ground = hit.point;
            position = ground + Vector3.up * lift;

            // A hit on anything but terrain (a grown crop, a path, a floor) rules out the pieces that need soil
            Heightmap heightmap = hit.collider.GetComponent<Heightmap>();
            if (heightmap == null)
            {
                if (piece.m_groundOnly || piece.m_groundPiece || piece.m_cultivatedGroundOnly ||
                    piece.m_vegetationGroundOnly || plant.m_needCultivatedGround) return false;
            }
            else
            {
                if ((piece.m_cultivatedGroundOnly || plant.m_needCultivatedGround) && !heightmap.IsCultivated(ground)) return false;

                Heightmap.Biome biome = heightmap.GetBiome(ground);
                if (piece.m_vegetationGroundOnly)
                {
                    float vegetation = heightmap.GetVegetationMask(ground);
                    if (biome == Heightmap.Biome.AshLands ? vegetation > 0.1f : vegetation < 0.25f) return false;
                }

                // Plant.UpdateHealth: vanilla lets these be planted, then the plant never grows
                if ((biome & plant.m_biome) == 0) return false;
                bool shielded = ShieldGenerator.IsInsideShield(ground);
                if (!plant.m_tolerateHeat && biome == Heightmap.Biome.AshLands && !shielded) return false;
                if (!plant.m_tolerateCold && (biome == Heightmap.Biome.DeepNorth || biome == Heightmap.Biome.Mountain) && !shielded)
                    return false;

                if (!piece.m_allowedInDeepSnow && biome == Heightmap.Biome.DeepNorth &&
                    heightmap.GetCultivationMask(ground) > player.m_deepSnowBuildHeight) return false;
            }

            if (piece.m_notOnTiltingSurface && hit.normal.y < 0.8f) return false;
            if (piece.m_noInWater && ground.y < ZoneSystem.instance.m_waterLevel) return false;
            if (piece.m_onlyInBiome != Heightmap.Biome.None && (Heightmap.FindBiome(ground) & piece.m_onlyInBiome) == 0) return false;
            if (Location.IsInsideNoBuildLocation(ground)) return false;
            if (!PrivateArea.CheckAccess(ground, 0f, false, false)) return false;

            // Plant.HaveGrowSpace, stricter: a young or sickly plant counts too, since it may become healthy
            return Physics.OverlapSphereNonAlloc(position, plant.m_growRadius, s_hit, SpaceMask) == 0;
        }

        /// <summary>Distance between two plants of this kind.</summary>
        private static float Step(Plant plant)
        {
            if (Plugin.SpacingOverride.Value > 0f) return Plugin.SpacingOverride.Value;

            if (!s_reach.TryGetValue(plant.name, out float reach))
            {
                reach = 0f;
                string widest = "";
                Reach(plant.gameObject, 1f, plant.m_growRadius, ref reach, ref widest);
                foreach (GameObject grown in plant.m_grownPrefabs)
                {
                    // Plant.Grow scales the grown object by up to m_maxScale
                    if (grown != null) Reach(grown, plant.m_maxScale, plant.m_growRadius, ref reach, ref widest);
                }
                s_reach[plant.name] = reach;
                Plugin.Log.LogInfo($"{plant.name}: grow radius {plant.m_growRadius:0.##} m, collider reach {reach:0.##} m" +
                                   (widest.Length > 0 ? $" ({widest})." : "."));
            }
            return Mathf.Max(0.1f, plant.m_growRadius + reach + Plugin.ExtraSpacing.Value);
        }

        /// <summary>
        /// Raises <paramref name="reach"/> to the horizontal distance from the prefab's root to the far edge of its
        /// farthest solid collider on the space mask, i.e. what a neighbour's HaveGrowSpace sphere may touch. That sphere
        /// is centred on the neighbour's base with m_growRadius, so a collider whose bottom is higher than that (a tree
        /// canopy) can never reach it and is ignored. Read from the collider shapes, since a prefab that is not in the
        /// scene has empty bounds; collider rotations are ignored.
        /// </summary>
        private static void Reach(GameObject prefab, float scale, float growRadius, ref float reach, ref string widest)
        {
            Vector3 root = prefab.transform.position;

            foreach (Collider collider in prefab.GetComponentsInChildren<Collider>(true))
            {
                if (collider.isTrigger || (SpaceMask & (1 << collider.gameObject.layer)) == 0) continue;

                Vector3 lossy = collider.transform.lossyScale * scale;
                float sx = Mathf.Abs(lossy.x);
                float sy = Mathf.Abs(lossy.y);
                float sz = Mathf.Abs(lossy.z);
                Vector3 center;
                float radius;
                float halfHeight;

                switch (collider)
                {
                    case SphereCollider sphere:
                        center = sphere.center;
                        radius = sphere.radius * Mathf.Max(sx, sy, sz);
                        halfHeight = radius;
                        break;
                    case CapsuleCollider capsule:
                        center = capsule.center;
                        radius = capsule.radius * Mathf.Max(sx, sz);
                        if (capsule.direction != 1)
                            radius += Mathf.Max(0f, capsule.height * 0.5f - capsule.radius) * (capsule.direction == 0 ? sx : sz);
                        halfHeight = capsule.direction == 1 ? capsule.height * 0.5f * sy : capsule.radius * sy;
                        break;
                    case BoxCollider box:
                        center = box.center;
                        radius = 0.5f * new Vector2(box.size.x * sx, box.size.z * sz).magnitude;
                        halfHeight = box.size.y * 0.5f * sy;
                        break;
                    case MeshCollider mesh when mesh.sharedMesh != null:
                        Bounds bounds = mesh.sharedMesh.bounds;
                        center = bounds.center;
                        radius = new Vector2(bounds.extents.x * sx, bounds.extents.z * sz).magnitude;
                        halfHeight = bounds.extents.y * sy;
                        break;
                    default:
                        continue;
                }

                Vector3 offset = (collider.transform.TransformPoint(center) - root) * scale;
                if (offset.y - halfHeight > growRadius) continue;

                float edge = new Vector2(offset.x, offset.z).magnitude + radius;
                if (edge <= reach) continue;
                reach = edge;
                widest = $"{collider.GetType().Name} {collider.name} of {prefab.name}";
            }
        }
    }

    /// <summary>Ghost copies shown on the extra spots.</summary>
    internal static class Preview
    {
        private static GameObject s_holder;
        /// <summary>Vanilla ghost the pool was copied from: a new ghost (other plant selected) rebuilds the pool.</summary>
        private static GameObject s_source;
        private static readonly List<GameObject> s_pool = new List<GameObject>();
        /// <summary>Last tint applied to each copy: 0 normal, 1 red, -1 not applied yet.</summary>
        private static readonly List<int> s_tint = new List<int>();

        internal static void Show(Player player)
        {
            GameObject ghost = player.m_placementGhost;
            int count = Field.Spots.Count;
            if (count == 0 || ghost == null)
            {
                Hide(0);
                return;
            }
            if (ghost != s_source)
            {
                Clear();
                s_source = ghost;
            }

            for (int i = 0; i < count; i++)
            {
                if (i == s_pool.Count)
                {
                    s_pool.Add(null);
                    s_tint.Add(-1);
                }
                GameObject copy = s_pool[i];
                if (copy == null)
                {
                    copy = s_pool[i] = Copy(ghost);
                    s_tint[i] = -1;
                }

                Spot spot = Field.Spots[i];
                copy.transform.SetPositionAndRotation(spot.Position, ghost.transform.rotation);
                if (!copy.activeSelf) copy.SetActive(true);

                int tint = spot.State == SpotState.Plant ? 0 : spot.State == SpotState.Blocked ? 1 : 2;
                if (tint != s_tint[i])
                {
                    Tint(copy, tint);
                    s_tint[i] = tint;
                }
            }
            Hide(count);
        }

        private static void Hide(int from)
        {
            for (int i = from; i < s_pool.Count; i++)
            {
                if (s_pool[i] != null && s_pool[i].activeSelf) s_pool[i].SetActive(false);
            }
        }

        internal static void Clear()
        {
            foreach (GameObject copy in s_pool)
            {
                if (copy != null) Object.Destroy(copy);
            }
            s_pool.Clear();
            s_tint.Clear();
            s_source = null;
        }

        private static GameObject Copy(GameObject ghost)
        {
            if (s_holder == null)
            {
                s_holder = new GameObject("FieldPlanter.PreviewHolder");
                s_holder.SetActive(false);
                Object.DontDestroyOnLoad(s_holder);
            }

            // Under an inactive parent no Awake runs, so the copied scripts can be removed before they register anywhere
            GameObject copy = Object.Instantiate(ghost, s_holder.transform, false);
            MonoBehaviour[] scripts = copy.GetComponentsInChildren<MonoBehaviour>(true);
            // Reverse order: a script is removed before the component it requires
            for (int i = scripts.Length - 1; i >= 0; i--) Object.DestroyImmediate(scripts[i]);
            foreach (Collider collider in copy.GetComponentsInChildren<Collider>(true)) collider.enabled = false;

            copy.name = ghost.name + " (FieldPlanter preview)";
            copy.transform.SetParent(null, false);
            return copy;
        }

        /// <summary>0 = normal, 1 = red (the spot cannot take a plant), 2 = yellow (a resource runs out before it).</summary>
        private static void Tint(GameObject copy, int tint)
        {
            if (MaterialMan.instance == null) return;
            if (tint == 0)
            {
                MaterialMan.instance.ResetValue(copy, ShaderProps._Color);
                MaterialMan.instance.ResetValue(copy, ShaderProps._EmissionColor);
                return;
            }
            Color color = tint == 1 ? Color.red : Color.yellow;
            MaterialMan.instance.SetValue(copy, ShaderProps._Color, color);
            MaterialMan.instance.SetValue(copy, ShaderProps._EmissionColor, color * 0.7f);
        }
    }

    /// <summary>Mode key, and the mouse wheel resizing the field while a modifier is held.</summary>
    [HarmonyPatch(typeof(Player), nameof(Player.UpdatePlacement))]
    internal static class Player_UpdatePlacement_Patch
    {
        private const int NoRestore = int.MinValue;
        private static float s_scroll;

        private static void Prefix(Player __instance, bool takeInput, out int __state)
        {
            __state = NoRestore;
            if (!Plugin.Enabled.Value || !takeInput || __instance != Player.m_localPlayer) return;
            if (Hud.IsPieceSelectionVisible() || !Plugin.InputFree()) return;
            if (Field.RowPlant(__instance) == null) return;

            if (Plugin.ModeKey.Value.IsDown())
            {
                Plugin.Mode.Value = (PlantMode)(((int)Plugin.Mode.Value + 1) % 3);
                Plugin.ShowLayout(__instance);
            }
            if (Plugin.Mode.Value == PlantMode.Single) return;

            bool width = Plugin.Mode.Value == PlantMode.Grid && Input.GetKey(Plugin.WidthModifier.Value);
            bool length = !width && Input.GetKey(Plugin.LengthModifier.Value);
            if (!width && !length)
            {
                s_scroll = 0f;
                return;
            }

            // The wheel resizes the field instead of turning the ghost: the postfix puts the rotation back
            __state = __instance.m_placeRotation;
            s_scroll += ZInput.GetMouseScrollWheel();
            if (Mathf.Abs(s_scroll) < __instance.m_scrollAmountThreshold) return;

            ConfigEntry<int> side = width ? Plugin.GridWidth : Plugin.RowLength;
            side.Value = Mathf.Clamp(side.Value + (s_scroll > 0f ? 1 : -1), 1, Field.MaxSide);
            s_scroll = 0f;
            Plugin.ShowLayout(__instance);
        }

        private static void Postfix(Player __instance, int __state)
        {
            if (__state == NoRestore) return;
            __instance.m_placeRotation = __state;
            __instance.m_scrollCurrAmount = 0f;
        }
    }

    /// <summary>Recomputes the spots and moves the preview each time the vanilla ghost moves.</summary>
    [HarmonyPatch(typeof(Player), nameof(Player.UpdatePlacementGhost))]
    internal static class Player_UpdatePlacementGhost_Patch
    {
        private static void Postfix(Player __instance)
        {
            if (__instance != Player.m_localPlayer) return;
            Field.Compute(__instance);
            Preview.Show(__instance);
        }
    }

    /// <summary>The main plant was placed: plants the rest of the field.</summary>
    [HarmonyPatch(typeof(Player), nameof(Player.TryPlacePiece))]
    internal static class Player_TryPlacePiece_Patch
    {
        private static void Postfix(Player __instance, Piece piece, bool __result)
        {
            if (!__result || __instance != Player.m_localPlayer) return;
            Field.PlaceExtras(__instance, piece);
        }
    }
}
