using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace AttackCancel
{
    /// <summary>
    /// Cuts an attack already under way: raising the shield or dodging in the middle of a swing stops it
    /// instead of being ignored until the animation is over.
    ///
    /// In vanilla an attack owns the character until its animation ends. Everything that could get you out of
    /// it is gated on Character.InAttack(): Humanoid.IsBlocking() returns false while it is true (the shield
    /// stays down even though the key is held), Player.UpdateDodge() refuses the queued dodge, and the
    /// movement speed stays locked to the attack. InAttack() itself is read from the animator, not from a
    /// field: Player.InAttack() asks every animator layer whether its state carries the "attack" tag. There is
    /// no supported way to force the animator out of that state, only the "stagger" trigger does it and it
    /// comes with a stagger. So the mod does not fight the animation, it retires the attack behind it:
    ///
    /// - The attack is ended for real, the vanilla way: Attack.Stop() (the same call the game makes when the
    ///   weapon is unequipped mid-swing), then m_currentAttack is set to null. Humanoid.OnAttackTrigger(), the
    ///   animation event that spawns the damage, the projectile and the effects, checks m_currentAttack and
    ///   does nothing once it is null, so a cancelled swing deals no damage at all.
    /// - InAttack() is then reported as false for the rest of the animation (Postfix on Player.InAttack), so
    ///   the shield goes up at once, the queued dodge fires, and the character walks at full speed again. The
    ///   lie stops by itself as soon as the animator really leaves the attack state, when the next attack
    ///   starts, or after SafetyTimeout seconds, whichever comes first.
    ///
    /// The swing animation itself keeps playing to its end on the base layer, with no weapon trail and no
    /// effect (Attack.Stop() takes care of those): the follow-through is cosmetic, the attack is dead. A dodge
    /// cancel does not even show that much, since the roll animation takes over.
    ///
    /// The attack costs (stamina, eitr, health) are spent by Attack.Update the very frame the swing starts, so
    /// a cancel never gives them back, exactly like an attack that misses.
    ///
    /// AllowEarlyAttack decides what the cancel buys you. Off (the default), a new attack is refused until the
    /// animation is over as in vanilla: the cancel is there to defend yourself, not to swing faster, and
    /// holding the attack button cannot restart a swing the moment you cancelled one. On, the cancel also
    /// shortens the recovery between two attacks, which is a real damage-per-second change.
    ///
    /// A drawn bow and the other looping attacks are left to the block key (it is the zoom key while aiming),
    /// and the game already cancels them when the attack button is released. A dodge cancels them like
    /// anything else.
    ///
    /// Multiplayer: an attack runs on the attacker's own client, which is the one that sends the damage. The
    /// cancel happens before anything is sent, so the other players simply never take the hit. Client only:
    /// neither the dedicated server nor the other players need the mod.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "valheim.attackcancel";
        public const string PluginName = "AttackCancel";
        public const string PluginVersion = "1.0.0";

        /// <summary>How long InAttack() may be reported as false, in case the animator never leaves the state.</summary>
        internal const float SafetyTimeout = 3f;

        internal static ManualLogSource Log;

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> OnBlock;
        internal static ConfigEntry<bool> OnDodge;
        internal static ConfigEntry<bool> AfterHit;
        internal static ConfigEntry<bool> AllowEarlyAttack;

        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;

            Enabled = Config.Bind("General", "Enabled", true, "Enables or disables the mod.");

            OnBlock = Config.Bind("Cancel", "OnBlock", true,
                "Raising the shield during an attack cuts that attack: the block key takes effect at once " +
                "instead of waiting for the end of the swing. A drawn bow and the other charged attacks are " +
                "left alone, since the block key is also the aiming zoom.");

            OnDodge = Config.Bind("Cancel", "OnDodge", true,
                "Dodging during an attack cuts that attack, including a drawn bow or a charged attack. The " +
                "dodge itself keeps its vanilla cost and rules.");

            AfterHit = Config.Bind("Cancel", "AfterHit", true,
                "Also allows cutting the end of an attack that has already landed its blow, i.e. the recovery. " +
                "Turn it off to only allow cancelling the wind-up, before the weapon connects: what is started " +
                "is then paid for, and the cancel can never be used to shorten an attack.");

            AllowEarlyAttack = Config.Bind("Cancel", "AllowEarlyAttack", false,
                "Allows a new attack right after a cancel, before the animation of the cancelled one is over. " +
                "Off by default: the cancel is meant to let you defend yourself, and leaving it off also keeps " +
                "a held attack button from starting a new swing the instant you cancel one. On, it shortens " +
                "the delay between two attacks, which does change how much damage you deal.");

            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll(typeof(Plugin).Assembly);

            Log.LogInfo($"{PluginName} {PluginVersion} loaded.");
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
        }
    }

    /// <summary>What cut the attack, since the block key does not cancel the same attacks as a dodge.</summary>
    internal enum Trigger
    {
        Block,
        Dodge
    }

    /// <summary>
    /// The cancel in progress: the local player whose attack was cut, and how long InAttack() must keep
    /// answering false. The state ends on its own once the animator has entered and left the attack state, so
    /// the mod stops lying exactly when vanilla would have given control back.
    /// </summary>
    internal static class Cancelled
    {
        private static Player _player;
        private static bool _sawAttackState;
        private static float _expiry;

        internal static bool Active(Player player)
        {
            return _player != null && _player == player;
        }

        internal static void Begin(Player player)
        {
            _player = player;
            _sawAttackState = false;
            _expiry = Time.time + Plugin.SafetyTimeout;
        }

        internal static void Clear()
        {
            _player = null;
            _sawAttackState = false;
        }

        /// <summary>
        /// Answers whether InAttack() must be hidden, from the animator's real answer. Once the attack state
        /// has been seen and left, the animation is over and the cancel is done with.
        /// </summary>
        internal static bool Hide(bool reallyInAttack)
        {
            if (Time.time > _expiry)
            {
                Clear();
                return false;
            }

            if (reallyInAttack)
            {
                _sawAttackState = true;
                return true;
            }

            if (_sawAttackState)
            {
                Clear();
                return false;
            }

            return true;
        }
    }

    /// <summary>Cuts the attack of the local player, and remembers which attack has already landed its blow.</summary>
    internal static class Cancel
    {
        /// <summary>The attack whose animation event already went through, for the AfterHit option.</summary>
        internal static Attack HitDone;

        internal static void Attempt(Player player, Trigger trigger)
        {
            if (!Plugin.Enabled.Value) return;
            if (player == null || player != Player.m_localPlayer) return;
            if (Cancelled.Active(player)) return;

            Attack attack = player.m_currentAttack;
            if (attack == null || attack.IsDone()) return;

            // InAttack() is the patched one, so this is also false while a cancel is already running.
            if (!player.InAttack()) return;

            // The block key is the aiming zoom with a bow, and the game already cancels a charged attack when
            // its button is released.
            if (trigger == Trigger.Block && (attack.m_loopingAttack || player.IsDrawingBow())) return;

            if (!Plugin.AfterHit.Value && ReferenceEquals(HitDone, attack)) return;

            attack.Stop();
            player.m_previousAttack = attack;
            player.m_currentAttack = null;

            // An attack clicked just before the cancel would fire the moment InAttack() goes false.
            player.m_queuedAttackTimer = 0f;
            player.m_queuedSecondAttackTimer = 0f;

            Cancelled.Begin(player);
        }
    }

    /// <summary>
    /// Block key: SetControls receives the press (block) and the hold (blockHold) separately, so the cancel is
    /// on the press only. Holding the shield up and then attacking keeps working as in vanilla.
    /// </summary>
    [HarmonyPatch(typeof(Player), nameof(Player.SetControls))]
    internal static class Player_SetControls_Patch
    {
        private static void Postfix(Player __instance, bool block)
        {
            if (!block || !Plugin.OnBlock.Value) return;

            Cancel.Attempt(__instance, Trigger.Block);
        }
    }

    /// <summary>
    /// Dodge: Player.Dodge queues the roll for half a second (m_queuedDodgeTimer), whatever the input that
    /// asked for it (keyboard, gamepad), and UpdateDodge then refuses it while InAttack() is true. Cancelling
    /// here is enough for the queued dodge to go through on its own.
    /// </summary>
    [HarmonyPatch(typeof(Player), "Dodge")]
    internal static class Player_Dodge_Patch
    {
        private static void Postfix(Player __instance)
        {
            if (!Plugin.OnDodge.Value) return;
            if (__instance.m_queuedDodgeTimer <= 0f) return;

            Cancel.Attempt(__instance, Trigger.Dodge);
        }
    }

    /// <summary>
    /// The lie that gives control back: while a cancel is running, the player is no longer in an attack as far
    /// as the game is concerned. IsBlocking(), UpdateDodge(), the movement speed and everything else gated on
    /// InAttack() follow at once.
    /// </summary>
    [HarmonyPatch(typeof(Player), nameof(Player.InAttack))]
    internal static class Player_InAttack_Patch
    {
        private static void Postfix(Player __instance, ref bool __result)
        {
            if (!Cancelled.Active(__instance)) return;

            if (Cancelled.Hide(__result)) __result = false;
        }
    }

    /// <summary>
    /// Keeps the cancel from turning into free attack speed: while the animation of the cancelled attack is
    /// still playing, a new attack is refused as it would be in vanilla. With AllowEarlyAttack, the new attack
    /// goes through and simply ends the cancel.
    /// </summary>
    [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.StartAttack))]
    internal static class Humanoid_StartAttack_Patch
    {
        private static bool Prefix(Humanoid __instance, ref bool __result)
        {
            if (Plugin.AllowEarlyAttack.Value) return true;

            Player player = __instance as Player;
            if (player == null || !Cancelled.Active(player)) return true;

            __result = false;
            return false;
        }

        private static void Postfix(Humanoid __instance, bool __result)
        {
            if (!__result) return;

            Player player = __instance as Player;
            if (player != null && Cancelled.Active(player)) Cancelled.Clear();
        }
    }

    /// <summary>
    /// Records the attack that has already dealt its blow, for the AfterHit option. This is the animation
    /// event that spawns the damage, the projectile and the effects.
    /// </summary>
    [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.OnAttackTrigger))]
    internal static class Humanoid_OnAttackTrigger_Patch
    {
        private static void Postfix(Humanoid __instance)
        {
            if (__instance != Player.m_localPlayer) return;

            Cancel.HitDone = __instance.m_currentAttack;
        }
    }
}
