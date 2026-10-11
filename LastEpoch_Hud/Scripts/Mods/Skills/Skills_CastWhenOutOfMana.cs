using System.Collections.Generic;
using HarmonyLib;
using Il2Cpp;
using LastEpoch_Hud.Scripts.Core.Skills;
using LastEpoch_Hud.Scripts.ModUI;
using UnityEngine;

namespace LastEpoch_Hud.Scripts.Mods.Skills;

/// <summary>
/// Two ways to cast without enough mana, both hooked only at InitialiseAbilityUse and UseAbility:
/// - When mana is short, the mod runs the game's own cooldown and remaining checks
///   (canUseAbilityChecksAfterManaAndCooldown) and, if they pass, starts the cast with the game's
///   ignoreCanUseAbilityChecks, skipping the mana refusal.
/// - That skips the game's pacing too (Heartseeker cast ~1.8x faster than attack speed), so
///   OutOfManaCastPacer (Core) paces forced casts to the character's own cast speed.
/// - The forced cast's spend is skipped (useMana = false); "Allow Negative Mana" charges the cost
///   afterwards through the game's reduceMana, with canGoNegative on. Casts the game allows by
///   itself (e.g. gear that lets mana go negative) are never changed.
/// What is deliberately not done, each of which broke casting in testing:
/// - patching the cast checks (ClientUsingAbilityState.CanUseAbility, UsingAbilityPlayer.canUseAbility):
///   they take the target as ref Vector3, and patching them broke targeting even with the modes off;
/// - changing current mana during a check, or letting the game spend mana it doesn't have:
///   both froze projectiles;
/// - patching getters such as getFreeWhenOutOfMana: tiny IL2CPP getters can share native code;
/// - setting freeWhenOutOfMana on the server-side CastingData: the server's check ignores it.
/// </summary>
internal static class Skills_CastWhenOutOfMana
{
    // The player mana whose canGoNegative this mod manages, and the character's own value to
    // restore once negative mode is off.
    private static System.IntPtr _managedMana = System.IntPtr.Zero;
    private static bool _originalCanGoNegative = false;

    // The ability whose short cast the mod started, until that cast's spend runs. The game can
    // report the start as failed and still cast a moment later, so it lives a few seconds.
    private static System.IntPtr _overriddenAbility = System.IntPtr.Zero;
    private static float _overrideExpires = 0f;

    // Paces forced casts to the character's own cast speed (see OutOfManaCastPacer).
    private static readonly OutOfManaCastPacer _pacer = new();

    // Where the player was at the last cast, to tell whether they've moved since.
    private static Vector3 _lastCastPosition = Vector3.zero;

    private static bool AnyMode() =>
        ModSettings.SkillsMana.CastWhenOutOfMana.Value
        || ModSettings.SkillsMana.AllowNegativeMana.Value;

    private static BaseMana PlayerMana() =>
        Refs_Manager.player_actor.IsNullOrDestroyed() ? null : Refs_Manager.player_actor.mana;

    /// <summary>Whether the player is giving movement input (keyboard, mouse or stick).</summary>
    private static bool MovementHeld()
    {
        EpochInputManager input = Refs_Manager.epoch_input_manager;
        if (input.IsNullOrDestroyed())
        {
            return false;
        }
        return input.isKeyboardMovementButtonHeld
            || input.isMouseMovementButtonHeld
            || input.input.sqrMagnitude > 0.01f;
    }

    private static bool IsShort(BaseMana mana, float cost) =>
        !mana.IsNullOrDestroyed() && mana.currentMana < cost;

    /// <summary>The cast's real mana cost, from the game's own BaseMana.getManaCost.</summary>
    private static float CostOf(BaseMana mana, Ability ability)
    {
        try
        {
            return mana.IsNullOrDestroyed() || ability.IsNullOrDestroyed()
                ? 0f
                : mana.getManaCost(ability);
        }
        catch (System.Exception)
        {
            return 0f;
        }
    }

    private static bool IsPlayer(UsingAbility usingAbility)
    {
        BaseMana player = PlayerMana();
        BaseMana mana = usingAbility.baseMana;
        return !player.IsNullOrDestroyed()
            && !mana.IsNullOrDestroyed()
            && player.Pointer == mana.Pointer;
    }

    [HarmonyPatch(typeof(UsingAbilityPlayer), nameof(UsingAbilityPlayer.InitialiseAbilityUse))]
    public class UsingAbilityPlayer_InitialiseAbilityUse
    {
        [HarmonyPrefix]
        static void Prefix(UsingAbilityPlayer __instance, Ability __0, Vector3 __1, ref bool __2)
        {
            try
            {
                BaseMana mana = __instance.baseMana;
                if (mana.IsNullOrDestroyed())
                {
                    return;
                }
                ApplyCanGoNegative(mana);
                if (__2 || !AnyMode() || __0.IsNullOrDestroyed())
                {
                    return;
                }
                float cost = CostOf(mana, __0);
                if (!IsShort(mana, cost))
                {
                    return;
                }
                ChargeManager charges = __instance.chargeManager;
                if (!charges.IsNullOrDestroyed() && charges.onCoooldown(__0))
                {
                    return;
                }
                bool ready = _pacer.IsReady(
                    __0.Pointer.ToInt64(),
                    Time.time,
                    __instance.midAbilityAnimation,
                    MovementHeld(),
                    Vector3.Distance(__instance.transform.position, _lastCastPosition)
                );
                if (!ready)
                {
                    Trace(
                        MovementHeld() ? "waiting (moving)" : "waiting for previous cast",
                        __0,
                        cost,
                        false
                    );
                    return;
                }
                AbilityInfo info = __instance.mutatorManager?.getMutatorOrAbility(__0);
                if (
                    info == null
                    || !__instance.canUseAbilityChecksAfterManaAndCooldown(__0, info, __1)
                )
                {
                    Trace("other checks failed", __0, cost, false);
                    return;
                }
                __2 = true;
                _overriddenAbility = __0.Pointer;
                _pacer.ForcedCastStarted(Time.time);
                _overrideExpires = Time.time + 3f;
                Trace("override", __0, cost, true);
            }
            catch (System.Exception ex)
            {
                ErrorLog.Report(ex, "Skills_CastWhenOutOfMana");
            }
        }

        /// <summary>
        /// "Allow Negative Mana" turns the game's canGoNegative on for the player's mana; otherwise
        /// the character's own value (gear can grant it) is put back and left alone.
        /// </summary>
        private static void ApplyCanGoNegative(BaseMana mana)
        {
            if (ModSettings.SkillsMana.AllowNegativeMana.Value)
            {
                if (_managedMana != mana.Pointer)
                {
                    _managedMana = mana.Pointer;
                    _originalCanGoNegative = mana.canGoNegative;
                }
                mana.canGoNegative = true;
            }
            else if (_managedMana == mana.Pointer)
            {
                mana.canGoNegative = _originalCanGoNegative;
                _managedMana = System.IntPtr.Zero;
            }
        }
    }

    /// <summary>
    /// Every player cast passes here: its time and duration pace later forced casts. For a cast
    /// the mod forced, the game's spend is skipped (useMana = false), and negative mode charges
    /// the cost afterwards through the game's reduceMana.
    /// </summary>
    [HarmonyPatch(typeof(UsingAbility), nameof(UsingAbility.UseAbility))]
    public class UsingAbility_UseAbility
    {
        [HarmonyPrefix]
        static void Prefix(UsingAbility __instance, Ability __0, ref bool __2, out float __state)
        {
            // The cost negative mode still has to charge after the cast; 0 = nothing to charge.
            __state = 0f;
            if (__0.IsNullOrDestroyed() || !IsPlayer(__instance))
            {
                return;
            }
            bool forced = _overriddenAbility == __0.Pointer && Time.time <= _overrideExpires;
            if (forced)
            {
                _pacer.ForcedCastReleased(__0.Pointer.ToInt64(), Time.time);
            }
            LogCastTiming(__instance, __0, forced);
            if (!forced)
            {
                return;
            }
            if (!__2 || !AnyMode())
            {
                return;
            }
            _overriddenAbility = System.IntPtr.Zero;
            __2 = false;
            if (ModSettings.SkillsMana.AllowNegativeMana.Value)
            {
                __state = CostOf(__instance.baseMana, __0);
            }
            Trace("spend skipped", __0, CostOf(__instance.baseMana, __0), true);
        }

        [HarmonyPostfix]
        static void Postfix(UsingAbility __instance, Ability __0, float __state)
        {
            if (__0.IsNullOrDestroyed() || !IsPlayer(__instance))
            {
                return;
            }
            // The cast's duration is set once it has started; pace the next forced cast by it.
            _pacer.CastReleased(Time.time, __instance.castDuration);
            _loggedCastDuration = __instance.castDuration;
            _lastCastPosition = __instance.transform.position;
            if (__state <= 0f)
            {
                return;
            }
            try
            {
                BaseMana mana = __instance.baseMana;
                if (!mana.IsNullOrDestroyed())
                {
                    mana.reduceMana(__state);
                    Trace("charged after cast", __0, __state, true);
                }
            }
            catch (System.Exception ex)
            {
                ErrorLog.Report(ex, "Skills_CastWhenOutOfMana");
            }
        }
    }

    // Diagnostic, local only: every player cast with the time since that skill's last cast and
    // the previous cast's duration, to compare forced casts with normal ones.
    private static readonly Dictionary<System.IntPtr, float> _lastCastTimeByAbility = new();
    private static float _loggedCastDuration = 0f;

    private static void LogCastTiming(UsingAbility usingAbility, Ability ability, bool forced)
    {
        float now = Time.time;
        string gap = _lastCastTimeByAbility.TryGetValue(ability.Pointer, out float last)
            ? ((int)((now - last) * 1000f)).ToString() + " ms"
            : "first";
        _lastCastTimeByAbility[ability.Pointer] = now;
        BaseMana mana = usingAbility.baseMana;
        Main.logger_instance?.Msg(
            "[Mana] cast: "
                + ability.abilityName
                + " gap "
                + gap
                + " castDuration "
                + (int)(_loggedCastDuration * 1000f)
                + " ms mana "
                + (mana.IsNullOrDestroyed() ? -1 : (int)mana.currentMana)
                + " cost "
                + CostOf(mana, ability)
                + " mode "
                + ModeName()
                + (
                    forced
                        ? " (mod bypass, wind-up "
                            + (int)(_pacer.WindupFraction(ability.Pointer.ToInt64()) * 100f)
                            + "%)"
                        : ""
                )
        );
    }

    private static string ModeName() =>
        ModSettings.SkillsMana.AllowNegativeMana.Value ? "negative"
        : ModSettings.SkillsMana.CastWhenOutOfMana.Value ? "free"
        : "off";

    // Diagnostic, local only: one line per step whenever its outcome changes.
    private static readonly Dictionary<string, string> _lastTrace = new();

    private static void Trace(string step, Ability ability, float cost, bool result)
    {
        BaseMana mana = PlayerMana();
        string state =
            (ability.IsNullOrDestroyed() ? "" : ability.abilityName + " ")
            + "cost "
            + cost
            + " mana "
            + (mana.IsNullOrDestroyed() ? -1 : (int)mana.currentMana)
            + " canGoNegative "
            + (!mana.IsNullOrDestroyed() && mana.canGoNegative)
            + " mode "
            + ModeName()
            + " => "
            + result;
        if (_lastTrace.TryGetValue(step, out string last) && last == state)
        {
            return;
        }
        _lastTrace[step] = state;
        Main.logger_instance?.Msg("[Mana] " + step + ": " + state);
    }
}
