using System;
using System.Globalization;
using System.Reflection;
using System.Text;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using EFT.Animations;
using EFT.Animations.NewRecoil;
using HarmonyLib;
using UnityEngine;

namespace FuckThesePhysics
{
    // Interpolates EFT's 50 Hz first-person procedural effector layer between fixed ticks for the local player
    // (docs/viewmodel-fixed-tick-handoff.md).
    //
    // Player.FixedUpdate only counts ticks; Player.VisualPass then runs ProceduralWeaponAnimation.ProcessEffectors once per frame:
    // FirstPersonStrategy.ProcessEffectors simulates every pending tick (springs, sway, recoil, breath, bob, force, wall pullback,
    // camera springs), then ApplyTransformations (and, for the non-left-stance case, ApplyCameraTransformations) turn the simulated
    // state into transforms. Above 50 fps consecutive frames therefore present the same state, and the viewmodel steps.
    // MountingStrategy (bipod / wall mount) and StationaryStrategy (stationary weapon) run the same tick loop over the same springs,
    // recoil effect and TurnAway, ending in the same CameraPosition.FixedUpdate (they only skip effectors that do not apply while
    // mounted); their camera apply runs in LateTransformations. They are patched alike. ThirdPersonStrategy is not.
    //
    // Mechanism: the last statement of the tick loop is HandsContainer.CameraPosition.FixedUpdate, so a postfix on Spring.FixedUpdate
    // that matches that one spring instance snapshots the whole swap set after every tick (prev = previous tick, cur = this tick,
    // consecutive even when one frame runs several ticks). A postfix on the strategy's ProcessEffectors (after the tick loop,
    // before any Apply*) writes lerp(prev, cur, alpha) into the swap set, alpha = (Time.time - Time.fixedTime) / Time.fixedDeltaTime,
    // so every Apply* reads the blended pose. The swap set is only displayed state: Spring.Current x4, SwaySpring._pPos, the
    // new-recoil Current/Offset values, WeaponRecoil value processes and the TurnAway pose (position, rotation, camera shift and
    // both elbow shifts, which Player.AdjustElbows reads). Velocities, force accumulators, Spring.Zero, update flags and every
    // Transform are never touched, so the simulation itself stays on the 50 Hz ticks.
    //
    // Restore rule: a postfix on ProceduralWeaponAnimation.LateTransformations (runs after the last Apply*) writes
    // field = cur + (fieldNow - blendedWritten). If nothing changed the field, that is exactly cur, the simulated state. Writes
    // made during Apply* (LerpCamera feeding the sway spring, for example) survive as deltas on top of cur. The transforms keep the
    // interpolated pose; that is the presented pose. Wherever a swap could outlive its frame (the next ProcessEffectors, toggle off,
    // GameWorld.Dispose, Uninstall, failure) the same restore runs first. The blend records every value it writes, starting from the
    // live field values, so a write that throws partway still restores exactly: written slots to cur plus their deltas, unwritten
    // slots to cur.
    //
    // Scope: the local first-person player only (GameWorld.MainPlayer's animation, point of view first person, with a first-person,
    // mounting or stationary strategy). Bots and observed players use other strategies or other PWA instances and are never swapped.
    // Shots read the fireport transforms, which hold the presented pose, so the point of impact matches the sight picture; the
    // gameplay pose lags by up to one tick (20 ms).
    //
    // History restarts (prev = cur) wherever the swap set is genuinely discontinuous: new animation, strategy (including the switch
    // between first-person, mounting and stationary), hands container, recoil effect, recoil value array or hands controller (weapon
    // swap), teleport, fixed time step change, point of view leaving first person, feature (re)enabled. Aim toggles and frame
    // hitches are not resets: snapshots are per tick, and aim blending lives outside the swap set.
    //
    // Failure: every target is resolved up front (a miss patches nothing). An exception in any patch is logged once, restores a
    // pending swap and latches the feature to vanilla until restart.
    internal static class ViewmodelInterpolation
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // Swap set slots. The recoil slots are only valid while a NewRecoilShotEffect is the live recoil effect.
        const int HandsPositionSlot = 0;
        const int HandsRotationSlot = 1;
        const int CameraRotationSlot = 2;
        const int CameraPositionSlot = 3;
        const int SwaySlot = 4;
        const int TurnPositionSlot = 5;
        const int TurnRotationSlot = 6;
        const int TurnCameraShiftSlot = 7;
        const int TurnLElbowShiftSlot = 8;
        const int TurnRElbowShiftSlot = 9;
        const int BaseSlotCount = 10;
        const int RecoilHandPositionCurrentSlot = 10;
        const int RecoilHandPositionOffsetSlot = 11;
        const int RecoilHandRotationCurrentSlot = 12;
        const int RecoilHandRotationOffsetSlot = 13;
        const int RecoilCameraRotationCurrentSlot = 14;
        const int SlotCount = 15;

        // Reset reasons, in the order the identity checks run.
        const int ResetEnabled = 0;
        const int ResetAnimation = 1;
        const int ResetStrategy = 2;
        const int ResetHandsContainer = 3;
        const int ResetRecoilEffect = 4;
        const int ResetRecoilValues = 5;
        const int ResetHandsController = 6;
        const int ResetEffectorParts = 7;
        const int ResetTeleport = 8;
        const int ResetFixedStep = 9;
        const int ResetPointOfView = 10;
        const int ResetReasonCount = 11;

        static readonly string[] ResetNames =
        {
            "enabled", "animation", "strategy", "hands container", "recoil effect", "recoil values", "hands controller",
            "effector parts", "teleport", "fixed step", "point of view"
        };

        static Harmony harmony;
        static ConfigEntry<bool> enabledSetting;
        static bool failed;
        static string failure;

        // Tracked identities. A change of any of them is a reset.
        static ProceduralWeaponAnimation trackedPwa;
        static IProceduralAnimationStrategy trackedStrategy;
        static PlayerSpring trackedHands;
        static Player.AbstractHandsController trackedController;
        static NewRecoilShotEffect trackedEffect;
        static WeaponRecoilValue[] trackedValues;
        static Spring handsPosition, handsRotation, cameraRotation, cameraPosition;
        static BetterSpring sway;
        static TurnAwayEffector turnAway;
        static RecoilProcessBase handPositionRecoil, handRotationRecoil, cameraRotationRecoil;
        static float trackedFixedStep;
        static bool wasEnabled;

        // Per-frame bracket state. tickSpring is only set between the ProcessEffectors prefix and postfix.
        static ProceduralWeaponAnimation activePwa;
        static Spring tickSpring;
        static bool swapped;

        // Snapshots. prev/cur trade places on every tick; history counts snapshots since the last reset (blend needs 2).
        static Vector3[] prev = new Vector3[SlotCount];
        static Vector3[] cur = new Vector3[SlotCount];
        static readonly Vector3[] blended = new Vector3[SlotCount];
        static readonly Vector3[] now = new Vector3[SlotCount];
        static Vector3[] valuesPrev = new Vector3[0];
        static Vector3[] valuesCur = new Vector3[0];
        static Vector3[] valuesBlended = new Vector3[0];
        static Vector3[] valuesNow = new Vector3[0];
        static int history;

        // Counters since the last report (main thread only).
        static int windowStartFrame;
        static int framesBlended, framesSkipped, framesWithoutHistory, tickSnapshots;
        static readonly int[] resets = new int[ResetReasonCount];
        static float lastAlpha;
        static int lastBlendFrame = -1;
        static bool lastRecoilSwap;

        // Read by ViewmodelPoseTrace: the user setting, the frame of the latest blend and the alpha that blend used.
        internal static bool SettingOn => enabledSetting != null && enabledSetting.Value;
        internal static int LastBlendFrame => lastBlendFrame;
        internal static float LastAlpha => lastAlpha;

        internal static void Install(string harmonyId, ConfigEntry<bool> enabled)
        {
            try
            {
                if (harmony != null) return;
                if (enabled == null)
                {
                    Plugin.Log?.LogWarning("Viewmodel interpolation: no config entry supplied; skipping patching.");
                    return;
                }

                // Resolve every target first; any miss means vanilla behaviour (nothing is patched).
                MethodBase[] processEffectors =
                {
                    ResolveProcessEffectors(typeof(FirstPersonStrategy)),
                    ResolveProcessEffectors(typeof(MountingStrategy)),
                    ResolveProcessEffectors(typeof(StationaryStrategy))
                };
                MethodBase springFixedUpdate = AccessTools.Method(typeof(Spring), nameof(Spring.FixedUpdate), new[] { typeof(float), typeof(int) });
                if (springFixedUpdate == null)
                    throw new MissingMethodException("EFT.Animations.Spring.FixedUpdate(float, int) not found");
                MethodBase lateTransformations = AccessTools.Method(typeof(ProceduralWeaponAnimation), nameof(ProceduralWeaponAnimation.LateTransformations),
                    new[] { typeof(float) });
                if (lateTransformations == null)
                    throw new MissingMethodException("EFT.Animations.ProceduralWeaponAnimation.LateTransformations(float) not found");
                MethodBase teleport = AccessTools.Method(typeof(Player), nameof(Player.Teleport), new[] { typeof(Vector3), typeof(bool) });
                if (teleport == null)
                    throw new MissingMethodException("EFT.Player.Teleport(Vector3, bool) not found");
                MethodBase dispose = AccessTools.Method(typeof(GameWorld), nameof(GameWorld.Dispose), Type.EmptyTypes);
                if (dispose == null)
                    throw new MissingMethodException("EFT.GameWorld.Dispose() not found");

                enabledSetting = enabled;
                failed = false;
                failure = null;
                ClearState();
                wasEnabled = false;
                ResetWindow();

                var instance = new Harmony(harmonyId);
                try
                {
                    var effectorsPrefix = new HarmonyMethod(typeof(ViewmodelInterpolation), nameof(ProcessEffectorsPrefix));
                    var effectorsPostfix = new HarmonyMethod(typeof(ViewmodelInterpolation), nameof(ProcessEffectorsPostfix));
                    foreach (MethodBase target in processEffectors)
                        instance.Patch(target, prefix: effectorsPrefix, postfix: effectorsPostfix);
                    instance.Patch(springFixedUpdate, postfix: new HarmonyMethod(typeof(ViewmodelInterpolation), nameof(SpringFixedUpdatePostfix)));
                    instance.Patch(lateTransformations, postfix: new HarmonyMethod(typeof(ViewmodelInterpolation), nameof(LateTransformationsPostfix)));
                    instance.Patch(teleport, postfix: new HarmonyMethod(typeof(ViewmodelInterpolation), nameof(TeleportPostfix)));
                    instance.Patch(dispose, postfix: new HarmonyMethod(typeof(ViewmodelInterpolation), nameof(DisposePostfix)));
                }
                catch
                {
                    instance.UnpatchSelf();
                    throw;
                }

                harmony = instance;
                Plugin.Log?.LogInfo("Viewmodel interpolation installed (setting " + (enabled.Value ? "on" : "off") + ").");
            }
            catch (Exception error)
            {
                Plugin.Log?.LogWarning("Viewmodel interpolation: install failed, running vanilla: " + error);
            }
        }

        // The three strategies that run the per-tick effector loop for the local player share this signature (IProceduralAnimationStrategy).
        internal static MethodBase ResolveProcessEffectors(Type strategy)
        {
            MethodBase method = AccessTools.Method(strategy, nameof(IProceduralAnimationStrategy.ProcessEffectors),
                new[] { typeof(ProceduralWeaponAnimation), typeof(float), typeof(int) });
            if (method == null)
                throw new MissingMethodException(strategy.FullName + ".ProcessEffectors(ProceduralWeaponAnimation, float, int) not found");
            return method;
        }

        // Removes this class's patches (its Harmony id is its own) and puts a pending swap back.
        internal static void Uninstall()
        {
            var instance = harmony;
            harmony = null;
            if (instance == null) return;
            try { instance.UnpatchSelf(); }
            catch (Exception error) { Plugin.Log?.LogWarning("Viewmodel interpolation unpatch failed: " + error.Message); }
            try { RestoreSwapped(); }
            catch (Exception error) { Plugin.Log?.LogWarning("Viewmodel interpolation restore on uninstall failed: " + error.Message); }
            ClearState();
        }

        static void Fail(string where, Exception error)
        {
            if (failed) return;
            failed = true;
            failure = where + ": " + error.Message;
            Plugin.Log?.LogWarning("Viewmodel interpolation: " + where + " failed, falling back to vanilla until restart (logged once): " + error);
            try { RestoreSwapped(); }
            catch (Exception restoreError) { Plugin.Log?.LogWarning("Viewmodel interpolation: restoring the swapped state failed: " + restoreError.Message); }
            ClearState();
        }

        static void ResetWindow()
        {
            windowStartFrame = Time.frameCount;
            framesBlended = framesSkipped = framesWithoutHistory = tickSnapshots = 0;
            Array.Clear(resets, 0, resets.Length);
        }

        // Drops every reference and the history. Callers restore a pending swap first.
        static void ClearState()
        {
            swapped = false;
            activePwa = null;
            tickSpring = null;
            trackedPwa = null;
            trackedStrategy = null;
            trackedHands = null;
            trackedController = null;
            trackedEffect = null;
            trackedValues = null;
            handsPosition = handsRotation = cameraRotation = cameraPosition = null;
            sway = null;
            turnAway = null;
            handPositionRecoil = handRotationRecoil = cameraRotationRecoil = null;
            trackedFixedStep = 0f;
            history = 0;
        }

        // Prefix on every strategy that runs the tick loop: a swap that outlived its frame is put back, then the tick bracket opens
        // for the local player only. Postfix and Spring.FixedUpdate only act inside that bracket.
        static void ProcessEffectorsPrefix(IProceduralAnimationStrategy __instance, ProceduralWeaponAnimation pwa)
        {
            if (failed) return;
            try
            {
                RestoreSwapped();
                activePwa = null;
                tickSpring = null;

                if (!enabledSetting.Value)
                {
                    if (wasEnabled)
                    {
                        wasEnabled = false;
                        history = 0;
                    }

                    framesSkipped++;
                    return;
                }

                if (!wasEnabled)
                {
                    wasEnabled = true;
                    resets[ResetEnabled]++;
                    history = 0;
                }

                Player player = MainPlayerOf(pwa);
                if (player == null)
                {
                    framesSkipped++;
                    return;
                }

                // PWA.PointOfView keeps a mounting or stationary strategy when the view leaves first person; nothing is presented
                // then, and the ticks that run meanwhile would leave the history stale.
                if (pwa.PointOfView != EPointOfView.FirstPerson)
                {
                    if (history != 0)
                    {
                        history = 0;
                        resets[ResetPointOfView]++;
                    }

                    framesSkipped++;
                    return;
                }

                if (!Track(__instance, pwa, player))
                {
                    framesSkipped++;
                    return;
                }

                activePwa = pwa;
                tickSpring = cameraPosition;
            }
            catch (Exception error)
            {
                Fail("ProcessEffectors prefix", error);
            }
        }

        // The main player owns pwa, or null. A first-person strategy is only allocated for first-person views, but observed or
        // spectated players must never be swapped.
        internal static Player MainPlayerOf(ProceduralWeaponAnimation pwa)
        {
            if (!Singleton<GameWorld>.Instantiated) return null;
            GameWorld world = Singleton<GameWorld>.Instance;
            if (world == null) return null;
            Player main = world.MainPlayer;
            if (main == null || !ReferenceEquals(main.ProceduralWeaponAnimation, pwa)) return null;
            return main;
        }

        // Re-reads the identities every frame; any change restarts the history. False when the effector objects are not (yet)
        // all there, in which case the frame is vanilla.
        static bool Track(IProceduralAnimationStrategy strategy, ProceduralWeaponAnimation pwa, Player player)
        {
            PlayerSpring hands = pwa.HandsContainer;
            if (hands == null) return false;
            Spring handsPositionNow = hands.HandsPosition;
            Spring handsRotationNow = hands.HandsRotation;
            Spring cameraRotationNow = hands.CameraRotation;
            Spring cameraPositionNow = hands.CameraPosition;
            BetterSpring swayNow = hands.SwaySpring;
            TurnAwayEffector turnAwayNow = pwa.TurnAway;
            if (handsPositionNow == null || handsRotationNow == null || cameraRotationNow == null || cameraPositionNow == null
                || swayNow == null || turnAwayNow == null)
                return false;

            // Any other recoil effect type (old recoil pipeline) leaves the recoil state alone: springs and TurnAway only.
            ShotEffector shooting = pwa.Shootingg;
            NewRecoilShotEffect effect = shooting == null ? null : shooting.CurrentRecoilEffect as NewRecoilShotEffect;
            RecoilProcessBase handPositionRecoilNow = null, handRotationRecoilNow = null, cameraRotationRecoilNow = null;
            WeaponRecoilValue[] recoilValuesNow = null;
            if (effect != null)
            {
                handPositionRecoilNow = effect.HandPositionRecoil;
                handRotationRecoilNow = effect.HandRotationRecoil;
                cameraRotationRecoilNow = effect.CameraRotationRecoil;
                if (handPositionRecoilNow == null || handRotationRecoilNow == null || cameraRotationRecoilNow == null)
                    effect = null;
                else
                {
                    WeaponRecoil weaponRecoil = effect.WeaponRecoil;
                    recoilValuesNow = weaponRecoil == null ? null : weaponRecoil.Values;
                }
            }

            if (effect == null)
            {
                handPositionRecoilNow = handRotationRecoilNow = cameraRotationRecoilNow = null;
            }

            Player.AbstractHandsController controller = player.HandsController;
            float fixedStep = Time.fixedDeltaTime;

            int reason = -1;
            if (!ReferenceEquals(pwa, trackedPwa)) reason = ResetAnimation;
            else if (!ReferenceEquals(strategy, trackedStrategy)) reason = ResetStrategy;
            else if (!ReferenceEquals(hands, trackedHands)) reason = ResetHandsContainer;
            else if (!ReferenceEquals(effect, trackedEffect)) reason = ResetRecoilEffect;
            else if (!ReferenceEquals(recoilValuesNow, trackedValues)) reason = ResetRecoilValues;
            else if (!ReferenceEquals(controller, trackedController)) reason = ResetHandsController;
            else if (!ReferenceEquals(handsPositionNow, handsPosition) || !ReferenceEquals(handsRotationNow, handsRotation)
                || !ReferenceEquals(cameraRotationNow, cameraRotation) || !ReferenceEquals(cameraPositionNow, cameraPosition)
                || !ReferenceEquals(swayNow, sway) || !ReferenceEquals(turnAwayNow, turnAway)
                || !ReferenceEquals(handPositionRecoilNow, handPositionRecoil) || !ReferenceEquals(handRotationRecoilNow, handRotationRecoil)
                || !ReferenceEquals(cameraRotationRecoilNow, cameraRotationRecoil))
                reason = ResetEffectorParts;
            else if (fixedStep != trackedFixedStep) reason = ResetFixedStep;

            if (reason >= 0)
            {
                resets[reason]++;
                trackedPwa = pwa;
                trackedStrategy = strategy;
                trackedHands = hands;
                trackedController = controller;
                trackedEffect = effect;
                trackedValues = recoilValuesNow;
                handsPosition = handsPositionNow;
                handsRotation = handsRotationNow;
                cameraRotation = cameraRotationNow;
                cameraPosition = cameraPositionNow;
                sway = swayNow;
                turnAway = turnAwayNow;
                handPositionRecoil = handPositionRecoilNow;
                handRotationRecoil = handRotationRecoilNow;
                cameraRotationRecoil = cameraRotationRecoilNow;
                trackedFixedStep = fixedStep;
                SizeValueArrays(recoilValuesNow == null ? 0 : recoilValuesNow.Length);
                history = 0;
            }

            if (history == 0) Seed();
            return true;
        }

        // Value arrays are reallocated only when the process count changes; a changed array reference with the same length reuses them.
        static void SizeValueArrays(int length)
        {
            if (valuesCur.Length == length) return;
            valuesPrev = new Vector3[length];
            valuesCur = new Vector3[length];
            valuesBlended = new Vector3[length];
            valuesNow = new Vector3[length];
        }

        // The pose before the first tick after a reset: prev == cur, so the first real tick blends from the fresh pair.
        static void Seed()
        {
            ReadAll(cur, valuesCur);
            Array.Copy(cur, prev, SlotCount);
            Array.Copy(valuesCur, valuesPrev, valuesCur.Length);
            history = 1;
        }

        // Matches CameraPosition.FixedUpdate, the last statement of every tick: the whole swap set is final for this tick here.
        static void SpringFixedUpdatePostfix(Spring __instance)
        {
            if (!ReferenceEquals(__instance, tickSpring)) return;
            try
            {
                Vector3[] swap = prev;
                prev = cur;
                cur = swap;
                Vector3[] valuesSwap = valuesPrev;
                valuesPrev = valuesCur;
                valuesCur = valuesSwap;
                ReadAll(cur, valuesCur);
                if (history < 2) history++;
                tickSnapshots++;
            }
            catch (Exception error)
            {
                Fail("Spring.FixedUpdate postfix", error);
            }
        }

        // Postfix after the tick loop, before ApplyTransformations / ApplyCameraTransformations.
        static void ProcessEffectorsPostfix(ProceduralWeaponAnimation pwa)
        {
            tickSpring = null;
            if (failed || !ReferenceEquals(pwa, activePwa)) return;
            activePwa = null;
            try
            {
                if (history < 2)
                {
                    framesWithoutHistory++;
                    return;
                }

                float step = Time.fixedDeltaTime;
                float alpha = step > 0f ? Mathf.Clamp01((Time.time - Time.fixedTime) / step) : 1f;
                int slotCount = ActiveSlotCount();
                for (int i = 0; i < slotCount; i++)
                    now[i] = Vector3.Lerp(prev[i], cur[i], alpha);
                for (int i = 0; i < valuesCur.Length; i++)
                    valuesNow[i] = Vector3.Lerp(valuesPrev[i], valuesCur[i], alpha);

                // blended starts as the live field values and each write records its value, so a throw partway through the write
                // leaves swapped set and RestoreSwapped exact: written slots restore to cur plus their deltas, unwritten ones to cur.
                ReadAll(blended, valuesBlended);
                swapped = true;
                WriteBlended(now, valuesNow);
                framesBlended++;
                lastAlpha = alpha;
                lastBlendFrame = Time.frameCount;
                lastRecoilSwap = trackedEffect != null;
            }
            catch (Exception error)
            {
                Fail("ProcessEffectors postfix", error);
            }
        }

        // Runs after the last Apply* of the frame (the left-stance / blindfire camera apply happens inside LateTransformations itself).
        static void LateTransformationsPostfix(ProceduralWeaponAnimation __instance)
        {
            if (!swapped || !ReferenceEquals(__instance, trackedPwa)) return;
            try { RestoreSwapped(); }
            catch (Exception error) { Fail("LateTransformations postfix", error); }
        }

        static void TeleportPostfix(Player __instance)
        {
            if (trackedPwa == null || failed) return;
            try
            {
                if (!ReferenceEquals(__instance.ProceduralWeaponAnimation, trackedPwa)) return;
                history = 0;
                resets[ResetTeleport]++;
            }
            catch (Exception error)
            {
                Fail("Player.Teleport postfix", error);
            }
        }

        // The raid is over: put a pending swap back and let go of every effector object.
        static void DisposePostfix()
        {
            if (failed) return;
            try
            {
                RestoreSwapped();
                ClearState();
            }
            catch (Exception error)
            {
                Fail("GameWorld.Dispose postfix", error);
            }
        }

        // field = cur + (fieldNow - blendedWritten): the simulated state plus whatever was written to the field since the swap.
        static void RestoreSwapped()
        {
            if (!swapped) return;
            swapped = false;
            ReadAll(now, valuesNow);
            int slotCount = ActiveSlotCount();
            for (int i = 0; i < slotCount; i++)
                SetSlot(i, cur[i] + (now[i] - blended[i]));
            if (trackedEffect == null) return;

            for (int i = 0; i < valuesNow.Length; i++)
            {
                WeaponRecoilValue value = trackedValues[i];
                if (value == null || value.Process == null) continue;
                value.Process._current = valuesCur[i] + (valuesNow[i] - valuesBlended[i]);
            }
        }

        static int ActiveSlotCount() => trackedEffect == null ? BaseSlotCount : SlotCount;

        static void ReadAll(Vector3[] slots, Vector3[] values)
        {
            int slotCount = ActiveSlotCount();
            for (int i = 0; i < slotCount; i++)
                slots[i] = GetSlot(i);
            if (trackedEffect == null) return;

            for (int i = 0; i < values.Length; i++)
            {
                WeaponRecoilValue value = trackedValues[i];
                if (value == null || value.Process == null) continue;
                values[i] = value.Process._current;
            }
        }

        // Writes the blended pose and records each value in blended / valuesBlended right after its field write.
        static void WriteBlended(Vector3[] slots, Vector3[] values)
        {
            int slotCount = ActiveSlotCount();
            for (int i = 0; i < slotCount; i++)
            {
                SetSlot(i, slots[i]);
                blended[i] = slots[i];
            }

            if (trackedEffect == null) return;

            for (int i = 0; i < values.Length; i++)
            {
                WeaponRecoilValue value = trackedValues[i];
                if (value == null || value.Process == null) continue;
                value.Process._current = values[i];
                valuesBlended[i] = values[i];
            }
        }

        static Vector3 GetSlot(int slot)
        {
            switch (slot)
            {
                case HandsPositionSlot: return handsPosition.Current;
                case HandsRotationSlot: return handsRotation.Current;
                case CameraRotationSlot: return cameraRotation.Current;
                case CameraPositionSlot: return cameraPosition.Current;
                case SwaySlot: return sway._pPos;
                case TurnPositionSlot: return turnAway._position;
                case TurnRotationSlot: return turnAway._rotation;
                case TurnCameraShiftSlot: return turnAway._cameraShift;
                case TurnLElbowShiftSlot: return turnAway._lElbowShift;
                case TurnRElbowShiftSlot: return turnAway._rElbowShift;
                case RecoilHandPositionCurrentSlot: return handPositionRecoil.Current;
                case RecoilHandPositionOffsetSlot: return handPositionRecoil.Offset;
                case RecoilHandRotationCurrentSlot: return handRotationRecoil.Current;
                case RecoilHandRotationOffsetSlot: return handRotationRecoil.Offset;
                case RecoilCameraRotationCurrentSlot: return cameraRotationRecoil.Current;
                default: throw new ArgumentOutOfRangeException(nameof(slot));
            }
        }

        static void SetSlot(int slot, Vector3 value)
        {
            switch (slot)
            {
                case HandsPositionSlot: handsPosition.Current = value; break;
                case HandsRotationSlot: handsRotation.Current = value; break;
                case CameraRotationSlot: cameraRotation.Current = value; break;
                case CameraPositionSlot: cameraPosition.Current = value; break;
                case SwaySlot: sway._pPos = value; break;
                case TurnPositionSlot: turnAway._position = value; break;
                case TurnRotationSlot: turnAway._rotation = value; break;
                case TurnCameraShiftSlot: turnAway._cameraShift = value; break;
                case TurnLElbowShiftSlot: turnAway._lElbowShift = value; break;
                case TurnRElbowShiftSlot: turnAway._rElbowShift = value; break;
                case RecoilHandPositionCurrentSlot: handPositionRecoil.Current = value; break;
                case RecoilHandPositionOffsetSlot: handPositionRecoil.Offset = value; break;
                case RecoilHandRotationCurrentSlot: handRotationRecoil.Current = value; break;
                case RecoilHandRotationOffsetSlot: handRotationRecoil.Offset = value; break;
                case RecoilCameraRotationCurrentSlot: cameraRotationRecoil.Current = value; break;
                default: throw new ArgumentOutOfRangeException(nameof(slot));
            }
        }

        internal static void Write(StringBuilder sb)
        {
            if (harmony == null)
            {
                sb.AppendLine("Viewmodel interpolation is not installed.");
                return;
            }

            sb.Append("setting: ").AppendLine(enabledSetting.Value ? "on" : "off");
            if (failed) sb.Append("DISABLED after failure: ").AppendLine(failure);
            sb.Append("window: ").Append(Time.frameCount - windowStartFrame).AppendLine(" frames");
            sb.Append("frames blended: ").Append(framesBlended)
                .Append(", skipped (inactive): ").Append(framesSkipped)
                .Append(", without history: ").Append(framesWithoutHistory).AppendLine();
            sb.Append("tick snapshots: ").Append(tickSnapshots).AppendLine();
            sb.Append("resets:");
            for (int i = 0; i < ResetReasonCount; i++)
                sb.Append(' ').Append(ResetNames[i]).Append('=').Append(resets[i]);
            sb.AppendLine();
            sb.Append("last alpha: ").Append(lastAlpha.ToString("F3", Inv))
                .Append(", recoil swap: ").Append(lastRecoilSwap ? "yes" : "no")
                .Append(", recoil value processes: ").Append(valuesCur.Length).AppendLine();
            ResetWindow();
        }
    }
}
