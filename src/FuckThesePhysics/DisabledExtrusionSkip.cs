using System;
using System.Globalization;
using System.Reflection;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace FuckThesePhysics
{
    // Skips ColliderExtrusion.Calculate(Vector3, Quaternion, float) for extruders whose collider is disabled or inactive
    // (docs/research/movement-collision-batching.md 8).
    //
    // PlayerOverlapManager.Init ends with Off(), which disables every head collider; On() is only called for the local player.
    // Bots' head extruders therefore still run Calculate every call, with a disabled collider. Collider.bounds is an empty box for a
    // disabled collider, so ColliderExtrusion.CalculateCastRadius gives radius 0 and RefreshNeighbours centres the sphere on
    // position + (bounds.center - transform.position), roughly the motion vector, i.e. next to the world origin and not near the bot.
    // The query tests whatever static mesh sits at the origin on every call, and CalculateDepenetration then tests the bot's capsule
    // against those colliders. [INFERENCE] The result is zero unless the bot stands near the origin; Debug / VerifyExtrusionSkip
    // exists to confirm that in game.
    //
    // The gate is the collider state, not "is a bot", so behaviour stays vanilla for any extruder whose collider is enabled.
    // Skipping means _depenetration is set to zero, which is what vanilla yields in the expected case. Replacing the original is the
    // point, so the patch is a skipping prefix.
    //
    // Verify mode runs vanilla for the gated calls instead of skipping, and a postfix counts the non-zero results (report only,
    // nothing is logged per call). It gives no performance gain while on.
    internal static class DisabledExtrusionSkip
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        static Harmony harmony;
        static ConfigEntry<bool> skipSetting;
        static ConfigEntry<bool> verifySetting;
        static bool failed;
        static string failure;

        static AccessTools.FieldRef<ColliderExtrusion, Vector3> depenetrationRef;
        static AccessTools.FieldRef<ColliderExtrusion, float> castRadiusRef;

        // Counters since the last report (main thread only).
        static int windowStartFrame;
        static int skippedCalls, verifiedCalls, nonZeroCalls;
        static float maxSquaredMagnitude;
        static bool hasSample;
        static Collider sampleCollider;
        static Vector3 sampleCentre, sampleDepenetration;
        static float sampleRadius;
        static int sampleFrame;

        internal static void Install(string harmonyId, ConfigEntry<bool> skipEnabled, ConfigEntry<bool> verifyEnabled)
        {
            try
            {
                if (harmony != null) return;
                if (skipEnabled == null || verifyEnabled == null)
                {
                    Plugin.Log?.LogWarning("Disabled-collider extrusion skip: no config entries supplied; skipping patching.");
                    return;
                }

                // Resolve every target first; any miss means vanilla behaviour (nothing is patched).
                MethodBase calculate = AccessTools.Method(typeof(ColliderExtrusion), nameof(ColliderExtrusion.Calculate),
                    new[] { typeof(Vector3), typeof(Quaternion), typeof(float) });
                if (calculate == null)
                    throw new MissingMethodException("ColliderExtrusion.Calculate(Vector3, Quaternion, float) not found");
                RequireField("_depenetration", typeof(Vector3));
                RequireField("_castRadius", typeof(float));

                depenetrationRef = AccessTools.FieldRefAccess<ColliderExtrusion, Vector3>("_depenetration");
                castRadiusRef = AccessTools.FieldRefAccess<ColliderExtrusion, float>("_castRadius");
                skipSetting = skipEnabled;
                verifySetting = verifyEnabled;
                failed = false;
                failure = null;
                ResetWindow();

                var instance = new Harmony(harmonyId);
                instance.Patch(calculate,
                    prefix: new HarmonyMethod(typeof(DisabledExtrusionSkip), nameof(CalculatePrefix)),
                    postfix: new HarmonyMethod(typeof(DisabledExtrusionSkip), nameof(CalculatePostfix)));
                harmony = instance;
                Plugin.Log?.LogInfo("Disabled-collider extrusion skip installed (setting " + (skipEnabled.Value ? "on" : "off") + ").");
            }
            catch (Exception error)
            {
                Plugin.Log?.LogWarning("Disabled-collider extrusion skip: install failed, running vanilla: " + error);
            }
        }

        // Removes this class's patches: its Harmony id is its own.
        internal static void Uninstall()
        {
            var instance = harmony;
            harmony = null;
            if (instance == null) return;
            try { instance.UnpatchSelf(); }
            catch (Exception error) { Plugin.Log?.LogWarning("Disabled-collider extrusion skip unpatch failed: " + error.Message); }
        }

        static void RequireField(string name, Type fieldType)
        {
            FieldInfo field = AccessTools.Field(typeof(ColliderExtrusion), name);
            if (field == null)
                throw new MissingFieldException("ColliderExtrusion." + name + " (" + fieldType + ") not found");
            if (field.FieldType != fieldType)
                throw new MissingFieldException("ColliderExtrusion." + name + " is " + field.FieldType + ", expected " + fieldType);
        }

        static void Fail(string where, Exception error)
        {
            if (failed) return;
            failed = true;
            failure = where + ": " + error.Message;
            Plugin.Log?.LogWarning("Disabled-collider extrusion skip: " + where + " failed, falling back to vanilla (logged once): " + error);
        }

        static void ResetWindow()
        {
            windowStartFrame = Time.frameCount;
            skippedCalls = verifiedCalls = nonZeroCalls = 0;
            maxSquaredMagnitude = 0f;
            hasSample = false;
            sampleCollider = null;
        }

        // True when the extruder's collider exists but is disabled or inactive, i.e. Collider.bounds is empty.
        static bool IsColliderInactive(ColliderExtrusion extruder)
        {
            Collider collider = extruder._collider;
            return collider != null && (!collider.enabled || !collider.gameObject.activeInHierarchy);
        }

        // Skipping prefix: replaces Calculate for gated extruders by a zero depenetration. __state tells the postfix that vanilla ran
        // in verify mode.
        static bool CalculatePrefix(ColliderExtrusion __instance, out bool __state)
        {
            __state = false;
            if (failed) return true;
            try
            {
                if (!skipSetting.Value || !IsColliderInactive(__instance)) return true;
                if (verifySetting.Value)
                {
                    __state = true;
                    return true;
                }
                depenetrationRef(__instance) = Vector3.zero;
                skippedCalls++;
                return false;
            }
            catch (Exception error)
            {
                Fail("Calculate prefix", error);
                return true;
            }
        }

        static void CalculatePostfix(ColliderExtrusion __instance, Vector3 position, bool __state)
        {
            if (!__state || failed) return;
            try
            {
                verifiedCalls++;
                Vector3 depenetration = __instance.GetDepenetration();
                float squared = depenetration.sqrMagnitude;
                if (squared <= 0f) return;

                nonZeroCalls++;
                if (squared > maxSquaredMagnitude) maxSquaredMagnitude = squared;
                Collider collider = __instance._collider;
                sampleCollider = collider;
                sampleCentre = position + (collider.bounds.center - collider.transform.position);
                sampleRadius = castRadiusRef(__instance);
                sampleDepenetration = depenetration;
                sampleFrame = Time.frameCount;
                hasSample = true;
            }
            catch (Exception error)
            {
                Fail("Calculate postfix", error);
            }
        }

        internal static void Write(StringBuilder sb)
        {
            if (harmony == null)
            {
                sb.AppendLine("Disabled-collider extrusion skip is not installed.");
                return;
            }

            sb.Append("setting: ").Append(skipSetting.Value ? "on" : "off").Append(", verify: ").AppendLine(verifySetting.Value ? "on" : "off");
            if (failed) sb.Append("DISABLED after failure: ").AppendLine(failure);
            sb.Append("window: ").Append(Time.frameCount - windowStartFrame).AppendLine(" frames");
            sb.Append("skipped calls: ").Append(skippedCalls).AppendLine();
            sb.Append("vanilla-verified calls: ").Append(verifiedCalls).Append(", non-zero depenetration: ").Append(nonZeroCalls)
                .Append(", max magnitude ").Append(Mathf.Sqrt(maxSquaredMagnitude).ToString("F4", Inv)).AppendLine();
            if (hasSample)
            {
                // The collider may be gone by report time.
                string who = sampleCollider != null ? PathOf(sampleCollider.transform) : "<destroyed>";
                sb.Append("last non-zero: ").Append(who)
                    .Append(" centre=").Append(sampleCentre.ToString("F2"))
                    .Append(" castRadius=").Append(sampleRadius.ToString("F3", Inv))
                    .Append(" depenetration=").Append(sampleDepenetration.ToString("F4"))
                    .Append(" frame=").Append(sampleFrame).AppendLine();
            }
            else sb.AppendLine("last non-zero: none");
            ResetWindow();
        }

        static string PathOf(Transform transform)
        {
            string path = transform.name;
            for (Transform parent = transform.parent; parent != null; parent = parent.parent)
                path = parent.name + "/" + path;
            return path;
        }
    }
}
