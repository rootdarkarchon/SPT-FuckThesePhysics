# Handoff: first-person weapon judder from un-interpolated fixed-tick sway

Status: implemented in 1.2.0 (see `docs/design.md` §13), awaiting in-game validation. EFT 0.16.9.40743, SPT 4.1.6, Unity 2022.3.43f1. Written 2026-11-06 from an investigation in SPT-FuckTheseFX.

## Symptom

While the camera holds still, the first-person weapon and hands "swim": they move in small irregular steps. This shows with TAA, DLSS or no AA at all, so it is real geometry motion, not a motion-vector or upscaler artefact. Under DLSS it is most visible at the scope's eyepiece rim.

## Evidence

- **Captures:** FuckTheseFX temporal captures (`Source/Diagnostics/TemporalCapture.cs`, analyzer `scripts/Analyze-TemporalCapture.py` in that repo). They show the motion vectors are correct to about 0.3 px.
  - With the world still, the viewmodel moves 0.2–0.7 internal px per frame, back and forth.
  - Some frames move exactly 0 px, followed by a jump (e.g. 0, 0, +0.68, +0.65 px). Frame times were 7–25 ms.
- **This mod is not the cause:** it has no time-stepping, `FixedUpdate`, player or animation code. HollywoodFX, SAIN and Fika (checked from source) don't change this cadence either.
- **Not yet captured:** a 16-frame capture with the new `pose` block (weapon pose in camera space plus `fixedStepsSincePrevFrame`) should show weapon motion tracking the tick count 1:1.

## Mechanism (vanilla EFT, read from Assembly-CSharp `cc2d80b0-6d5b-4cb1-a581-6d2cc901d4c7`)

- `EFT.Player.FixedUpdate` (`0600829D`) only counts ticks: `_nFixedFrames++; _fixedTime += Time.fixedDeltaTime;`.
- `EFT.Player.LateUpdate` (`0600813B`) calls `VisualPass` (`0600813A`), which calls `ProceduralWeaponAnimation.ProcessEffectors((_nFixedFrames > 0) ? _fixedTime : _armsTime, Mathf.Max(0, _nFixedFrames), Motion, Velocity)`. `LateUpdate` then resets `_nFixedFrames` and `_fixedTime`.
- `FirstPersonStrategy.ProcessEffectors` (`06003F1B`) divides dt by `nFixedFrames` and runs the whole effector set **`nFixedFrames` times**:
  - `MotionReact.FixedTracking/Process`, `ForceReact`, `Breath`, `Walk`, `HandShakeEffector`, `TurnAway`;
  - the springs `HandsContainer.HandsPosition/HandsRotation.FixedUpdate` and `SwaySpring.Process`;
  - `Shootingg.CurrentRecoilEffect.FixedUpdate`, `HandsContainer.CameraRotation/CameraPosition.FixedUpdate`.
- `ApplyTransformations` (`06003F1C`) then writes the transforms once per rendered frame, e.g. `HandsContainer.WeaponRootAnim.localPosition = HandsPosition.Get() + recoil`.
- **No interpolation:** with no physics tick since the last frame the loop runs zero times and the pose freezes. With one or more ticks, the full tick integration lands in a single frame. There is no previous/current pose pair anywhere.
- **Extra noise:** `BreathEffector` adds per-tick random walks (`HipX/HipZ/X/Y` random, `Tremor*`) and a breathing oscillator. `ForceEffector.Process` ignores dt and counts ticks.
- **Result:** at frame rates above the tick rate (`fixedDeltaTime` 0.02 s = 50 Hz expected, not measured), the viewmodel advances in a stop-start staircase against a smoothly moving camera.

## Proposed fix: interpolate the effector outputs between ticks

Use the same idea as `Rigidbody.interpolation`: render the pose one tick late, blended between the last two tick states.

1. **Leave the simulation alone.** The effectors and springs keep running exactly as now, `nFixedFrames` times per frame with fixed dt. That keeps their tuning, stability and tick-counted envelopes (`ForceEffector`) intact.
2. **Snapshot the outputs per tick.** Hook around the per-tick loop body, or wrap `FirstPersonStrategy.ProcessEffectors` and replicate its loop. After each tick, store `prev = cur; cur = outputs`. The outputs are the values `ApplyTransformations` and `ApplyCameraTransformations` consume:
   - spring outputs for `HandsPosition`, `HandsRotation`, `SwaySpring`, `CameraRotation`, `CameraPosition`;
   - the recoil effect offsets;
   - Breath/HandShake write into `CurrentRecoilEffect.HandRotationRecoil.Offset` (not `HandRotationRecoilEffect`).

   Verify the exact getters in `EFT.Animations.PlayerSpring` (`020016BD`) and the strategy's `Apply*` methods before choosing the hook point.
3. **Render the blend.** For `ApplyTransformations`/`ApplyCameraTransformations`, present `lerp(prev, cur, a)` with `a = clamp01((Time.time - Time.fixedTime) / Time.fixedDeltaTime)`. The camera apply can also run in `ProceduralWeaponAnimation.LateTransformations` (left stance / blindfire), so the restore must happen in that postfix as well as after `ProcessEffectors`. The implementation uses component lerp, not slerp, for rotations; see `docs/design.md` §13. Restore the true `cur` values afterwards if anything else reads the springs later in the frame.
4. **Reset on discontinuities.** Set `prev = cur` on weapon or hands-controller change, teleport, first-person ↔ third-person switch, aim-mode snaps, death/respawn, and big `nFixedFrames` jumps after hitches.
5. **Scope:** local first-person player only. Bots, observed players and Fika remote players stay native. Fika-Headless: the `VisualPass` patch in this repo is commented out, so headless does not replace `VisualPass`; the mod still skips install on headless because nothing renders there.

### Risks to settle before shipping

- **Aim and ballistics:** shots originate from weapon transforms (fireport). Interpolation shows the pose up to one tick (~20 ms) late. Decide whether shot origin/direction use the true tick pose (gameplay unchanged, sight picture up to one tick off) or the interpolated pose (gameplay effectively one tick delayed). Read the shot-direction path (`Player.FirearmController` fireport usage) first.
- **Camera:** `CameraRotation`/`CameraPosition` springs also drive the camera. Interpolating the hands but not the camera, or the reverse, would desync the sight picture. Treat both the same way.
- **Feel:** about one tick of extra sway/recoil latency. Consider a config toggle, default on, so you can A/B it.
- Rejected alternative: running the effectors once per rendered frame with frame dt. It changes spring stability and makes the tick-counted `ForceEffector` frame-rate dependent.

## Validation

Use FuckTheseFX's temporal capture (`9. Debug / Temporal capture`, 16 frames) while aiming with light sway, then run `python scripts/Analyze-TemporalCapture.py <folder>` in that repo:

- **Before:** the `Viewmodel pose` section shows `stallThenJump`/`zigzag` events, and weapon motion groups by `fixedStepsSincePrevFrame` (0 ticks = 0 mm).
- **After:** the weapon moves every frame, with no stalls and a smooth delta series.
- Also check DLSS on/off, high and low frame rates (< 50 fps too), recoil, breath hold and weapon swaps, and that point of impact matches the sight picture.
