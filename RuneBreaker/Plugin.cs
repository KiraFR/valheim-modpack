using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace RuneBreaker
{
    /// <summary>
    /// Rune stones (lore stones) and Vegvisirs can be broken with a pickaxe.
    ///
    /// - These stones are not networked objects: they are part of a location prefab that every peer instantiates
    ///   locally from a LocationProxy (ZoneSystem.SpawnProxyLocation), with only the ZNetView children spawned as
    ///   real objects. Destroying one therefore does nothing on its own: it would come back on the next zone load
    ///   and other players would still see it.
    /// - The proxy itself is networked and saved. Each stone is identified by its index among the location's
    ///   RuneStone then Vegvisir components (includeInactive, so the order is the same on every peer), and broken
    ///   stones are stored as a bit mask in the proxy's ZDO. Every time the location is spawned, stones whose bit is
    ///   set are destroyed right away.
    /// - A BreakableStone component (IDestructible) is added to each stone: Attack finds it through
    ///   Projectile.FindHitObject (GetComponentInParent), so pickaxe hits reach it like any rock. Its health is local
    ///   to the player hitting it. When it breaks, that player drops the stone items and sends an RPC to everybody on
    ///   the proxy: each peer plays the destruction effect and removes its own copy, and the proxy's owner sets the bit.
    /// - Boss altars (BossStone) and stones sitting on a networked object are left alone.
    ///
    /// Multiplayer: install it for every player (a player without the mod keeps seeing the stones) and on the
    /// dedicated server, which permanently owns the locations around the world spawn and must record the bit there.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "valheim.runebreaker";
        public const string PluginName = "RuneBreaker";
        public const string PluginVersion = "1.0.0";

        internal static ManualLogSource Log;

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> BreakRuneStones;
        internal static ConfigEntry<bool> BreakVegvisirs;
        internal static ConfigEntry<float> Health;
        internal static ConfigEntry<int> MinToolTier;
        internal static ConfigEntry<int> StoneDrop;

        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;

            Enabled = Config.Bind("General", "Enabled", true, "Enables or disables the mod.");

            BreakRuneStones = Config.Bind("General", "BreakRuneStones", true,
                "Rune stones (the lore stones found across the world) can be broken.");

            BreakVegvisirs = Config.Bind("General", "BreakVegvisirs", true,
                "Vegvisirs (the stones that mark a boss on the map) can be broken. Read them first!");

            Health = Config.Bind("General", "Health", 200f,
                "Pickaxe damage needed to break a stone. A bronze pickaxe deals about 30 per hit.");

            MinToolTier = Config.Bind("General", "MinToolTier", 0,
                "Minimum pickaxe tier: 0 = any pickaxe, 2 = bronze, 3 = iron.");

            StoneDrop = Config.Bind("General", "StoneDrop", 10,
                "Stone items dropped by a broken stone (0 = none).");

            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll(typeof(Plugin).Assembly);

            Log.LogInfo($"{PluginName} {PluginVersion} loaded.");
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
        }
    }

    /// <summary>Stone bookkeeping shared by the patches, the stone component and the RPC.</summary>
    internal static class Stones
    {
        internal const string RpcBreak = "RuneBreaker_Break";

        /// <summary>Bit mask of broken stone indices, in the LocationProxy's ZDO.</summary>
        internal static readonly int BrokenKey = "RuneBreaker.broken".GetStableHashCode();

        private static bool _effectsLookedUp;
        private static EffectList _hitEffect;
        private static EffectList _destroyedEffect;

        /// <summary>Same list, in the same order, on every peer.</summary>
        internal static List<Component> Collect(GameObject instance)
        {
            var list = new List<Component>();
            list.AddRange(instance.GetComponentsInChildren<RuneStone>(true));
            list.AddRange(instance.GetComponentsInChildren<Vegvisir>(true));
            return list;
        }

        internal static bool IsBreakable(Component stone, Transform proxyRoot)
        {
            if (stone is RuneStone)
            {
                if (!Plugin.BreakRuneStones.Value || stone.GetComponent<BossStone>() != null) return false;
            }
            else if (!Plugin.BreakVegvisirs.Value) return false;

            // A stone on a networked object is not part of the local location copy (or is an inactive leftover of it)
            for (Transform t = stone.transform; t != null && t != proxyRoot; t = t.parent)
                if (t.GetComponent<ZNetView>() != null) return false;
            return true;
        }

        /// <summary>Removes broken stones and makes the others breakable, each time the location is spawned.</summary>
        internal static void Apply(LocationProxy proxy)
        {
            ZDO zdo = proxy.m_nview.GetZDO();
            if (zdo == null || proxy.m_instance == null) return;

            long broken = zdo.GetLong(BrokenKey);
            List<Component> stones = Collect(proxy.m_instance);
            for (int i = 0; i < stones.Count; i++)
            {
                Component stone = stones[i];
                if (i < 64 && (broken & (1L << i)) != 0)
                    Object.Destroy(stone.gameObject);
                else if (i < 64 && IsBreakable(stone, proxy.transform))
                    stone.gameObject.AddComponent<BreakableStone>().Init(proxy, i);
            }
        }

        /// <summary>RPC received by every peer that has the location loaded.</summary>
        internal static void OnBreak(LocationProxy proxy, int index)
        {
            if (index < 0 || index >= 64) return;

            if (proxy.m_instance != null)
            {
                List<Component> stones = Collect(proxy.m_instance);
                if (index < stones.Count && stones[index] != null)
                {
                    Transform t = stones[index].transform;
                    LookUpEffects();
                    _destroyedEffect?.Create(t.position, t.rotation);
                    Object.Destroy(t.gameObject);
                }
            }

            ZNetView nview = proxy.m_nview;
            if (nview.IsValid() && nview.IsOwner())
            {
                ZDO zdo = nview.GetZDO();
                zdo.Set(BrokenKey, zdo.GetLong(BrokenKey) | (1L << index));
            }
        }

        internal static void PlayHitEffect(Vector3 point)
        {
            LookUpEffects();
            _hitEffect?.Create(point, Quaternion.identity);
        }

        /// <summary>Borrows the hit and destruction effects of a vanilla rock, so breaking sounds like mining.</summary>
        private static void LookUpEffects()
        {
            if (_effectsLookedUp || ZNetScene.instance == null) return;
            _effectsLookedUp = true;

            foreach (string name in new[] { "rock4_forest", "rock4_coast", "rock1_mountain", "Rock_3", "Rock_4" })
            {
                GameObject prefab = ZNetScene.instance.GetPrefab(name);
                if (prefab == null) continue;

                if (prefab.TryGetComponent(out MineRock5 mineRock5))
                {
                    _hitEffect = mineRock5.m_hitEffect;
                    _destroyedEffect = mineRock5.m_destroyedEffect;
                }
                else if (prefab.TryGetComponent(out MineRock mineRock))
                {
                    _hitEffect = mineRock.m_hitEffect;
                    _destroyedEffect = mineRock.m_destroyedEffect;
                }
                else if (prefab.TryGetComponent(out Destructible destructible))
                {
                    _hitEffect = destructible.m_hitEffect;
                    _destroyedEffect = destructible.m_destroyedEffect;
                }
                else continue;

                return;
            }
            Plugin.Log.LogWarning("No vanilla rock found to borrow effects from: stones will break silently.");
        }
    }

    /// <summary>Makes a location stone take pickaxe hits from the local player.</summary>
    internal class BreakableStone : MonoBehaviour, IDestructible
    {
        private LocationProxy _proxy;
        private int _index;
        private float _health;
        private bool _broken;

        internal void Init(LocationProxy proxy, int index)
        {
            _proxy = proxy;
            _index = index;
            _health = Plugin.Health.Value;
        }

        public DestructibleType GetDestructibleType() => DestructibleType.Default;

        public void Damage(HitData hit)
        {
            // Attack.DoMeleeAttack calls this on the attacker's own peer
            if (!Plugin.Enabled.Value || _broken || _proxy == null) return;
            if (hit.GetAttacker() != Player.m_localPlayer || Player.m_localPlayer == null) return;

            float damage = hit.m_damage.m_pickaxe;
            if (damage <= 0f) return;

            if (!hit.CheckToolTier(Plugin.MinToolTier.Value))
            {
                DamageText.instance.ShowText(DamageText.TextType.TooHard, hit.m_point, 0f);
                return;
            }

            DamageText.instance.ShowText(DamageText.TextType.Normal, hit.m_point, damage);
            Stones.PlayHitEffect(hit.m_point);

            _health -= damage;
            if (_health > 0f) return;

            _broken = true;
            DropStone();

            ZNetView nview = _proxy.m_nview;
            if (nview != null && nview.IsValid())
                nview.InvokeRPC(ZNetView.Everybody, Stones.RpcBreak, _index);
            else
                Destroy(gameObject);
        }

        private void DropStone()
        {
            int amount = Plugin.StoneDrop.Value;
            GameObject prefab = amount > 0 ? ObjectDB.instance?.GetItemPrefab("Stone") : null;
            if (prefab == null) return;

            Vector3 pos = transform.position + Vector3.up;
            GameObject go = Instantiate(prefab, pos, Quaternion.Euler(0f, Random.Range(0f, 360f), 0f));
            ItemDrop drop = go.GetComponent<ItemDrop>();
            drop.SetStack(amount);
            ItemDrop.OnCreateNew(drop);
        }
    }

    /// <summary>Registers the break RPC on every location proxy.</summary>
    [HarmonyPatch(typeof(LocationProxy), nameof(LocationProxy.Awake))]
    internal static class LocationProxy_Awake_Patch
    {
        private static void Postfix(LocationProxy __instance)
        {
            if (__instance.m_nview == null || !__instance.m_nview.IsValid()) return;
            __instance.m_nview.Register<int>(Stones.RpcBreak, (sender, index) => Stones.OnBreak(__instance, index));
        }
    }

    /// <summary>The location was just instantiated (possibly delayed to a later frame by the game).</summary>
    [HarmonyPatch(typeof(LocationProxy), nameof(LocationProxy.SpawnLocation))]
    internal static class LocationProxy_SpawnLocation_Patch
    {
        private static void Postfix(LocationProxy __instance, bool __result)
        {
            if (!__result || !Plugin.Enabled.Value) return;
            Stones.Apply(__instance);
        }
    }
}
