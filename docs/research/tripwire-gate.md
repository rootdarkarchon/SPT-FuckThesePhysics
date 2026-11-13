# Handoff: planted tripwires keep EFT's physics step running

Status: **implemented in `src/FuckThesePhysics/TripwireRelease.cs` (2026-11-13), not yet verified in game**. Written from a profiling session in SPT-Profiler; §3/§4 updated and §8 (findings) and §9 (fix, with implementation notes) added by a static investigation the same day. Targets EFT 0.16.9.40743 (Assembly-CSharp MVID `cc2d80b06d5b4cb1a5816d2cc901d4c7`), Unity 2022.3.43f1, SPT 4.1.6, Fika 2.4.4 host (non-headless), FTP 1.2.0, Rupture 1.4.1.

Tags: **CONFIRMED** = read in decompiled code, a report or a capture. **[INFERENCE]** = reasoned, not observed. **UNVERIFIED** = not checked. Member ids are Assembly-CSharp tokens.

Before starting, read `skill://tarkov-modding`, `skill://tarkov-investigate` and `skill://csharp-consistency`, then `docs/design.md`, `docs/research/gate-holders.md` (registry mechanics, §0) and `docs/research/eft-airdrop-physics.md` / `airdrop-trigger-debug.md` (the airdrop is the same pattern: a trigger-detected synchronizable object registered with the gate).

## 1. Goal

Stop planted tripwires from holding `PhysicsExtensions.UpdateController` open for the whole raid, **without changing tripwire gameplay**: walking into an armed wire must still set it off for players and bots (including the bot ignore chance), shooting it must still work, disarming and inert timers must keep working, and Fika host/client behaviour must be unchanged.

Non-goals: changing `CheckRigidbodiesDone` semantics for other holders, tripwire placement/AI, or the cost of a physics step itself.

## 2. Measured cost

### FTP report (CONFIRMED)
`$TARKOV_SPT_INSTALL/BepInEx/plugins/FuckThesePhysics/reports/physics-20261113-104558.txt` (also dumped into `LogOutput.log`), Customs, written about 614 s into the window:

- `Physics.Simulate` ran on **27,047 / 38,679 frames (69.9%)**, mean **1.689 ms/call** (p50 1.874, p95 2.911, max 45.98), i.e. **1.18 ms per frame** averaged over the window (63.0 fps).
- `UpdateController._rigidbodies`: total 26, counting 26, **all** `POOLS/Unloadable/tripwire.bundle/tripwire(Clone)` with owners `TripwireSynchronizableObject + AssetPoolObject + PoolContainerObject`. Each: active, **kinematic, sleeping, speed 0**, colliders `SphereCollider x1 [trigger]`, `CapsuleCollider x2 [both trigger]`, **no visibility checker**. Nothing else was registered.
- Gate log: `physics gate UpdateEnabled=True registry=26 frame=35127` … `UpdateEnabled=False registry=0 frame=48720` (raid end).

### ETW (CONFIRMED, SPT-Profiler, release player, main thread; the raid is main-thread bound, main thread ~98% busy)
Captures in `$SOURCE_FOLDER/SPT-Profiler/artifacts/profiles/` (analyse with `scripts/profile-cpu.ps1 -Analyze`; ms/frame comes from DXGI presents, see that repo's README):

| Capture (same raid) | fps | `PhysicsExtensions.Simulation.SmoothSimulate` ms/frame | of which `NPhaseCore::processTriggerInteractions` | broadphase (`AABBManager::finalizeUpdate`) |
|---|---:|---:|---:|---:|
| `cpu-20261113-114054` | 70.6 | 1.54 (11.1%) | 0.49 | 0.42 |
| `cpu-20261113-114419` | 68.4 | 1.24 | 0.33 | – |
| `cpu-20261113-114803` | 48.3 | 2.77 | 0.89 | – |

Earlier captures without planted tripwires had the step at 0.03–1.7% of the main thread (`cpu-20261113-103518`, `-112322`, `cpu-20261104-162052`). The step's cost is the whole scene (trigger pairs and broadphase of every collider), not the tripwires; the tripwires only decide that it runs.

## 3. Why tripwires register (CONFIRMED unless tagged)

- `EFT.SynchronizableObjects.BaseTripwire.Init(int objectId, Vector3 position, Vector3 rotation)` (`0600AF33`): activates the sync object, sets `IsInited/IsActive/IsStatic = true`, then `PhysicsExtensions.UpdateController.SupportRigidbody(SyncObjectRigidbody)` (no visibility checker, default quality) and `useGravity = false`. Same idiom as `ServerAirDrop.Init`.
- `BaseTripwire.ReturnToPool` (`0600AF3B`) deactivates the object and calls `SupportRigidbody` again; the inactive entry is purged by `CheckRigidbodiesDone` (gate-holders.md row 6b). So an entry only leaves when the tripwire returns to the pool or its GameObject goes inactive. Kinematic/sleeping does **not** release it (gate-holders.md §0).
- Walk-in detection: `BaseTripwire.CollisionEnter(Collider)` (`0600AF39`):
  ```
  if (TripwireState == None || collider.isTrigger) return;
  if (TripwireCheckLayerMask contains collider layer)
      player = GameWorld.GetPlayerByCollider(collider);
      if (player == null || !player.IsAI || !IsTrue100(bot Mind.CHANCE_TO_IGNORE_TRIPWIRE))
          GameWorld.TriggerTripwire(_tripwireSyncObject);
  ```
  CONFIRMED (§8.1): the only caller is `SyncObjectCollisionChecker.OnTriggerEnter` (`0600AF6C`) on the prefab child `ObjectCollision`, i.e. on `_collisionCollider`. Trigger callbacks only fire inside `Physics.Simulate`, which is why BSG keeps the rigidbody registered.
- Other trigger paths that do not need the step: `TripwireSynchronizableObject.OnBallisticHit` (`0600AF94`) → `ISyncEnvironment.TriggerTripwire` (ballistics, query based); `TripwireManager.TriggerTripwireByHit(int netId, IPlayer)` (`0600AF75`, distance check vs `ShotSqrDistance`). Interaction/disarm goes through `TripwireInteractionTrigger` (an `IInteractive` with a distance check, no physics callback).
- Geometry: `TripwireSynchronizableObject.SetupStakes(from, to)` (`0600AF95`) places `_collisionCollider` (a `CapsuleCollider`, direction X) at the wire midpoint, `height` = distance between the stake pivots, rotated `FromToRotation(Vector3.right, to - from)`. Radius is the prefab's 0.03 m; the `COLLISION_CAPSULE_RADIUS` / `_OFFSET` constants have no usages. `_ballisticCapsule` mirrors it for shots. `SetupStakes` also starts the inert timer (`TripwiresSettings.InertSeconds`, or the placing bot's `Mind.TRIPWIRE_INERT_TIME`).

## 4. Where the 26 tripwires come from (answered, see §8.4)

- `gate-holders.md` row 6 assumed tripwires are player-placed and absent from ordinary raids. **Wrong**: the Partisan boss (`bossPartisan`) plants them, and he is the only AI that can. Customs spawn chance 15 in the SPT DB.
- Disarmed and inert wires stay registered until their dropped loot grenade is destroyed (possibly never); triggered wires stay registered until the explosion finishes (§8.2).

## 5. Constraints a fix must meet

1. **Single owner of walk-in detection.** If tripwires are unregistered but the step still runs for another holder (corpse ragdolls, grenades, loot drops: transient, frequent in a fight), the vanilla trigger path fires as well. A replacement detector must therefore either suppress the vanilla relay or be the vanilla relay's only caller, so a wire is never evaluated twice.
2. **Enter edges, not per-frame overlaps.** Vanilla rolls `CHANCE_TO_IGNORE_TRIPWIRE` once per `OnTriggerEnter`. A per-frame overlap that called `CollisionEnter` every frame would re-roll every frame and effectively remove the bot ignore chance. Track the set of colliders inside each wire and call only for new entries.
3. **Same filter as vanilla.** Reuse `BaseTripwire.CollisionEnter` itself for each new contact rather than reimplementing it (non-trigger colliders, `TripwireCheckLayerMask`, `GetPlayerByCollider`, ignore chance, `TripwireState`). Query with the same capsule as `_collisionCollider` and `QueryTriggerInteraction.Ignore`. An overlap query also returns colliders that vanilla trigger pairs might never report (UNVERIFIED which ones); log what actually reaches `CollisionEnter` in vanilla first and match that set.
4. **Authority under Fika.** Find where `CollisionEnter` runs on a Fika host and on clients (`GameWorld.TriggerTripwire` vs `ClientNetworkGameWorld.TriggerTripwire` `06003F7F`; Fika source in `$TARKOV_MOD_SOURCES/Fika-Plugin`). Detect only where vanilla does; do not add a second authority.
5. **Release timing.** Unregister only while the wire is armed and static, and keep the registry consistent when it is triggered, disarmed, destroyed or returned to the pool (`UnsupportRigidbody` removes one entry; `ReturnToPool` re-supports once). Follow `AirdropRelease`'s latch pattern for pooled instances.
6. **Cost.** The replacement must be far cheaper than 1.2–2.8 ms/frame: one capsule overlap per armed wire per frame with a non-alloc buffer, ideally skipped when no player or bot is within a few metres.

## 6. Acceptance

- In a raid where wires are planted, FTP's report shows the step running only for transient holders (no `tripwire(Clone)` entries counting), and an ETW capture shows `SmoothSimulate` near the no-tripwire baseline.
- Walking into an armed wire sets it off for the local player and for a bot; a bot ignores a wire at roughly its configured chance (log the roll); shooting a wire still sets it off; disarming works; an inert wire does not fire; behaviour is the same on a Fika client.
- Config toggle next to the existing releases (`Airdrop / ReleaseLandedAirdrops`, `BTR / ReleaseParkedSuspension`); off restores vanilla for wires armed afterwards.
- Docs: `docs/design.md` (holders, config row, Harmony id), `README.md`, `gate-holders.md` row 6, and a KB note in `$TARKOV_KB` for the tripwire registration and detection facts.

## 7. How to measure

- FTP report (existing): Simulate frames/ms and the registry census. Write it before and after.
- SPT-Profiler ETW: elevated `scripts/profile-cpu.ps1 -Start`, play, `-Stop`, write the JIT map in game (`Hotkeys/WriteJitSymbolMap`, any time before closing EFT), `-Analyze -Etl <etl> -JitMap <tsv>`. Compare `inclusiveMsPerFrame` of `Assembly-CSharp!PhysicsExtensions/Simulation:SmoothSimulate (single)` in `main-functions.csv`.

## 8. Investigation findings (2026-11-13, static: decompiler, prefab, Fika v2.4.4 tag, SPT DB)

### 8.1 Detection path (CONFIRMED)
- Prefab `Assets/Content/LOCATION_OBJECTS/LOOTABLE/PREFAB/tripwire.prefab` (dump; same in `tripwire.bundle`): root `tripwire` (layer 0) has the kinematic `Rigidbody` and `TripwireSynchronizableObject`, no collider. Child `ObjectCollision` (layer 0) has `SyncObjectCollisionChecker` + the trigger capsule `_collisionCollider` (r 0.03). `BallisticCollision` (layer 12, trigger capsule, `BallisticCollider`) and `Interaction` (layer 22, trigger sphere, `TripwireInteractionTrigger`) have no trigger handler.
- `SyncObjectCollisionChecker` has only `Awake` and `OnTriggerEnter`; no Stay/Exit anywhere on the prefab, `BaseTripwire.ManualUpdate` is empty, `BaseTripwire` has no derived types. Enter edge only.
- `TripwireCheckLayerMask` (`LayersMaskController..cctor` `060037DE`) = layers 0, 8, 9, 11, 12, 15, 18 (0x49B01). Players and bots: runtime `CapsuleCollider` from `CharacterControllerSpawner` on layer 8, non-trigger, with a kinematic rigidbody (all three `CharacterControllerSettings` modes). Hitboxes (layer 16, triggers) are not in the mask.
- `GetPlayerByCollider == null` passes the filter, so **any** non-trigger collider on a mask layer that enters (loot item, grenade, prop) sets the wire off in vanilla. A player-only replacement detector would change gameplay.
- No other physics-callback consumer: shots are query based (`OnBallisticHit`), disarm is an `IInteractive`, placement validation uses `Physics.Linecast`.

### 8.2 Lifecycle and registry (CONFIRMED)
`ETripwireState`: None 0, Wait 1, Active 2, Exploding 3, Exploded 4, Inert 5. `HideContent()` (from `SetGrenadeState`, i.e. on trigger and on disarm/inert) deactivates `ObjectCollision`, so the relay is dead from then on. The entry leaves the registry only after `Destroyed()` → `IsActive = false` → `RemoveNonActiveAndStaticObjects` → `ReturnToPool` (SetActive(false), extra self-purging Support).

| State | `ObjectCollision` | Gate entry |
|---|---|---|
| `Wait` (armed) | active | present, needed for walk-in |
| `Active` (triggered, fuse) | inactive | present, not needed |
| `Exploding` → `Exploded` | inactive | present for `DestroyedSeconds`, then purged |
| `Inert` (disarmed by hand or inert timer) | inactive | present until the dropped loot grenade is destroyed; may be all raid |

Calling `CollisionEnter`/`SetGrenadeState(true)` on an already hidden wire falls through to `SpawnLootGrenade()` (duplicate grenade), so nothing may drive the relay once `ObjectCollision` is inactive.

### 8.3 Authority (CONFIRMED)
`BaseTripwire.CollisionEnter` → virtual `ISyncEnvironment.TriggerTripwire`. Real body only in `GameWorld` (`06007B62`: `TripwireManager.RemoveTripwire` + `tripwire.TriggerTripwire()`), used by `ClientLocalGameWorld` (SPT offline). Empty in `ClientNetworkGameWorld` (`06003F7F`) and Fika's `FikaClientGameWorld` (`FikaClientGameWorld.cs:140-153` @v2.4.4). `FikaHostGameWorld.TriggerTripwire` sends a sync packet then calls base. Fika clients still run `BaseTripwire.Init` from `SpawnTripwire.Execute` and register the rigidbody, so **every peer holds its own gate**, while only the host/offline world acts on a walk-in. `UpdateController.Enabled` is true in every process (`PhysicsSettings.ManualUpdate` defaults true; no `client.config.json`). The `TripwireSynchronizableObject_*` and `AIPlaceLogicPartisan_Dispose_Patch` lines in `LogOutput.log` are **Fika** patches (grenade registration, host `Exploded` packet, a Dispose null check); SPT client modules contain no tripwire code.

### 8.4 Origin (CONFIRMED mechanism; [INFERENCE] that Partisan spawned in the measured raid)
- Only creation chain: `PlantTripwireOperation.Dispose` (`0600D2A7`) → `GameWorld.PlantTripwire` (`06007B60`) → `InitSyncObject` → `BaseTripwire.Init`, one registry entry per wire. `GameWorld.SpawnTripwire` throws `NotImplementedException` and has no callers; locations carry no tripwire data; `InitStaticObject` is empty.
- Ordinary bots cannot plant (`PlantMineNode` needs `MinesData.CurrentActive`, set only by Partisan layers). `BossPartisan.PrewarmMines` (`06000F6C`) plants up to 21 wires within 50 m of his goal enemy, one per bot update; cache layers add 8–10 more every ≥180 s; `PartisanFightManyLayer` plants on his path. Each wire consumes a frag from his pockets/boss container. 26 wires fits one Partisan.
- SPT DB Customs: `bossPartisan`, `BossChance` 15, `DependKarma`, `TriggerId PARTISAN_TRIGGER`. Other maps with a Partisan entry were not enumerated.
- Gate log correction: `frame=35127 registry=26` is the first tick after the logging toggle was switched on (`GateLog.cs`), not when the wires appeared. [INFERENCE] With 27,047 simulated frames, the first wire existed by about frame 11,600 (~185 s).

### 8.5 Open (UNVERIFIED, runtime)
- That PhysX reports the kinematic-trigger × kinematic-non-trigger pair (wire × player capsule) is required by the design (it is the only detection path) but was not observed.
- Host receiving `OnTriggerEnter` for a Fika remote player's impostor capsule.
- State mix and placer of the 26 measured wires (the report does not print them).

## 9. Fix (implemented): never register wires, detect players geometrically (`Tripwire / ReleaseIdleTripwires`)

Revised 2026-11-13. The first proposal (register an armed wire while a player is within 5 m) was dropped: Partisan walks among his own wires, so the step would stay on for long stretches. Partisan is the only AI that plants and ordinary bots never do (§8.4, matches the user's game knowledge), so a raid with Partisan is exactly the case to fix.

### Idea
Split vanilla's single detector by **what moves the entering collider**:
- **Players and bots** move their capsules by transform and never hold the gate. Their walk-in is the only reason a wire needs the step. FTP detects these geometrically every frame and calls vanilla `BaseTripwire.CollisionEnter(playerCollider)` on an enter edge, so the filter, `GetPlayerByCollider`, the bot ignore roll and the `TriggerTripwire` virtual (Fika authority) stay vanilla.
- **Everything else** that can enter a wire (loot drops, grenades, ragdolls, splinters) registers its own rigidbody while it moves, so the step runs anyway and the vanilla relay handles it unchanged.

Wire rigidbodies are removed from the registry at `Init` and never re-added, so tripwires never decide that the step runs.

### Why geometry, not `Physics.OverlapCapsule`
EFT runs with `Physics.autoSyncTransforms = false` (design.md §2); the only per-frame `Physics.SyncTransforms` is in the local player's `Player.ArmsLateUpdate`, so PhysX poses of bot and remote-player capsules are frame-order dependent when the step is off. A query would need its own `SyncTransforms` (0.6–0.7 ms per frame measured in `sync-transforms-investigation.md`). Transform math has no such dependency and costs microseconds.

### Rules
1. **Track** in a `BaseTripwire.Init` postfix: wire, rigidbody, `_collisionCollider`. Remove every registry entry for the rigidbody (count, then `UnsupportRigidbody` that many times, as `AirdropRelease.Release`). Untrack in a `ReturnToPool` prefix (its own extra `SupportRigidbody` on the inactive object self-purges); clear in `GameWorld.Dispose`.
2. **Armed** = `_collisionCollider.gameObject.activeInHierarchy`. Hidden wires (states `Active`, `Exploding`, `Exploded`, `Inert`, §8.2) are skipped and never driven: a second `SetGrenadeState(true)` would drop a duplicate grenade. `CollisionEnter` itself still rejects `TripwireState == None`.
3. **Detector**, once per frame in a prefix on `PhysicsExtensions.Simulation.Update` (where vanilla's `Simulate`, and so its trigger callbacks, would run): for each armed wire and each player in `GameWorld.AllAlivePlayersList`:
   - wire segment = collider centre ± `transform.right * (height / 2 − radius)`, radius 0.03 × scale. Unity's capsule `height` includes both end caps, so the volume ends exactly at the two stake pivots, as vanilla's trigger does; using `height / 2` would extend it 3 cm past each stake;
   - player capsule from `player.CharacterController.GetCollider()` (`CapsuleCollider`: `center`, `height`, `radius`, `direction`, via its transform, not `bounds`, which come from PhysX);
   - overlap = segment-to-segment distance ≤ wire radius + capsule radius. Early reject by squared distance between segment midpoints.
   - Sweep: if the capsule moved more than its radius since last frame, also test intermediate positions, so a fast mover cannot skip a 6 cm wire.
4. **Enter edges**: per wire, a small set of colliders currently inside. Call `CollisionEnter(collider)` only when a collider joins; drop it when the distance exceeds the overlap limit + 1 cm (hysteresis, so standing on the wire does not re-roll `CHANCE_TO_IGNORE_TRIPWIRE`). A collider already inside when the wire arms counts as an enter, as PhysX would report on its first step.
5. **Single owner**: a skipping prefix on `SyncObjectCollisionChecker.OnTriggerEnter` (intentional behaviour replacement) returns false when its sync object is a tracked wire and `GameWorld.GetPlayerByCollider(collider) != null`. This stops a double roll or double trigger when another holder (a corpse, a grenade) runs the step while a player crosses a wire. Disabling the component would not work: Unity sends trigger messages to disabled MonoBehaviours. Non-player colliders pass through to vanilla.
6. **Setting off** (live): re-`SupportRigidbody` every tracked armed wire, stop skipping the relay, clear the sets. Wires armed afterwards are vanilla.
7. **Fika**: no Fika code. On clients `CollisionEnter` reaches the empty `TriggerTripwire` override, exactly as the vanilla relay does; on the host remote players' impostor capsules are in `AllAlivePlayersList` and `PlayersColliders`.

### Gameplay contract
What must stay identical: crossing the wire between the two stakes trips it (players and bots, with the bot ignore roll), and a wire can be disarmed without setting it off. Both hold by construction:
- The trigger volume is vanilla's own capsule (0.03 m radius from stake pivot to stake pivot, same axis and height as set by `SetupStakes`), tested against the player's actual collider every frame, so crouch/prone capsule changes count as they do in vanilla. Approaching a stake to disarm trips the wire only where the vanilla trigger would.
- Disarming never uses physics: `TripwireInteractionTrigger` (an `IInteractive` with a distance check) → `DeActivateTripwire` → `SetGrenadeState(false)` → `HideContent()` deactivates `ObjectCollision`, so rule 2 stops testing that wire immediately and nothing drives it again.

Accepted divergence (user decision, 2026-11-13): non-player colliders that move without their own registry entry (doors swinging through a wire in a doorway, other animated scene colliders) no longer set wires off unless something else happens to run the step. Not checked or compensated.

### Implementation sketch
- New `src/FuckThesePhysics/TripwireRelease.cs` modelled on `AirdropRelease` (resolve-all-or-nothing install, per-site failure latch, rollback of its own patches only, shared Harmony id). Patches: `BaseTripwire.Init` postfix, `BaseTripwire.ReturnToPool` prefix, `PhysicsExtensions.Simulation.Update` prefix, `SyncObjectCollisionChecker.OnTriggerEnter` prefix, `GameWorld.Dispose` postfix.
- Preallocated wire list (struct: `BaseTripwire`, `Rigidbody`, `CapsuleCollider`, small inside-set) and a per-player last-position cache; no LINQ or allocation per frame. Private fields (`_collisionCollider`, `SyncObjectCollisionChecker._synchronizableObject`) via cached `AccessTools.FieldRefAccess`; verify names in the decompiler first.
- Config: `Tripwire / ReleaseIdleTripwires`, bool, default on, next to Airdrop/BTR.
- Diagnostics: add `TripwireState`, `PlacerPlayerId`, `IsStatic` per tripwire entry to `SimulationOwners.Inspect`; behind the debug toggle, rate-limited lines for each FTP-driven `CollisionEnter` (wire id, player profile id, AI or not, state before and after, so bot ignore rolls are visible) and for relay calls skipped by rule 5.

### Verification
Same as §6/§7, plus: with Partisan wires on the map the report never shows `tripwire(Clone)` entries, and Simulate frames fall to transient-holder levels even while Partisan walks among his wires; walking between the stakes sets a wire off once; walking up to a stake and disarming it does not set it off, and the disarmed wire is never tested again; a bot's ignore rate over repeated crossings matches `CHANCE_TO_IGNORE_TRIPWIRE`; the rule-5 skip line appears when a corpse holds the step during a crossing, with no double trigger.

### Implementation notes (as built)

Found while implementing `src/FuckThesePhysics/TripwireRelease.cs`. The rules above still hold; these are the deviations and details.

1. Player colliders are not always `CapsuleCollider`. `ImpostorCharacterController` and `SimpleCharacterController` return a capsule. `PlayerCharacterController` holds a Unity `CharacterController` (upright capsule, rotation ignored). The detector handles both, reading fields and transform rather than `bounds`. Other collider types are skipped with a one-time warning.
2. Relay skip: the wire is identified by the `SyncObjectCollisionChecker` found beside `_collisionCollider` at `Init`, not by reading `_synchronizableObject` (its type is the Sirenix-typed base). If no checker is found, the wire is left to vanilla and this is logged once.
3. Tracking runs at `Init` even while the setting is off; registry entries are removed only while it is on. Turning it on later releases tracked wires on the next frame. The relay is skipped only for wires whose entries this class removed.
4. Turning the setting off re-registers armed wires (rule 6) and skips hidden ones. Turning it back on releases every tracked wire still registered, hidden ones included.
5. Wire geometry is computed on the first armed frame and kept until the wire is seen hidden. This is safe because `SetupStakes` positions the collider before activation.
6. Faults: a per-frame detector exception sets a fault flag that returns every wire to vanilla (re-registered, relay skip stopped, no new tracking). An exception from one `CollisionEnter` is logged once and nothing is disabled. `Uninstall` re-registers held wires.
7. Teardown: wires whose GameObject is destroyed are dropped. If vanilla changes the wire list during a call, the detector stops the rest of that frame. A wire already hit earlier in the same frame is not called again.
8. Sweep: steps are at most one capsule radius apart, up to 16 steps. A move over 25 m is treated as a teleport and not swept. Grazing contacts at the wire edge (about 3 cm) can be missed.
9. Disabled player colliders (`ICharacterController.isEnabled = false`: BTR passengers, bots created but not yet activated) are not tested, as PhysX reports nothing for them.
10. The relay skip applies only to colliders the detector tested in the current frame; anything else stays with the vanilla relay.
11. The detector prefix runs with `Priority.First`, before `SimulateTimer`'s prefix on `Simulation.Update`, so the report's step timing excludes it.

