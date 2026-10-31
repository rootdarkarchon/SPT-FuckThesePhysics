# Fuck These Physics: design

Developer-facing description of what v1.0.0 does, why, and how it was checked. User-facing documentation is in `README.md`.

Tags used where the status still matters: **CONFIRMED** = read in decompiled code, prefab data or the in-game log. **[INFERENCE]** = reasoned, not observed. **UNVERIFIED** = not checked.

## 1. Purpose and target

EFT runs its physics step (`Physics.Simulate`) from script and only when something is registered for it. Two things register a rigidbody and never unregister it, so the step runs every frame for the rest of the raid for no reason:

- the landed airdrop crate (offline and Fika host);
- the BTR suspension body (Woods; every machine that renders the BTR).

This mod unregisters those two bodies at the point where the game itself no longer needs them. It uses EFT's own `UnsupportRigidbody`; nothing else about physics is changed. The cost of the step is established from earlier raid measurements (it is why Rupture moved ragdolls to an external PhysX world); this project does not re-measure it.

Non-goals: changing EFT's physics driver or `CheckRigidbodiesDone` semantics, airdrop scheduling or loot, per-map drop-point coverage.

| Component | Version |
|---|---|
| SPT | 4.1 |
| Unity | 2022.3.43f1 |
| BepInEx | 5 |
| Fika (analysed against) | 2.4.4 |

Fika analysis was done against 2.4.4. The mod targets Fika 2.4.x generally (2.4.6 is expected to behave the same); no Fika version gating exists in code.

Identity: plugin name "Fuck These Physics", GUID `com.rootdarkarchon.fuckthesephysics`, output `BepInEx/plugins/FuckThesePhysics/FuckThesePhysics.dll`, package `FuckThesePhysics-1.0.0-SPT4.1.zip`.

### Configuration

| Key | Type | Default | Notes |
|---|---|---|---|
| `Airdrop / ReleaseLandedAirdrops` | bool | on | Live. Read on every `ManualUpdate` postfix; off restores vanilla for crates that land afterwards. |
| `BTR / ReleaseParkedSuspension` | bool | on | Applies from the next BTR stop. |
| `Debug / SpawnAirdropKey` | `KeyboardShortcut` | unset | Advanced. Polled in `Plugin.Update`. |
| `Debug / LogPhysicsGate` | bool | off | Advanced. See §8. |
| `Report / Key` | `KeyboardShortcut` | unset | Advanced. Writes the registry and step timing report. See §8. |

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

**Other registrants.** Full census in `docs/research/gate-holders.md`. Transient ones release on their own: loot drops (up to 15 s), corpse ragdolls (up to about 16 s), grenades (until they explode), lamps (up to 30 s, only after an explosion), broken doors (up to 6 s), `WheelDrive` (about 3 s). Persistent ones: the airdrop crate, the BTR suspension, player-placed tripwires while one exists, and the MG belt while visible and switched on. Release paths of smoke grenades, `FlareCartridge`, `GrenadeCartridge`, `BeltDetachablePart` and `SmallPhysicsObject` with `TimeOfLife == 0` are UNVERIFIED; they only matter as competing holders.

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
- **Lifecycle log lines** (BepInEx logger, because EFT suppresses `Debug.Log`): crate `Init`, `Landed` (with release on/off), `release` (removed / remainingRegistry / simulationEnabled), `IsStatic`; and one line per BTR suspension release.

## 9. Who is affected

| Setup | Airdrop crate | BTR suspension |
|---|---|---|
| Stock SPT offline | Affected. `ClientAirDrop(offlineServerLogic: true)` → `ServerAirDrop` registers the crate. Fix applies. | Affected on Woods. Fix applies. |
| Fika host / headless | Affected. Fika's prefix on `GetSyncObjectStrategyByType` builds `ClientAirDrop(FikaBackendUtils.IsServer)`, so only the host owns a `ServerAirDrop`. Fix applies. | Affected on Woods. Fix applies. |
| Fika client | Not affected: registers nothing, only interpolates toward host packets. The airdrop patches never fire. | Affected on Woods: each client runs its own `BTRView`. Fix applies if the mod is installed on the client. |

Neither Fika nor Rupture patches `ServerAirDrop.ManualUpdate`, `ServerAirDrop.Init`, `PhysicsExtensions` or `VehicleSuspension`, so there are no Harmony conflicts among the targets. ORBIT's `AirdropLandedPatch` hooks `ClientAirDrop.PlayLandingSound`, a different method.

## 10. Rejected alternatives

- **Patch `CheckRigidbodiesDone` to ignore kinematic bodies.** Changes the gate for every registrant (global blast radius).
- **Patch `CanRunSimulate`.** Breaks every physics user.
- **Skip `SupportRigidbody` in `ServerAirDrop.Init`.** Breaks landing detection (trigger callbacks need the step).
- **Release the crate at `IsStatic` instead of `Landed`.** Safe, but keeps the step on for the smoke duration after landing (see §3).
- **Destroy the crate rigidbody, deactivate the GameObject, or toggle `isKinematic`.** `Init` dereferences the rigidbody again on pool reuse; the others change game state for no gain.
- **BTR: dedupe in a `SuspensionOn` prefix.** Marginal once the `SuspensionOff` release exists (entries are at most 1 at any time), and does not fix the parked-hold by itself.
- **BTR: skip the suspension entirely, as EFT does on Streets.** Changes visuals on Woods and removes the BTR from the gate even while driving. Needs an explicit policy decision and prefab data that is UNVERIFIED.
- **Trigger the debug airdrop through server config or Fika commands.** See §7 dead ends.

## 11. In-game verification

Woods, Fika host, 2026-10-31, from `LogOutput.log`. The user ran this; it is the only runtime verification so far.

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
- Release paths of the UNVERIFIED registrants listed in §2.

## 12. Research notes

`docs/research/` holds the analysis done before implementation. These files predate the implementation and were written under the old working name "SPT-FuckThisAirdrop"; names, GUIDs and the recommended release point (`IsStatic`) in them are historical. Where they disagree with this document, this document wins.

| File | Content |
|---|---|
| `docs/research/eft-airdrop-physics.md` | Physics driver, airdrop lifecycle, crate prefab, fix point |
| `docs/research/gate-holders.md` | Every `SupportRigidbody` registrant; proof of crate pooling and lifetime |
| `docs/research/btr-suspension.md` | BTR call graph, cadence, maps and spawn chance, Fika behaviour, candidate fixes |
| `docs/research/physics-interop.md` | Interop with installed mods and Fika; PhysX pose safety after release |
| `docs/research/airdrop-trigger-debug.md` | Airdrop scheduling, debug-spawn design, Fika host/client split |
| `docs/research/mod-template.md` | Build/package template and config patterns |
