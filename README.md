# Fuck These Physics

A BepInEx client mod for SPT 4.1 that stops landed airdrop crates and the parked
BTR from keeping EFT's physics running for the rest of the raid.

EFT runs `Physics.Simulate` every frame for as long as anything is registered
with its physics step. A landed airdrop crate and the parked BTR both stay
registered for the whole raid, even though neither needs simulating any more.
The mod unregisters them once they no longer need physics, so the physics step
can switch off when nothing else needs it, as the game intends. The mod has no
server component.

## Requirements and compatibility

- SPT 4.1 (Unity 2022.3.43f1)
- BepInEx 5
- Fika 2.4.x is optional (see [Fika](#fika)). Built against 2.4.4; newer 2.4
  releases are expected to work the same way. The mod does not check the Fika
  version.

Before patching, the mod looks up every game member it needs. If any is
missing, for example after a game update, it logs the reason and patches
nothing. The game then behaves as vanilla.

## Installation

1. Close the game.
2. Extract `FuckThesePhysics-1.2.0-SPT4.1.zip` into your SPT game folder. The
   plugin ends up at `BepInEx/plugins/FuckThesePhysics/FuckThesePhysics.dll`.
3. Start the game.

To uninstall, close the game and delete the `BepInEx/plugins/FuckThesePhysics`
folder. You can also delete
`BepInEx/config/com.rootdarkarchon.fuckthesephysics.cfg`.

## What it changes

- **Airdrops.** On the frame a crate reaches its landed stage, the mod removes
  the crate's rigidbody from EFT's physics registry. The crate's final position
  is applied to the physics engine once before that.
- **BTR.** When the BTR stops and the game switches its suspension off, the mod
  removes the suspension body from the registry. The game registers it again
  when the BTR drives off, so physics still runs while the BTR is moving.
- **Movement collision.** The game disables the head collider of every bot. Its head-collision query then runs against empty bounds near the world origin, which is wasted work. The mod skips that query for disabled player colliders and returns zero instead, which is what the game's query yields for them. Set `Movement / SkipDisabledHeadExtrusion` off to run the game's query again. See [Fika](#fika) for who benefits.
- **Viewmodel.** The first-person weapon, hands and camera follow EFT's sway, recoil, breathing and bob between the game's 50 Hz ticks, instead of jumping at each tick. This includes the mounted (bipod, wall) and stationary-weapon views. The simulation itself is unchanged. The mod only changes how the local view is drawn between ticks. Set `Viewmodel / InterpolateFixedTick` off to return to the game's behaviour.

Looting, collision and bullet hits on crates keep working. The crate stays solid
and hittable, and you can open it as usual. The mod does not move, hide or
delete any object, and does not change how the BTR drives.

Everything else is left alone. Grenades, doors, dropped loot and other physics
users still register as normal. They turn the physics step on while they need
it, and it turns off again when none are left.

## Fika

- **Airdrops:** only the host (or headless client) runs the crate logic, so the
  release is applied on the host only. Clients do not own the crate logic and the
  setting has no effect there.
- **BTR:** every machine runs its own BTR suspension, so the release runs on every
  machine, including clients. Install the mod on the host and on every client.
  Installing it on a client is harmless for airdrops.
- **Movement collision:** bots run on the host (or on a headless host), so the skip matters there, and offline play gets the same. A Fika client runs the query only for its own player, whose collider is enabled, so the skip does nothing on clients.
- **Viewmodel interpolation:** client-local. Each machine that renders the first-person view (offline, the Fika host, and each client that installs the mod) interpolates its own weapon and camera. It is not installed on Fika headless, which renders nothing. Other players are not affected.

## Settings

Settings are available in BepInEx Configuration Manager and in
`BepInEx/config/com.rootdarkarchon.fuckthesephysics.cfg`.

| Setting | Default | Effect |
|---|---|---|
| `Airdrop / ReleaseLandedAirdrops` | on | Unregisters a crate from the physics registry when it lands. Applies live; when off, crates that land afterwards behave as vanilla. Host and offline only. |
| `BTR / ReleaseParkedSuspension` | on | Unregisters the BTR suspension body each time the BTR stops. Applies from the next BTR stop; when off, the BTR behaves as vanilla. Runs on every machine, including Fika clients. |
| `Movement / SkipDisabledHeadExtrusion` | on | Skips the head-collision query for disabled player colliders (the bots'), whose result is zero anyway. Applies live; when off, the game's query runs. See [Fika](#fika). |
| `Viewmodel / InterpolateFixedTick` | on | Shows the first-person weapon, hands and camera sway between the game's 50 Hz ticks instead of in steps. Adds up to about 20 ms of latency to those effects. Applies live; when off, the game's behaviour returns from the next frame. Local player only. |

### Advanced settings

These settings are for troubleshooting and testing. They are hidden unless you
enable **Advanced settings** in Configuration Manager. You can also edit them in
the `.cfg` file.

| Setting | Default | Effect |
|---|---|---|
| `Debug / SpawnAirdropKey` | unset | Calls in a real airdrop (plane, parachute, landing) at the drop point nearest to you. |
| `Debug / LogPhysicsGate` | off | Logs each time the physics step turns on or off, together with the number of registered rigidbodies. Only changes are logged. |
| `Report / Key` | unset | Writes a report of the physics registry and step timing to the BepInEx log and to `BepInEx/plugins/FuckThesePhysics/reports/`. |
| `Debug / VerifyExtrusionSkip` | off | Runs the game's query for the calls that would be skipped and counts non-zero results in the report. Only acts while `Movement / SkipDisabledHeadExtrusion` is on. No performance gain while on. |

**Spawn key.** It only works where the game owns the airdrop logic: offline
raids and the Fika host. It does nothing on a Fika client and logs "not host".
It also logs a reason and does nothing if you are not in a raid, the airdrop
manager is not ready, the map has no drop points or there is no player. The key
does not change the airdrop timers or counters.

Landed crates and BTR stops are written to `BepInEx/LogOutput.log`. The log
shows when a crate is initialised, lands, is released and becomes static, and
each BTR suspension release.

## Known limitations and notes

- The BTR effect only applies on Woods. Streets never enables the BTR
  suspension and other maps have no BTR.
- Other short-lived physics users, such as raid start and bot spawns, still turn
  the physics step on briefly. This is intended.
- Corpse ragdolls are left alone: the game already releases them after a few
  seconds. With Rupture installed, Rupture simulates them outside EFT's physics.
- Verified in game so far: Woods, Fika host, two spawned airdrops and three BTR
  drive and stop cycles. Crates were lootable, solid and hittable, and no
  entries accumulated. The following have not been tested: turning the setting
  off to reproduce the leak, grenades, ragdolls and Fika clients.
- The disabled-collider skip was verified in game (works, improves performance). Fika clients were not tested; nothing is skipped there because a client's own player has an enabled collider. If the skip path throws an error, it switches itself off until the game restarts.
- Viewmodel interpolation (`Viewmodel / InterpolateFixedTick`, version 1.2.0) was verified in game: it removes the 50 Hz stop-and-go of weapon sway. Other conditions (low frame rates, firing, weapon swaps, mounting, Fika clients) are not tested yet. Shots follow the weapon pose on screen, which can be up to about 20 ms behind the game's internal pose. A fine left/right swim of near parts of the weapon (eyepiece rim, rear of the weapon) far from the map origin remains, with or without this setting. It comes from float precision in world coordinates, which this mod does not change.

## Building from source

1. Copy `Directory.Build.local.props.example` to `Directory.Build.local.props`
   and set `SptInstallPath`, or set the `SPT_INSTALL_PATH` environment
   variable. It must point to an SPT 4.1 install.
2. Build:

   ```powershell
   dotnet build FuckThesePhysics.slnx -c Release
   ```

3. Package:

   ```powershell
   pwsh scripts/Build-Package.ps1
   ```

   The zip is written to `bin/Release/FuckThesePhysics-<version>-SPT4.1.zip`.

## Technical details

How the physics step is gated and how the mod releases entries is described in
[docs/design.md](docs/design.md). Research notes are in
[docs/research/](docs/research/).

## License

MIT. See [LICENSE](LICENSE).
