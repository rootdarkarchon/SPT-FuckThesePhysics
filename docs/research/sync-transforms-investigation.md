# Handoff: cost of EFT's explicit `Physics.SyncTransforms` per frame (investigation)

Status: **declined (2026-11-04)**. The hitbox/trigger layer lever was judged too risky for the measured gain (§9), and the diagnostics (`SyncTransformsProbe`, `HitboxCensus`) were removed from FTP. The findings below are kept as a record. Written 2026-11-04. Targets EFT 0.16.9.40743 (Assembly-CSharp MVID `cc2d80b06d5b4cb1a5816d2cc901d4c7`), Unity 2022.3.43f1, SPT 4.1.6, Fika 2.4.4 host, Rupture 1.3.0.

Tags: **CONFIRMED** = read in decompiled code, dumped assets, source or a capture. **[INFERENCE]** = reasoned, not observed. **UNVERIFIED** = not checked. Member ids are Assembly-CSharp tokens unless noted.

Supporting evidence lives in SPT-Profiler (`$SOURCE_FOLDER/SPT-Profiler`):
- `docs/research/cpu-sampling-2026-11-04.md`: ETW captures `artifacts/profiles/cpu-20261104-151435` and `cpu-20261104-152104`, Streets, Fika host, about 23 bots plus the player, 60–70 FPS.
- `docs/research/cpu-levers-2026-11-04.md` §2: physics stepping and sync.
- The newer capture `cpu-20261104-160749` was taken with FTP 1.1.0.

Related handoff: `movement-collision-batching.md` (§8: bots' head colliders stay disabled).

## 1. Measured cost
CONFIRMED (ETW, release player, main-thread shares, raid windows only):

| Capture | `Simulation.ForceSyncTransforms` inclusive | ≈ms per frame at 60–70 FPS |
|---|---:|---:|
| `151435` (240–368 s) | 5.37% | 0.8 |
| `152104` (whole, in raid) | 3.64% | 0.5–0.6 |
| `160749` (60–185 s, FTP 1.1.0) | 4.7–5.5% | 0.7–0.8 |

The raid is main-thread bound (the main thread is busy 97.5–98.8% of wall time), so this cost goes straight into frame time.

Native self time under the sync, capture `152104`, 3.64% in total:

| Native frame | Share |
|---|---:|
| `physx::Sc::ElementSim::ElementInteractionIterator::getNext` | 1.42 |
| `notifyActorInteractionsOfTransformChange` | 0.70 |
| `PhysicsManager::SyncTransforms` | 0.40 |
| `Transform::TransformPoint` | 0.28 |
| `physx::Sc::ShapeSim::onVolumeOrTransformChange` | 0.22 |
| `physx::NpRigidDynamic::getKinematicTarget`, `BoxCollider::PoseChanged`, … | small |

[INFERENCE] About **two thirds is PhysX walking the existing interactions** of each moved shape (the pairs PhysX keeps between overlapping colliders and triggers) and notifying them. Only about one third is copying transforms. The interaction part grows with *pairs per moved collider*, not just with the number of moved colliders.

## 2. Who calls it
CONFIRMED (ETW, the only path in the main-thread tree; Assembly-CSharp):
`Player.LateUpdate` (0600813B) → `Player.ComplexLateUpdate` (060082A7) → `Player.ArmsLateUpdate` (060082AD) → `PhysicsExtensions.Simulation.SyncTransforms` (060111CA) → `Simulation.ForceSyncTransforms` (060111CB) → `UnityEngine.Physics.SyncTransforms()` → native `PhysicsManager::SyncTransforms`.

- CONFIRMED: `ArmsLateUpdate` does this only with a hands controller and `IsYourPlayer`, i.e. **once per frame for the local player**, right before the hands controller's `BallisticUpdate` (`ClientFirearmController.BallisticUpdate` 06009B55 → `BallisticsCalculator.ManualUpdate`).
- CONFIRMED: with manual physics, EFT sets `Physics.autoSyncTransforms = false` and `simulationMode = Script` (`PhysicsExtensions.UpdateController.set_Enabled` 060111D0). `Simulation.SyncTransforms` calls `ForceSyncTransforms` because auto-sync is off.
- CONFIRMED: the `Physics.SyncColliderTransform` and `SyncRigidbodyTransform` markers that the Unity Profiler (development player) shows under `Player.LateUpdate` are the inside of this call.
- CONFIRMED: Rupture does **not** trigger this call. Rupture's own `Physics.SyncTransforms()` calls (`rupture/src/Rupture.PhysXBackend/MeshGeometrySources.cs:275,437,533,564`) are occasional geometry-enumeration and probe-setup calls and did not appear in the capture above the 0.1% tree cutoff. `ShadowRagdollWorld.cs:2629` deliberately avoids a global sync.

## 3. Why removing or moving it is not the lever
[INFERENCE] With auto-sync off, this call is what brings moved colliders up to date for everything after it in the frame:
- the local player's ballistics (`BallisticsCalculator`);
- `LateUpdate` queries;
- the next frame's `Update` queries (SAIN vision raycasts, movement overlaps, trigger searches) until the next sync or `Physics.Simulate`.

Removing it leaves stale collider poses. In frames where EFT's physics step is gated off (capture `152104`: `PhysicsExtensions.Update` ≈0%), they could stay stale for many frames. Moving it or syncing less often doesn't reduce the work: the next sync or `Simulate` pushes the same dirty set. The lever is **making each sync cheaper**.

## 4. Main hypothesis: hitbox ↔ trigger interactions
CONFIRMED (dump `$TARKOV_UNITY_DUMP/ProjectSettings/DynamicsManager.asset` and `TagManager.asset`):
- Layers: Player 8, DoorLowPolyCollider 9, PlayerCollisionTest 10, Terrain 11, HighPolyCollider 12, **Triggers 13**, Loot 15, **HitCollider 16**, LowPolyCollider 18, Interactive 22, Deadbody 23. Layers 3, 6 and 7 are unnamed.
- From `m_LayerCollisionMatrix`, the layers each one collides with:
  - `HitCollider` → **only `Triggers`** (plus the unnamed 3, 6, 7).
  - `Triggers` → Terrain, HighPolyCollider, HitCollider, Interactive, TransparentCollider (plus 3, 6, 7).
  - `Player` → Default, Player, DoorLowPolyCollider, Terrain, LowPolyCollider, LevelBorder (plus 3, 6, 7).
- `m_ContactPairsMode: 0` (default: no kinematic-kinematic or kinematic-static contact pairs), `m_AutoSyncTransforms: 1` in project settings, which EFT overrides at runtime, `m_BroadphaseType: 1`.

[INFERENCE] Hitbox colliders follow animated bones on every alive player (≈24 here), so they move every frame. The PhysX pairs they can form are only with trigger colliders, so the interaction iteration under the sync is most likely **hitboxes inside the map's trigger volumes** (zones, areas, exfils and so on). Every sync then walks hitbox × overlapping trigger pairs.

**Candidate lever:** `Physics.IgnoreLayerCollision(HitCollider, Triggers, true)`, a single global setting. This removes those pairs, both from the sync notifications and from broadphase pair maintenance in `Simulate`. [INFERENCE] Upper bound ≈ the interaction share of the sync (≈2.3 of 3.6%, ≈0.3–0.4 ms per frame) plus whatever pair upkeep `Simulate` drops.

**Its risk:** any gameplay that relies on PhysX trigger callbacks (`OnTriggerEnter`/`Stay`/`Exit`) firing for a **hitbox** collider would stop working. [INFERENCE, UNVERIFIED] EFT detects zones for players through `TriggerColliderSearcher` (`Player.UpdateTriggerColliderSearcher` 060082AC → `TriggerColliderSearcher.Core.Search` 060111E7), a manual overlap with the player's own trigger collider, which suggests PhysX trigger events on hitboxes are not what drives zone logic. Not proven.

## 5. Investigation tasks
**Static (read-only):**
1. Enumerate every `OnTriggerEnter`/`OnTriggerStay`/`OnTriggerExit` implementation in Assembly-CSharp, plus SPT and installed mods where source exists (`$TARKOV_MOD_SOURCES`, own repos in `$SOURCE_FOLDER`).
   - For each: which layer its trigger collider is on, and whether it can receive a `HitCollider` collider. Look for `GetComponent<BodyPartCollider>` (type name UNVERIFIED), checks for layer 16, or parent-`Player` lookups from the other collider.
   - Candidates to check first: barbed wire and damage zones, minefields, radiation and sniper zones, BTR, environment and lighting triggers, door and interactable triggers, Fika and SAIN/ORBIT components.
2. Find which layer EFT's hitboxes actually use at runtime: set in the player prefab (`Assets/Content/CommonPrefabs/PlayerSuperior.prefab` in the dump) or assigned in code (search `HitCollider` layer usage, `LayersMaskController`).
3. Inventory the `Triggers`-layer colliders in the Streets scenes (`Assets/Content/Locations/City/**/*.unity`; streaming scripts only, see the dump rules): count, size, and whether they are big volumes that overlap many hitboxes.
4. Check the unnamed layers 3, 6 and 7: what, if anything, sits on them at runtime.

**Runtime (counting build, no behaviour change):**
5. Time the single `Simulation.ForceSyncTransforms` call per frame with a prefix/postfix `Stopwatch`, reusing the pattern in `SimulateTimer.cs`; one call per frame costs nothing measurable. Report mean and p95 per frame alongside the alive-player count.
6. Debug-only experiment, test raids only, behaviour-changing: a config toggle that switches `Physics.IgnoreLayerCollision(16, 13, …)` on and off in **alternating windows** of a few seconds within one raid. Compare the sync time between on and off windows. Drift in the raid affects both sides, so this works without reproducible raids. Also log a count of `OnTrigger*` callbacks per layer pair if a cheap hook exists.
7. Confirm the dirty set: count hitbox colliders per player and whether far/culled bots' hitboxes still move every frame. EFT sets body animators to `CullUpdateTransforms` beyond `AnimatorCullDistance` (10 m default) when not visible (`Player.VisualPass` 0600813A). [INFERENCE] Culled bots should then produce no dirty hitbox transforms, but whether this applies under Fika host and FTC renderer culling is UNVERIFIED.

## 6. Secondary contributors (lower priority)
- Rupture dismemberment adds Unity rigidbodies and convex `MeshCollider`s (`LimbDismembermentSystem.cs:1290,1475`) and ragdoll effect bodies (`ShadowRagdollWorld.cs:2578`). While moving, they add to the dirty set. Event-driven, not steady state.
- The local player's own colliders and the hands/weapon colliders. Small.

## 7. Scope
If the lever holds, it is a global physics-layer setting applied at raid start and restored at raid end, behind a config toggle, with its own Harmony id or none at all (it's just an API call). Fika: it must apply wherever hitboxes move, i.e. host and clients alike. Clients also run this sync for their own player and move observed players' hitboxes. The README `## Fika` statement must say so. Minor version bump per house rules.


## 8. Static findings (2026-11-04)

Scope: EFT 0.16.9.40743, Assembly-CSharp MVID `cc2d80b06d5b4cb1a5816d2cc901d4c7`. Tokens below are from that assembly. **CONFIRMED** means the indicated code or serialized object was inspected, not that its runtime invocation or performance was measured.

### 8.1 Trigger callback inventory and authority

**CONFIRMED:** `search_members` for `OnTrigger(Enter|Stay|Exit)$` returned 67 methods including three interface declarations, not implementations. The complete implementation inventory follows; E/X/S denote Enter/Exit/Stay, respectively. Unless explicitly stated, the owner collider layer and actual scene/prefab instance are **UNVERIFIED**: a callback signature alone does not prove which layer a particular instance uses. `GetPlayerByCollider` is not a parent search: `GameWorld.GetPlayerByCollider` (06007B84) is a dictionary lookup; `GetAlivePlayerBridgeByCollider` (06007B24) is a different dictionary lookup. `GameWorld.RegisterPlayer` (06007B7F) registers the character-controller collider (and, conditionally, spirit controller/aura in the player dictionary), not the body-part array. `RegisterPlayerCollider` (06007B81) has only the hideout fake-player-collider caller 0600A7BC in this assembly. Thus ordinary hitboxes are not shown to be accepted by either lookup; other assembly patches or runtime registrations remain possible. [INFERENCE] For dictionary-filtered callbacks, a raw HitCollider contact is normally rejected, so losing that pair is not itself shown to break that callback.

**CONFIRMED:** There is a separate game-managed trigger path. `TriggerColliderSearcher.Core.Search` (060111E7) issues an overlap query with a layer mask and `QueryTriggerInteraction.Collide`, and `PerformEnter` (060111E8) finds `IPhysicsTrigger` on the overlap result and directly invokes it with the searcher collider. `DamageTrigger` stay (0600E147) separately queries `HitColliderMask` for `BodyPartCollider` and damages the matching player. [INFERENCE] Ignoring a physics collision layer pair does not by itself remove those explicit masked overlap queries or their direct dispatch. Whether every particular volume is served by that path remains **UNVERIFIED**; public method names alone do not establish it. This confirms the manual-search mechanism proposed in §4, not the stronger claim that all gameplay uses it.

| Owner and exact callback tokens | Other-collider checks and consequence; HitCollider verdict |
|---|---|
| `DebugTrainStarter` E 06000BB9, X 06000BBA | Player dictionary, non-AI; debug train action; X empty. Ordinary hitbox rejected as above. |
| `AIPlaceInfo` E 0600299E, X 0600299D | Explicit `IPhysicsTrigger`; player dictionary and AIData, adds/leaves place. Normal searcher collider, not evidence of hitbox dependence. |
| `ServerRoomColliderArea` E 06002A9D, S 06002A9E, X 06002A9F | Alive bridge dictionary, per-collider tracking and optional player-center-inside validation; invokes area transitions. Ordinary hitbox rejected; stay is explicit interface. |
| `MineDirectional` E 0600303A, X 0600303C; `MineDirectionalColliders` E 06003046, X 06003047 | Child forwards to parent; mine checks player dictionary in `ShouldIgnore` 0600303B and role/offline trader-zone policy, explodes only in `ClientLocalGameWorld`; exit empty. Ordinary hitbox rejected; mine trigger layer **UNVERIFIED**. Fika prefix further skips clients, below. |
| `PhysicsTriggerHandler` E 06003059, X 0600305A | Explicit interface forwards collider to configured delegates without filtering. Delegate consumers and layer **UNVERIFIED**. |
| `RainControllerOld` E 06004BB7, X 06004BB8 | No filter, flips direction; hitbox could be accepted if a 13/16 pair and Unity dispatch occur; instance/layer/use **UNVERIFIED**. |
| `GPUInstancerModificationCollider` E 06006265 | Requires `GPUInstancerPrefab` on the other GameObject and an enabled runtime-modifiable prototype; ordinary hitbox not shown to qualify. |
| `RootMotion.TriggerEventBroadcaster` E/S/X 06006B41–06006B43 | Unfiltered `SendMessage` forwards collider to a target; hitbox possible if instantiated on layer 13, target/use/layer **UNVERIFIED**. |
| `RootMotion.FinalIK.InteractionSystem` E/X 06006E21–06006E22 | Gets `InteractionTrigger` on the other object; hitbox is not shown to have it. |
| `HideoutAreaTrigger` E/X 0600A0C4–0600A0C5; `HideoutInteractionTrigger` E/X 0600A132–0600A133 | Player dictionary; latter also requires `HideoutPlayerOwner`. Ordinary hitbox rejected; hideout layers **UNVERIFIED**. |
| `ShootingAreaTrigger` E/X 0600A818–0600A819 | Unfiltered, enables/disables shooting-range targets; hitbox could affect it if layer 13 and callback delivered; layer/raid scope **UNVERIFIED**. |
| `SyncObjectCollisionChecker` E 0600AF6C | Unfiltered forwarding to synchronizable object; actual subtype, receiving layer, and hitbox effect **UNVERIFIED**. |
| `TriggerZone` E/X 0600D959–0600D95A | Player dictionary; authority callbacks only on `ClientLocalGameWorld`. Ordinary hitbox rejected. |
| `EnvironmentSwitcherTrigger` E/S/X 0600D9BF–0600D9C1 | All three inspected methods empty. No gameplay dependence on these bodies. |
| `MovingPlatform` E/X 0600DA09–0600DA0A | Requires `IPlatformTransportee` component on other collider; hitbox not shown to have one. |
| `BufferOuterBattleZone` E/X 0600DDA7–0600DDA8 | Explicit interface, player dictionary, maintains zone membership. Ordinary hitbox rejected. |
| `ExfiltrationPoint` E/X 0600E05D–0600E05E | Explicit interface, player dictionary plus BTR passenger/state, bans and infiltration; adds/cancels extraction. Ordinary hitbox rejected; BTR check is not evidence of hitbox-based BTR detection. |
| `ObstacleCollider` E/X 0600E0A4–0600E0A5 | Player dictionary, bot policy, movement obstacle enter/exit; ordinary hitbox rejected. |
| `TransitPoint` E/X 0600E0B7–0600E0B8 | `TryGetPlayer`, enable/events and summoned transit gates; hitbox acceptance and layer **UNVERIFIED**. |
| `BorderZone` E/X 0600E0F9, 0600E0FC | Player dictionary and target-list bookkeeping; ordinary hitbox rejected. |
| `DamageTrigger` E/X/S 0600E145–0600E147 | Alive bridge dictionary for entering player, applies/removes penalty; explicit stay uses HitCollider-mask overlap and same-player `BodyPartCollider` to apply body-part damage. The damage query involves hitboxes, but is not a native 13/16 trigger callback; actual runtime configuration **UNVERIFIED**. |
| `TreeInteractive` E/X/S 0600E2C7–0600E2C9 | Alive bridge dictionary and sound/AI tracking, stay explicit interface; ordinary hitbox rejected. |
| `TriggerWithId` E/X 0600E2D2, 0600E2D4 | Player dictionary, invokes ID trigger events; ordinary hitbox rejected. |
| `EventObjectTrigger` E/X 0600E3CB–0600E3CC | Local-player and validity checks via helper, opens/closes event panel; hitbox acceptance and owner layer **UNVERIFIED**. |
| `BetterPropagationVolume` E/X 0600E3E1–0600E3E2 | Player dictionary, volume/audio state; ordinary hitbox rejected. |
| `BrokenWindowPieceCollider` S/E/X 0600E440–0600E442 | E/X empty; explicit S tests `PlayerMask` overlap and fires one collision event. City example below is layer 13. No demonstrated need for hitbox PhysX callbacks. |
| `ColliderReporter` E/X 0600E472–0600E473 | Unfiltered bool and event forwarding; consumers/owner layer **UNVERIFIED**; potential if attached to layer 13. |
| `PhysicsCustomOverlapBoxSimulationTest.BoxColliderGizmoDrawer` E/X 060111A0–060111A1 | Requires exact `Tester` collider; diagnostic; whether tester is hitbox **UNVERIFIED**. |

**CONFIRMED:** Searches of checked-out `$TARKOV_MOD_SOURCES` and `$SOURCE_FOLDER` sources for callback names found the Fika patch `$TARKOV_MOD_SOURCES/Fika-Plugin/Fika.Core/Main/Patches/Mines/MineDirectional_OnTriggerEnter_Patch.cs:7-22`: client prefix returns false, host continues vanilla. Targeted searches of Fika, SAIN, ORBIT, Rupture and SPT client `modules` found no new callback declarations; `VC/VisceralCombat/Ragdolls/Classes/RootMotion/TriggerEventBroadcaster.cs:16-30` is a duplicate-style forwarding implementation, with deployment and owning collider layer **UNVERIFIED**. These source searches do not cover unavailable, generated, or binary-only mods and do not establish checked-out revision matches every installed binary. Minefield, radiation/sniper, BTR and doors may use non-callback logic; no claim of exhaustive behavioral tracing for those systems follows merely from callback enumeration.

### 8.2 Runtime hitbox layer

**CONFIRMED:** `Player.SetupHitColliders` (060082C7) collects `BodyPartCollider` and `ArmorPlateCollider`; `Player.SetupBodyPartCollider` (060082C8) assigns `LayerMask.NameToLayer("HitCollider")` to each component GameObject and, for armor, children. `ObservedPlayerView.SetupHitColliders` (0600C061) and `SetupBodyPartCollider` (0600C062) do the same using `LayersMaskController.HitColliderLayer`; its initializer (060037DE) resolves that name. The dumped TagManager maps it to 16. `PlayerSuperior.prefab:141-143,4947-4950` has a pelvis collider component on a serialized layer-8 bone; setup changes the runtime layer. This is code-path confirmation, not a live census of all active colliders or other prefab variants.

### 8.3 Streets trigger inventory

**CONFIRMED:** Targeted search of the serialized `City/City_Scripts.unity` found one explicit `m_Layer: 13` GameObject, `Child` (fileID 1702, line 31846), with enabled trigger BoxCollider 10011 (`m_Size` 1×1×1 in local coordinates, lines 209825–209845), and component 12074. Its script GUID resolves to `Assets/Scripts/Assembly-CSharp/EFT/Interactive/BrokenWindowPieceCollider.cs.meta`; parent scaling and world bounds were not resolved. This is not a whole-map count: other City scenes, streaming area scenes, instantiated prefabs, asset bundles, and runtime-created objects are not represented by counting this one scripts scene; serialized local dimensions are not world dimensions. **UNVERIFIED:** actual loaded Streets layer-13 counts, world sizes and overlap population; the in-raid `HitboxCensus` report is the intended runtime census, subject to what it actually records.

### 8.4 Unnamed layers 3, 6 and 7

**CONFIRMED:** `TagManager.asset` names none of 3, 6 or 7. Targeted lookups of `PlayerSuperior.prefab` and `City_Scripts.unity` found no serialized GameObjects on those exact layers. `LayersMaskController` initializer 060037DE uses named layers, with no named 3/6/7 assignments in that initializer. **UNVERIFIED:** other prefabs, streamed scenes, dynamically assigned layers and code constants elsewhere. This is not proof the layers are unused in a raid.

### 8.5 Risk verdict

**[INFERENCE] No concrete gameplay break from disabling only HitCollider↔Triggers is established by the inspected static evidence.** Standard player-identity callbacks use registered movement colliders, not a parent-player lookup from every hitbox; the damage zone explicitly queries hitboxes separately. However, **UNVERIFIED is not safe**: unfiltered callbacks and forwarding delegates, actual trigger layers, runtime registrations and mod/binary-specific behavior remain open. Physics may reset existing trigger state when the setting changes, so alternating windows themselves can produce transition effects independent of steady-state behavior. Do not treat this as a production-safe conclusion or as a measured saving; use the planned debug-only raid experiment and targeted checks of mines, damage zones, extraction, audio/lighting and interactions.

## 9. Runtime findings (2026-11-04, FTP 1.1.0 development build)

Source: `BepInEx/LogOutput.log` from one user-run raid, plus two reports (`Report / Key`) at 16:02:39 and 16:05:05 UTC, saved as `BepInEx/plugins/FuckThesePhysics/reports/physics-20261104-160239.txt` and `physics-20261104-160506.txt`. Streets (the census lists `City_*` audio triggers), Fika host, 21 alive players (20 bots plus the local player), about 60 FPS. One run, one map, no repeat. All numbers below are CONFIRMED log values unless tagged.

### 9.1 Setup and validity
- `Debug / SyncLayerExperiment` on, window 5 s, 10 settle frames plus one `Physics.Simulate` after each flip. The experiment started normally (layers HitCollider 16 × Triggers 13, original `ignore=false`) and was still running at the last report: 207.6 s, 41 flips.
- Physics step held on with an unreleased crate: `Airdrop / ReleaseLandedAirdrops` off, crate called with `Debug / SpawnAirdropKey`. In the second report window the step ran on 8806 of 8806 frames, with the crate as the only registry entry (kinematic, sleeping).
- `flips waiting for a Physics.Simulate: 7`. The count was already 7 at the 60 s summary and stayed at 7 through 207 s, so all unsettled flips fell in the first minute, before the crate held the step on. The cumulative numbers include that first minute; its unsettled frames are excluded from the phase statistics by design.

### 9.2 Layer experiment result
Cumulative at 207 s (second report):

| | BASELINE | IGNORED | IGNORED − BASELINE |
|---|---:|---:|---:|
| Sampled frames | 4992 | 5095 | |
| `ForceSyncTransforms` ms/frame (p50 / p95) | 0.723 (0.652 / 1.229) | 0.669 (0.594 / 1.162) | **−0.053 (−7.4%)** |
| `Physics.Simulate` calls / ms per frame | 4571 / 2.814 | 4906 / 2.435 | **−0.38 ms/frame** |
| `Physics.Simulate` ms per call ([INFERENCE] ms/frame × frames ÷ calls) | ≈3.07 | ≈2.53 | ≈−0.55 |
| Frame ms (hitches excluded) | 16.114 | 15.647 | −0.467 (−2.9%) |
| Mean alive players | 20.11 | 20.08 | |

- The sync delta was stable across the 120 s, 180 s and 207 s summaries (−7.7%, −7.6%, −7.4%).
- The frame-time delta was not stable: +1.044 ms at 60 s, −0.026 ms at 120 s, −0.215 ms at 180 s, −0.467 ms at 207 s. Treat it as noise for this run.
- Overall sync cost with the probe: 0.700 ms/frame mean, p50 0.686, p95 1.328 (8806 frames). This matches the ETW estimate in §1 (0.5–0.8 ms/frame).

**Verdict on §4.** CONFIRMED for this run: ignoring HitCollider↔Triggers removes about 0.05 ms of the ≈0.7 ms sync per frame. The §1 inference that about two thirds of the sync (≈0.3–0.4 ms) is hitbox↔trigger interaction walking does not hold. Most of the sync cost comes from something else. [INFERENCE] Candidates: the transform copy itself for ≈700 moving hitboxes plus other moving colliders, and interactions with layers other than Triggers; neither is measured.

**Simulate.** The same setting cuts about 0.4–0.55 ms from each `Physics.Simulate` [INFERENCE from the per-phase call and frame counts]. That saving exists only in frames where EFT's physics step runs. With this mod's releases on, the step ran in 10.6% of frames in the first report window (3396 of 32009 frames, 0.247 ms/frame average simulate cost). [INFERENCE] With the step mostly gated off, the expected average gain is small; it matters only while something (BTR driving, grenades, ragdolls, unreleased bodies) holds the step on.

**Side measurement.** Holding the step on with one sleeping, kinematic crate raised `Physics.Simulate` from 0.247 ms/frame (first window, releases on) to 2.841 ms/frame (second window). The windows differ in time and raid state, so this is a rough live figure for what the airdrop release saves, not a controlled comparison.

### 9.3 Hitbox census (tasks 2, 4, 7)
- **Runtime hitbox layer (task 2).** Every alive player had 45 colliders on layer 16, 27–34 of them live (enabled and active in hierarchy), and 39 `BodyPartCollider` components, all on layer 16. 693 live hitbox colliders in total. Confirms §8.2 at runtime.
- **Unnamed layers (task 4).** Layers 3, 6 and 7: 0 colliders loaded, including inactive objects.
- **Dirty set (task 7).** All 20 bots were at 55–485 m, culled by the `VisualPass` rule, with body and arms animators reporting `CullUpdateTransforms`; 3 of them were `OnScreen`. 72 two-frame samples over 145 s:
  - local player: 90.5% of hitbox colliders moved between consecutive frames (89.7% beyond Unity's `==` tolerance);
  - culled bots: 67.8% moved (67.6% beyond tolerance), 41,943 collider samples.
  - [INFERENCE] Animator culling does not keep culled bots' hitboxes out of the dirty set; the likely cause is the bots' own movement moving the whole hierarchy. "Moved" is a lower bound of Unity's dirty set.

### 9.4 Triggers layer inventory (task 3)
- Layer 13: 15,879 colliders, 9,402 active, 8,583 of those `isTrigger`.
- Hitbox AABB × trigger AABB overlap estimate: 12,101 pairs (10,710 in the first report), 248 triggers with at least one pair. This is an upper bound; PhysX pairs need shape overlap.
- The pairs come from a few very large volumes:
  - `SpawnPointMarker` sphere colliders, 180–300 m across (up to 181 pairs each);
  - `AudioTriggerArea` boxes from `City_Sound` / `SpatialAudioSystem`, up to 550 m long (up to 181 pairs);
  - `ExperienceTrigger` quest zones such as `quest_weap_race` (300 × 20 × 300 m, 203 pairs) and `quest_zone_kill_stilo`.
- [INFERENCE] These volumes overlap most hitboxes on the map, about 17 estimated pairs per live hitbox. That removing them saves only ≈0.05 ms of sync is consistent with the §9.2 verdict.

### 9.5 Open
- Gameplay safety of ignoring HitCollider↔Triggers (§8.5) is still unverified; this run measured cost, not behaviour.
- One run on one map. A repeat on Streets and on a second map would show whether the Simulate saving is stable.
- What makes up the remaining ≈0.65 ms of sync is unmeasured.
