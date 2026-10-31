# EFT airdrop physics: static analysis (SPT 4.1 install)

Targets:
- `D:\Tarkov-SPT-4.1\EscapeFromTarkov_Data\Managed\Assembly-CSharp.dll`, MVID `cc2d80b0…c7`.
- `UnityEngine.PhysicsModule.dll` for the Unity-side callers.
- Crate/plane prefab bundles, read with UnityPy (read-only).

Tags: CONFIRMED means I read the decompiled code or prefab data. [INFERENCE] and UNVERIFIED mean I did not directly observe it.
Member IDs are `<mvid>:<token>` and are truncated to the token below.
No code, build or game run was involved. Nothing was edited.

## 0. Verdict

**Claim: "once spawned, airdrop never releases physics; Simulate keeps running for the rest of the raid".**

**Verdict: CONFIRMED in substance, with a corrected mechanism, for the offline/host path.**

- The crate's `Rigidbody` is **kinematic** with gravity off. The plane has **no rigidbody**.
- Both are moved purely by script (transform writes). There is no dynamic simulation to "fail to sleep".
- The leak is in EFT's own physics registry. `ServerAirDrop.Init` calls `PhysicsExtensions.UpdateController.SupportRigidbody(rb)` with no visibility checker.
- Nothing ever calls `UnsupportRigidbody` for the crate.
- The registry counts any registered rigidbody whose GameObject is `activeInHierarchy`. It never looks at `isKinematic`, `IsSleeping()` or ground state. The landed crate stays active until the raid ends, so `Simulation.UpdateEnabled` stays true.
- Left open: the explicit "never returned to pool during the raid" check. I did not run `find_callers` on `SynchronizableObjectLogicProcessor.ReturnToPool`. What I read says no (§2.5).
- Also left open: `ClientAirPlane` was not read.

Scope of the leak: it only occurs where `ClientAirDrop.offlineMode == true`, i.e. `AbstractGame.GameType == EGameType.Offline`.
- `ClientAirDrop.Init` only calls `OfflineServerLogic.Init` when `offlineMode`.
- `SynchronizableObjectLogicProcessor.GetSyncObjectStrategyByType` picks `new ClientAirDrop(offlineServerLogic: true)` only when `GameType == Offline`.
- A pure network client never runs `ServerAirDrop.Init`, so it has no registry entry.
- Fika's GameType and its airdrop handling were not inspected. UNVERIFIED for Fika.

## 1. Who drives physics

### 1.1 Manual simulation (CONFIRMED)

EFT sets Unity to script-simulation mode and calls `Physics.Simulate` itself, once per frame, from its own `Update`.

- `EFT.AbstractApplication.Awake` (token 0600799A):
  ```
  if (AppEnvironment.Config.Physics.ManualUpdate) { PhysicsExtensions.UpdateController.Enabled = true; }
  ```
- `ApplicationConfig.PhysicsSettings.ManualUpdate` is declared `public bool ManualUpdate = true;` (default true).
- `ApplicationConfig.PatchConfig` only copies `BackendUrl` and `MatchingVersion`. I found no `ManualUpdate` string in `StreamingAssets` or `BepInEx\plugins\spt`.
- The setter `PhysicsExtensions.UpdateController.Enabled` (060111D0) does the following when set to true:
  ```
  Simulation.UpdateControlledByUnity = false;
  Simulation.UpdateEnabled = false;
  Simulation.Quality = 1f;
  Simulation.SyncTransformsControlledByUnity = false;
  Simulation.UpdateMode = SmoothSimulate;
  ```
- `Simulation.UpdateControlledByUnity` setter: `Physics.simulationMode = value ? FixedUpdate : Script`.
- `Simulation.SyncTransformsControlledByUnity` setter writes `Physics.autoSyncTransforms`.

### 1.2 Per-frame call chain

1. `EFT.AbstractApplication.Update` (060079A3) is just `PhysicsExtensions.Update();`.
2. `PhysicsExtensions.Update()` (0600316F) runs `Simulation.Update(); UpdateController.Update();`.
3. `Simulation.Update()` (060111C9) branches on `UpdateMode`:
   - `Update` → `Simulate(Time.deltaTime)`
   - `QualityControl` → `QualityControlUpdate`
   - `SmoothSimulate` → `SmoothSimulate(Time.deltaTime)`
4. `Simulation.SmoothSimulate(float)` (060111C7):
   ```
   _simulationDeltaTimes.AddValue(dt);
   if (CanRunSimulate) { dt = avg; if (dt > EFTHardSettings.Instance.MaxSmoothPhysicsDeltaTime) dt = Max…; Physics.Simulate(dt); }
   ```
5. `Simulation.CanRunSimulate` is `Physics.simulationMode == SimulationMode.Script ? _updateEnabled : false`.
6. `Simulation.Simulate(float)` (060111C6) is `if (CanRunSimulate) Physics.Simulate(dt)`.
7. `AbstractApplication.FixedUpdate` (060079A2) → `PhysicsExtensions.FixedUpdate` → `Simulation.FixedUpdate`. This simulates only if `UpdateMode == FixedUpdate`. The mode is always `SmoothSimulate` after `Enabled = true`, so this path is dead.
8. `UpdateController.Update()` (060111D5) runs `if (_enabled) CheckRigidbodiesDone();`.
   - Because `Simulation.Update` runs before it, the flag has one frame of lag.

### 1.3 Call-site inventory (assembly-wide, DecompilerServer `find_usages` / `find_callers`, workspace scope)

| Unity API | Callers |
|---|---|
| `PhysicsScene.Simulate` | none in Assembly-CSharp |
| `Physics.Simulate(float)` | `PhysicsExtensions.Simulation.Simulate`, `Simulation.SmoothSimulate`, and `PhysicsCustomOverlapBoxSimulationTest.Update` (a test behaviour) |
| `Physics.simulationMode` | `Simulation.get/set_UpdateControlledByUnity`, `Simulation.get_CanRunSimulate`, `PhysicsCustomOverlapBoxSimulationTest.Awake` |
| `Physics.autoSimulation` | no callers (including Assembly-CSharp) |
| `Physics.autoSyncTransforms` | `Simulation.get/set_SyncTransformsControlledByUnity`, `Simulation.SyncTransforms`, test `Awake` |
| `Physics.SyncTransforms` | `Simulation.ForceSyncTransforms`, `HideoutGameWorld.BallisticsTick`, `EFT.PlayerSpiritBase.SyncAnimators`, test `Start` |

- `PhysicsExtensions.SyncTransforms` (06003174) is called from `CorpseRagdollTestApplication.LateUpdate`, `PlayerSpiritBase.PlayerSync` and `LootItem.SetupBoundingCollider`.
- `PhysicsExtensions.ForceSyncTransforms` (06003175) has no callers.
- A byte grep of `Managed\*.dll` for `Simulate` hits only Unity modules (Physics, Physics2D, VFX, Timeline). No other game assembly calls it.
- Plugins whose DLLs contain `set_simulationMode`, `set_autoSimulation` or `SyncTransforms` byte strings: `Rupture`, `Rupture.PhysXBackend`, `VisitAPI`, `7Bpencil.WeaponCamoAndStickers`. Not analysed.

### 1.4 The gate: what makes Simulate run vs be skipped (CONFIRMED)

The only runtime gate is the static bool `Simulation._updateEnabled`. It is written only by `UpdateController.SimulationEnabled(bool)` and the `Enabled` setter. `SimulationEnabled` is called only from `CheckRigidbodiesDone`.

`UpdateController.CheckRigidbodiesDone()` (060111D3), verbatim:

```csharp
int num = 0;
for (int i = _rigidbodies.Count - 1; i >= 0; i--) {
  var d = _rigidbodies[i];
  if (!(d.rigidbody == null) && d.rigidbody.gameObject.activeInHierarchy) {
    if (d.visibilityChecker == null || d.visibilityChecker.IsVisible) num++;
  } else _rigidbodies.RemoveAt(i);
}
SimulationEnabled(num > 0);
if (_rigidbodies.Count == 0) Simulation.Quality = 0f;
```

Simulate runs on a frame iff at least one registered `Rigidbody` is non-null and `activeInHierarchy`, and either has no `ObjectVisibility` checker or the checker reports `IsVisible`.

- It does not check `isKinematic`, `IsSleeping()`, velocity or `useGravity`.
- Entries are removed only when the rigidbody is destroyed or its GameObject is inactive, or by an explicit `UnsupportRigidbody`.

Registry API:
- `SupportRigidbody(Rigidbody rigidbody, float quality = 1f, ObjectVisibility visibilityChecker = null)` (060111D1):
  - no-op unless `_enabled`.
  - raises `Simulation.Quality` to `max(quality, Quality)`.
  - does `_rigidbodies.Add(...)` with **no dedupe**.
  - calls `CheckRigidbodiesDone()`.
- `UnsupportRigidbody(Rigidbody)` (060111D2):
  - no-op unless `_enabled`.
  - removes the **first** matching entry only, then `CheckRigidbodiesDone()`.
- `_rigidbodies` is `public static List<RigidbodyData>`. `RigidbodyData` is `{ Rigidbody rigidbody; ObjectVisibility visibilityChecker }`.

### 1.5 Registrants

`SupportRigidbody` callers (find_callers 060111D1, 18 sites):

| Caller | Visibility checker | Releases |
|---|---|---|
| `EFT.Airdrop.ServerAirDrop.Init` | none | never |
| `EFT.Grenade.Awake` | `GetVisibilityChecker()` | none via `Unsupport` (found no caller) |
| `EFT.Interactive.LootItem.StartPhysics` | `GetVisibilityChecker()` | coroutine `PhysicsStopWorker` → `StopPhysics()` does `Object.Destroy(_rigidBody)` and the registry prunes it |
| `EFT.Interactive.CorpseRagdoll.Start` / `AttachWeapon` | not inspected | `StopRigidbody` calls `UnsupportRigidbody`, then sets `isKinematic = true` and `Discrete` |
| `EFT.SynchronizableObjects.BaseTripwire.Init` | not inspected | `ReturnToPool` calls `UnsupportRigidbody` |
| `WheelDrive.OnEnable`, `VehicleSuspension.SuspensionOn` | none | no `Unsupport` found: counted for as long as the GameObject is active |
| `LampController.method_3` | none, quality 0 | none |
| `SmallPhysicsObject.PlayCollision`, `BrokenDoor.Breach` | not inspected | not inspected |
| `MgBelt.SetPhysicsPartsEnabled` | not inspected | `Unsupport` on disable |
| `FlareCartridge.Init`, `GrenadeCartridge.Init`, `BeltDetachablePart.Spawn`, `HideoutArea.CG_SetupItem.MoveNext` | not inspected | `CG_SetupItem` unsupports at IL 538 |

`UnsupportRigidbody` callers: `MgBelt.SetPhysicsPartsEnabled`, `CorpseRagdoll.StopRigidbody`, `HideoutArea.CG_SetupItem.MoveNext`, `BaseTripwire.ReturnToPool` (the last was listed in my first pass and is inferred from the shown site list, token AF3B).

**Caveat for the fix:** other registrants without a visibility checker and without a release (`WheelDrive`, `VehicleSuspension`, `LampController`) also hold the gate open while their GameObjects are active. Fixing the airdrop only helps on maps/raids where nothing else is registered. Nothing says those components are always present.

## 2. Airdrop lifecycle

### 2.1 Types (all Assembly-CSharp)

| Type | Token | Role |
|---|---|---|
| `EFT.Airdrop.ServerAirdropManager : AirdropManager` | 020015A5 | timers, flare handling, `InitContainerLoot` |
| `EFT.Airdrop.ClientAirdropManager : AirdropManager` | 020015AE | client side; `ProjectilesCollector` |
| `EFT.Airdrop.ServerPlane : ISynchronizableLogic` | 020015A6 | plane flight logic |
| `EFT.Airdrop.ClientAirPlane` | 020015AF | not read |
| `EFT.Airdrop.ServerAirDrop : ISynchronizableLogic` | 020015A4 | crate fall, parachute, contact, landing |
| `EFT.Airdrop.ClientAirDrop : ISynchronizableLogic` | 020015AD | visuals, audio, sync; wraps `OfflineServerLogic` |
| `EFT.SynchronizableObjects.AirdropSynchronizableObject : SynchronizableObject` | 0200158F | crate MonoBehaviour |
| `AirplaneSynchronizableObject` | 02001590 | plane MonoBehaviour |
| `EFT.SynchronizableObjects.SynchronizableObjectLogicProcessor` | 02001598 | active/static lists, pooling |
| `ClientSynchronizableObjectLogicProcessor` | 02001593 | adds `ServerAirdropManager` and `ProcessSyncObjectPackets` |
| `SyncObjectCollisionChecker : MonoBehaviour` | 0200159A | `OnTriggerEnter` → `SynchronizableObject.CollisionEnter` |
| `EAirdropFallingStage` | 02000668 | `Fall, ParachuteStartOpen, ParachuteEndOpen, Contact, Landed, None` |

### 2.2 Spawn

Triggers:
- `ServerAirdropManager.ManualUpdate(float)` calls `GameWorld.InitAirdrop()`, either on the timer/chance or after a successful flare (`_projectileSuccessPositions`).
- `AirdropEvent(long)` and `ArtilleryShellingControllerServer` (`CG_InitAirdrop.MoveNext`) call it too.
- find_callers of `GameWorld.InitAirdrop` (06007B48): `ServerAirdropManager.AirdropEvent` @82, `ServerAirdropManager.ManualUpdate` @104 and @296, `ArtilleryShellingControllerServer.CG_InitAirdrop.MoveNext` @151.

`EFT.ClientGameWorld.InitAirdrop(string lootTemplateId = null, bool takeNearbyPoint = false, Vector3 position = default)` (060096C3), verbatim:

```csharp
GameObject go = TakeAirdropPoint(takeNearbyPoint, position);
if (go == null) { Debug.LogError("There is no airdrop points here!"); return; }
SynchronizableObject so = ClientSynchronizableObjectLogicProcessor.TakeFromPool(SynchronizableObjectType.AirPlane);
if (so.Logic is ClientAirPlane { offlineMode: not false } p) p.OfflineServerLogic.ContainerTemplateId = lootTemplateId;
ClientSynchronizableObjectLogicProcessor.InitSyncObject(so, go.transform.position, Vector3.forward);
```

- The base `GameWorld.InitAirdrop` is an empty virtual.

### 2.3 Plane

Facts (CONFIRMED, `ServerPlane`):
- The plane prefab (`il76md-90.prefab` bundle) has **no Rigidbody and no Collider**. Its component list is Transform, Light, MeshFilter/Renderer, ParticleSystem, Animator, 7 MonoBehaviours. It is a pure transform mover.
- The bundle path is inferred from the `InGameBundles` cctor string literal. The `AirPlane → il76md-90` and `AirDrop → scontainer_crate` mapping itself is [INFERENCE].
- `ServerPlane.Init(int objectID, Vector3 airdropPos, Vector3 rotation)`:
  - computes start/exit points against `LevelPhysicsSettings.GetGlobalBounds()`.
  - sets `_flyToAirdrop = true`.
  - starts `AwaitContainerLoot(ContainerTemplateId)`, which takes an `AirDrop` object from the pool and calls `ServerAirdropManager.InitContainerLoot` (backend call `/client/airdrop/loot`).
- `ManualUpdate` → `ProcessFlyToAirdrop` → `AirdropReached()`:
  ```csharp
  SynchronizableObjectLogicProcessor.InitSyncObject(_airdrop, new Vector3(_airdropPosition.x, _airdropPosition.y - 5f, _airdropPosition.z), Vector3.forward, _airdrop.ObjectId);
  ```
- The plane then flies to exit (`ProcessFlyToExit`) until `_isLastObjectPacket`.
- `UpdateOfflineClientLogic` / `UpdateSyncObjectData` call `ReturnToPool()`, which does `IsActive = false; gameObject.SetActive(false)`. The plane GameObject goes inactive and later returns to the pool, so it is fully released.

### 2.4 Crate

Prefab `scontainer_crate.bundle`, via UnityPy (CONFIRMED):

- Root `scontainer_crate` (layer 0) has **one Rigidbody** and `AirdropSynchronizableObject`.
  - Rigidbody: `m_IsKinematic: True`, `m_UseGravity: False`, `m_Mass 2.0`, `m_Drag 0.25`, `m_Interpolate 0` (none), `m_CollisionDetection 0` (Discrete), `m_Constraints 0`.
  - `ServerAirDrop.Init` also sets `SyncObjectRigidbody.useGravity = false`.
- Child `land_trigger` (layer 0): `BoxCollider`, `isTrigger=True`, size 1.325×0.519×0.939, with `SyncObjectCollisionChecker`.
- Child `loot_collider` (layer 22): `LootableContainer` plus trigger `BoxCollider` 1.487×0.374×1.061. `loot_collider_original` ×2 are inactive, non-trigger BoxColliders.
- Child `scontainer_crete_COLLIDER` and `Christmas_airdrop_COLLIDER` (layer 18): convex, non-trigger `MeshCollider`s.
- Children `scontainer_crete_BALLISTIC_woodthick`, `Christmas_airdrop_BALLISTIC_fabric`, `Christmas_airdrop_BALLISTIC_woodthick` (layer 12): non-convex `MeshCollider` + `BallisticCollider`.
- `default_airdrop_view`: `BoxCollider` (non-trigger) + `LODGroup`.
- Other children: `Flicker_Light` (`Light`, `LightFlicker`, `CullingLightObject`), particle systems (`Flare_Smoke`, `Flare_Smoke_fast`, `Flare_fire`, `CreteDust`), `parachute` (Animator), `Parachute_LP12` and `Object000` (SkinnedMeshRenderer), 3 LODGroups.
- Layer names for 12, 18, 22, 28 were not resolved.

Fall logic: **no physics force is used**, all transform writes (CONFIRMED).
- `ServerAirDrop.Init`:
  ```csharp
  _syncObject.gameObject.SetActive(true);
  …
  SyncObjectTransform.position = position;
  PhysicsExtensions.UpdateController.SupportRigidbody(SyncObjectRigidbody);
  SyncObjectRigidbody.useGravity = false;
  …
  _signalFireCoroutine = _syncObject.StartCoroutine(SignalFireCoroutine());
  ```
- `SetActive(true)` precedes `SupportRigidbody`, so the registry entry counts immediately.
- `ManualUpdate(float)` runs by stage:
  - `Fall`: manual ballistic integration with `Physics.gravity` on the transform.
  - `ParachuteStartOpen` / `ParachuteEndOpen`: `ParashuteOpening` / `ParashuteLanding`, plus `Swing()` and `Torque()`.
- Stage changes come from `GetRaycastPoint` (`Physics.RaycastAll` against `LayersMaskController.TerrainLowPoly`) at `ParachuteStartOpenHeight` / `ParachuteEndOpenHeight`.
- Contact detection: `SyncObjectCollisionChecker.OnTriggerEnter(Collider)` → `SynchronizableObject.CollisionEnter` → `ServerAirDrop.CollisionEnter(Collider)`.
  - It ignores Loot, Player, Triggers and Default layer colliders.
  - Otherwise it sets `_fallingStage = Contact`, `_descending = false`, then `CalcExtents(true)`.
  - One-point contact → `OnOnePointContact` → coroutine `RotateAfterDrop` (transform `RotateAround`) → `OnTwoPointContact`.
  - Two-point → `RotateAfterDrop` → `Landed`. Flat → directly `Landed`.
  - Once `_descending == false`, `CollisionEnter` returns early: no further physics events are consumed.
- **This `OnTriggerEnter` is why the crate registers with the physics driver.** A kinematic trigger vs static terrain only produces messages during `Physics.Simulate` ([INFERENCE] from Unity's collision matrix; I did not run it).

### 2.5 Landing and post-landing state (CONFIRMED)

- `ServerAirDrop.ManualUpdate` tail:
  ```csharp
  if (_signalFireCoroutineDone && _fallingStage == EAirdropFallingStage.Landed) _syncObject.IsStatic = true;
  ```
- `_signalFireCoroutineDone` becomes true after `SignalFireCoroutine` runs. It waits for a raycast hit within `SmokeActivateHeight`, then `PlaneAirdropSmoke` seconds of `Time.unscaledTime`.
- When the object is static:
  - `SynchronizableObjectLogicProcessor.RemoveNonActiveAndStaticObjects()` moves it from `_activeSynchronizableObjects` to `_staticSynchronizableObjects`. It is no longer in `ManualUpdate`'s loop.
  - The same method returns objects with `IsActive == false` to the pool. The crate's `IsActive` stays true.
  - `UpdateRequiredHandler` can move it back to active if `OnUpdateRequired` fires.
- `ClientAirDrop.ManualUpdate` (offline) additionally lerps `SyncObjectTransform` toward `_destinationPosition/_destinationRotation`, drives parachute, dust, flare and audio. It stops being called once the crate is static.
- **Nothing unregisters the rigidbody** (CONFIRMED): no `UnsupportRigidbody` anywhere in `ServerAirDrop`, `ClientAirDrop`, `AirdropSynchronizableObject` or the processor.
- Nothing sets the crate inactive on landing.
  - `ServerAirDrop.ReturnToPool` and `ClientAirDrop.ReturnToPool` (the latter ends with `_syncObject.gameObject.SetActive(false)`) exist, but only fire from `SynchronizableObjectLogicProcessor.ReturnToPool`.
  - The callers I read: `RemoveOutdatedObjects` (network packets with `Outdated`; the server airdrop packet sets `Outdated = false`), `ServerPlane.AwaitContainerLoot` (plane failure path) and `RemoveNonActiveAndStaticObjects`. UNVERIFIED: a full `find_callers` on `SynchronizableObjectLogicProcessor.ReturnToPool`.
- `ClientAirDrop.CollisionEnter` forwards to `OfflineServerLogic.CollisionEnter` when `offlineMode`.
- Resulting steady state: one `RigidbodyData{ rb, null }` per crate init stays in `_rigidbodies`. `GameObject.activeInHierarchy` stays true. `CheckRigidbodiesDone` sets `UpdateEnabled = true` every frame, so `SmoothSimulate` → `Physics.Simulate` runs every frame until raid teardown.
- If the pooled airdrop object is re-`Init`'d, `SupportRigidbody` adds a duplicate entry.
- At raid end `Dispose()` clears the lists. The next raid's registry sees destroyed Rigidbodies as `== null` and prunes them.
- What it costs: [INFERENCE] Simulate's per-frame cost comes from the whole default physics scene (transform sync, kinematic actors, callbacks), not the crate itself.

### 2.6 Loot container (what must stay working)

- `AirdropSynchronizableObject.Deserialize` (client) and `ServerAirdropManager.InitContainerLoot` (host/offline) enable the `LootableContainer`, set its `Id`, call `GameWorld.RegisterWorldInteractionObject` and `LootItem.CreateLootContainer(...)`.
- `AirdropSynchronizableObject.ApplyAirdropViewType` adjusts the `BoxCollider` center/size of the container.
- None of that touches the physics driver. [INFERENCE] Interaction is via raycast/overlap on layer 22 colliders, not Simulate. I did not verify this.

## 3. Minimal fix points

### 3.1 Recommended (surgical)

Patch **`EFT.Airdrop.ServerAirDrop.ManualUpdate(float deltaTime)`** (public instance, void). The token is not shown above; resolve it by name via the decompiler.

- Postfix:
  ```csharp
  static void Postfix(ServerAirDrop __instance)
  {
      var so = __instance._syncObject;
      if (so != null && so.IsStatic)   // set in the same ManualUpdate call when _signalFireCoroutineDone && Landed
          Release(__instance);
  }
  ```
- `Release` should:
  1. Run a one-shot guard (e.g. a `ConditionalWeakTable` or a flag on a side dictionary) so it does not run twice.
  2. Optionally call `PhysicsExtensions.ForceSyncTransforms()` first.
  3. Call `PhysicsExtensions.UpdateController.UnsupportRigidbody(__instance.SyncObjectRigidbody)` in a loop. It removes only the first match, and `Init` may have added duplicates. A loop bound by scanning `_rigidbodies` for that rigidbody is safer.
- Why this hook:
  - `IsStatic` implies `Landed` plus the full smoke timer, so many Simulate frames have already synced the crate's final pose to PhysX.
  - `ManualUpdate` is then not called again, so the postfix runs exactly once on the transition frame.
- Earlier alternative: release as soon as `_fallingStage == Landed`, or in a postfix on `ServerAirDrop.CollisionEnter(Collider)` once `_descending == false`.
  - Trigger events are no longer consumed after that.
  - A `RotateAfterDrop` transform animation may still be settling, so the pose-sync risk is higher.
- Do not destroy the Rigidbody. `ServerAirDrop.SyncObjectRigidbody` is `GetComponent<Rigidbody>()` and is dereferenced in `Init` (`SyncObjectRigidbody.useGravity = false`). Pool reuse after a destroyed rigidbody would NRE. `LootItem.StopPhysics` destroys because loot items are not re-pooled.
- Gate: `UnsupportRigidbody` is a no-op when `UpdateController._enabled == false`, so no extra guard is needed.

### 3.2 Other candidate patch points (all signatures from the decompile)

| Target | Signature | Note |
|---|---|---|
| `EFT.Airdrop.ServerAirDrop.Init` | `public void Init(int objectId, Vector3 position, Vector3 rotation)` | Postfix could attach a visibility checker, or prefix could skip `SupportRigidbody`. Skipping loses `OnTriggerEnter` contact detection during descent, so landing would never trigger. |
| `PhysicsExtensions.UpdateController.CheckRigidbodiesDone` | `public static void CheckRigidbodiesDone()` | Global: could treat `isKinematic` or sleeping bodies as done. Wider blast radius (all registrants) and runs every frame; not recommended. |
| `PhysicsExtensions.UpdateController.UnsupportRigidbody` | `public static void UnsupportRigidbody(Rigidbody rigidbody)` | The API to call. |
| `PhysicsExtensions.Simulation.CanRunSimulate` | `public static bool CanRunSimulate { get; }` | Do not patch; it breaks all users. |

### 3.3 State to preserve

- **Loot container:** leave the `LootableContainer` registration alone (§2.6).
- **Crate collider presence:** leave the GameObject active. That covers bullet hits (ballistic colliders, layer 12) and player collision (layer 18 mesh colliders). The physics scene retains static/kinematic colliders without Simulate, [INFERENCE]. The final pose must be synced, hence `ForceSyncTransforms` or the `IsStatic` timing.
- **Network sync:** `ServerAirDrop.UpdateSyncObjectData` and the packet path are untouched. Releasing the registry entry does not alter the `IsStatic` flag handling.
- **Smoke / flare:** `_signalFireCoroutine` is independent of physics and has already finished at `IsStatic`. Any flare particles are particle-system driven.
- **Server loot request:** done at spawn time in `InitContainerLoot`; unaffected.

### 3.4 Risks

- **Other registrants:** `WheelDrive`, `VehicleSuspension`, `LampController`, `Grenade`, `CorpseRagdoll` and others also hold `UpdateEnabled` true while active (§1.5). On maps with those active, the fix has no visible effect.
- **Slopes / roofs:** `_extents`/raycast logic is a pure transform alignment. No rigidbody response exists to hold the crate in place. Releasing physics cannot make it slide. The crate is kinematic, so explosions and impulses cannot move it either. "Re-enabling if hit by explosions" is not needed for the crate itself. [INFERENCE] from `isKinematic = true`.
- **Missing Simulate for triggers and callbacks:** with `UpdateEnabled == false`, no `OnTriggerEnter/Exit` or contact callbacks occur at all, for any collider in the scene. EFT already runs in that state whenever nothing is registered (`UpdateController.Enabled` sets `UpdateEnabled = false` initially), so the game tolerates it. [INFERENCE]
- **Duplicate registry entries on pooled re-init:** loop `UnsupportRigidbody` or filter `_rigidbodies` directly.
- **Thread/ordering:** `CheckRigidbodiesDone` has a one-frame lag relative to `Simulation.Update`; it does not change correctness.
- **Fika:** the network client path never registers (§0). The host path runs the same code if it uses `EGameType.Offline` logic. Unverified.

### 3.5 Debug spawn of the real airdrop (CONFIRMED mechanism, not exercised)

- **`EFT.Player.DebugSpawnAirdrop(bool spawnNearPlayer, Vector3 playerPosition)`** (06008301) is a public **virtual empty stub**: no overrides, no callers. It is not a usable entry point.
- Real entry: **`EFT.ClientGameWorld.InitAirdrop(string lootTemplateId = null, bool takeNearbyPoint = false, Vector3 position = default)`** (override, 060096C3).
  - It does not check `CanSummonAirdrop`.
  - It needs `ClientSynchronizableObjectLogicProcessor.ServerAirdropManager` to exist and have non-empty `_airdropPoints` (from `LocationScene.GetAll<AirdropPoint>()` in `AirdropManager.Init`). With `takeNearbyPoint = true` and a `position` it picks the closest point.
  - Offline (`GameType == Offline`) is required for the full plane-drops-crate behaviour.
  - It also needs `Singleton<GameWorld>.Instance` as a `ClientGameWorld`, `LevelPhysicsSettings` for plane bounds, and the backend `/client/airdrop/loot` endpoint for loot (via `ServerAirdropManager.InitContainerLoot` → `ClientBackendSession.LoadLootContainerData`).
  - `ClientAirPlane` internals were not read.
  - `EFT.DevelopAirdrop` (token 02001097; fields `SpawnNearPlayer`, `PlayerPosition`) is an unread network packet type, reachable via `Client2ServerPacket.DevelopAirdrop`.
- Harmony or reflection hook: call `(Singleton<GameWorld>.Instance as ClientGameWorld).InitAirdrop(null, true, playerPos)`.

## 4. Per-frame costs that persist after landing

CONFIRMED (from code):
- `PhysicsExtensions.Update` runs every frame on the whole registry: `CheckRigidbodiesDone` iterates `_rigidbodies`, plus `Simulation.Update` → `Physics.Simulate(avgDeltaTime)`.
- The crate is in the static list, so there is no `ServerAirDrop.ManualUpdate` / `ClientAirDrop.ManualUpdate` work after landing. `ManualUpdate`'s raycast sorting loops (`GetRaycastPoint`, `HasRaycastHit`) are not run once static.

From prefab only (Unity script bodies not decompiled, cost UNVERIFIED):
- `LightFlicker` and `CullingLightObject` on `Flicker_Light` (the signal-flare light, `CullDistance 30`).
- 3 LODGroups and 4 particle systems (`Flare_Smoke`, `Flare_Smoke_fast`, `Flare_fire`, `CreteDust`) on the active crate.
- The parachute is deactivated by `ParachuteFadeCoroutine`.
- The SynchronizableObject base is a `SerializedMonoBehaviour` with no `Update`.

## 5. Unverified / not done

- `find_callers` on `SynchronizableObjectLogicProcessor.ReturnToPool` (confirms the crate is never returned mid-raid).
- `ClientAirPlane`, `AirplaneSynchronizableObject` and `EFT.DevelopAirdrop` / server packet handling.
- Layer index → name mapping (12, 18, 22, 28).
- Fika's airdrop logic, `EGameType` under Fika, and the actual `Physics.Simulate` cost in a live raid.
- `ObjectVisibility.IsVisible` internals.
- Whether any runtime config overrides `ApplicationConfig.Physics.ManualUpdate` (default `true` in code).
- Whether the final pose of the crate is flushed to PhysX without `SyncTransforms` (my recommendation covers it with the `IsStatic` timing plus an optional `ForceSyncTransforms`).
