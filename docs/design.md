# Fuck These Physics: design

Developer-facing description of what v1.3.0 does, why, and how it was checked. User-facing documentation is in `README.md`.

Tags used where the status still matters: **CONFIRMED** = read in decompiled code, prefab data or the in-game log. **[INFERENCE]** = reasoned, not observed. **UNVERIFIED** = not checked.

## 1. Purpose and target

EFT runs its physics step (`Physics.Simulate`) from script and only when something is registered for it. Three things register a rigidbody and never unregister it, so the step runs every frame for the rest of the raid for no reason:

- the landed airdrop crate (offline and Fika host);
- the BTR suspension body (Woods; every machine that renders the BTR).
- planted tripwires (placed by the Partisan boss; see §14).

This mod unregisters the airdrop and BTR bodies at the point where the game itself no longer needs them, and stops planted tripwires from registering at all (§14). It uses EFT's own `UnsupportRigidbody`; it changes nothing else about physics.

A second feature skips the head-collision query for disabled player colliders, which are the bots' head colliders. It does not touch physics registration. It changes how one overlap query is computed, to cut main-thread time (§12). Its runtime verification is not yet done.

A third feature (§13, version 1.2.0) interpolates the local first-person weapon, hands and camera between EFT's 50 Hz effector ticks. It targets viewmodel judder above the tick rate. It changes the presentation of the effector layer, not the simulation; its runtime verification is not yet done.

A fourth feature (§14) stops planted tripwires from registering with the physics step. The mod detects walk-ins itself, every frame, and hands them to the game's own trigger handling. Its runtime verification is not yet done.

Non-goals: changing EFT's physics driver or `CheckRigidbodiesDone` semantics, airdrop scheduling or loot, per-map drop-point coverage, replacing the penetration computation, and batching the character controller's neighbour queries (research §3.3, not implemented).

| Component | Version |
|---|---|
| SPT | 4.1 |
| Unity | 2022.3.43f1 |
| BepInEx | 5 |
| Fika (analysed against) | 2.4.4 |

Fika analysis was done against 2.4.4. The mod targets Fika 2.4.x generally (2.4.6 is expected to behave the same); no Fika version gating exists in code.

Identity: plugin name "Fuck These Physics", version 1.3.0, GUID `com.rootdarkarchon.fuckthesephysics`, output `BepInEx/plugins/FuckThesePhysics/FuckThesePhysics.dll`, package `FuckThesePhysics-1.3.0-SPT4.1.zip`. The disabled-collider extrusion skip (§12) uses the Harmony id `com.rootdarkarchon.fuckthesephysics.extrusionskip`; viewmodel interpolation (§13) uses `com.rootdarkarchon.fuckthesephysics.viewmodelinterp`; the tripwire release (§14) uses the shared id `com.rootdarkarchon.fuckthesephysics`.

### Configuration

| Key | Type | Default | Notes |
|---|---|---|---|
| `Airdrop / ReleaseLandedAirdrops` | bool | on | Live. Read on every `ManualUpdate` postfix; off restores vanilla for crates that land afterwards. |
| `BTR / ReleaseParkedSuspension` | bool | on | Applies from the next BTR stop. |
| `Tripwire / ReleaseIdleTripwires` | bool | on | Live. Planted tripwires are not kept in the physics registry; the mod checks walk-ins itself. Off re-registers armed wires and restores vanilla. See §14. |
| `Movement / SkipDisabledHeadExtrusion` | bool | on | Live. Off restores the vanilla head-collision query for disabled colliders from the next frame. See §12. |
| `Debug / SpawnAirdropKey` | `KeyboardShortcut` | unset | Advanced. Polled in `Plugin.Update`. |
| `Debug / LogPhysicsGate` | bool | off | Advanced. See §8. |
| `Report / Key` | `KeyboardShortcut` | unset | Advanced. Writes the registry and step timing report. See §8. |
| `Debug / VerifyExtrusionSkip` | bool | off | Advanced. Runs vanilla for skipped calls and counts non-zero results in the report. No gain while on. See §12. |
| `Viewmodel / InterpolateFixedTick` | bool | on | Live. Renders sway, recoil, breathing, bob and wall pullback between the 50 Hz ticks. Off restores vanilla from the next frame. Adds up to ~20 ms latency. See §13. |
| `Debug / TraceViewmodelPose` | bool | off | Advanced. Records a 512-frame ring of per-frame viewmodel pose data for the local player and dumps it in the report. Patches only active while on. See §13 "Diagnostics: pose trace". |

Camera-relative viewmodel rendering, precise pose math and the render probe moved to SPT-FuckTheseOptics (2026-11-06). Both mods can be installed together: they patch ProceduralWeaponAnimation-related methods under distinct Harmony ids and share no code.

Advanced entries are hidden unless "Advanced settings" is enabled in Configuration Manager.

## 2. The physics gate (CONFIRMED)

**Setup.** `EFT.AbstractApplication.Awake` sets `PhysicsExtensions.UpdateController.Enabled = true`, because `ApplicationConfig.PhysicsSettings.ManualUpdate` defaults to true. That setter sets `Physics.simulationMode = Script`, `Physics.autoSyncTransforms = false`, `Simulation.UpdateMode = SmoothSimulate` and `UpdateEnabled = false`.

**Per-frame path.** `AbstractApplication.Update` → `PhysicsExtensions.Update()` → `Simulation.Update()` → `SmoothSimulate(dt)` → `Physics.Simulate(avgDt)`. The last call runs only when `CanRunSimulate` is true, which requires script mode and `_updateEnabled`.

**The gate.** `UpdateController.CheckRigidbodiesDone()` runs every frame and on every Support/Unsupport call. It sets `_updateEnabled` when at least one entry in `public static List<RigidbodyData> UpdateController._rigidbodies` meets all of these:

- the rigidbody is non-null;
- its GameObject is `activeInHierarchy`;
- it has no visibility checker, or its checker reports `IsVisible`.

**What the gate ignores, and how entries leave.**

- It never looks at `isKinematic`, `IsSleeping()` or velocity.
- Destroyed or inactive entries are pruned automatically.
- Otherwise an entry leaves only through `UnsupportRigidbody(rb)`, which removes **only the first match**.
- `SupportRigidbody` never deduplicates. A body registered N times needs N Unsupport calls.

**Baseline.**

- No other game code steps the physics scene (`PhysicsScene.Simulate` has no callers).
- No installed mod changes `simulationMode`, `autoSimulation` or `autoSyncTransforms`; Rupture only observes `Physics.Simulate`.
- With nothing registered the step is off. That is EFT's designed default.

**Other registrants.** Full census in `docs/research/gate-holders.md`. Transient ones release on their own: loot drops (up to 15 s), corpse ragdolls (up to about 16 s), grenades (until they explode), lamps (up to 30 s, only after an explosion), broken doors (up to 6 s), `WheelDrive` (about 3 s). Persistent ones: the airdrop crate, the BTR suspension, planted tripwires while armed (Partisan-placed; released by §14), and the MG belt while visible and switched on. Release paths of smoke grenades, `FlareCartridge`, `GrenadeCartridge`, `BeltDetachablePart` and `SmallPhysicsObject` with `TimeOfLife == 0` are UNVERIFIED; they only matter as competing holders.

## 3. Airdrop leak and why release at `Landed` is safe

### The leak (CONFIRMED)

`EFT.Airdrop.ServerAirDrop.Init(int objectId, Vector3 position, Vector3 rotation)` activates the crate, calls `SupportRigidbody(SyncObjectRigidbody)` with no visibility checker, sets `useGravity = false` and `_descending = true`, and starts `SignalFireCoroutine`. Nothing unregisters it:

- The only callers of `UnsupportRigidbody` in the game are `MgBelt.SetPhysicsPartsEnabled`, `CorpseRagdoll.StopRigidbody` and `HideoutArea.CG_SetupItem`.
- The crate is never deactivated mid-raid. None of the three callers of `SynchronizableObjectLogicProcessor.ReturnToPool` fires for a landed crate. SPT's `FixAirdropCrashPatch` runs only at raid end.

From the drop until raid end, every frame runs `Physics.Simulate`.

The crate needs no simulation to exist: the prefab rigidbody is kinematic with gravity off, and the fall, parachute and post-contact rotation are all transform writes. It registers only for landing detection: `SyncObjectCollisionChecker.OnTriggerEnter` on the `land_trigger` child calls `ServerAirDrop.CollisionEnter`, and trigger callbacks fire only during `Physics.Simulate`. The step must therefore stay on while the crate descends.

### Why `Landed`, not `IsStatic` (CONFIRMED)

- `ServerAirDrop.ManualUpdate(float)` switches on `_fallingStage`. `Fall`, `ParachuteStartOpen` and `ParachuteEndOpen` write the transform. **`Landed` has no case**, so no transform write happens after it.
- `CollisionEnter` returns immediately once `!_descending`. The first valid contact sets `_fallingStage = Contact` and `_descending = false`. Three-point contact sets `Landed` directly; one- and two-point contact run the `RotateAfterDrop` transform coroutine, which ends in `Landed`. From `Landed` on, no crate code consumes physics events.
- `SignalFireCoroutine` uses only `HasRaycastHit` (a scene query, no simulation step needed) and unscaled-time waits.
- `ManualUpdate` sets `_syncObject.IsStatic = true` only once `_signalFireCoroutineDone && Landed`, which is `PlaneAirdropSmoke` seconds after landing. In-game, crate 2 logged `IsStatic` about 65,000 frames after `Landed`. Waiting for it would keep the step on for that whole time.
- A static crate never returns to the active list, so `ManualUpdate` is not called again after `IsStatic`.
- `ClientAirDrop.ShakeAfterLanding` has no callers; on the host the `ClientAirDrop` lerp targets the same transform, so it is a no-op.

**Collider pose.** Any final transform delta reaches PhysX through one sync call. The release does that itself (`PhysicsExtensions.SyncTransforms()`, once, immediately before unregistering). EFT also runs a global `Physics.SyncTransforms` every frame for the local player (`Player.ArmsLateUpdate`).

**What keeps working without the step.** Loot (layer-22 trigger plus `LootableContainer`), player collision (layer-18 mesh colliders) and ballistics (layer-12 `BallisticCollider`) all work through queries, which need no simulation step. Confirmed in game: the crates were lootable, solid and hittable after release.

## 4. Airdrop fix (`src/FuckThesePhysics/AirdropRelease.cs`)

### Hooks

Installed with Harmony under the plugin GUID:

| Target | Patch | Purpose |
|---|---|---|
| `ServerAirDrop.Init(int, Vector3, Vector3)` | prefix | Clears the instance's latch (instances are pooled and reused) and logs `Airdrop crate Init`. |
| `ServerAirDrop.ManualUpdate(float)` | postfix | Detects `Landed`, releases, logs lifecycle. |
| `EFT.GameWorld.Dispose()` | postfix | Clears every latch entry at raid end. |

No `GameType`/`IsServer` gate: the postfix only fires where a `ServerAirDrop` exists, which is offline play and the Fika host.

### Latch

`Dictionary<ServerAirDrop, int>`, one entry per crate, with three bits:

| Bit | Meaning |
|---|---|
| `LandedSeen` (1) | `Landed` lifecycle line written. |
| `Released` (2) | Release attempted (setting was on at the time). Never repeated for this drop. |
| `StaticSeen` (4) | `IsStatic` lifecycle line written. |

`ManualUpdate` keeps being called every frame until `IsStatic`, so the postfix path before the first latch change is allocation-free: one enum compare against `EAirdropFallingStage.Landed`, one dictionary lookup, bit tests. It returns early when all three bits are set. The latch is committed **before** any action runs, so an action that throws is attempted and logged once per drop, never every frame. Because `Released` is set only when the setting is on, enabling the setting while a crate sits in `Landed` (before `IsStatic`) releases it on the next frame.

### Release

Once per drop, in `Release`:

1. Count registry entries whose `rigidbody == SyncObjectRigidbody` (usually 1, but Support never deduplicates, so count rather than assume).
2. If any: `PhysicsExtensions.SyncTransforms()` once, then `UnsupportRigidbody(rb)` that many times.
3. Log `Airdrop crate release id=… removed=N remainingRegistry=… simulationEnabled=…`.

Not done, deliberately: destroying the rigidbody (`Init` dereferences it again on pool reuse), deactivating the GameObject, toggling `isKinematic`, writing `simulationMode` or `UpdateEnabled`.

### Accessors for Sirenix-typed fields

`ServerAirDrop._syncObject` is an `AirdropSynchronizableObject`, whose base chain ends in Sirenix's `SerializedMonoBehaviour`. This project does not reference Sirenix, so the fields on that object cannot be bound at compile time. `ObjectId` and `IsStatic` (declared on `SynchronizableObject`) are read through `Func<ServerAirDrop, T>` delegates built once at startup with `System.Linq.Expressions` (`drop._syncObject.ObjectId` / `.IsStatic`) and compiled. They are compiled rather than called through `FieldInfo.GetValue` because `IsStatic` is read every frame until the crate goes static. `IsStatic` and `ObjectId` types are checked (`bool`, `int`) before the accessors are built. `_fallingStage`, `SyncObjectRigidbody` and `SyncObjectTransform` are accessed directly.

### Failure behaviour

- `Install` resolves every target first (the three patch targets, the `_fallingStage`/`_syncObject` fields, `ObjectId`, `IsStatic`, `SyncObjectRigidbody`, `SyncObjectTransform`, `UnsupportRigidbody`, `_rigidbodies`, `SyncTransforms`). Any miss logs a warning naming the member and patches nothing: vanilla behaviour.
- If one of the three patches fails to apply, the ones already applied are reverted. The Harmony owner id is shared with `SimulateTimer` and `BtrSuspensionRelease`, so rollback calls `Unpatch(target, patchMethod)` for exactly the patches this class applied, never `UnpatchSelf`. `Uninstall` (plugin `OnDestroy`) does use `UnpatchSelf`, because the whole plugin is going away.
- Runtime exceptions inside the patches are caught and logged once per site (`Init`, `ManualUpdate`, `Dispose`); they never propagate into EFT.

## 5. BTR leak (CONFIRMED; full analysis in `docs/research/btr-suspension.md`)

`VehicleSuspension` (`EFT.Vehicle.BTRView.suspension`):

- `SuspensionOn()` activates four spring GameObjects, sets `parentBody.isKinematic = false` and `useGravity = true`, calls `SupportRigidbody(parentBody)` with no checker, and sets `joint.yMotion = Free`. While on, it is a real dynamic body and legitimately needs the step.
- `SuspensionOff()` sets `parentBody.isKinematic = true` and `useGravity = false`, deactivates the springs, sets `suspensionEnabled = false`, and **never calls `UnsupportRigidbody`**. `parentBody` stays active, so its entry keeps counting until raid end.

**Cadence.** `BTRView.Update` starts `EnableSuspensionCoroutineStart` when `CurrentSpeed > 0 && !suspensionEnabled && BtrState == Running`, and `DisableSuspensionCoroutineStart` when `CurrentSpeed == 0 && suspensionEnabled`. The flag resets on each stop, so each departure adds another entry and each stop leaves it in place. The BTR pauses 90–150 s at each destination, so a large share of a raid is spent parked with the step held open.

**Maps (SPT 4.1 install).**

- `GlobalConfiguration.BTRSettings.LocationsWithBTR` = `develop`, `Woods`, `TarkovStreets`. Customs and Lighthouse have no BTR.
- **Woods:** the BTR spawns in every raid (`ChanceSpawn` 100), 180–360 s after raid start. This is the affected map.
- **Streets:** has a BTR, but `EnableSuspensionCoroutine` begins with `if (LocationId == "TarkovStreets") skip`, so the suspension body is never enabled or registered. Not affected. EFT's own build therefore runs the BTR without the dynamic suspension.
- **`develop`:** same code path as Woods (spawn chance 70). Not a retail map.

**Fika.** Fika does not sync suspension state. The host sends the whole BTR snapshot (position, rotation, `currentSpeed`, state, turret) at 20 Hz, and each client drives its own `BTRView` and suspension from that packet. Every client therefore leaks entries independently of the host, so the BTR fix must run on clients too.

**Gameplay role of the dynamic suspension.** The authoritative BTR (passengers, turret, doors, speed and state) is a separate `BTRVehicle` object moved by transform writes; `BTRView` is a client replica whose root pose comes from the packet. The suspension body is a sprung, world-colliding child that is periodically snapped back; its collisions with the view colliders are ignored. No collision, bot, turret or sync dependency was found. [INFERENCE] its visible effect is body bob and settling. Prefab values (`ActivateSuspensionTime`, default `isKinematic`, joint setup) are UNVERIFIED.

## 6. BTR fix (`src/FuckThesePhysics/BtrSuspensionRelease.cs`)

Single Harmony postfix on `VehicleSuspension.SuspensionOff()`:

```text
SuspensionOffPostfix(VehicleSuspension __instance):
  if !ReleaseParkedSuspension.Value: return
  rb = __instance.parentBody; if rb == null: return
  n = count of UpdateController._rigidbodies entries with .rigidbody == rb
  if n == 0: return
  repeat n: UnsupportRigidbody(rb)
  log: pos, removed n, remainingRegistry, simulationEnabled
```

- The body is already kinematic with gravity off and the springs inactive when `SuspensionOff` returns, so nothing simulates it. The next `SuspensionOn` registers it again, so the step comes back when the BTR drives off.
- It runs once per BTR stop, so there is no latch; it allocates only for the log line.
- It runs on every machine, including Fika clients, because each runs its own `BTRView`.
- It counts and removes every entry rather than one, so entries accumulated before the mod loaded or by any double-On are also cleared.
- `Install` resolves `VehicleSuspension.SuspensionOff()`; if missing it logs a warning and patches nothing. `Uninstall` removes only this class's patch (`Unpatch(target, patch)`), because the Harmony id is shared. Exceptions are caught and logged once.
- The setting is read on each call, so toggling it applies from the next stop.

## 7. Debug airdrop trigger (`src/FuckThesePhysics/AirdropSpawner.cs`)

`Debug / SpawnAirdropKey`, polled in `Plugin.Update` (main thread). Calls the public virtual `EFT.GameWorld.InitAirdrop(string lootTemplateId = null, bool takeNearbyPoint = false, Vector3 position = default)` as `world.InitAirdrop(null, true, player.Position)`: nearest drop point, server picks the loot type. That bypasses timers, chance and `CanSummonAirdrop` and runs the real chain: plane → `/client/airdrop/loot` → parachute drop → landing. It does not touch `ServerAirdropManager` timers or counters. Every member used is public in Assembly-CSharp (SPT 4.1), so no reflection.

**Guards.** Each failed guard logs a warning with the reason and does nothing:

1. `Singleton<GameWorld>.Instantiated` and a non-null instance (not in raid otherwise).
2. The world is a `ClientGameWorld`. `FikaHostGameWorld` derives from it, so this covers offline and Fika host. `FikaClientGameWorld.InitAirdrop` is an empty override.
3. `ClientSynchronizableObjectLogicProcessor.ServerAirdropManager` is non-null (false on Fika clients).
4. `manager.IsInited`.
5. `manager.CachedAirdropParameters != null`.
6. `manager._airdropPoints` is non-empty.
7. `MainPlayer` is non-null.

The whole call is wrapped; an exception is logged as an error and never thrown into the game. A successful request logs `airdrop spawn requested at <pos> (drop points available before take=N)`.

**Dead ends considered** (details in `docs/research/airdrop-trigger-debug.md`): `Player.DebugSpawnAirdrop` is an empty stub; `spt-debugging` has no airdrop code; Fika's `CommandPacket.SpawnAirdrop` has no handler; forcing the schedule through `base.json` fails for solo raids (timer-gated, and `MinPlayersCountToSpawnAirdrop = 6` on every map).

**Failure modes that are not mod errors:** the plane vanishes if the loot request fails after 3 retries; nothing spawns (with an error from the game) if the plane's start point is out of level bounds or the map has no drop points.

## 8. Diagnostics

All read-only and independent of the fixes.

- **`GateLog.cs`** (`Debug / LogPhysicsGate`). Logs `physics gate UpdateEnabled=… registry=… frame=…` on the first tick after enabling and on every `Simulation.UpdateEnabled` change. Allocation-free while nothing changes.
- **`SimulationOwners.cs`** (part of the `Report / Key` report). Dumps `UpdateController._rigidbodies` under the exact `CheckRigidbodiesDone` rule: each entry is marked as counting or not, with its path, visibility state, colliders and the script types on it and its ancestors. Entries are grouped by owner, with a closing verdict line of the form `N entries count toward keeping simulation on; owner types among them: …`.
- **`SimulateTimer.cs`** (part of the `Report / Key` report). Observation-only Harmony prefix/postfix pairs on `PhysicsExtensions.Simulation.Update`, `UnityEngine.Physics.Simulate(float)` and a prefix on `PhysicsExtensions.UpdateController.SimulationEnabled(bool)`. Per reporting window: call counts, total/max milliseconds, percentiles from a 4096-sample ring buffer, and the last 32 enable/disable transitions.
- **Report.** `Report / Key` writes both sections to the BepInEx log and to `BepInEx/plugins/FuckThesePhysics/reports/`.
- **Disabled-collider extrusion skip counters** (part of the `Report / Key` report). Since the last report: skipped calls, vanilla-verified calls, non-zero results and max magnitude in verify mode, and the last non-zero sample. Plain counters, reset on each report. See §12.
- **Viewmodel interpolation counters** (part of the `Report / Key` report). Frames blended, tick snapshots, resets by reason, frames skipped (inactive), last alpha, and recoil swap active. See §13.
- **Lifecycle log lines** (BepInEx logger, because EFT suppresses `Debug.Log`): crate `Init`, `Landed` (with release on/off), `release` (removed / remainingRegistry / simulationEnabled), `IsStatic`; and one line per BTR suspension release.

## 9. Who is affected

| Setup | Airdrop crate | BTR suspension |
|---|---|---|
| Stock SPT offline | Affected. `ClientAirDrop(offlineServerLogic: true)` → `ServerAirDrop` registers the crate. Fix applies. | Affected on Woods. Fix applies. |
| Fika host / headless | Affected. Fika's prefix on `GetSyncObjectStrategyByType` builds `ClientAirDrop(FikaBackendUtils.IsServer)`, so only the host owns a `ServerAirDrop`. Fix applies. | Affected on Woods. Fix applies. |
| Fika client | Not affected: registers nothing, only interpolates toward host packets. The airdrop patches never fire. | Affected on Woods: each client runs its own `BTRView`. Fix applies if the mod is installed on the client. |

Neither Fika nor Rupture patches `ServerAirDrop.ManualUpdate`, `ServerAirDrop.Init`, `PhysicsExtensions` or `VehicleSuspension`, so there are no Harmony conflicts among the targets. ORBIT's `AirdropLandedPatch` hooks `ClientAirDrop.PlayLandingSound`, a different method.

Extrusion skip (§12) runs wherever the head extrusion runs for bots: offline, the Fika host and headless hosts. A Fika client's own player has an enabled collider, so nothing is skipped there, and remote players never run the extrusion there.

Viewmodel interpolation (§13) runs only on the local first-person player (including while mounted or on a stationary weapon), on offline and Fika clients and host. It is not installed on Fika headless. Bots, observed players and remote players are not affected.

Tripwire release (§14) runs on every machine where wires exist. Wires are placed by the Partisan boss, so on Fika that is the host. On clients the detector also runs, and `CollisionEnter` reaches the same empty `TriggerTripwire` override as vanilla. Walk-in authority on the host is unchanged.

## 10. Rejected alternatives

- **Patch `CheckRigidbodiesDone` to ignore kinematic bodies.** Changes the gate for every registrant (global blast radius).
- **Patch `CanRunSimulate`.** Breaks every physics user.
- **Skip `SupportRigidbody` in `ServerAirDrop.Init`.** Breaks landing detection (trigger callbacks need the step).
- **Release the crate at `IsStatic` instead of `Landed`.** Safe, but keeps the step on for the smoke duration after landing (see §3).
- **Destroy the crate rigidbody, deactivate the GameObject, or toggle `isKinematic`.** `Init` dereferences the rigidbody again on pool reuse; the others change game state for no gain.
- **BTR: dedupe in a `SuspensionOn` prefix.** Marginal once the `SuspensionOff` release exists (entries are at most 1 at any time), and does not fix the parked-hold by itself.
- **BTR: skip the suspension entirely, as EFT does on Streets.** Changes visuals on Woods and removes the BTR from the gate even while driving. Needs an explicit policy decision and prefab data that is UNVERIFIED.
- **Movement collision batching of the head query (v1.1.0 development build, dropped).** It ran only for the local player: `PlayerOverlapManager.Init` ends with `Off()`, and only `GamePlayerOwner` calls `On()`, so bots never got a batch slot. In game (research §8), 252,712 of 266,262 calls had no slot, and the batched path cost under 0.1% of the main thread. It is replaced by the disabled-collider skip (§12), which covers the bots without a batch. The batching code is removed; 1.1.0 was never released.

## 11. In-game verification

Woods, Fika host, 2026-10-31, from `LogOutput.log`, for the airdrop and BTR fixes. The user ran this. The disabled-collider extrusion skip was verified separately by the user on 2026-11-04 (see §12).

- **Airdrop.** Two debug-spawned crates. Each logged `release removed=1 remainingRegistry=0 simulationEnabled=False` right after `Landed`. Physics ran only during the fall (about 10,000 frames each). Both crates were lootable, solid and hittable. `IsStatic` was logged about 65,000 frames after landing for crate 2, which is why release is at `Landed`.
- **BTR.** Three drive/stop cycles. Each stop logged `removed=1`, the gate was off while parked, and no entries accumulated.
- **Transient registrants.** Short-lived registrants (raid start, bot spawn) still toggle the gate on and off.
- **No mod warnings or errors** in the log.
- **Coexistence.** ORBIT's `AirdropLandedPatch` (hooks `ClientAirDrop.PlayLandingSound`) showed no conflict.

### Not yet tested

- Leak reproduction with `ReleaseLandedAirdrops` off (the toggle is implemented and read live, but the off-state was not exercised in game).
- Grenades and ragdolls while a crate is landed. Rupture replaces ragdolls, so this may not be observable.
- Fika client: the BTR release on a client, and the client's handling of the crate `IsStatic` packet ([INFERENCE] harmless).
- Maps other than Woods.
- Disabled-collider extrusion skip (§12): Fika clients (expected no-op: a client's own player has an enabled collider).
- Release paths of the UNVERIFIED registrants listed in §2.
- Viewmodel interpolation (§13): not run in game yet. Covers the temporal capture before/after, DLSS on/off, high and low frame rates, recoil, breath hold, weapon swaps, point of impact against the sight picture, and Fika client behaviour.
- Tripwire release (§14): not run in game. Covers: a report with Partisan wires shows no `tripwire(Clone)` entries and Simulate near transient-holder levels; walking between the stakes trips a wire once; walking up to a stake and disarming does not trip it; a bot's ignore rate matches `CHANCE_TO_IGNORE_TRIPWIRE`; shooting trips a wire; an inert wire does not fire; no double trigger when a corpse holds the step during a crossing; the accepted door divergence (§14); Fika client behaviour.

## 12. Disabled-collider extrusion skip (`src/FuckThesePhysics/DisabledExtrusionSkip.cs`)

Status: implemented and **verified in game by the user (2026-11-04)**: works, and improves performance. It replaces the movement-collision batching built for v1.1.0 and dropped (§10). The verification numbers were not recorded in this repo.

### Finding (CONFIRMED numbers, research §8)

Source: the 2026-11-04 ETW capture (Streets, Fika host, about 23 bots) and the mod's physics report from the same session, both from the batching build.

- `ColliderExtrusion.Calculate` was called 266,262 times: 11,006 batched and 255,256 vanilla. Of the vanilla calls, 252,712 had no batch slot.
- `PlayerOverlapManager.Init` ends with `Off()`, which disables every head collider. Only `GamePlayerOwner` calls `On()`. Bots' head colliders therefore stay disabled.
- `Collider.bounds` is empty for a disabled collider (Unity 2022.3 docs). Vanilla `CalculateCastRadius` and `RefreshNeighbours` read it, so the radius is 0 and the centre is `position + (bounds.center - transform.position)`: roughly the frame's motion vector, near the world origin.
- With batching on, `RefreshNeighbours` was still 7.4% of the main thread (called through the vanilla fallback) and `gSphereVsMeshCallback` 4.1%.

Each bot call therefore queries a point at the map origin and runs `CalculateDepenetration` against the bot's capsule at its real position. That gives zero penetration unless the bot is near the origin. Confirmed in game by the user (2026-11-04); `Debug / VerifyExtrusionSkip` stays available to re-check after game updates.

### Behaviour

Skipping Harmony prefix on `ColliderExtrusion.Calculate(Vector3, Quaternion, float)`. All callers reach it: `Calculate()`, `CalculateThroughMotion` and `PlayerOverlapManager.Calculate` (the rotation-prediction path).

Gate: `_collider != null && (!_collider.enabled || !_collider.gameObject.activeInHierarchy)`, which is the case where `Collider.bounds` is empty. The gate is on collider state, not on "is a bot", so behaviour stays the same if anything enables a bot's collider.

- Setting on, gate true, verify off: the prefix sets `_depenetration = Vector3.zero` and returns false. No overlap query and no penetration test run. Replacing the body is the point of this prefix. The neighbour buffer is not refreshed for these calls; [INFERENCE] nothing reads it while the collider is disabled (not checked).
- Otherwise: vanilla runs.

### Verify mode

`Debug / VerifyExtrusionSkip`. Only acts while `Movement / SkipDisabledHeadExtrusion` is on. When on and the gate is true, the prefix runs vanilla and a postfix reads `GetDepenetration()`. It counts calls and non-zero results, tracks the maximum magnitude, and keeps the last non-zero sample (collider transform path, query centre, `_castRadius`, depenetration vector, frame). Nothing is logged per call. Verify mode gives no performance gain.

### Report

The `Disabled-collider extrusion skip` section of `Report / Key` shows: setting on/off, verify on/off, window length in frames, skipped calls, vanilla-verified calls, non-zero count, max magnitude and the last non-zero sample. Counters reset each time the report is written. The hot path allocates nothing.

### Install and failure

- Install resolves `ColliderExtrusion.Calculate(Vector3, Quaternion, float)` by name and parameter types (`MissingMethodException` with the full signature on a miss) and the private `Vector3 _depenetration` through a cached `AccessTools.FieldRefAccess` (field type checked; `MissingFieldException` on a miss). A miss logs a warning and nothing is patched.
- Harmony owner id: `PluginInfo.Guid + ".extrusionskip"`. `Uninstall` unpatches that id.
- An exception in the prefix or postfix is logged once, and the feature latches to vanilla until the game restarts.

### Configuration

| Key | Type | Default | Notes |
|---|---|---|---|
| `Movement / SkipDisabledHeadExtrusion` | bool | on | Live. Off restores the vanilla query for disabled colliders from the next call. |
| `Debug / VerifyExtrusionSkip` | bool | off | Advanced. Runs vanilla for skipped calls and counts non-zero results in the report. No gain while on. |

### Verification

Verified in game by the user on 2026-11-04: the skip works and improves performance. No capture or report numbers were recorded in this repo.

## 13. Viewmodel fixed-tick interpolation (`src/FuckThesePhysics/ViewmodelInterpolation.cs`)

Status: implemented in 1.2.0. The tick stepping is **verified fixed in game** (pose trace, 2026-11-06, see "In-game results"). The remaining checks under "Validation" are still to run.

Problem: `FirstPersonStrategy.ProcessEffectors` (and the `MountingStrategy` and `StationaryStrategy` equivalents, see "Strategies") runs EFT's whole 50 Hz effector layer `nFixedFrames` times per rendered frame, and `ApplyTransformations` writes the pose once. With no new tick the viewmodel freezes; with a tick burst it steps. Mechanism and evidence: `docs/viewmodel-fixed-tick-handoff.md` (CONFIRMED from decompiled Assembly-CSharp `cc2d80b0-6d5b-4cb1-a581-6d2cc901d4c7`, EFT 0.16.9.40743).

### Approach

- The simulation is unchanged. Effectors and springs run as before, with fixed dt and the tick counts `ForceEffector` depends on.
- The mod records each output after every tick (`prev`, `cur`) and presents `lerp(prev, cur, alpha)` for the frame, with `alpha = clamp01((Time.time - Time.fixedTime) / Time.fixedDeltaTime)`. Component lerp, not slerp: the rotation values are small additive Euler offsets.
- Rejected: running the effectors once per rendered frame with frame dt. That changes spring stability and makes the tick-counted `ForceEffector` frame-rate dependent.

### Hooks

| Target | Patch | Role |
|---|---|---|
| `FirstPersonStrategy`, `MountingStrategy`, `StationaryStrategy` `.ProcessEffectors(ProceduralWeaponAnimation, float, int)` | prefix (one shared method) | Safety-restore if still swapped. Decide `active` (feature on, tracked PWA is the local MainPlayer's, `PWA.PointOfView` is first person). Run identity checks and resets (strategy identity included). Arm the snapshot reference. |
| `Spring.FixedUpdate` | postfix | Only when the instance is the armed `CameraPosition` spring (last statement of the tick loop): `prev = cur`, then read the swap set into `cur`. |
| the three `ProcessEffectors` | postfix (one shared method) | Disarm. If active and two snapshots exist, write the blended values into the swap set. |
| `ProceduralWeaponAnimation.LateTransformations` | postfix | If swapped, restore the true values and clear the flag. |

Frame topology: `PWA.ProcessEffectors` (0600B830) runs the tick loop (`_strategy.ProcessEffectors`), `ApplyTransformations`, and then one camera apply. `PWA.LateTransformations` (0600B831) runs the camera apply instead when stance is left or blindfire. Exactly one camera apply happens per frame, so the blend covers both cases, and the restore sits in `LateTransformations`, which runs after both. For `MountingStrategy` and `StationaryStrategy` the camera work (`MountedWeaponCamera`, `StationaryCamera` + `LerpCamera`) is in `Strategy.LateTransformations`, which `PWA.LateTransformations` calls before the restore postfix runs, so it also reads the blended pose.

### Strategies (CONFIRMED, EFT 0.16.9.40743)

`IProceduralAnimationStrategy` (`02000764`) has exactly four implementors: `FirstPersonStrategy` (`02000765`), `ThirdPersonStrategy` (`02000766`), `StationaryStrategy` (`02000767`), `MountingStrategy` (`02000768`). Its only caller of `ProcessEffectors` (`06003F14`) is `PWA.ProcessEffectors` (0600B830).

- `FirstPersonStrategy.ProcessEffectors` (`06003F1B`), `StationaryStrategy.ProcessEffectors` (`06003F2E`) and `MountingStrategy.ProcessEffectors` (`06003F3B`) have the same signature, the same guard and the same `deltaTime /= nFixedFrames` loop. Each tick runs `MotionReact`, `ForceReact`, `TurnAway.Process`, then `HandsPosition.FixedUpdate`, `HandsRotation.FixedUpdate`, `SwaySpring.Process`, `Shootingg.CurrentRecoilEffect.FixedUpdate`, `CameraRotation.FixedUpdate` and, as the last statement, `CameraPosition.FixedUpdate`. They differ only in which extra effectors run: `Breath`, `Walk` and `HandShake` in first person, `Breath` in mounting, none of them in stationary. The effectors that run write only springs, `HandRotationRecoil.Offset` and TurnAway, so the swap set is identical.
- Their `Apply*` read the same state. `MountingStrategy.ApplyTransformations` (`06003F38`) is the first-person sequence. `StationaryStrategy.ApplyTransformations` (`06003F2F`) calls `PWA.ApplyStationaryWeaponPosition` (`0600B815`: `HandsPosition.Get()` plus `CurrentRecoilEffect.GetHandPositionRecoil()`) and `ApplyComplexRotation`. In `LateTransformations`, `PWA.MountedWeaponCamera` (`0600B823`) reads `CameraPosition`, `CameraRotation`, `HandsPosition`, `TurnAway.Position` and the recoil camera getters, and `PWA.StationaryCamera` (`0600B822`) reads `CameraPosition` and `CameraRotation`. Both strategies' `ApplyCameraTransformations` (`06003F36`, `06003F2D`) are empty, so `PWA.LateTransformations` adds no further camera apply.
- The local player uses these strategies while mounted or on a stationary weapon: `IdleWeaponMountingState.Enter` (`0600B620`) calls the `MovementContext` method `06008B9D` (its name contains a Cyrillic `е`), which sets the persistent `_mountingStrategy` instance; `StationaryPlayerState.Spawned` (`06008C6F`) calls `MovementContext.SetStationaryStrategy` (`06008B9C`), which allocates a new `StationaryStrategy` each time. `IdleWeaponMountingState.Exit` leaves through `PWA.SetStrategy(EPointOfView)` (`0600B81E`), which allocates a new `FirstPersonStrategy`. Entering and leaving therefore always change the strategy reference, and the history resets.
- `PWA.PointOfView` setter (`0600B7D6`) does not replace a `StationaryStrategy` or `MountingStrategy` (`!(_strategy is StationaryStrategy) && !(_strategy is MountingStrategy)`). Both strategies' camera code is guarded by `PointOfView == FirstPerson`, so the feature is only active in first person and the history restarts when the view leaves it.

### Swap set

- `Spring.Current` on `HandsPosition`, `HandsRotation`, `CameraRotation`, `CameraPosition`.
- `BetterSpring._pPos` on `SwaySpring` (its `Value` is get-only).
- Recoil, when `CurrentRecoilEffect` is `NewRecoilShotEffect`: `HandPositionRecoil` and `HandRotationRecoil` `.Current/.Offset`, `CameraRotationRecoil.Current`, and `WeaponRecoil.Values[i].Process._current`. Otherwise only springs and TurnAway are interpolated, and nothing throws.
- TurnAway `_position`, `_rotation`, `_cameraShift`, `_lElbowShift` and `_rElbowShift` (public `Vector3` fields, `04003366`..`0400336A`). `TurnAway.Process` zeroes and rewrites all of them every tick; the elbow shifts are read through `LElbowShift`/`RElbowShift` (`06003F02`/`06003F03`) by `Player.AdjustElbows` (`0600816D`), between `PWA.ProcessEffectors` and `LateTransformations` in `VisualPass`, so they are inside the bracket.

Completeness: every per-tick write that is read outside the tick loop lands in this set. Walk, Force, Motion, Breath and HandShake only feed spring accelerations, velocities or `HandRotationRecoil.Offset`, which is in the set. Evidence: `local://viewmodel-interp-trace-completeness.md`. Velocities, force accumulators, `_update`, `_pVel`, `NeedUpdate`, `Spring.Zero` and Transforms are never touched.

Restore rule: `field = cur + (fieldNow - blendedWritten)`. This keeps writes made during `Apply*` (for example `LerpCamera` into `SwaySpring.ApplyVelocity`). Transforms keep the blended pose after restore; that is the presented pose.

Partial writes: before the write, `blended` is initialised from the live field values, `swapped` is set, and each field write records its value into `blended` right after it. If a write throws partway, the failure path's restore is therefore exact: written fields go to `cur` plus their deltas, and unwritten fields, whose `fieldNow` equals their recorded value, go to `cur`.

### Resets

On a reset, `prev = cur` and history counts as one snapshot, so the next frame blends from a fresh pair. Resets fire on: tracked PWA ref change; strategy ref change (a new `FirstPersonStrategy` is allocated on each POV switch and on leaving a mount; entering a mount or stationary weapon swaps in the other strategy); HandsContainer ref change; CurrentRecoilEffect ref change; `WeaponRecoil.Values` ref or length change; `HandsController` ref change (weapon swap); `Player.Teleport` on MainPlayer; `Time.fixedDeltaTime` change; the point of view leaving first person; feature re-enabled.

Not reset on aim toggles, hitches or `nFixedFrames` spikes. Snapshots are per tick, so `prev` and `cur` are always consecutive ticks. Aim blending lives in `Zero` and the aim weight, not in the swap set.

### Ballistics and camera

Shots read the fireport transform hierarchy (`FirearmController.Shot`: fireport position and direction). Nothing reads springs, so shots use the presented pose. Point of impact matches the sight picture; the gameplay pose is up to one tick (~20 ms) late. Camera and weapon use the same alpha in the same bracket, which keeps ADS sight alignment, since the ADS camera target derives from weapon transforms. [INFERENCE] until checked in game.

### Scope

- Local first-person `MainPlayer` only, with `FirstPersonStrategy`, `MountingStrategy` or `StationaryStrategy`. Bots and observed players (`ThirdPersonStrategy`, `NextObservedPlayer` with fixed `nFixedFrames = 2`) are untouched, as is the local player outside first person.
- Fika host and client: works, client-local. Each client renders its own viewmodel.
- Fika headless: not installed. The check is `BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("com.fika.headless")`, and one info line is logged. The `VisualPass` patch in Fika headless is commented out, so headless does not replace `VisualPass`; install is skipped because nothing renders there.
- Other installed mods (Fika 2.4.4, SAIN, ORBIT, Rupture) do not patch these targets, per the design source check.

### Configuration

`Viewmodel / InterpolateFixedTick` (bool, default on, category "6. Viewmodel", order 100, live). Off restores vanilla from the next frame. On adds up to one tick (~20 ms) of latency to sway, recoil, breathing, bob and wall pullback. SPT-FuckTheseOptics' temporal capture reads this entry read-only, by plugin GUID `com.rootdarkarchon.fuckthesephysics` and section/key `Viewmodel` / `InterpolateFixedTick`, and records it in `capture.json`; the section and key must not be renamed.

### Report

A "Viewmodel interpolation" section in the `Report / Key` report: frames blended, tick snapshots, resets by reason, frames skipped (inactive), last alpha, and whether the recoil swap is active. Counters are plain integers, reset on each report. The hot path does not allocate.

### Install and failure

- Install resolves every target (the three `ProcessEffectors` methods, `Spring.FixedUpdate`, `PWA.LateTransformations`, `Player.Teleport`, `GameWorld.Dispose`) by name and parameter types. A miss logs a warning with the full signature and patches nothing.
- An exception in any patch is logged once, swapped state is restored if possible, and the feature latches to vanilla until the game restarts.

### Diagnostics: pose trace (`src/FuckThesePhysics/ViewmodelPoseTrace.cs`)

`Debug / TraceViewmodelPose` (bool, default off, category "3. Debug", order 70, advanced, live) finds where remaining viewmodel judder comes from by recording what each stage of the frame produced. It records with `InterpolateFixedTick` on or off and has its own Harmony id (`<guid>.viewmodelpose`) and failure latch. Its patches and a static `Camera.onPreCull` delegate exist only while the toggle is on (turning it on clears the buffer; turning it off keeps the rows for the report). One row per rendered frame goes into a preallocated 512-row ring buffer, and nothing is logged per frame. The row is written only while `GameWorld.MainPlayer`'s PWA is the tracked one.

| Stage (in frame order) | Hook | Sampled |
|---|---|---|
| ticks | prefix on the three `ProcessEffectors(pwa, deltaTime, nFixedFrames)`, priority first, before the interpolation hooks | `nFixedFrames` for MainPlayer's PWA. A frame whose prefix did not run for MainPlayer is `skipped` (`tk` = -1). |
| hands rotation, simulated | postfix on the same methods, ordered before the interpolation postfix | `HandsContainer.HandsRotation.Current` after the tick loop, before any blend |
| hands rotation, presented | postfix on the same methods, ordered after the interpolation postfix | the same value after the blend (equals the simulated value when not blended) |
| weapon root | postfix on `PWA.LateTransformations(float)`, ordered after the interpolation restore | `HandsContainer.WeaponRootAnim` `localPosition`/`localRotation`, the last PWA write of `VisualPass` (restore only writes effector fields, so this is the presented pose) |
| fireport and camera | `Camera.onPreCull`, only for `CameraManager.Instance.Camera` (checked with `CameraManager.Exist` first) | latest point before the FPS camera renders: camera world position and rotation, and `HandsContainer.Fireport` in camera space (`InverseTransformPoint`, `inverse(camera rotation) * fireport rotation`). The row is committed here. A second pre-cull of the camera in the same frame overwrites the row. |

Per row: frame, `dt` in ms, `tk`, `I` (interpolation setting on), `B` (blended this frame) and `alpha` (-1 when not blended), then |delta vs the previous frame's row| for: hands rotation simulated and presented (raw `Spring.Current` units), weapon root position (mm) and rotation (deg), fireport position (mm) and rotation (deg) in camera space, camera position (mm) and rotation (deg). A delta is `-` unless the previous row is the previous frame and has the data. Rotation deltas use the vector part of `inverse(a) * b`, which is exact for tiny angles.

The "Viewmodel pose trace" report section starts with a summary (frames in the buffer, frames with 0 ticks, frames with ticks, skipped frames, interpolation setting on, blended frames), then per metric the median non-zero |delta| and the number of stall frames (|delta| below 1% of that median) out of the valid frames, split into 0 ticks / ticks > 0 / skipped, then the 512-row fixed-width table, oldest first. Reading it: if the fireport-in-camera-space stalls line up with 0-tick frames while the interpolation setting is on, the presented pose is still stepping; if camera-space stalls are absent but the judder remains, it is outside the pose (frame pacing, DLSS, `dt` spikes). Camera world deltas show whether the camera itself steps.

Verified against EFT 0.16.9.40743 (`Assembly-CSharp` MVID `cc2d80b0...`): `PlayerSpring.WeaponRootAnim` (`04009382`), `PlayerSpring.Fireport` (`04009386`, written in `PWA.InitTransforms`, read by `GetWorldCalibrationPoint`), `CameraManager.Instance` (`17002657`, lazily creates), `CameraManager.Exist` (`17002658`), `CameraManager.Camera` (`17002662`), `Player.VisualPass` (`0600813A`: `ProcessEffectors`, then IK/animation passes, then `LateTransformations`); `UnityEngine.Camera.onPreCull` from `UnityEngine.CoreModule`.

### In-game results (2026-11-06, Woods, about 110 fps, 49.6 Hz ticks)

Pose trace, 512 frames each, aiming with light sway, camera still; about 45% of frames had 0 ticks. Rotation of the fireport in FPS-camera space:

| | Interpolation off | Interpolation on |
|---|---|---|
| Frozen on 0-tick frames | 230/230 | 0/233 |
| Mean delta, 0-tick / tick frames | 0 / 0.0046° | 0.00159° / 0.00158° |
| Median ratio of consecutive per-frame speeds | stepped | 1.02 |

**Residual swim is not tick-related (user-confirmed: it changes with map position).** At the test spot (x ≈ 507 m) the interpolation setting does not remove a fine swim of near parts of the weapon (eyepiece rim, rear of the weapon). It is caused by float32 precision at large world coordinates, which this mod does not change and which is out of scope here; it is handled by SPT-FuckTheseOptics.

### Validation (remaining)

From `docs/viewmodel-fixed-tick-handoff.md`:

- FuckTheseFX temporal capture (16 frames, aiming with light sway), then `python scripts/Analyze-TemporalCapture.py <folder>`. Before: `stallThenJump`/`zigzag` events in the `Viewmodel pose` section. After: the weapon moves every frame, with a smooth delta series.
- DLSS on and off; high and low frame rates (below 50 fps too); recoil; breath hold; weapon swap; point of impact against the sight picture.
- Mounting on a bipod or wall and using a stationary weapon (enter, aim, fire, leave), and switching to third person while mounted; wall pullback with elbows visible (`_lElbowShift`/`_rElbowShift`).
- Fika client: confirm client-local behaviour.

## 14. Tripwire release (`src/FuckThesePhysics/TripwireRelease.cs`)

Status: implemented (`Tripwire / ReleaseIdleTripwires`, on by default). **Not yet verified in game.** The investigation, measurements and rules are in `docs/research/tripwire-gate.md` §8–§9. This section records what the code does.

Problem: planted tripwires (the Partisan boss plants them, `tripwire-gate.md` §8.4) register their sync rigidbody in `BaseTripwire.Init` and stay registered while armed, triggered or disarmed. While any wire exists, the physics step runs for the rest of the raid. Measured on Customs (CONFIRMED, FTP report): 26 `tripwire(Clone)` entries, Simulate on 69.9% of frames.

Approach: wires never register. Each frame the mod tests every player and bot capsule geometrically against each armed wire. On an enter edge it calls vanilla `BaseTripwire.CollisionEnter(collider)`, so the filter, the bot ignore roll and `TriggerTripwire` stay vanilla. Everything else that can enter a wire keeps using vanilla's relay, which needs the step to run for its own registrant (loot, grenades, ragdolls).

### Hooks

| Target | Patch | Role |
|---|---|---|
| `BaseTripwire.Init(int, Vector3, Vector3)` | postfix | Track the wire. Remove its registry entries (count, then `UnsupportRigidbody` that many times). |
| `BaseTripwire.ReturnToPool()` | prefix | Untrack. |
| `PhysicsExtensions.Simulation.Update()` | prefix | Per-frame detector: player and bot capsules against armed wires; calls `CollisionEnter` on enter edges. |
| `SyncObjectCollisionChecker.OnTriggerEnter(Collider)` | prefix (skips the original) | For a tracked wire and a player or bot collider, skip vanilla. Single owner of walk-in detection. |
| `GameWorld.Dispose()` | postfix | Clear tracking. |

### Rules

- Armed = `_collisionCollider.gameObject.activeInHierarchy`. Hidden wires (Active, Exploding, Exploded, Inert) are skipped, so one wire is never driven twice.
- Geometry: the trigger volume is vanilla's capsule (radius 0.03 × scale, stake pivot to stake pivot). Player and bot capsules come from their `CharacterController` collider. Overlap is segment-to-segment distance against the sum of radii. Fast movers are swept.
- Enter edges: `CollisionEnter` is called only when a collider joins a wire's inside set. Leaving needs 1 cm of hysteresis, so standing on a wire does not re-roll `CHANCE_TO_IGNORE_TRIPWIRE`.
- Setting off (live): every tracked armed wire is re-registered, the relay skip stops and the sets are cleared. Wires armed afterwards behave as vanilla.

### Gameplay contract

Crossing between the stakes trips a wire for players and bots, with the bot ignore roll. Shooting still trips it. Disarming goes through `TripwireInteractionTrigger` and `DeActivateTripwire`, not physics; the disarmed wire is no longer tested.

### Accepted divergence

Decided by the user on 2026-11-13: non-player colliders that move without their own registry entry (a door swinging through a doorway wire, other animated scene colliders) no longer trip wires unless something else runs the physics step. Not checked or compensated.

### Failure behaviour

- Install resolves every target by name and parameter types, all or nothing. A miss logs a warning and rolls back the patches this feature made.
- A detector exception sets a fault flag: every wire goes back to vanilla (re-registered, relay skip stopped, no new tracking) until the game restarts.
- An exception in a single `CollisionEnter` call is logged once; nothing is disabled.
- Harmony id: `PluginInfo.Guid` (shared, no suffix).

Implementation deviations from the spec are listed in `docs/research/tripwire-gate.md` §9 "Implementation notes".

### Configuration

`Tripwire / ReleaseIdleTripwires` (bool, default on, category "3. Tripwire", live). Walk-in and skipped-relay debug lines go behind `Debug / LogPhysicsGate`. The report's SimulationOwners rows show each wire's state and release status (`release=untracked|held|vanilla`).

### Verification

Not run in game. The checks are listed under §11 "Not yet tested".

## 15. Research notes

`docs/research/` holds the analysis done before implementation. These files predate the implementation and were written under the old working name "SPT-FuckThisAirdrop"; names, GUIDs and the recommended release point (`IsStatic`) in them are historical. Where they disagree with this document, this document wins.

| File | Content |
|---|---|
| `docs/research/eft-airdrop-physics.md` | Physics driver, airdrop lifecycle, crate prefab, fix point |
| `docs/research/gate-holders.md` | Every `SupportRigidbody` registrant; proof of crate pooling and lifetime |
| `docs/research/btr-suspension.md` | BTR call graph, cadence, maps and spawn chance, Fika behaviour, candidate fixes |
| `docs/research/physics-interop.md` | Interop with installed mods and Fika; PhysX pose safety after release |
| `docs/research/airdrop-trigger-debug.md` | Airdrop scheduling, debug-spawn design, Fika host/client split |
| `docs/research/mod-template.md` | Build/package template and config patterns |
| `docs/research/movement-collision-batching.md` | Movement collision: profiler shares, hook analysis, the batching proposal, and §8 (result of the dropped 1.1.0 batching build and the disabled-collider finding that §12 implements). |
| `docs/research/sync-transforms-investigation.md` | Physics.SyncTransforms cost: measured share, callers, the hitbox/trigger layer hypothesis, static findings and one in-raid experiment. Declined (2026-11-04): too little gain for the risk; the diagnostics were removed. |
