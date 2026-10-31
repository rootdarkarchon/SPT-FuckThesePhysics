# Mod template and repo survey for SPT-FuckThisAirdrop

Scope: read-only survey of four repos under `A:\dev\source\`. No edits, builds, or tests.
Tags: **CONFIRMED** = read in a file or directory listing; **[INFERENCE]** = derived; **UNVERIFIED** = not checked.

## 1. SPT-FuckThesePhysics: what it does

Not an airdrop mod. It is a diagnostic-only plugin that writes reports.

- `src/FuckThesePhysics/Plugin.cs`: `[BepInPlugin(Guid,"FuckThesePhysics","0.1.0")]`, `Guid = "com.rootdarkarchon.fuckthesephysics"`. CONFIRMED.
  - `Awake` binds `Report/Key` = `KeyboardShortcut(KeyCode.F9)` (line 26) and `Report/TriggerSearchRadiusMetres` = `0f` (line 27). CONFIRMED.
  - `Awake` calls `SimulateTimer.Install(Guid)` (line 29). `OnDestroy` calls `SimulateTimer.Uninstall()` (line 40). CONFIRMED.
  - `Update` calls `StaticMovers.Sample()` (line 36) and then `if (reportKey.Value.IsDown()) WriteReport();` (line 37). CONFIRMED.
  - `WriteReport` writes `<plugin dir>/reports/physics-<yyyyMMdd-HHmmss>.txt` and logs to BepInEx. It uses four writers: `SimulateTimer.Write`, `SimulationOwners.Write`, `TriggerSuspects.Write`, `StaticMovers.Write`. CONFIRMED.
- `SimulateTimer.cs` (Harmony): patches `PhysicsExtensions.Simulation.Update()` (prefix/postfix), `UnityEngine.Physics.Simulate(float)` (prefix/postfix), and `PhysicsExtensions.UpdateController.SimulationEnabled(bool)` (prefix). Types are resolved by `AccessTools.TypeByName("PhysicsExtensions")` plus `GetNestedType`. All patches are observation-only. CONFIRMED.
- `SimulationOwners.cs`: read-only. Reads `Physics.simulationMode`, `Physics.autoSyncTransforms`, `PhysicsExtensions.Simulation.{UpdateMode,UpdateEnabled,CanRunSimulate,Quality}`, `PhysicsExtensions.UpdateController.Enabled` and `_rigidbodies`. Counts rigidbodies with the same rule as `UpdateController.CheckRigidbodiesDone`. CONFIRMED.
- `TriggerSuspects.cs`: read-only estimator. Uses `Physics.Overlap{Sphere,Box,Capsule}NonAlloc` with `QueryTriggerInteraction.Collide`, `Physics.GetIgnoreLayerCollision`, `Rigidbody.IsSleeping()`, `ArticulationBody.IsSleeping()`. CONFIRMED.
- `StaticMovers.cs`: read-only. Scans `FindObjectsByType<Collider>` on an interval and compares cached `localToWorldMatrix`. Does not touch `Transform.hasChanged`. CONFIRMED.
- Writes no physics state. CONFIRMED by source reads above.

**Airdrop overlap**: `grep -i "airdrop|crate"` over `src` returned **no matches**. CONFIRMED. The physics-adjacent surface it observes is `PhysicsExtensions` (the same API a physics-release mod would patch), so it is useful as a diagnostic alongside the eventual fix, not as a dependency. [INFERENCE]

**Reusable helpers**: the `KeyboardShortcut` + `IsDown()` hotkey pattern (Plugin.cs:26, 37), the report-file writer under `Path.GetDirectoryName(Info.Location)`, and the Harmony install/uninstall with `UnpatchSelf` (SimulateTimer). CONFIRMED.

**Installed?** **No.** `D:\Tarkov-SPT-4.1\BepInEx\plugins\` has no `FuckThesePhysics` entry. CONFIRMED by directory listing. Present related entries: `FuckThisCulling/`, `FuckThisLoadingScreen/`, `FuckTheseFX/`, `FuckTheseRays/`, `SAIN/`, `spt/`, `Fika/`, and others.

## 2. Smallest clean standalone template

### Layout to copy

Recommended mix: Culling's `Directory.Build.*` system (single version source, generated `PluginInfo`, gitignored local SPT path), with Physics's single-csproj shape and explicit `Compile` list. FX and LoadingScreen have no `Directory.Build.*` (CONFIRMED: LoadingScreen root listing has none; FX root lists none), so they are the simpler alternative: version and GUID hard-coded in the csproj.

### csproj pattern (from Physics `FuckThesePhysics.csproj` and Culling `FuckThisCulling.csproj`)

- `TargetFramework netstandard2.1`, `LangVersion latest`, `Nullable disable` (FX), `Deterministic true` (FX). CONFIRMED.
- `EnableDefaultCompileItems=false` with explicit `<Compile Include="...">` (Physics). CONFIRMED.
- `OutputPath ../../obj/<Name>/plugin/` (Physics, LoadingScreen). CONFIRMED.
- References with `Private=false` via `$(SptInstallPath)\EscapeFromTarkov_Data\Managed\...` and `$(SptInstallPath)\BepInEx\core\...` (FX lines 22–34; Physics). CONFIRMED.
- Minimal set for a Harmony plugin with a hotkey: `BepInEx`, `0Harmony`, `Assembly-CSharp`, `Comfort`, `UnityEngine`, `UnityEngine.CoreModule`, `UnityEngine.InputLegacyModule` (Physics uses this set plus `PhysicsModule`). CONFIRMED.
- `ValidateInstallPath` target: `Error` if `Assembly-CSharp.dll` is missing (Physics and LoadingScreen, FX `ValidateReferences`). CONFIRMED.

### SptInstallPath resolution (Culling `Directory.Build.props`/`.targets`)

- `Directory.Build.props` imports `Directory.Build.local.props` if present, then defaults `SptInstallPath` from `$(SPT_INSTALL_PATH)`. CONFIRMED.
- `Directory.Build.local.props.example` holds `<SptInstallPath Condition="'$(SptInstallPath)' == ''">D:\Tarkov-SPT-4.1</SptInstallPath>`. CONFIRMED.
- `Directory.Build.targets` `RequireSptInstallPath` errors if the path is empty or lacks `BepInEx/core/BepInEx.dll`. Gated by `FtcStandaloneTest != true`. CONFIRMED.
- Physics hard-codes `D:/Tarkov-SPT-4.1` as a csproj default. Do **not** copy this. [INFERENCE: prefer the Culling approach]

### Generated PluginInfo (Culling `Directory.Build.targets` lines 7–18, CONFIRMED)

`GeneratePluginInfo` (BeforeTargets `CoreCompile`) writes `$(IntermediateOutputPath)PluginInfo.g.cs` with `WriteOnlyWhenDifferent="true"`, containing `internal static class PluginInfo { Guid, Name, Version, LegacyGuid }`, and adds it to `Compile`. The Plugin then uses `[BepInPlugin(PluginInfo.Guid,PluginInfo.Name,PluginInfo.Version)]` (Culling `Plugin.cs:13–14`). CONFIRMED. For the new mod, drop `LegacyGuid` and `BepInIncompatibility`, because there is no legacy plugin. [INFERENCE: no legacy GUID exists for a new mod]

### Build/package script (Culling `scripts/Build.ps1` and LoadingScreen `scripts/Build-Package.ps1`)

- LoadingScreen `Build-Package.ps1` (CONFIRMED, full file read): resolves `SPT_INSTALL_PATH`, runs `dotnet build <csproj> -c Release "-p:SptInstallPath=..."`, reads FileVersion for the zip name, writes a zip with `BepInEx/plugins/<Name>/<Name>.dll` and `LICENSE`, verifies entry count and SHA-256 against the built dll. **This is the smallest reference.** Use it.
- Culling `Build.ps1` (CONFIRMED lines 62–114): Mono.Cecil metadata verification, `build-receipt.json`, `-Install` with a "game running" check and backup. Heavier than needed. Reuse only the install-backup idea if wanted. Do not copy the native `GpuTiming`, asset-bundle, or Cecil steps.

### .gitignore

Physics `.gitignore` (CONFIRMED, 295 B): `**/bin/`, `**/obj/`, `/artifacts/`, `/TestResults/`, `/BepInEx/`, `**/Library/`, `**/Temp/`, `**/Logs/`, `**/UserSettings/`, `.vs/`, `.vscode/`, `.idea/`, `.claude/`, `.codex/`, `.agents/`, `*.user`, `*.suo`, `*.log`, `*.tmp`, `*.bak`, `.DS_Store`, `Thumbs.db`.
Add from Culling (CONFIRMED): `Directory.Build.local.props`, `*.zip`. [INFERENCE: the local props file must be ignored because it holds the local SPT path.]

### Copy list (source → `A:\dev\source\SPT-FuckThisAirdrop\`)

| Source | Destination | Action |
|---|---|---|
| `SPT-FuckThisCulling/Directory.Build.props` | `Directory.Build.props` | copy, then edit (see rename list) |
| `SPT-FuckThisCulling/Directory.Build.targets` | `Directory.Build.targets` | copy, then edit |
| `SPT-FuckThisCulling/Directory.Build.local.props.example` | `Directory.Build.local.props.example` | copy as is |
| `SPT-FuckThesePhysics/.gitignore` | `.gitignore` | copy, add `Directory.Build.local.props`, `*.zip` |
| `SPT-FuckThesePhysics/FuckThesePhysics.slnx` | `FuckThisAirdrop.slnx` | copy, rename project entry |
| `SPT-FuckThesePhysics/src/FuckThesePhysics/FuckThesePhysics.csproj` | `src/FuckThisAirdrop/FuckThisAirdrop.csproj` | copy shape only; drop hard-coded D: default and Physics-specific references |
| `SPT-FuckThisLoadingScreen/scripts/Build-Package.ps1` | `scripts/Build-Package.ps1` | copy, rename paths/name |
| `SPT-FuckThisLoadingScreen/LICENSE` | `LICENSE` | copy only if the owner wants the same license |
| `SPT-FuckThisCulling/src/FuckThisCulling/Plugin.cs:1–14` | pattern only | do not copy file; reuse `[BepInPlugin(PluginInfo.*)]` shape |

Do **not** copy: `obj/`, `bin/`, `Release/`, `reports/`, `artifacts/`, `*.g.cs`, the Culling `Build.ps1` native/asset/Cecil parts, or any `FuckThesePhysics` source.

### Rename list

- `FtcPluginGuid` → `com.rootdarkarchon.fuckthisairdrop` (new GUID, [INFERENCE] keeps the owner's naming convention).
- `FtcPluginName` → `FuckThisAirdrop`.
- `FtcSptLabel` → `SPT4.1`.
- `Version` → start at `0.1.0`.
- Remove `FtcLegacyPluginGuid` and `LegacyGuid` from Props/Targets, and `[BepInIncompatibility]` from the plugin.
- `RootNamespace`/`AssemblyName` → `FuckThisAirdrop`. Namespace `FuckThisAirdrop`.
- Output path `../../obj/FuckThisAirdrop/plugin/`.
- Zip name pattern `FuckThisAirdrop-<ver>-SPT4.1.zip`; entry `BepInEx/plugins/FuckThisAirdrop/`.

## 3. ConfigurationManager usage patterns

### A. Keybind (BepInEx `KeyboardShortcut`), works with the ConfigurationManager keybind editor
- Physics `Plugin.cs:26`: `reportKey = Config.Bind("Report","Key",new KeyboardShortcut(KeyCode.F9),"...")`. CONFIRMED.
- Trigger: `reportKey.Value.IsDown()` in `Update` (Physics `Plugin.cs:37`). CONFIRMED.
- Needs no compile-time CM reference. BepInEx renders `KeyboardShortcut` natively in ConfigurationManager. [INFERENCE: standard BepInEx behavior; not verified in the installed CM build]

### B. Section/order metadata for CM layout (Culling)
- `src/FuckThisCulling/Settings.cs` (CONFIRMED lines ~18–33): `SettingDescription(category,name,order,description,range,advanced,drawer)` builds a `ConfigDescription` with a private `ConfigurationManagerAttributes` (`Category`, `DispName`, `Order`, `Description`, `IsAdvanced`, `CustomDrawer`, `HideDefaultButton`, `HideSettingName`). Comment: CM discovers categories in bind order, then sorts by descending `Order`.
- FX `ConfigurationManagerAttributes.cs` (CONFIRMED, full file): `internal sealed class ConfigurationManagerAttributes { bool? IsAdvanced, ShowRangeAsPercent, HideDefaultButton, HideSettingName; Action<ConfigEntryBase> CustomDrawer; }`. Comment: CM discovers metadata by public field names. Use this shape: no compile-time dependency on the CM assembly.
- FX `SettingsCatalog.cs`: `config.Bind(section,key,value,new ConfigDescription(..., new ConfigurationManagerAttributes{...}))`; comment: CM 18.4 preserves first-seen section order. Has a `0. Presets` section with `CustomDrawer = DrawPresets`. CONFIRMED.

### C. Runtime-side CM control (Culling `Controls.cs`, reflection, no compile dependency)
- Locates the CM `ConfigurationManager.ConfigurationManager` instance and reads `DisplayingWindow` (property) and `OverrideHotkey` (field) via `GetProperty`/`GetField`, logging state (Controls.cs:121–123). CONFIRMED. Avoid unless a custom in-raid toggle is needed. [INFERENCE]

**Recommendation for the debug spawn button**: use pattern A (`KeyboardShortcut` bound in `Config.Bind`, polled with `IsDown()`), and pattern B's attribute shim for CM grouping. Do not add reflection hooks (pattern C).

## 4. Open items (UNVERIFIED)

- The CM version installed (`D:\Tarkov-SPT-4.1\BepInEx\plugins\spt\ConfigurationManager\`) was not opened; keybind rendering is assumed from BepInEx behavior.
- `Plugin.cs` in Culling beyond line 63 was not read.
- The Physics `.gitattributes` and `.slnx` contents were not opened (only the size of `.slnx`, 89 B, was seen).
- Whether an airdrop release mod must avoid `PhysicsExtensions.Simulation.Update` is UNVERIFIED; this report does not answer it.

## Summary

- Physics repo is diagnostic-only and does not touch airdrops; no `airdrop`/`crate` matches. CONFIRMED.
- `FuckThesePhysics` is not installed in `D:\Tarkov-SPT-4.1\BepInEx\plugins`. CONFIRMED.
- Template: Culling `Directory.Build.props`/`.targets`/`.local.props.example` plus Physics csproj shape plus LoadingScreen `Build-Package.ps1` plus Physics `.gitignore`.
- Remove the legacy GUID and incompatibility lines, the native/asset/Cecil steps, and the D: default.
- Keybind: `KeyboardShortcut` + `IsDown()` (Physics Plugin.cs:26, 37). CM grouping: FX-style attribute shim (no compile dependency).
