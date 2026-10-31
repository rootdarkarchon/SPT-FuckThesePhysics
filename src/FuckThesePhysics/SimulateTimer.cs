using System;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace FuckThesePhysics
{
    // Times EFT's script-driven physics step (PhysicsExtensions.Simulation.Update), the Physics.Simulate calls it makes,
    // and records every PhysicsExtensions.UpdateController.SimulationEnabled(bool) transition.
    // Observation only: prefixes/postfixes return void and swallow their own failures.
    internal static class SimulateTimer
    {
        const int RingSize = 4096;
        const int TransitionCapacity = 32;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

        struct Transition
        {
            public float RealTime;
            public int Frame;
            public bool Value;
        }

        static Harmony harmony;

        // Accumulators since the last Write (main thread only).
        static int windowStartFrame;
        static float windowStartTime;
        static int updateCalls, updateDepth, updateFrames, lastUpdateFrame = -1;
        static double updateTotalMs, updateMaxMs;
        static int simulateCalls, simulateInsideUpdate, simulateFrames, lastSimulateFrame = -1, maxSimulatesInFrame, simulatesThisFrame;
        static double simulateTotalMs, simulateMaxMs;
        static readonly float[] simulateRing = new float[RingSize];
        static int simulateRingCount;
        static readonly Transition[] transitions = new Transition[TransitionCapacity];
        static int transitionCount, transitionsTotal;
        static bool hasLastEnabled, lastEnabled;
        static int enabledCount, disabledCount;

        internal static void Install(string harmonyId)
        {
            if (harmony != null) return;
            var instance = new Harmony(harmonyId);
            harmony = instance;
            ResetWindow();

            Type physicsExtensions = AccessTools.TypeByName("PhysicsExtensions");
            const BindingFlags nested = BindingFlags.Public | BindingFlags.NonPublic;
            Type simulation = physicsExtensions?.GetNestedType("Simulation", nested);
            Type controller = physicsExtensions?.GetNestedType("UpdateController", nested);

            Patch(instance, "PhysicsExtensions.Simulation.Update",
                simulation == null ? null : AccessTools.Method(simulation, "Update", Type.EmptyTypes),
                nameof(UpdatePrefix), nameof(UpdatePostfix));
            Patch(instance, "UnityEngine.Physics.Simulate(float)",
                AccessTools.Method(typeof(Physics), "Simulate", new[] { typeof(float) }),
                nameof(SimulatePrefix), nameof(SimulatePostfix));
            Patch(instance, "PhysicsExtensions.UpdateController.SimulationEnabled(bool)",
                controller == null ? null : AccessTools.Method(controller, "SimulationEnabled", new[] { typeof(bool) }),
                nameof(EnabledPrefix), null);
        }

        internal static void Uninstall()
        {
            var instance = harmony;
            harmony = null;
            if (instance == null) return;
            try { instance.UnpatchSelf(); }
            catch (Exception error) { Plugin.Log?.LogWarning("Physics step timer unpatch failed: " + error.Message); }
        }

        static void Patch(Harmony instance, string label, MethodBase target, string prefix, string postfix)
        {
            if (target == null)
            {
                Plugin.Log?.LogWarning("Physics step timer: " + label + " not found; skipping.");
                return;
            }
            try
            {
                instance.Patch(target,
                    prefix: prefix == null ? null : new HarmonyMethod(typeof(SimulateTimer), prefix),
                    postfix: postfix == null ? null : new HarmonyMethod(typeof(SimulateTimer), postfix));
            }
            catch (Exception error)
            {
                Plugin.Log?.LogWarning("Physics step timer: patching " + label + " failed: " + error.Message);
            }
        }

        static void ResetWindow()
        {
            windowStartFrame = Time.frameCount;
            windowStartTime = Time.realtimeSinceStartup;
            updateCalls = 0; updateFrames = 0; lastUpdateFrame = -1; updateTotalMs = 0; updateMaxMs = 0;
            simulateCalls = 0; simulateInsideUpdate = 0; simulateFrames = 0; lastSimulateFrame = -1;
            maxSimulatesInFrame = 0; simulatesThisFrame = 0; simulateTotalMs = 0; simulateMaxMs = 0;
            simulateRingCount = 0;
            transitionCount = 0; transitionsTotal = 0; enabledCount = 0; disabledCount = 0;
        }

        static void UpdatePrefix(out long __state)
        {
            __state = Stopwatch.GetTimestamp();
            updateDepth++;
        }

        static void UpdatePostfix(long __state)
        {
            try
            {
                double ms = (Stopwatch.GetTimestamp() - __state) * TicksToMs;
                if (updateDepth > 0) updateDepth--;
                updateCalls++;
                updateTotalMs += ms;
                if (ms > updateMaxMs) updateMaxMs = ms;
                int frame = Time.frameCount;
                if (frame != lastUpdateFrame) { lastUpdateFrame = frame; updateFrames++; }
            }
            catch { }
        }

        static void SimulatePrefix(out long __state)
        {
            __state = Stopwatch.GetTimestamp();
        }

        static void SimulatePostfix(long __state)
        {
            try
            {
                double ms = (Stopwatch.GetTimestamp() - __state) * TicksToMs;
                simulateCalls++;
                if (updateDepth > 0) simulateInsideUpdate++;
                simulateTotalMs += ms;
                if (ms > simulateMaxMs) simulateMaxMs = ms;
                simulateRing[simulateRingCount % RingSize] = (float)ms;
                simulateRingCount++;
                int frame = Time.frameCount;
                if (frame != lastSimulateFrame)
                {
                    lastSimulateFrame = frame;
                    simulateFrames++;
                    simulatesThisFrame = 0;
                }
                simulatesThisFrame++;
                if (simulatesThisFrame > maxSimulatesInFrame) maxSimulatesInFrame = simulatesThisFrame;
            }
            catch { }
        }

        static void EnabledPrefix(bool __0)
        {
            try
            {
                transitions[transitionsTotal % TransitionCapacity] = new Transition
                {
                    RealTime = Time.realtimeSinceStartup,
                    Frame = Time.frameCount,
                    Value = __0,
                };
                transitionsTotal++;
                if (transitionCount < TransitionCapacity) transitionCount++;
                if (__0) enabledCount++; else disabledCount++;
                hasLastEnabled = true;
                lastEnabled = __0;
            }
            catch { }
        }

        internal static void Write(StringBuilder sb)
        {
            if (harmony == null)
            {
                sb.AppendLine("Physics step timer is not installed.");
                return;
            }

            int frames = Math.Max(1, Time.frameCount - windowStartFrame);
            float seconds = Time.realtimeSinceStartup - windowStartTime;
            sb.Append("window: ").Append(Time.frameCount - windowStartFrame).Append(" frames, ")
                .Append(seconds.ToString("F1", Inv)).Append(" s");
            if (seconds > 0f) sb.Append(" (").Append(((Time.frameCount - windowStartFrame) / seconds).ToString("F1", Inv)).Append(" fps)");
            sb.AppendLine();
            sb.Append("PhysicsExtensions.Simulation.Update: ").Append(updateCalls).Append(" calls in ").Append(updateFrames)
                .Append(" frames; total ").Append(F(updateTotalMs)).Append(" ms, mean ")
                .Append(F(updateCalls > 0 ? updateTotalMs / updateCalls : 0)).Append(" ms/call, ")
                .Append(F(updateTotalMs / frames)).Append(" ms/frame, max ").Append(F(updateMaxMs)).AppendLine(" ms");
            sb.Append("Physics.Simulate(float): ").Append(simulateCalls).Append(" calls (")
                .Append(simulateInsideUpdate).Append(" inside Simulation.Update) in ").Append(simulateFrames)
                .Append(" frames; total ").Append(F(simulateTotalMs)).Append(" ms, mean ")
                .Append(F(simulateCalls > 0 ? simulateTotalMs / simulateCalls : 0)).Append(" ms/call, ")
                .Append(F(simulateTotalMs / frames)).Append(" ms/frame, max ").Append(F(simulateMaxMs))
                .Append(" ms, max calls in one frame ").Append(maxSimulatesInFrame).AppendLine();

            int sampleCount = Math.Min(simulateRingCount, RingSize);
            if (sampleCount > 0)
            {
                var sorted = new float[sampleCount];
                Array.Copy(simulateRing, sorted, sampleCount);
                Array.Sort(sorted);
                sb.Append("Physics.Simulate ms distribution (last ").Append(sampleCount).Append(" calls): p50 ")
                    .Append(F(Percentile(sorted, 0.50))).Append(", p95 ").Append(F(Percentile(sorted, 0.95)))
                    .Append(", max ").Append(F(sorted[sampleCount - 1])).AppendLine();
            }
            else sb.AppendLine("Physics.Simulate ms distribution: no calls observed.");

            sb.Append("frames where simulation ran (Physics.Simulate called): ").Append(simulateFrames).Append('/')
                .Append(frames).Append(" = ").Append((100.0 * simulateFrames / frames).ToString("F1", Inv)).AppendLine("%");

            sb.AppendLine();
            sb.Append("UpdateController.SimulationEnabled transitions: ").Append(transitionsTotal).Append(" calls (")
                .Append(enabledCount).Append(" true, ").Append(disabledCount).Append(" false)");
            if (transitionsTotal > transitionCount) sb.Append("; showing last ").Append(transitionCount);
            sb.AppendLine();
            if (hasLastEnabled) sb.Append("last known value: ").Append(lastEnabled ? "true" : "false").AppendLine();
            else sb.AppendLine("last known value: never called since install.");
            float now = Time.realtimeSinceStartup;
            for (int i = 0; i < transitionCount; i++)
            {
                int index = (transitionsTotal - transitionCount + i) % TransitionCapacity;
                var entry = transitions[index];
                sb.Append("  frame ").Append(entry.Frame).Append(" t=").Append(entry.RealTime.ToString("F2", Inv))
                    .Append("s (").Append((now - entry.RealTime).ToString("F1", Inv)).Append(" s ago) SimulationEnabled(")
                    .Append(entry.Value ? "true" : "false").AppendLine(")");
            }

            ResetWindow();
        }

        static double Percentile(float[] sorted, double p)
        {
            int index = (int)Math.Ceiling(p * sorted.Length) - 1;
            if (index < 0) index = 0;
            if (index >= sorted.Length) index = sorted.Length - 1;
            return sorted[index];
        }

        static string F(double value) => value.ToString("F3", Inv);
    }
}
