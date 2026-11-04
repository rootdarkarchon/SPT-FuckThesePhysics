# Handoff: batch per-player movement-collision overlaps onto job workers

Status: closed. The batching proposal (§3) was built for 1.1.0 development and dropped (§8); §8 step 2 shipped instead as the disabled-collider extrusion skip (`docs/design.md` §12), verified in game by the user on 2026-11-04. Written 2026-11-04 from SPT-Profiler measurements and static analysis. Targets EFT 0.16.9.40743 (Assembly-CSharp MVID `cc2d80b06d5b4cb1a5816d2cc901d4c7`), Unity 2022.3.43f1, SPT 4.1.6, Fika 2.4.4.

Tags: **CONFIRMED** = read in decompiled code, dumped assets or a capture. **[INFERENCE]** = reasoned, not observed. **UNVERIFIED** = not checked. Member ids are Assembly-CSharp tokens unless noted.

Supporting evidence in SPT-Profiler (`$SOURCE_FOLDER/SPT-Profiler/docs/research/`):
- `cpu-sampling-2026-11-04.md`: ETW captures on the release player.
- `movement-collision-2026-11-04.md`: full static analysis, query budgets and the Streets collider inventory.
- `cpu-levers-2026-11-04.md`: physics stepping and sync.

## 1. Problem

The raid is main-thread bound and the job workers are mostly idle.
- CONFIRMED (two ETW captures, Streets, Fika host, about 23 bots plus the player, 60–70 FPS): the main thread is busy 97.5–98.8% of wall time.
- CONFIRMED: all 31 job workers combined do about 25 ms of work per 40 ms frame (development-player capture).

Per-player movement collision costs about **11–13% of the main thread, roughly 1.8–2.1 ms per frame**:

| Site | Main-thread share | Native leaves underneath |
|---|---:|---|
| `PlayerStateContainer.OnStateMove` (06003580) → head extrusion | 7.9–9.6% | `distancePointTriangleSquared` 2.5, `intersectTriangleBoxBV4` 1.4, `gSphereVsMeshCallback` 1.3, `AABBTreeOverlap<SphereAABBTest>` 0.7, `BV4_ProcessStreamSwizzledNoOrderQ<LeafFunction_BoxOverlapCB>` 0.7 |
| `MovementContext.AnimatorStatesLateUpdate` (06008A63) → `SimpleCharacterController.Move` (060031A9) | 3.1–4.8% | `AABBTreeOverlap<SphereAABBTest>` 0.84, `AABBTreeRaycast` 0.69, BV4 box overlap 0.17 |

Caching between frames is ruled out: bots move practically every frame, so the query inputs are never unchanged. The proposal below keeps the per-frame query but moves its expensive part off the main thread.

## 2. What the game does

**Head extrusion.** Path: animator callback → `MovePlayerState.UpdatePosition` (06008BF1) → `MovementState.ApplyMotion` (06008BC4) → `LimitMotion` (06008BC1) → `MovementContext.LimitMotionXZ` (06008B2C) → `PlayerOverlapManager.Calculate` (06003568) → `ColliderExtrusion.CalculateThroughMotion` (06003123) → `Calculate(Vector3, Quaternion)` (06003125).
- CONFIRMED: runs for every player whose `POM` is non-null, **including for zero motion**.
- Per call:
  - `RefreshNeighbours` (06003126) → `Physics.OverlapSphereNonAlloc(center, radius, buffer[16], mask, QueryTriggerInteraction.Ignore)`.
    - Centre = predicted collider position + current bounds-centre offset.
    - Radius = largest world-space bounds extent + `_castHalo`. The motion term is zero on this path.
    - Mask = `PlayerCollisionTestMask` = `LowPolyCollider(18) | DoorLowPolyCollider(9) | Terrain(11) | Player(8)`, set in `PlayerOverlapManager.Init` (06003560).
  - Then `CalculateDepenetration` (06003127): one `Physics.ComputePenetration` per returned, non-ignored collider, with the offsets **summed**.
- CONFIRMED (dump `Assets/Content/CommonPrefabs/PlayerSuperior.prefab`, also `PlayerSimple`, `PlayerZombie_Superior`): the head collider is a capsule r 0.22 m, h 0.70 m (prone r 0.175 m, h 1.6 m), `_castHalo: 0`. [INFERENCE] The radius is ≈0.35 m standing and up to ≈0.8 m prone.
- Prone adds up to 4 ground rays (`LimitProneMotion` 06008B30) and a rotation-prediction pass (`RotationOverlapPrediction` 06008B2D) that runs another extrusion. Camera extrusion (`ExtrudeCamera`, box 0.13×0.23×0.23 m) runs only for non-AI players, from `Player.LateUpdate` (0600813B).

**Character controller.** `SimpleCharacterController.Move` (060031A9) does nothing for zero motion.
- Otherwise there are n = floor(d / `_fixedDeltaDistance`) substeps plus one remainder `SingleMove` (060031AD), which runs even when the remainder is zero.
- Each `SingleMove`:
  - `UpdateNeighboursColliders` (060031B0): `OverlapSphereNonAlloc`, buffer 64, `PlayerCollisionsMask`, triggers ignored, radius = bounds extent + halo.
  - Up to 1 + T `ResolvePenetration` passes (060031AF) over the neighbours. T defaults to 3 in the field initializers; serialized values are UNVERIFIED.
- Then one grounding `Physics.Raycast`, and the optional depenetration-workaround ray (060031BB).

**Geometry.** CONFIRMED (dump `Assets/Content/Locations/City/**/*.unity`, 247 files): 95,669 colliders on the extrusion layers.
- 83% are non-convex meshes: 78,944 on LowPolyCollider, 871 on DoorLowPolyCollider.
- Triangles per mesh: median 12, p90 60, max 6,284 (whole-building collision meshes such as `Primorskiy_60a_walls_COLLIDER`).

**Mods.** CONFIRMED (sources in `$TARKOV_MOD_SOURCES`): no installed mod with available source patches `ColliderExtrusion`, `PlayerOverlapManager`, `LimitMotionXZ` or `SimpleCharacterController`.
- SAIN 4.5.1 forces `MovementContext.IsAI` (06008A74) to false. None of the collision methods read that getter, so this does not change which collision code runs.
- Fika's `ObservedMovementContext` skips POM limiting only for remote observed players. That doesn't apply to host bots.
- Third-party binaries without source were not checked. Read the live Harmony inventory at startup (see §6).

## 3. Proposal

Move the sphere-overlap **broad and narrow phase** of the head extrusion onto Unity's batched query jobs. Keep `ComputePenetration` on the main thread.

CONFIRMED (`UnityEngine.PhysicsModule`, MVID `9ee453a2…`): `OverlapSphereCommand`, `OverlapCapsuleCommand` and `OverlapBoxCommand` exist in this Unity build. EFT already runs batched queries on the workers (`BatchQuery.ExecuteRaycastJob`, `Physics.SyncBatchQueries` in the captures).

### 3.1 Static/dynamic split (recommended form)
Split the extrusion mask:
- **Static layers** `LowPolyCollider | Terrain`: tens of thousands of triangle meshes. Here the cost is.
- **Dynamic layers** `Player | DoorLowPolyCollider`: few colliders that move. Keep the vanilla synchronous query for these, with the original buffer and settings.

Once per frame, for every player with a POM, schedule one `OverlapSphereCommand` against the static layers:
- Centre = the player's head-collider bounds centre at schedule time.
- Radius = the vanilla radius plus a margin `m`.

Pick `m` so that it bounds how far the head collider can move between scheduling and the extrusion call, including the predicted motion (`ApplyMotion` passes at most one frame's displacement). Complete the batch before the first extrusion of the frame.

In the extrusion call, replace the vanilla `OverlapSphereNonAlloc` with:
1. **Guard.** If the collider moved more than `m - ε` since scheduling, or the player has no batch entry (spawned this frame), run vanilla unchanged.
2. **Candidates.** The static candidates from the batch, plus the dynamic-layer hits from a synchronous query.
3. **Penetration.** Run the vanilla `CalculateDepenetration` over the union. A static candidate that doesn't actually overlap returns no penetration and adds nothing to the sum.

**Why the result is preserved.** [INFERENCE]
- Static colliders don't move, so the enlarged sphere returns a superset of the vanilla hits. The guard covers self-motion.
- Penetration offsets are summed, so the order of candidates doesn't matter.
- Dynamic colliders use exactly the vanilla query.
- **Known difference:** vanilla keeps at most 16 hits and drops an arbitrary subset beyond that. The batch tests every candidate. This only matters when more than 16 colliders overlap one head, so measure how often the vanilla buffer fills (§6).

### 3.2 Expected effect ([INFERENCE] from capture shares)
- Moved off the main thread: the overlap part, `gSphereVsMeshCallback` + `distancePointTriangleSquared` + `AABBTreeOverlap<SphereAABBTest>`. About 4–5% of the main thread, roughly 0.6–0.8 ms per frame at 60–70 FPS.
- Stays on the main thread: `ComputePenetration` (≈2%, the BV4 box leaves are most likely the capsule-vs-mesh penetration midphase), plus a little more for the extra candidates, plus the small dynamic-layer query.
- Wall-clock gain only if the main thread doesn't wait at completion. Schedule as early as the guard allows.

### 3.3 Optional second step: controller neighbours
`UpdateNeighboursColliders` can use the same technique, but only for the **first** `SingleMove`. Later substeps start from the position the previous substep resolved. Mask `PlayerCollisionsMask` is derived from the layer collision matrix (`LayersMaskController` 0200068F); split it into static and dynamic layers the same way. This is lower value (≈1–1.5% overlap share) and the substep logic adds more risk. Do it after 3.1 has been measured.

## 4. Hook points
- **Scheduling.** A custom `PlayerLoopSystem` inserted after `Update.ScriptRunBehaviourUpdate` (the world tick and Fika's bot update have run by then). Complete it in another system at the start of `PreLateUpdate`, before `PreLateUpdate.DirectorUpdateAnimationBegin`; that is where the animator callbacks, and so the extrusions, run.
  - UNVERIFIED: whether any code moves player transforms between those points. Candidates are SAIN `BotMover.SetPosition`, teleports and vaulting. The per-call guard catches any that do.
- **Replacement.** A Harmony prefix on `ColliderExtrusion.RefreshNeighbours(Vector3, Vector3)` (06003126) for instances registered as head extruders of a player with a batch entry. It fills the neighbour buffer and count from the merged candidate set and skips the original. Resolve the method by name and parameter types and throw `MissingMethodException` with the full signature if it is absent (house rule).
  - Prefer `RefreshNeighbours` over `Calculate`: it leaves `CalculateDepenetration`, ignore lists and thresholds untouched.
  - The neighbour buffer has a fixed size of 16, so the merged set may need its own array plus a matching prefix on `CalculateDepenetration` (06003127). Read both bodies before choosing.
- **Player set.** Players with a non-null `POM` among `GameWorld.AllAlivePlayersList`. Each player's head `ColliderExtrusion` is reached through `PlayerOverlapManager`. The field names are obfuscated; resolve them by type and record them in `docs/` against this build.
- **Fika.** CONFIRMED (Fika `v2.4.4` `Fika.Core/Main/ObservedClasses/ObservedMovementContext.cs:63–97`):
  - On every machine, remote players are `ObservedPlayer`s whose `ObservedMovementContext` overrides `LimitMotionXZ`, `LimitProneMotion` and `ApplyGravity` with no-ops. The head extrusion therefore never runs for them.
  - Their `DirectApplyMotion` still calls `CharacterController.Move`. The controller type comes from `AppEnvironment.Config.CharacterController.ObservedPlayerMode` (`CoopHandler.cs:465–470`); the configured value is UNVERIFIED.
    - If it is the impostor controller, `ImpostorCharacterController.Move` (0600364D) does at most one `Physics.Raycast` (`IsValidMove` 0600364E) and no overlap or penetration.
  - A Fika **client** therefore runs the full extrusion and controller pipelines only for its own player. The host runs them for its own player and all host-side bots.
  - The feature matters on the host (or offline) and does next to nothing on clients.

## 5. Risks
- **Superset assumption.** If anything on `LowPolyCollider` or `Terrain` moves at runtime (breakables, animated props, vehicles), a mover can be missed. Check the dump and runtime for movable objects on those layers. If any exist, move their layer handling to the dynamic side or exclude those colliders.
- **Truncation at 16 hits** (see 3.1).
- **Native containers and jobs.** Allocate persistent `NativeArray`s sized for the maximum player count and dispose them on raid end and plugin destroy. Batch results are `ColliderHit`. Turn them into `Collider` on the main thread only.
- **Ordering assumptions** about where in the player loop transforms change. The guard turns violations into vanilla fallbacks, not wrong results.
- **Interaction with other mods** patching the same members. None known; verify at runtime (§6).

## 6. Verification plan
1. **Counting build first, no behaviour change.** Per frame, record:
   - extrusion calls;
   - vanilla hit counts, and how often 16 is reached;
   - how many static vs dynamic hits;
   - head displacement between the proposed scheduling point and each call;
   - and log the live Harmony patch info for the hooked members once.

   This settles `m`, the truncation frequency and the timing assumption.
2. **Equivalence check, Debug toggle.** Run vanilla and batched side by side for N frames and compare the summed penetration offsets. They should agree exactly except for frames where vanilla truncated at 16.
3. **Cost.** Take an ETW capture with SPT-Profiler (`scripts/profile-cpu.ps1`, JIT map via its `Write JIT symbol map` action) with the feature on. Compare **shares within the capture** (`OnStateMove` inclusive, `gSphereVsMeshCallback` on the main thread vs workers), not frame times across raids. Raids are not reproducible, and these shares were stable across two captures within about 3 points.
4. Behaviour: in-game check by the user (bots near walls and doors, prone in tight spaces, vaulting).

## 7. Scope and versioning
This is a new feature beyond the current non-goals in `design.md` §1. It changes how a query is computed, not EFT's physics driver. If adopted:
- bump the minor version;
- add a config toggle, on by default once verified;
- give the feature its own Harmony id (`PluginInfo.Guid + ".movementbatch"`);
- update `design.md` and the README `## Fika` section (host-side only; no effect on clients).

## 8. Result of the first batched build (v1.1.0, capture 2026-11-04 16:07)

Evidence: SPT-Profiler ETW capture `artifacts/profiles/cpu-20261104-160749` (Streets, Fika host, 23 bots, release player), plus the mod's own physics report in `LogOutput.log`.

- CONFIRMED (report): `Calculate` calls 266,262: **11,006 batched, 255,256 vanilla fallbacks**, of which `noSlot` = 252,712. On average 1.0 player was scheduled per frame. Only the local player is ever batched.
- CONFIRMED (ETW, 120–185 s window): `ColliderExtrusion.RefreshNeighbours` is still 7.4% of the main thread, called through the `DMD<ColliderExtrusion::Calculate>` fallback. `gSphereVsMeshCallback` is still 4.1%. The batched path itself costs <0.1%.

**Why bots get no slot.** CONFIRMED (Assembly-CSharp):
- `PlayerOverlapManager.Init` (06003560) ends with `Off()` (06003567), which sets every head collider and the camera collider to `enabled = false`.
- `On()` (06003564) has exactly one caller, `GamePlayerOwner` (06009990), so it only runs for the local player.
- Bots' head colliders therefore stay disabled, and `Schedule()` skips them at `if (collider == null || !collider.enabled) continue;`.

**What vanilla does for those bots.**
- CONFIRMED (Unity 2022.3 docs, `Collider.bounds`): "this will be an empty bounding box if the collider is disabled".
- CONFIRMED (`CalculateCastRadius` 0600311E, `RefreshNeighbours` 06003126): the radius comes from `_collider.bounds.extents`, and the centre is `position + (_collider.bounds.center - _collider.transform.position)`.
- [INFERENCE] For a bot this gives **radius 0**. The centre is the head's predicted position minus its current position, i.e. roughly the frame's motion vector, which is **next to the world origin**, not near the bot.
- [INFERENCE] Every bot therefore queries a point at the map origin each call. Whatever static mesh contains that point is narrow-phase tested again and again; this would explain the ~0.1 ms per call. `CalculateDepenetration` (06003127) then tests the bot's capsule, posed at the bot, against colliders at the origin, which yields no penetration unless the bot stands near the origin. **Bot head extrusion would then be pure cost with a zero result.**

**Next step for the implementers.**
1. **Counting check.** For extruders whose collider is disabled, record the query centre, radius, neighbour count, neighbour names, and how often `GetDepenetration()` is non-zero. Expected: radius 0, centre within a few metres of the origin, non-zero results never (or only for bots near the origin).
2. **If confirmed:** a prefix that returns zero depenetration without querying, for extruders whose collider is disabled. This keeps the result vanilla produces today and removes about 7% of the main thread outright; no batching is needed for bots. Gate it to "collider disabled", **not** to "is a bot", so behaviour stays identical if anything ever enables a bot's colliders.
3. Batching stays only for the local player (≈0.2% share). Consider dropping it to remove complexity.
