# BTR suspension and the physics gate: research

Static analysis only; written before the fix existed. Option (a) below was implemented as `BTR/ReleaseParkedSuspension` (`src/FuckThesePhysics/BtrSuspensionRelease.cs`, see `docs/design.md` §6). Where this file disagrees with `docs/design.md`, design.md wins.

Sources:
- `Assembly-CSharp.dll` from `D:/Tarkov-SPT-4.1/EscapeFromTarkov_Data/Managed/` (context alias `airdroprelease`).
- `Fika.Core.dll` from `D:/Tarkov-SPT-4.1/BepInEx/plugins/Fika/` (context alias `btr_fika`).
- SPT server data under `D:/Tarkov-SPT-4.1/SPT_Runtime/SPT_Data/`.

Tags: CONFIRMED = read in decompiled code or config. [INFERENCE] = reasoned. UNVERIFIED = not checked.
Answers the BTR questions Q1–Q5 posed in the earlier handoff (since folded into `docs/design.md`). Gate mechanics are in `docs/design.md` §2 and `docs/research/gate-holders.md`.

## 0. Summary

- CONFIRMED: `SuspensionOn` registers `parentBody` on every start-moving. `SuspensionOff` never unregisters. One On/Off pair happens per stop, so entries accumulate until raid end.
- CONFIRMED: the BTR exists only on **Woods** (and the `develop` map) in this SPT 4.1 install. Streets is excluded by EFT's own check. Customs and Lighthouse are NOT in `LocationsWithBTR`, so `gate-holders.md` §2 was wrong to list them.
- CONFIRMED: on Woods the BTR spawns with 100% chance, 180–360 s after raid start. After its first departure it holds the gate open for the rest of the raid, even when parked.
- CONFIRMED: Fika clients are affected too. The `BTRView` is client-side on every machine, unlike the airdrop crate, which only the host owns.
- Recommendation: option (a), a `SuspensionOff` postfix, as a separate config toggle. Implemented as `BTR/ReleaseParkedSuspension`. Details in §6.

## 1. Q1: callers, triggers and cadence

### Call graph (CONFIRMED, `find_callers` with workspace scope)

| Member | Sole caller |
|---|---|
| `VehicleSuspension.SuspensionOff` | `VehicleSuspension.CG_DisableSuspension.MoveNext` (IL 31, after `WaitForSeconds`) |
| `VehicleSuspension.SuspensionOn` | `VehicleSuspension.CG_EnableSuspensionCoroutine.MoveNext` (IL 31) |
| `VehicleSuspension.EnableSuspensionCoroutineStart(float)` | `EFT.Vehicle.BTRView.Update` (IL 481) |
| `VehicleSuspension.DisableSuspensionCoroutineStart(float)` | `EFT.Vehicle.BTRView.Update` (IL 525) |

Nothing in Fika.Core references suspension. A `Suspension` symbol search in Fika returned 0 hits.

### Trigger logic (CONFIRMED, `BTRView.Update`)

```csharp
if (CurrentSpeed > 0f && !suspension.suspensionEnabled && BtrState == EBtrState.Running)
    suspension.EnableSuspensionCoroutineStart(ActivateSuspensionTime);
else if (CurrentSpeed == 0f && suspension.suspensionEnabled)
    suspension.DisableSuspensionCoroutineStart(ActivateSuspensionTime);
```

`VehicleSuspension` flag and coroutine behaviour:
- `EnableSuspensionCoroutineStart` sets `suspensionEnabled = true` immediately, then starts `EnableSuspensionCoroutine(time)`.
- `DisableSuspensionCoroutineStart` sets `suspensionEnabled = false` immediately, then starts `DisableSuspension(time)`.
- Both coroutines skip on `LocationId == "TarkovStreets"`. Otherwise they wait `time` seconds, then call `SuspensionOn()` or `SuspensionOff()`.
- Neither coroutine handle is ever stopped, except in `OnDestroy`.
- `SuspensionOff` also sets `suspensionEnabled = false`.

Answer to "does the flag reset so On repeats": **yes**. The flag is false again after every stop, so the next departure calls `EnableSuspensionCoroutineStart` and `SuspensionOn` again.

### Where `CurrentSpeed` and `BtrState` come from (CONFIRMED)

- `BTRView.CurrentSpeed` is a plain property, written only by `BTRView.SyncViewFromServer(ref ShapshotBTRMessage)` from `packet.currentSpeed`.
- The offline path (`BtrController.SyncBtrOffline`) copies `BtrVehicle.currentSpeed`, `BtrVehicle.BtrState` and the pose into `_offlineSyncPacket` and calls `BtrView.SyncViewFromServer`. It runs in `GameWorld.OnLateUpdate`, so once per frame. Note it copies only when `BTRTurret` and `gunsBlockRoot` are non-null.
- `VehicleBase.FixedUpdate` runs `PauseProcess()` then `MoveProcess()`. `MoveProcess` sets `currentSpeed = 0` whenever `_pauseOn` is true or `_moveBySplineOn` is false.
- `MovePauseOn` (from `InitPauseOnDestinationPoint`, called by `IncomingToDestination`) sets `BtrState = Stop/StopPaid`, `_pauseOn = true` and calls `ResetCurrentSpeed()`. `MovePauseOff` sets `BtrState = Running`.
- Pause length is `Random.Range(pauseDurationRange.x, y)` = **90–150 s** (from `locationServices.json`, §4).

### Cadence per raid

Each destination arrival is one stop, and each departure is one start. Each stop gives one On/Off pair and one added entry.

- Woods routes in `globals.json` (28423–28552) are circle routes. For example `RouteOne` has `circleCount: 5` with 6 path points, and `enterPoint`/`exitPoint` `p10`. [INFERENCE] That is on the order of 5 × 6 stops per route, so tens of On calls and tens of leaked entries over a long raid.
- At a stop the BTR is parked for 90–150 s with the body kinematic but still registered. The BTR drives between stops. The drive time per leg is UNVERIFIED (speed 8 and spline lengths not measured). [INFERENCE] Parked time is therefore a large fraction of the raid.
- `ActivateSuspensionTime` (BTRView prefab field) is UNVERIFIED. The prefab data was not readable with the available tooling (MonoBehaviour script resolution failed in `resources.assets`).
- Ordering race (CONFIRMED structure, [INFERENCE] effect): Enable and Disable coroutines use the same delay, so On/Off fire in the order started. A stop shorter than `ActivateSuspensionTime` still yields On, Off, On in order. Two Ons without an Off between them are not expected, but the code does not forbid them.

### Despawn

- `VehicleBase.IncomingToDestination` at the exit point calls `MoveDisable()`, which calls `Reset()`. That sets `BtrState = Stop`, deactivates the server `BTRVehicle` GameObject and sets `VehicleRouteState = OnDepot`.
- The speed goes to 0 first, so the normal Off path runs.
- `BTRView` is not destroyed by this. It lives until `OnDestroy` at raid dispose. UNVERIFIED: what `BTRView.method_19` and `method_20` do on OnDepot/OnRoute. They were not read, so it is unknown whether the view is hidden.
- Neither path unregisters anything.

## 2. Q2: gameplay role of the dynamic suspension

### Evidence that it is visual or physics-dummy only

- CONFIRMED: **the authoritative BTR is a separate object.**
  - `BtrController.InitServer` loads the `BTRVehicle` from `BTRTemplates/Server/BTRTemplate`. `BTRVehicle.Start` calls `DisableRender()` (offline) and moves it along splines by transform writes in `VehicleBase.FixedUpdate`.
  - Passengers, the `_killCollider` pedestrian overlap, bot turret, doors and speed/state all live on `BTRVehicle`.
  - `BTRView` is loaded in `InitClient` as a client replica. `BtrImplementationsPhysicsIgnore()` makes the two ignore each other's colliders.
- CONFIRMED: the view root pose comes from the packet, not from physics. `BTRView.method_5` lerps `_cachedTransform` toward `_targetPosition` / `TargetRotation`. It only calls `suspension.SetSuspensionInertiaOffset` as an extra input.
- CONFIRMED: `VehicleSuspensionSpring.FixedUpdate` does `Physics.Raycast` (`MOVEMENT_MASK`) then `rb.AddForceAtPosition`. Those forces only take effect when `Physics.Simulate` runs.
- CONFIRMED: `BTRView.Update` snaps `PhysicsRoot` back to `localPosition` / `localEulerAngles` zero when it drifts beyond `_fallFactor` or the angle limits. The suspension body is a sprung child that is repeatedly reset, which fits a visual/feel role.
- CONFIRMED: **Streets never enables the suspension** (the coroutine early-out), yet the BTR spawns, drives, carries passengers and gets shot there. So BSG's own build runs the BTR without it.
- CONFIRMED: `AddViewCollidersToIgnore` ignores collisions between `SuspensionCollider` and all `ViewRoot` colliders. [INFERENCE] The dynamic body is a world-colliding dummy separate from the view's ballistic/player colliders.

### Fika

- CONFIRMED: Fika does not sync suspension state. The host sends the whole `_offlineSyncPacket` (`ShapshotBTRMessage`: position, rotation, `currentSpeed`, state, turret, side states) through `FikaServer.SendBTRPacket` at 20 Hz, `DeliveryMethod.Unreliable` (`BTRViewSynchronizer.UpdateRate = 20f`).
- CONFIRMED: clients run `BtrController_InitController_Patch`, which calls `InitClient` only. Their `BTRView.Update` then drives their own suspension from the packet's `CurrentSpeed`. Every Fika client therefore leaks entries independently of the host.
- Fika gates this behind `FikaConfig.UseBTR` (`BaseGameController.InitBTRController`, then the `LocationsWithBTR` check).

### Residual uncertainty

- UNVERIFIED (prefab data): `parentBody` default `isKinematic`, the `ConfigurableJoint` setup (connected body/anchor), whether `ViewRoot` is a child of `PhysicsRoot`, and `ActivateSuspensionTime`.
- [INFERENCE] The visible effect of removing the suspension is body bob and settling on bumps. No collision, bot, turret or sync dependency was found in code.
- `Player.ArmsLateUpdate` calls `SyncTransforms` every frame for the local player (see `docs/research/physics-interop.md`), so collider staleness while the step is off is not an issue for the local player.

## 3. Q3: candidate fixes

### Registry facts the patches rely on

- `PhysicsExtensions.UpdateController.UnsupportRigidbody(Rigidbody)` removes only the first matching entry, then runs `CheckRigidbodiesDone()`.
- `SupportRigidbody` never deduplicates.
- `isKinematic` has no effect on the gate.

### (a) `SuspensionOff` postfix: unsupport all `parentBody` entries

Target: `[HarmonyPatch(typeof(VehicleSuspension), nameof(VehicleSuspension.SuspensionOff))]`. `VehicleSuspension` is in the global namespace of Assembly-CSharp, and the method is public instance `void SuspensionOff()`.

```csharp
static void Postfix(VehicleSuspension __instance)
{
    var rb = __instance.parentBody;
    if (rb == null) return;
    // remove every entry for rb (access _rigidbodies with the same style as SimulationOwners.cs)
    // loop UnsupportRigidbody(rb) while any entry with .rigidbody == rb remains
}
```

- Blast radius: only the BTR body, and only after the game itself has made it kinematic with gravity off and the springs inactive. While driving, the step still runs, as designed.
- Risks:
  - The body is unregistered until the next `SuspensionOn`, which re-registers. If the step is off while the BTR starts moving during the pre-On delay, the view root still moves because it is driven by transform writes, not physics.
  - Loop termination: guard with an iteration cap or break when `UnsupportRigidbody` removes nothing.
  - A Harmony patch on a method called from a compiler-generated coroutine is fine. It patches `SuspensionOff` itself, not the iterator.
- No Harmony conflict is known. Fika and Rupture were already checked against `ServerAirDrop` and `PhysicsExtensions`, and Fika has no suspension references.

### (b) (a) plus a `SuspensionOn` dedupe prefix

Target: `[HarmonyPatch(typeof(VehicleSuspension), nameof(VehicleSuspension.SuspensionOn))]`, with a `static void Prefix(VehicleSuspension __instance)` that removes all existing entries for `parentBody`. The original then adds exactly one.

- Value is marginal once (a) is in place. A double On without an Off is not expected from the cadence in §1, so entries are already at most 1 at any time.
- Cost: one more patch on a hot-ish but rare path. No extra gameplay risk.
- Reasonable only as defense-in-depth, or as the sole fix if the user wants no release while parked. It does not fix the leak by itself, because the one remaining entry would still hold the step open while parked.

### (c) Skip the suspension, as Streets does

Target: prefix on `VehicleSuspension.SuspensionOn` returning false, and optionally on `SuspensionOff` for full Streets parity. Alternatively patch `BTRView.Update`, which is larger.

- Blast radius: changes visual behaviour on Woods and removes the BTR from the gate entirely, including while driving, so the step could be off the whole raid.
- Risks: loses the bob/settle look and deviates from EFT's intended Woods behaviour. No code dependency was found, but the prefab internals are UNVERIFIED.
- Policy decision for the user. Streets proves the BTR functions without it.

### Ranking (smallest blast radius first)

1. (a): fixes the leak, keeps vanilla behaviour.
2. (b) on top of (a): marginal.
3. (c): largest, and only if the user wants the Streets behaviour.

## 4. Q4: maps and spawn chance

### Which maps get a `BtrController` (CONFIRMED)

- `EFT.BaseLocalGame.CG_Run.MoveNext` (IL 584–601) and `EFT.NetworkGame.CG_Struct547.MoveNext` (IL 1546–1565) create `new BtrController(gameWorld)` only when `GlobalConfiguration.BTRSettings.LocationsWithBTR` contains `Location.Id`.
- Fika's `BaseGameController.InitBTRController` does the same check after `FikaPlugin.Instance.Settings.UseBTR`.
- SPT install config: `SPT_Runtime/SPT_Data/database/globals.json` lines 28266–28270 give `LocationsWithBTR` = `develop`, `Woods`, `TarkovStreets`. `MapsConfigs` (28271–28552) has entries for `Develop`, `TarkovStreets` and `Woods`.
- Customs and Lighthouse are not listed, so no BTR on those maps in this install.

### Spawn chance and timing (CONFIRMED values, [INFERENCE] source mapping)

`SPT_Runtime/SPT_Data/database/templates/locationServices.json`, key `BTRServerSettings.ServerMapBTRSettings`:

| Map | `ChanceSpawn` | `SpawnPeriod` (s) | `MoveSpeed` | `PauseDurationRange` (s) |
|---|---|---|---|---|
| Woods | 100 | 180–360 | 8 | 90–150 |
| TarkovStreets | 100 | 300–600 | 8 | 90–150 |
| Develop | 70 | 300–600 | 8 | 90–150 |

- `BtrController.InitServer` reads `_btrServerSettings.ServerMapBTRSettings` matched by `MapID == locationId`, where `_btrServerSettings = GlobalConfiguration.BTRLocalSettings`.
- `GenerateChanceSpawnBtr` is `Random.Next(0,100) < ChanceSpawn`, so 100 always spawns.
- `BTRLocalSettings` is written in `EftClientBackendSession.CG_LocalRaidStarted.MoveNext` and read in `BtrController.CG_InitServer`. [INFERENCE] It is fed from the server's `BTRServerSettings` data (names match); the response path was not traced.
- Spawn timing: after the raid reaches `Started`, a `Task.Delay(random(SpawnPeriod) s)` runs, then `InitBTRServer()` and `MoveEnable()`. On Woods the first departure, and the first `SuspensionOn`, is about 3–6 minutes into the raid, plus `ActivateSuspensionTime`.
- Effective result: on **Woods** a BTR spawns every raid. Streets has the BTR but no suspension, so no leak. Other maps have no BTR.

### Competition with the airdrop

- Woods: the airdrop fix alone is not enough. Once the BTR departs, its leaked entry holds the step open for the rest of the raid, including while parked.
- Other maps: the airdrop fix (`docs/design.md` §4) is sufficient. Streets: the BTR is not a gate holder.

## 5. Q5: in-game verification steps

Use the plugin's gate log (`Debug/LogPhysicsGate`) and the F9 report. Run on **Woods** with the BTR (Customs has none here).

1. Baseline without any BTR release patch: start a Woods raid, stand away from the BTR route. Confirm `UpdateController._rigidbodies.Count` and `Simulation.UpdateEnabled` are quiet before the BTR spawns.
2. Wait for the first BTR departure (≈3–6 min). Log `_rigidbodies.Count` and the count of entries whose rigidbody is the BTR `parentBody` at each BTR stop and start.
3. Observe the entry count rising by one per On, and not falling at Off. Expect `UpdateEnabled` to stay true while the BTR is parked.
4. Enable fix (a) and repeat. Expect the BTR entry count to drop to 0 within a frame of each stop (after `ActivateSuspensionTime`), and `UpdateEnabled` to go false while the BTR is parked and nothing else holds the gate. It should come back true on the next departure.
5. Confirm no regressions while the step is on or off: the BTR still bobs and drives, passengers can enter/exit, the turret rotates and bots shoot it, and the BTR is still hit by bullets while parked.
6. Fika (optional): run host plus a client and check the entry count on both sides, since each runs its own `BTRView`. Test one machine without the patch to confirm the leak is per-client.
7. Also verify transient registrants still toggle the gate (grenade, bot kill) while the BTR is parked.
8. Record `ActivateSuspensionTime` and the parked/driving fraction from the logged timings, to quantify the gain.

## 6. Recommendation (implemented)

Fix **(a)** was implemented as a **separate config toggle**, `BTR/ReleaseParkedSuspension` (default on), distinct from `Airdrop/ReleaseLandedAirdrops`:
- Postfix `VehicleSuspension.SuspensionOff`; unsupport every entry for `__instance.parentBody` (loop with a safety cap).
- Skip (b) unless a double-On is ever observed in the gate log.
- Do **not** ship (c) without an explicit user decision; it changes visuals and is not needed to remove the leak.
- Ship the airdrop fix first. The BTR fix is only relevant on Woods (and `develop`), and for Fika it must be installed on clients as well as the host.

Open items:
- UNVERIFIED: prefab values (`ActivateSuspensionTime`, `parentBody` default kinematic state, joint setup).
- UNVERIFIED: `BTRView.method_19/method_20` behaviour at depot/route transitions.
- UNVERIFIED: the Fika client receive path from the `BTR` packet to `BtrController.SyncBTRVehicleFromServer` (only the EFT side, `VehicleMessageReceiver.OnSnapshotBTRVehicles`, was seen).
- Correction to `docs/research/gate-holders.md` §2 (applied there): BTR maps are Woods and the `develop` map, plus Streets without suspension; the Customs/Lighthouse claim was removed.

No checks were run for this report; no code was changed by it. Scratch probe scripts used for a failed prefab read were deleted.
