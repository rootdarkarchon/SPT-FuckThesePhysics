using System;
using System.Globalization;
using System.IO;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace FuckThesePhysics
{
    // Releases landed airdrop crates, the parked BTR suspension body and planted tripwires from EFT's physics registry
    // (PhysicsExtensions.UpdateController._rigidbodies), so they stop holding the script-driven physics step open: this
    // writes physics registry state (see AirdropRelease, BtrSuspensionRelease, TripwireRelease). Tripwires are then tested
    // against player capsules by a per-frame detector instead of by PhysX trigger callbacks. It also skips the per-call head
    // extrusion of players whose head collider is disabled (see DisabledExtrusionSkip).
    // Finally, it interpolates EFT's 50 Hz first-person effector layer (springs, sway, recoil, breath, bob, wall pullback) between
    // fixed ticks for the local player (see ViewmodelInterpolation); that is skipped on Fika headless, which has no viewmodel.
    // Diagnostics: physics gate log, F9 registry/timing report and an opt-in airdrop spawn key.
    [BepInDependency("com.fika.headless", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInPlugin(PluginInfo.Guid, PluginInfo.Name, PluginInfo.Version)]
    public sealed class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;
        ConfigEntry<bool> releaseLanded;
        ConfigEntry<bool> releaseBtr;
        ConfigEntry<bool> releaseTripwires;
        ConfigEntry<KeyboardShortcut> spawnKey;
        ConfigEntry<bool> logGate;
        ConfigEntry<bool> skipExtrusion;
        ConfigEntry<bool> interpolateViewmodel;
        ConfigEntry<bool> verifyExtrusion;
        ConfigEntry<bool> traceViewmodelPose;
        ConfigEntry<KeyboardShortcut> reportKey;
        string reportDirectory;

        // Display metadata only. ConfigurationManager discovers categories in bind order, then sorts settings by descending Order.
        // Advanced settings stay hidden until "Advanced settings" is enabled in ConfigurationManager.
        static ConfigDescription Describe(string category, string name, int order, string description, bool advanced = false) =>
            new ConfigDescription(description, null, new ConfigurationManagerAttributes
            {
                Category = category, DispName = name, Order = order, Description = description, IsAdvanced = advanced
            });

        void Awake()
        {
            Log = Logger;
            releaseLanded = Config.Bind("Airdrop", "ReleaseLandedAirdrops", true,
                Describe("1. Airdrop", "Release landed airdrops", 100,
                    "Once an airdrop crate has landed, unregister it from EFT's physics registry so it stops keeping the physics step running. Loot and collision are unaffected. Applies live; turning it off restores vanilla behaviour for crates that land afterwards."));
            releaseBtr = Config.Bind("BTR", "ReleaseParkedSuspension", true,
                Describe("2. BTR", "Release parked BTR suspension", 100,
                    "When the BTR stops and its suspension switches off, unregister the suspension body from EFT's physics registry so a parked BTR stops keeping the physics step running. The game registers it again when the BTR drives off. Applies from the next stop; turning it off restores vanilla behaviour. Affects every client that renders the BTR (Woods), including Fika clients."));
            releaseTripwires = Config.Bind("Tripwire", "ReleaseIdleTripwires", true,
                Describe("3. Tripwire", "Release idle tripwires", 100,
                    "Planted tripwires (the Partisan boss plants them) normally keep the physics step running for the rest of the raid. With this on they are unregistered from EFT's physics registry and the mod checks players' and bots' capsules against each armed wire every frame instead, calling the game's own walk-in handling when one crosses it (same filter, bot ignore chance and Fika behaviour). Shooting, disarming and timers are unaffected. Trade-off: things that are not players or bots and move without their own physics registration (for example a door swinging through the wire) no longer set a wire off unless the physics step happens to be running. Applies live; turning it off restores vanilla behaviour for armed wires."));
            spawnKey = Config.Bind("Debug", "SpawnAirdropKey", new KeyboardShortcut(),
                Describe("4. Debug", "Spawn airdrop key", 100,
                    "Debug key (unset by default): call in a real airdrop (plane, parachute drop, landing) at the drop point nearest to you while in raid. Only works where this client owns the airdrop manager (offline raids, Fika host).", true));
            logGate = Config.Bind("Debug", "LogPhysicsGate", false,
                Describe("4. Debug", "Log physics gate", 90,
                    "Log every change of the physics step gate (Simulation.UpdateEnabled) together with the number of registered rigidbodies to BepInEx/LogOutput.log. Logs on change only. Also logs, rate-limited, every tripwire walk-in the mod detects and every game relay call it skips.", true));
            reportKey = Config.Bind("Report", "Key", new KeyboardShortcut(),
                Describe("5. Report", "Report key", 100,
                    "Debug key (unset by default): write a physics registry and step timing report to BepInEx/LogOutput.log and the plugin's reports folder.", true));
            skipExtrusion = Config.Bind("Movement", "SkipDisabledHeadExtrusion", true,
                Describe("6. Movement", "Skip head extrusion for disabled colliders", 100,
                    "Skip the per-call head collision extrusion (ColliderExtrusion.Calculate) for extruders whose collider is disabled or inactive, and use a zero depenetration instead. The game disables the head collider of every player except the local one, which makes the query run with an empty bounds box (radius 0 at a point near the world origin); the result is expected to be zero unless the character stands near the origin. Applies live; turning it off restores vanilla behaviour from the next call."));
            verifyExtrusion = Config.Bind("Debug", "VerifyExtrusionSkip", false,
                Describe("4. Debug", "Verify extrusion skip", 80,
                    "Run vanilla for the calls that would be skipped and count how many return a non-zero depenetration in the report (Report / Key). Only acts while Movement / SkipDisabledHeadExtrusion is on. There is no performance gain while this is on.", true));
            traceViewmodelPose = Config.Bind("Debug", "TraceViewmodelPose", false,
                Describe("4. Debug", "Trace viewmodel pose", 70,
                    "Record one row per rendered frame (the last 512) for the local player: ticks run, simulated and presented hand rotation, weapon root, fireport and camera motion, then write them as a table with stall statistics in the Report / Key output (section \"Viewmodel pose trace\"). Works with or without Viewmodel / InterpolateFixedTick. Patches are only active while this is on; nothing is logged per frame.", true));
            interpolateViewmodel = Config.Bind("Viewmodel", "InterpolateFixedTick", true,
                Describe("7. Viewmodel", "Interpolate fixed-tick weapon motion", 100,
                    "Render the weapon, hands and camera sway, recoil, breathing, walking bob and wall pullback between EFT's 50 Hz ticks instead of in steps, which removes the viewmodel judder at frame rates above 50 fps. Adds up to one tick (about 20 ms) of latency to these effects, and shots follow the displayed weapon pose. Only affects the local player. Applies live; turning it off restores vanilla behaviour from the next frame."));
            reportDirectory = Path.Combine(Path.GetDirectoryName(Info.Location) ?? BepInEx.Paths.PluginPath, "reports");
            try { SimulateTimer.Install(PluginInfo.Guid); }
            catch (Exception error) { Log.LogWarning("Physics step timer unavailable: " + error.Message); }
            try { AirdropRelease.Install(PluginInfo.Guid, releaseLanded); }
            catch (Exception error) { Log.LogWarning("Airdrop release unavailable: " + error.Message); }
            try { BtrSuspensionRelease.Install(PluginInfo.Guid, releaseBtr); }
            catch (Exception error) { Log.LogWarning("BTR suspension release unavailable: " + error.Message); }
            try { TripwireRelease.Install(PluginInfo.Guid, releaseTripwires, logGate); }
            catch (Exception error) { Log.LogWarning("Tripwire release unavailable: " + error.Message); }
            try { DisabledExtrusionSkip.Install(PluginInfo.Guid + ".extrusionskip", skipExtrusion, verifyExtrusion); }
            catch (Exception error) { Log.LogWarning("Disabled-collider extrusion skip unavailable: " + error.Message); }
            // Soft dependency on Fika headless (declared above) makes sure its plugin is already registered here.
            if (BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("com.fika.headless"))
                Log.LogInfo("Viewmodel interpolation skipped: Fika headless has no first-person viewmodel.");
            else
            {
                const string interpolationId = PluginInfo.Guid + ".viewmodelinterp";
                try { ViewmodelInterpolation.Install(interpolationId, interpolateViewmodel); }
                catch (Exception error) { Log.LogWarning("Viewmodel interpolation unavailable: " + error.Message); }
                try { ViewmodelPoseTrace.Install(PluginInfo.Guid + ".viewmodelpose", interpolationId, traceViewmodelPose); }
                catch (Exception error) { Log.LogWarning("Viewmodel pose trace unavailable: " + error.Message); }
            }
            Log.LogInfo(PluginInfo.Name + " " + PluginInfo.Version + " ready.");
        }

        void Update()
        {
            if (reportKey.Value.IsDown()) WriteReport();
            if (spawnKey.Value.IsDown()) AirdropSpawner.Spawn();
            if (logGate.Value) GateLog.Tick(); else GateLog.Reset();
        }

        void OnDestroy()
        {
            ViewmodelPoseTrace.Uninstall();
            ViewmodelInterpolation.Uninstall();
            DisabledExtrusionSkip.Uninstall();
            TripwireRelease.Uninstall();
            BtrSuspensionRelease.Uninstall();
            AirdropRelease.Uninstall();
            SimulateTimer.Uninstall();
        }

        void WriteReport()
        {
            var report = new StringBuilder();
            report.Append("# FuckThesePhysics report ").Append(DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture))
                .Append(" frame=").Append(Time.frameCount).AppendLine();
            var camera = Camera.main;
            if (camera != null) report.Append("camera=").Append(camera.transform.position.ToString("F1")).AppendLine();
            Section(report, "Physics step timing", SimulateTimer.Write);
            Section(report, "What keeps the physics step running", SimulationOwners.Write);
            Section(report, "Disabled-collider extrusion skip", DisabledExtrusionSkip.Write);
            Section(report, "Viewmodel interpolation", ViewmodelInterpolation.Write);
            Section(report, "Viewmodel pose trace", ViewmodelPoseTrace.Write);
            string text = report.ToString();
            Log.LogInfo(text);
            try
            {
                Directory.CreateDirectory(reportDirectory);
                string file = Path.Combine(reportDirectory, "physics-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".txt");
                File.WriteAllText(file, text);
                Log.LogInfo("Physics report saved: " + file);
            }
            catch (Exception error) { Log.LogWarning("Physics report file write failed: " + error.Message); }
        }

        static void Section(StringBuilder report, string title, Action<StringBuilder> writer)
        {
            report.AppendLine().Append("## ").AppendLine(title);
            try { writer(report); }
            catch (Exception error) { report.Append("section failed: ").AppendLine(error.ToString()); }
        }
    }
}
