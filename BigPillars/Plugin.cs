using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace BigPillars
{
    /// <summary>
    /// Adds build pieces made of 3 or 5 copies (Counts) of an existing piece laid end to end: by default the medium Grausten pillar
    /// (stacked vertically) and the medium Grausten beam (in a row along its length), to raise a tall pillar or a long
    /// beam in one placement instead of several.
    ///
    /// - The new pieces are real prefabs ("&lt;source&gt;_x3", "&lt;source&gt;_x5"), cloned from the vanilla ones when ZNetScene wakes up:
    ///   every direct child of the source (meshes, colliders, snap points) is copied into each segment and the segments are
    ///   shifted along the chosen axis, keeping the pivot at the same relative spot. Children that WearNTear toggles
    ///   (new / worn / broken / wet) are extended from the inside so damage shows on the whole piece.
    /// - Cost and health are N x the source. Removing the piece gives back N x the materials.
    /// - The clones are added to the build table holding their source (the hammer), right after it.
    ///
    /// Multiplayer: pieces are ZDOs referencing the clone's prefab name, so every player needs the mod, and the dedicated
    /// server too (it instantiates the pieces around the spawn it owns). Without the mod, those pieces are only a
    /// "missing prefab" warning: they are kept in the world and come back once the mod is installed again.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "valheim.bigpillars";
        public const string PluginName = "BigPillars";
        public const string PluginVersion = "1.0.0";

        internal static ManualLogSource Log;

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<string> Pieces;
        internal static ConfigEntry<string> Counts;

        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;

            Enabled = Config.Bind("General", "Enabled", true,
                "Shows the x3 / x5 pieces in the build menu. When off, pieces already built stay in the world (their prefabs " +
                "are still registered), they just can no longer be placed.");

            // Both medium Grausten pieces are 1 m cubes, so their axis cannot be guessed: the beam is the pillar mesh
            // turned a quarter around Z, its length runs along X.
            Pieces = Config.Bind("General", "Pieces", "Piece_grausten_pillarbase_medium:y, Piece_grausten_pillarbeam_medium:x",
                "Comma-separated prefab names of the pieces to offer in x3 / x5 (see Counts). Optional ':x', ':y' or ':z' after a name forces " +
                "the axis the copies are laid along (local to the piece); without it, the piece's longest side is used. " +
                "Removing a name hides its pieces but also removes the prefab: pieces already built with it disappear " +
                "from view (they stay saved in the world). Applied on the next world load.");
            // The first default had no axis for the beam, which then stacked vertically.
            if (Pieces.Value.Trim() == "Piece_grausten_pillarbase_medium:y, Piece_grausten_pillarbeam_medium")
                Pieces.Value = (string)Pieces.DefaultValue;

            Counts = Config.Bind("General", "Counts", "3, 5",
                "Comma-separated numbers of copies: one piece is offered per number and per entry of Pieces (2 to 10). " +
                "The number is part of the prefab name (\"<name>_x5\"), so removing one makes pieces already built with it " +
                "disappear from view like removing a name from Pieces. Applied on the next world load.");

            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll(typeof(Plugin).Assembly);

            Log.LogInfo($"{PluginName} {PluginVersion} loaded.");
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
        }

        internal static IEnumerable<int> ParseCounts()
        {
            var counts = new List<int>();
            foreach (string raw in Counts.Value.Split(','))
            {
                string entry = raw.Trim();
                if (entry.Length == 0) continue;
                if (int.TryParse(entry, out int count) && count >= 2 && count <= 10)
                {
                    if (!counts.Contains(count)) counts.Add(count);
                }
                else Log.LogWarning($"Invalid count '{entry}' in Counts (2 to 10), skipped.");
            }
            counts.Sort();
            return counts;
        }

        internal static IEnumerable<(string name, int axis)> ParsePieces()
        {
            foreach (string raw in Pieces.Value.Split(','))
            {
                string entry = raw.Trim();
                if (entry.Length == 0) continue;

                int axis = -1;
                int colon = entry.IndexOf(':');
                if (colon >= 0)
                {
                    string axisName = entry.Substring(colon + 1).Trim().ToLowerInvariant();
                    entry = entry.Substring(0, colon).Trim();
                    axis = axisName == "x" ? 0 : axisName == "y" ? 1 : axisName == "z" ? 2 : -1;
                    if (axis < 0) Log.LogWarning($"Unknown axis '{axisName}' for {entry}, using its longest side.");
                }
                yield return (entry, axis);
            }
        }
    }

    /// <summary>Builds the xN prefabs. They live under an inactive holder so they never wake up on their own.</summary>
    internal static class PieceBuilder
    {
        private static GameObject _holder;
        private static readonly Dictionary<string, GameObject> Built = new Dictionary<string, GameObject>();

        internal static GameObject GetOrBuild(GameObject source, int forcedAxis, int segments)
        {
            string name = source.name + "_x" + segments;
            if (Built.TryGetValue(name, out GameObject existing) && existing != null) return existing;

            if (_holder == null)
            {
                _holder = new GameObject("BigPillars_Prefabs");
                _holder.SetActive(false);
                UnityEngine.Object.DontDestroyOnLoad(_holder);
            }

            GameObject clone = UnityEngine.Object.Instantiate(source, _holder.transform, false);
            clone.name = name;
            try
            {
                Extend(clone, forcedAxis, segments);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Could not build {name}: {e}");
                UnityEngine.Object.DestroyImmediate(clone);
                return null;
            }

            Built[name] = clone;
            return clone;
        }

        private static void Extend(GameObject root, int forcedAxis, int segments)
        {
            Transform rt = root.transform;
            MoveRootComponentsToChild(root);

            // Length and axis from the snap points (the building grid of the piece), meshes as a fallback.
            Bounds bounds = SnapBounds(rt, out int snapCount);
            if (snapCount < 2) bounds = MeshBounds(rt);
            Vector3 size = bounds.size;
            int axis = forcedAxis;
            if (axis < 0)
            {
                axis = 1;
                if (size.x > size[axis] * 1.01f) axis = 0;
                if (size.z > size[axis] * 1.01f) axis = 2;
                for (int other = 0; other < 3; other++)
                {
                    if (other != axis && size[other] >= size[axis] * 0.99f)
                    {
                        Plugin.Log.LogWarning($"{root.name}: no side is clearly the longest, copies stacked along " +
                            $"{"xyz"[axis]}. Add ':x', ':y' or ':z' after its name in Pieces to choose.");
                        break;
                    }
                }
            }
            float length = size[axis];
            if (length < 0.05f) throw new InvalidOperationException($"piece too thin along axis {axis} ({length} m)");

            Vector3 dir = Vector3.zero;
            dir[axis] = 1f;
            // Keeps the pivot at the same fraction of the piece: a pivot at the bottom stays at the bottom, a centered one stays centered.
            float fraction = Mathf.Clamp01(-bounds.min[axis] / length);
            float baseShift = -(segments - 1) * fraction * length;

            WearNTear wnt = root.GetComponent<WearNTear>();
            var containers = new HashSet<Transform>();
            if (wnt != null)
            {
                foreach (GameObject go in new[] { wnt.m_new, wnt.m_worn, wnt.m_broken, wnt.m_wet })
                    if (go != null) containers.Add(go.transform);
            }

            var rendererCopies = new Dictionary<Renderer, List<Renderer>>();
            ExtendChildren(rt, rt, dir * length, dir * baseShift, segments, containers, rendererCopies);

            RemoveDuplicateSnapPoints(rt);
            ExtendLods(root, rendererCopies);

            Piece piece = root.GetComponent<Piece>();
            if (piece != null)
            {
                piece.m_name = piece.m_name + " x" + segments;
                foreach (Piece.Requirement req in piece.m_resources) req.m_amount *= segments;
            }
            if (wnt != null) wnt.m_health *= segments;
        }

        /// <summary>
        /// Copies each child of <paramref name="parent"/> into the other segments. Children WearNTear shows or hides as a whole
        /// are not copied themselves: their own children are, so each damage state covers every segment.
        /// </summary>
        private static void ExtendChildren(Transform root, Transform parent, Vector3 step, Vector3 shift, int segments,
            HashSet<Transform> containers, Dictionary<Renderer, List<Renderer>> rendererCopies)
        {
            Vector3 localStep = parent.InverseTransformVector(root.TransformVector(step));
            Vector3 localShift = parent.InverseTransformVector(root.TransformVector(shift));

            List<Transform> children = parent.Cast<Transform>().ToList();
            foreach (Transform child in children)
            {
                if (containers.Contains(child))
                {
                    ExtendChildren(root, child, step, shift, segments, containers, rendererCopies);
                    continue;
                }

                Renderer[] originals = child.GetComponentsInChildren<Renderer>(true);
                for (int i = 1; i < segments; i++)
                {
                    Transform copy = UnityEngine.Object.Instantiate(child.gameObject, parent, false).transform;
                    copy.name = child.name;
                    copy.localPosition = child.localPosition + localShift + localStep * i;

                    // Same component order in the copy: pairs each renderer with its copy for the LOD groups.
                    Renderer[] copies = copy.GetComponentsInChildren<Renderer>(true);
                    for (int r = 0; r < originals.Length && r < copies.Length; r++)
                    {
                        if (!rendererCopies.TryGetValue(originals[r], out List<Renderer> list))
                            rendererCopies[originals[r]] = list = new List<Renderer>();
                        list.Add(copies[r]);
                    }
                }
                child.localPosition += localShift;
            }
        }

        /// <summary>Meshes and colliders sitting on the root itself cannot be copied, so they move to a child first.</summary>
        private static void MoveRootComponentsToChild(GameObject root)
        {
            MeshFilter filter = root.GetComponent<MeshFilter>();
            MeshRenderer renderer = root.GetComponent<MeshRenderer>();
            Collider[] colliders = root.GetComponents<Collider>();
            if (renderer == null && colliders.Length == 0) return;

            var body = new GameObject("body");
            body.layer = root.layer;
            body.transform.SetParent(root.transform, false);

            if (renderer != null)
            {
                body.AddComponent<MeshFilter>().sharedMesh = filter != null ? filter.sharedMesh : null;
                MeshRenderer r = body.AddComponent<MeshRenderer>();
                r.sharedMaterials = renderer.sharedMaterials;
                r.shadowCastingMode = renderer.shadowCastingMode;
                r.receiveShadows = renderer.receiveShadows;
                UnityEngine.Object.DestroyImmediate(renderer);
                if (filter != null) UnityEngine.Object.DestroyImmediate(filter);
            }

            foreach (Collider c in colliders)
            {
                Collider moved;
                switch (c)
                {
                    case BoxCollider b:
                        var nb = body.AddComponent<BoxCollider>(); nb.center = b.center; nb.size = b.size; moved = nb; break;
                    case SphereCollider s:
                        var ns = body.AddComponent<SphereCollider>(); ns.center = s.center; ns.radius = s.radius; moved = ns; break;
                    case CapsuleCollider k:
                        var nk = body.AddComponent<CapsuleCollider>(); nk.center = k.center; nk.radius = k.radius;
                        nk.height = k.height; nk.direction = k.direction; moved = nk; break;
                    case MeshCollider m:
                        var nm = body.AddComponent<MeshCollider>(); nm.sharedMesh = m.sharedMesh; nm.convex = m.convex; moved = nm; break;
                    default:
                        Plugin.Log.LogWarning($"{root.name}: collider {c.GetType().Name} on the root is not copied.");
                        continue;
                }
                moved.isTrigger = c.isTrigger;
                moved.sharedMaterial = c.sharedMaterial;
                UnityEngine.Object.DestroyImmediate(c);
            }
        }

        private static Bounds SnapBounds(Transform root, out int count)
        {
            count = 0;
            var bounds = new Bounds();
            foreach (Transform child in root)
            {
                if (!child.CompareTag("snappoint")) continue;
                Vector3 p = root.InverseTransformPoint(child.position);
                if (count++ == 0) bounds = new Bounds(p, Vector3.zero);
                else bounds.Encapsulate(p);
            }
            return bounds;
        }

        private static Bounds MeshBounds(Transform root)
        {
            bool any = false;
            var bounds = new Bounds();
            foreach (MeshFilter f in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (f.sharedMesh == null) continue;
                Bounds b = f.sharedMesh.bounds;
                for (int i = 0; i < 8; i++)
                {
                    var corner = new Vector3((i & 1) == 0 ? b.min.x : b.max.x, (i & 2) == 0 ? b.min.y : b.max.y, (i & 4) == 0 ? b.min.z : b.max.z);
                    Vector3 p = root.InverseTransformPoint(f.transform.TransformPoint(corner));
                    if (!any) { bounds = new Bounds(p, Vector3.zero); any = true; }
                    else bounds.Encapsulate(p);
                }
            }
            return bounds;
        }

        /// <summary>Where two segments meet, both bring a snap point at the same spot: keeps only one.</summary>
        private static void RemoveDuplicateSnapPoints(Transform root)
        {
            var kept = new List<Vector3>();
            foreach (Transform child in root.Cast<Transform>().ToList())
            {
                if (!child.CompareTag("snappoint")) continue;
                Vector3 p = child.localPosition;
                if (kept.Any(k => (k - p).sqrMagnitude < 0.0001f)) UnityEngine.Object.DestroyImmediate(child.gameObject);
                else kept.Add(p);
            }
        }

        /// <summary>A LOD group on the root only knows the first segment's renderers: adds their copies to the same LOD levels.</summary>
        private static void ExtendLods(GameObject root, Dictionary<Renderer, List<Renderer>> rendererCopies)
        {
            if (rendererCopies.Count == 0) return;
            foreach (LODGroup group in root.GetComponentsInChildren<LODGroup>(true))
            {
                LOD[] lods = group.GetLODs();
                bool changed = false;
                for (int i = 0; i < lods.Length; i++)
                {
                    var renderers = new List<Renderer>(lods[i].renderers);
                    foreach (Renderer r in lods[i].renderers)
                    {
                        if (r != null && rendererCopies.TryGetValue(r, out List<Renderer> copies))
                        {
                            // A LOD group inside a copied child was copied with it and already points at its own
                            // renderers; only a group above the segments (root, damage state) must take the copies.
                            foreach (Renderer c in copies)
                                if (c.transform.IsChildOf(group.transform)) renderers.Add(c);
                        }
                    }
                    if (renderers.Count != lods[i].renderers.Length)
                    {
                        lods[i].renderers = renderers.ToArray();
                        changed = true;
                    }
                }
                if (changed) group.SetLODs(lods);
            }
        }
    }

    /// <summary>
    /// Registers the xN prefabs before ZNetScene indexes its prefabs (so the network can spawn them), and puts them in
    /// the build table of their source, right after it.
    /// </summary>
    [HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.Awake))]
    internal static class ZNetSceneAwakePatch
    {
        private static void Prefix(ZNetScene __instance)
        {
            var tables = __instance.m_prefabs
                .Select(p => p != null ? p.GetComponent<ItemDrop>() : null)
                .Where(i => i != null && i.m_itemData?.m_shared?.m_buildPieces != null)
                .Select(i => i.m_itemData.m_shared.m_buildPieces)
                .Distinct()
                .ToList();

            List<int> counts = Plugin.ParseCounts().ToList();
            foreach ((string name, int axis) in Plugin.ParsePieces())
            {
                GameObject source = __instance.m_prefabs.FirstOrDefault(p => p != null && p.name == name);
                if (source == null || source.GetComponent<Piece>() == null)
                {
                    Plugin.Log.LogWarning($"Piece '{name}' not found, skipped.");
                    continue;
                }

                // Inserted in increasing count right after the source: source, x3, x5...
                GameObject previous = source;
                foreach (int count in counts)
                {
                    GameObject clone = PieceBuilder.GetOrBuild(source, axis, count);
                    if (clone == null) continue;

                    if (!__instance.m_prefabs.Contains(clone)) __instance.m_prefabs.Add(clone);

                    foreach (PieceTable table in tables)
                    {
                        if (!table.m_pieces.Contains(source)) continue;
                        table.m_pieces.Remove(clone);
                        if (Plugin.Enabled.Value) table.m_pieces.Insert(table.m_pieces.IndexOf(previous) + 1, clone);
                    }
                    previous = clone;
                    Plugin.Log.LogInfo($"Registered {clone.name}.");
                }
            }
        }
    }
}
