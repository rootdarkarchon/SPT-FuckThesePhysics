using System;
using System.Globalization;
using System.Reflection;
using System.Text;
using BepInEx.Configuration;
using EFT.Animations;
using EFT.CameraControl;
using HarmonyLib;
using UnityEngine;

namespace FuckThesePhysics
{
    // Debug / TraceViewmodelPose: records, for every rendered frame of the local player, what the viewmodel layer simulated and what
    // was finally presented, so judder can be located (docs/design.md §13, "Diagnostics"). It is independent of
    // ViewmodelInterpolation's setting and failure latch: with interpolation off the same columns are recorded.
    //
    // Per frame, in execution order:
    //   1. ProcessEffectors prefix (FirstPersonStrategy / MountingStrategy / StationaryStrategy, MainPlayer's PWA only): ticks = nFixedFrames.
    //   2. ProcessEffectors postfix "simulated", ordered before ViewmodelInterpolation's postfix: HandsRotation Spring.Current after the
    //      tick loop, before any blend.
    //   3. ProcessEffectors postfix "presented", ordered after ViewmodelInterpolation's postfix: the same value after the blend (equal to
    //      the simulated one when nothing was blended).
    //   4. PWA.LateTransformations postfix, ordered after the interpolation restore: WeaponRootAnim local pose (restore only writes
    //      effector fields, so the transform keeps the presented pose; LateTransformations is the last PWA write of the frame).
    //   5. Camera.onPreCull for CameraManager.Instance.Camera (the FPS camera): the latest point before rendering. Camera world pose and
    //      the Fireport pose in camera space are sampled here, and the row is committed. A frame whose steps 1-3 did not run for the
    //      MainPlayer (view not first person, PWA disabled, setting race) is still committed, marked skipped.
    //
    // The patches and the onPreCull delegate exist only while the setting is on. The ring buffer is preallocated and kept when the
    // setting goes off, so the report can still be written. Nothing is logged per frame. Main thread only.
    internal static class ViewmodelPoseTrace
    {
        const int Capacity = 512;
        const int Mask = Capacity - 1;

        // Metrics evaluated for stalls. Floors separate real motion from float noise when the median is taken.
        const int MetricCount = 8;
        static readonly string[] MetricNames =
        {
            "hands rot sim", "hands rot presented", "root pos", "root rot", "fireport pos (cam)", "fireport rot (cam)", "camera pos", "camera rot"
        };
        static readonly string[] MetricUnits = { "u", "u", "mm", "deg", "mm", "deg", "mm", "deg" };
        static readonly float[] MetricFloors = { 1e-6f, 1e-6f, 1e-3f, 1e-3f, 1e-3f, 1e-3f, 1e-3f, 1e-3f };

        struct Row
        {
            public int frame;
            public float dtMs;
            public int ticks;               // nFixedFrames of the frame's ProcessEffectors call, -1 when it was not called (skipped)
            public bool settingOn;          // ViewmodelInterpolation setting at commit
            public bool blended;            // ViewmodelInterpolation blended this frame
            public float alpha;             // alpha of that blend, -1 when not blended
            public bool hasHands;
            public Vector3 handsSim, handsPresented;
            public bool hasRoot;
            public Vector3 rootPos;
            public Quaternion rootRot;
            public bool hasFireport;
            public Vector3 fireCamPos;
            public Quaternion fireCamRot;
            public Vector3 camPos;
            public Quaternion camRot;
            // Deltas against the previous frame's row (NaN when that row is not the previous frame or lacks the data).
            public float handsSimDelta, handsPresentedDelta;
            public float rootPosDelta, rootRotDelta;
            public float fireCamPosDelta, fireCamRotDelta;
            public float camPosDelta, camRotDelta;
        }

        static readonly Row[] Ring = new Row[Capacity];
        static readonly float[] Scratch = new float[Capacity];
        static readonly int[] Stalled = new int[3];
        static readonly int[] Valid = new int[3];
        static readonly Camera.CameraCallback PreCullCallback = OnPreCull;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        static string harmonyId;
        static string interpolationId;
        static ConfigEntry<bool> setting;
        static MethodBase[] processEffectors;
        static MethodBase lateTransformations;
        static Harmony harmony;
        static bool failed;
        static string failure;

        static int head, count, recorded;

        // Stage data of the current frame. The stamps say which frame each stage last ran in.
        static ProceduralWeaponAnimation tracePwa;
        static int effectorsFrame = -1, lateFrame = -1;
        static bool simSeen;
        static int stageTicks;
        static bool stageHasHands;
        static Vector3 stageHandsSim, stageHandsPresented;
        static Vector3 stageRootPos;
        static Quaternion stageRootRot;

        internal static void Install(string id, string interpolationHarmonyId, ConfigEntry<bool> enabled)
        {
            try
            {
                if (setting != null) return;
                if (enabled == null)
                {
                    Plugin.Log?.LogWarning("Viewmodel pose trace: no config entry supplied; skipping.");
                    return;
                }

                // Resolve every target first; any miss means the trace is unavailable (nothing is patched).
                MethodBase[] effectors =
                {
                    ViewmodelInterpolation.ResolveProcessEffectors(typeof(FirstPersonStrategy)),
                    ViewmodelInterpolation.ResolveProcessEffectors(typeof(MountingStrategy)),
                    ViewmodelInterpolation.ResolveProcessEffectors(typeof(StationaryStrategy))
                };
                MethodBase late = AccessTools.Method(typeof(ProceduralWeaponAnimation), nameof(ProceduralWeaponAnimation.LateTransformations),
                    new[] { typeof(float) });
                if (late == null)
                    throw new MissingMethodException("EFT.Animations.ProceduralWeaponAnimation.LateTransformations(float) not found");

                harmonyId = id;
                interpolationId = interpolationHarmonyId;
                processEffectors = effectors;
                lateTransformations = late;
                failed = false;
                failure = null;
                setting = enabled;
                enabled.SettingChanged += OnSettingChanged;
                if (enabled.Value) Attach();
            }
            catch (Exception error)
            {
                Plugin.Log?.LogWarning("Viewmodel pose trace: install failed, trace unavailable: " + error);
                Detach();
                if (setting != null) setting.SettingChanged -= OnSettingChanged;
                setting = null;
            }
        }

        internal static void Uninstall()
        {
            if (setting == null) return;
            setting.SettingChanged -= OnSettingChanged;
            setting = null;
            Detach();
        }

        static void OnSettingChanged(object sender, EventArgs args)
        {
            try
            {
                if (setting.Value) Attach();
                else Detach();
            }
            catch (Exception error)
            {
                Fail("attach", error);
            }
        }

        static void Attach()
        {
            if (harmony != null || failed) return;
            head = count = recorded = 0;
            Array.Clear(Ring, 0, Ring.Length);
            tracePwa = null;
            effectorsFrame = lateFrame = -1;
            simSeen = false;

            // The simulated read and the ticks capture run before ViewmodelInterpolation's hooks, the presented read and the root sample
            // after them. Explicit before/after on its Harmony id, plus priority, so the order never depends on patch order.
            var prefix = new HarmonyMethod(typeof(ViewmodelPoseTrace), nameof(ProcessEffectorsPrefix))
                { priority = HarmonyLib.Priority.First, before = new[] { interpolationId } };
            var simulated = new HarmonyMethod(typeof(ViewmodelPoseTrace), nameof(SimulatedPostfix))
                { priority = HarmonyLib.Priority.First, before = new[] { interpolationId } };
            var presented = new HarmonyMethod(typeof(ViewmodelPoseTrace), nameof(PresentedPostfix))
                { priority = HarmonyLib.Priority.Last, after = new[] { interpolationId } };
            var late = new HarmonyMethod(typeof(ViewmodelPoseTrace), nameof(LateTransformationsPostfix))
                { priority = HarmonyLib.Priority.Last, after = new[] { interpolationId } };

            var instance = new Harmony(harmonyId);
            try
            {
                foreach (MethodBase target in processEffectors)
                {
                    instance.Patch(target, prefix: prefix, postfix: simulated);
                    instance.Patch(target, postfix: presented);
                }

                instance.Patch(lateTransformations, postfix: late);
            }
            catch
            {
                instance.UnpatchSelf();
                throw;
            }

            harmony = instance;
            Camera.onPreCull += PreCullCallback;
        }

        // Removes the hooks. The ring buffer stays so the report can still be written.
        static void Detach()
        {
            Camera.onPreCull -= PreCullCallback;
            var instance = harmony;
            harmony = null;
            tracePwa = null;
            if (instance == null) return;
            try { instance.UnpatchSelf(); }
            catch (Exception error) { Plugin.Log?.LogWarning("Viewmodel pose trace unpatch failed: " + error.Message); }
        }

        static void Fail(string where, Exception error)
        {
            if (failed) return;
            failed = true;
            failure = where + ": " + error.Message;
            Plugin.Log?.LogWarning("Viewmodel pose trace: " + where + " failed, trace disabled until restart (logged once): " + error);
            Detach();
        }

        static void ProcessEffectorsPrefix(ProceduralWeaponAnimation pwa, int nFixedFrames)
        {
            if (failed) return;
            try
            {
                if (ViewmodelInterpolation.MainPlayerOf(pwa) == null) return;
                tracePwa = pwa;
                effectorsFrame = Time.frameCount;
                stageTicks = nFixedFrames;
                simSeen = false;
                stageHasHands = false;
            }
            catch (Exception error)
            {
                Fail("ProcessEffectors prefix", error);
            }
        }

        static void SimulatedPostfix(ProceduralWeaponAnimation pwa)
        {
            if (failed || !ReferenceEquals(pwa, tracePwa) || effectorsFrame != Time.frameCount) return;
            try
            {
                Spring rotation = HandsRotationOf(pwa);
                if (rotation == null) return;
                stageHandsSim = rotation.Current;
                simSeen = true;
            }
            catch (Exception error)
            {
                Fail("ProcessEffectors simulated postfix", error);
            }
        }

        static void PresentedPostfix(ProceduralWeaponAnimation pwa)
        {
            if (failed || !simSeen || !ReferenceEquals(pwa, tracePwa) || effectorsFrame != Time.frameCount) return;
            try
            {
                Spring rotation = HandsRotationOf(pwa);
                if (rotation == null) return;
                stageHandsPresented = rotation.Current;
                stageHasHands = true;
            }
            catch (Exception error)
            {
                Fail("ProcessEffectors presented postfix", error);
            }
        }

        static Spring HandsRotationOf(ProceduralWeaponAnimation pwa)
        {
            PlayerSpring hands = pwa.HandsContainer;
            return hands == null ? null : hands.HandsRotation;
        }

        // Last PWA write of the frame. Sampled for the MainPlayer's PWA whether or not ProcessEffectors ran this frame.
        static void LateTransformationsPostfix(ProceduralWeaponAnimation __instance)
        {
            if (failed || !ReferenceEquals(__instance, tracePwa)) return;
            try
            {
                PlayerSpring hands = __instance.HandsContainer;
                if (hands == null) return;
                Transform root = hands.WeaponRootAnim;
                if (root == null) return;
                stageRootPos = root.localPosition;
                stageRootRot = root.localRotation;
                lateFrame = Time.frameCount;
            }
            catch (Exception error)
            {
                Fail("LateTransformations postfix", error);
            }
        }

        // Latest point before the FPS camera renders. Other cameras (optics, UI, reflection) are ignored.
        static void OnPreCull(Camera camera)
        {
            if (failed || !CameraManager.Exist) return;
            Camera fps = CameraManager.Instance.Camera;
            if (fps == null || !ReferenceEquals(camera, fps)) return;
            ProceduralWeaponAnimation pwa = tracePwa;
            if (pwa == null) return;
            try
            {
                if (ViewmodelInterpolation.MainPlayerOf(pwa) == null) return;
                Commit(camera, pwa);
            }
            catch (Exception error)
            {
                Fail("Camera.onPreCull", error);
            }
        }

        static void Commit(Camera camera, ProceduralWeaponAnimation pwa)
        {
            int frame = Time.frameCount;
            int slot;
            int previousSlot = -1;
            int last = (head - 1) & Mask;
            if (count > 0 && Ring[last].frame == frame)
            {
                // A second pre-cull of the same camera in one frame: the later sample is the one that renders.
                slot = last;
                if (count > 1) previousSlot = (last - 1) & Mask;
            }
            else
            {
                slot = head;
                if (count > 0) previousSlot = last;
                head = (head + 1) & Mask;
                if (count < Capacity) count++;
                recorded++;
            }

            bool hasPrevious = previousSlot >= 0 && Ring[previousSlot].frame == frame - 1;
            Row previous = hasPrevious ? Ring[previousSlot] : default;
            ref Row row = ref Ring[slot];
            row = default;

            row.frame = frame;
            row.dtMs = Time.deltaTime * 1000f;
            row.settingOn = ViewmodelInterpolation.SettingOn;
            row.blended = ViewmodelInterpolation.LastBlendFrame == frame;
            row.alpha = row.blended ? ViewmodelInterpolation.LastAlpha : -1f;
            row.ticks = -1;
            if (effectorsFrame == frame)
            {
                row.ticks = stageTicks;
                row.hasHands = stageHasHands;
                row.handsSim = stageHandsSim;
                row.handsPresented = stageHandsPresented;
            }

            row.hasRoot = lateFrame == frame;
            row.rootPos = stageRootPos;
            row.rootRot = stageRootRot;

            Transform cameraTransform = camera.transform;
            row.camPos = cameraTransform.position;
            row.camRot = cameraTransform.rotation;
            PlayerSpring container = pwa.HandsContainer;
            Transform fireport = container == null ? null : container.Fireport;
            if (fireport != null)
            {
                row.hasFireport = true;
                row.fireCamPos = cameraTransform.InverseTransformPoint(fireport.position);
                row.fireCamRot = Quaternion.Inverse(row.camRot) * fireport.rotation;
            }

            row.handsSimDelta = row.handsPresentedDelta = row.rootPosDelta = row.rootRotDelta = float.NaN;
            row.fireCamPosDelta = row.fireCamRotDelta = row.camPosDelta = row.camRotDelta = float.NaN;
            if (!hasPrevious) return;

            if (row.hasHands && previous.hasHands)
            {
                row.handsSimDelta = (row.handsSim - previous.handsSim).magnitude;
                row.handsPresentedDelta = (row.handsPresented - previous.handsPresented).magnitude;
            }

            if (row.hasRoot && previous.hasRoot)
            {
                row.rootPosDelta = (row.rootPos - previous.rootPos).magnitude * 1000f;
                row.rootRotDelta = AngleDegrees(previous.rootRot, row.rootRot);
            }

            if (row.hasFireport && previous.hasFireport)
            {
                row.fireCamPosDelta = (row.fireCamPos - previous.fireCamPos).magnitude * 1000f;
                row.fireCamRotDelta = AngleDegrees(previous.fireCamRot, row.fireCamRot);
            }

            row.camPosDelta = (row.camPos - previous.camPos).magnitude * 1000f;
            row.camRotDelta = AngleDegrees(previous.camRot, row.camRot);
        }

        // Angle of a * inverse(b) from the vector part: exact for tiny angles, unlike Quaternion.Angle's acos of a dot product near 1.
        static float AngleDegrees(Quaternion from, Quaternion to)
        {
            Quaternion relative = Quaternion.Inverse(from) * to;
            float vector = Mathf.Sqrt(relative.x * relative.x + relative.y * relative.y + relative.z * relative.z);
            return 2f * Mathf.Atan2(vector, Mathf.Abs(relative.w)) * Mathf.Rad2Deg;
        }

        static float MetricOf(ref Row row, int metric)
        {
            switch (metric)
            {
                case 0: return row.handsSimDelta;
                case 1: return row.handsPresentedDelta;
                case 2: return row.rootPosDelta;
                case 3: return row.rootRotDelta;
                case 4: return row.fireCamPosDelta;
                case 5: return row.fireCamRotDelta;
                case 6: return row.camPosDelta;
                case 7: return row.camRotDelta;
                default: throw new ArgumentOutOfRangeException(nameof(metric));
            }
        }

        // Stall buckets: frames with 0 ticks, frames with ticks, frames without a ProcessEffectors call.
        static int BucketOf(ref Row row) => row.ticks == 0 ? 0 : row.ticks > 0 ? 1 : 2;

        internal static void Write(StringBuilder sb)
        {
            if (setting == null)
            {
                sb.AppendLine("Viewmodel pose trace is not installed.");
                return;
            }

            sb.Append("setting: ").Append(setting.Value ? "on" : "off").AppendLine();
            if (failed) sb.Append("DISABLED after failure: ").AppendLine(failure);
            if (count == 0)
            {
                sb.AppendLine("no rows recorded. Enable Debug / TraceViewmodelPose, play in raid for a few seconds, then write the report.");
                return;
            }

            Camera fps = CameraManager.Exist ? CameraManager.Instance.Camera : null;
            sb.Append("fps camera: ").Append(fps == null ? "none" : fps.name)
                .Append(" (CameraManager.Instance.Camera), Camera.main is the same: ").Append(fps != null && fps == Camera.main ? "yes" : "no").AppendLine();

            int zeroTicks = 0, withTicks = 0, skipped = 0, settingOn = 0, blended = 0;
            for (int i = 0; i < count; i++)
            {
                ref Row row = ref Ring[(head - count + i) & Mask];
                int bucket = BucketOf(ref row);
                if (bucket == 0) zeroTicks++;
                else if (bucket == 1) withTicks++;
                else skipped++;
                if (row.settingOn) settingOn++;
                if (row.blended) blended++;
            }

            sb.Append("frames: ").Append(count).Append(" in buffer (capacity ").Append(Capacity).Append(", ").Append(recorded).Append(" recorded since the setting was turned on)")
                .Append(", frames 0 ticks: ").Append(zeroTicks)
                .Append(", frames with ticks: ").Append(withTicks)
                .Append(", skipped (no ProcessEffectors): ").Append(skipped)
                .Append(", interpolation setting on: ").Append(settingOn)
                .Append(", blended: ").Append(blended).AppendLine();

            WriteStalls(sb);
            WriteTable(sb);
        }

        // A stall frame presented (almost) no change in a metric: |delta| < 1% of that metric's median non-zero |delta|.
        static void WriteStalls(StringBuilder sb)
        {
            sb.AppendLine();
            sb.AppendLine("stall frames per metric: |delta vs previous frame| < 1% of the median non-zero |delta|; stalled/valid frames");
            sb.AppendLine("metric                     median  unit  ticks=0       ticks>0       skipped");
            for (int metric = 0; metric < MetricCount; metric++)
            {
                int nonZero = 0;
                for (int i = 0; i < count; i++)
                {
                    float value = MetricOf(ref Ring[(head - count + i) & Mask], metric);
                    if (!float.IsNaN(value) && value > MetricFloors[metric]) Scratch[nonZero++] = value;
                }

                sb.Append(MetricNames[metric].PadRight(22));
                if (nonZero == 0)
                {
                    sb.AppendLine(" no motion recorded");
                    continue;
                }

                Array.Sort(Scratch, 0, nonZero);
                float median = (nonZero & 1) == 1 ? Scratch[nonZero / 2] : 0.5f * (Scratch[nonZero / 2 - 1] + Scratch[nonZero / 2]);
                float threshold = median * 0.01f;
                Array.Clear(Stalled, 0, Stalled.Length);
                Array.Clear(Valid, 0, Valid.Length);
                for (int i = 0; i < count; i++)
                {
                    ref Row row = ref Ring[(head - count + i) & Mask];
                    float value = MetricOf(ref row, metric);
                    if (float.IsNaN(value)) continue;
                    int bucket = BucketOf(ref row);
                    Valid[bucket]++;
                    if (value < threshold) Stalled[bucket]++;
                }

                sb.Append(median.ToString("F4", Inv).PadLeft(11)).Append("  ").Append(MetricUnits[metric].PadRight(4)).Append("  ")
                    .Append((Stalled[0] + "/" + Valid[0]).PadRight(12)).Append("  ")
                    .Append((Stalled[1] + "/" + Valid[1]).PadRight(12)).Append("  ")
                    .Append(Stalled[2] + "/" + Valid[2]).AppendLine();
            }
        }

        static void WriteTable(StringBuilder sb)
        {
            sb.AppendLine();
            sb.AppendLine("rows, oldest first; every delta is against the previous frame's row, '-' = not available");
            sb.AppendLine("frame  frame number (Time.frameCount)   dt  Time.deltaTime ms   tk  nFixedFrames of the frame's ProcessEffectors call (-1 = skipped)");
            sb.AppendLine("I  interpolation setting on   B  blended this frame   alpha  blend alpha (-1 = not blended)");
            sb.AppendLine("hSim / hPre  |delta| of HandsRotation Spring.Current: simulated (after ticks, before blend) / presented (after blend), raw units");
            sb.AppendLine("rP / rR  WeaponRootAnim local position mm / rotation deg after LateTransformations");
            sb.AppendLine("fP / fR  Fireport pose in FPS camera space at Camera.onPreCull, mm / deg   cP / cR  FPS camera world position mm / rotation deg");
            sb.AppendLine("flags  S skipped (no ProcessEffectors)  R no root sample  H no hands sample  F no fireport");
            sb.AppendLine();
            sb.AppendLine("   frame     dt  tk I B alpha |     hSim      hPre |      rP      rR |      fP      fR |      cP      cR | flags");
            for (int i = 0; i < count; i++)
            {
                ref Row row = ref Ring[(head - count + i) & Mask];
                sb.Append(row.frame.ToString(Inv).PadLeft(8))
                    .Append(' ').Append(row.dtMs.ToString("F2", Inv).PadLeft(6))
                    .Append(' ').Append(row.ticks.ToString(Inv).PadLeft(3))
                    .Append(' ').Append(row.settingOn ? 'I' : '.')
                    .Append(' ').Append(row.blended ? 'B' : '.')
                    .Append(' ').Append(row.alpha.ToString("F3", Inv).PadLeft(5)).Append(" |");
                Cell(sb, row.handsSimDelta, "F5", 9);
                Cell(sb, row.handsPresentedDelta, "F5", 10);
                sb.Append(" |");
                Cell(sb, row.rootPosDelta, "F3", 8);
                Cell(sb, row.rootRotDelta, "F3", 8);
                sb.Append(" |");
                Cell(sb, row.fireCamPosDelta, "F3", 8);
                Cell(sb, row.fireCamRotDelta, "F3", 8);
                sb.Append(" |");
                Cell(sb, row.camPosDelta, "F3", 8);
                Cell(sb, row.camRotDelta, "F3", 8);
                sb.Append(" | ")
                    .Append(row.ticks < 0 ? 'S' : '.')
                    .Append(row.hasRoot ? '.' : 'R')
                    .Append(row.ticks < 0 || row.hasHands ? '.' : 'H')
                    .Append(row.hasFireport ? '.' : 'F')
                    .AppendLine();
            }
        }

        static void Cell(StringBuilder sb, float value, string format, int width)
        {
            sb.Append((float.IsNaN(value) ? "-" : value.ToString(format, Inv)).PadLeft(width));
        }
    }
}
