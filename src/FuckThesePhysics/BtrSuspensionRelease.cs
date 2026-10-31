using System;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace FuckThesePhysics
{
    // Releases the BTR suspension body from EFT's physics registry (PhysicsExtensions.UpdateController._rigidbodies) when
    // the BTR parks.
    //
    // VehicleSuspension.SuspensionOn registers parentBody through SupportRigidbody every time the BTR departs, and
    // SuspensionOff (BTR stopped: body made kinematic, gravity off, springs deactivated) never unregisters it. The entries
    // pile up and keep Physics.Simulate running for the rest of the raid, parked or not (docs/research/btr-suspension.md).
    // After SuspensionOff the body is kinematic and nothing simulates it; the next SuspensionOn registers it again.
    //
    // Only calls EFT's own UpdateController.UnsupportRigidbody. Never touches the body, joint, springs or physics settings.
    internal static class BtrSuspensionRelease
    {
        static Harmony harmony;
        static MethodBase target;
        static MethodInfo patch;
        static ConfigEntry<bool> releaseSetting;
        static bool failureLogged;

        internal static void Install(string harmonyId, ConfigEntry<bool> releaseEnabled)
        {
            try
            {
                if (harmony != null) return;
                MethodBase suspensionOff = AccessTools.Method(typeof(VehicleSuspension), nameof(VehicleSuspension.SuspensionOff), Type.EmptyTypes);
                if (suspensionOff == null)
                {
                    Plugin.Log?.LogWarning("BTR suspension release: VehicleSuspension.SuspensionOff() not found; skipping patching (vanilla behaviour).");
                    return;
                }

                releaseSetting = releaseEnabled;
                var instance = new Harmony(harmonyId);
                MethodInfo postfix = AccessTools.Method(typeof(BtrSuspensionRelease), nameof(SuspensionOffPostfix));
                instance.Patch(suspensionOff, postfix: new HarmonyMethod(postfix));
                harmony = instance;
                target = suspensionOff;
                patch = postfix;
                Plugin.Log?.LogInfo("BTR suspension release installed (setting " + (releaseEnabled.Value ? "on" : "off") + ").");
            }
            catch (Exception error)
            {
                Plugin.Log?.LogWarning("BTR suspension release: install failed, running vanilla: " + error);
            }
        }

        // Removes only this class's patch: the Harmony owner id is shared with the other patch classes.
        internal static void Uninstall()
        {
            var instance = harmony;
            harmony = null;
            if (instance == null) return;
            try { instance.Unpatch(target, patch); }
            catch (Exception error) { Plugin.Log?.LogWarning("BTR suspension release unpatch failed: " + error.Message); }
        }

        // SuspensionOff runs once per BTR stop, so this path is rare; it still allocates only for the log line.
        static void SuspensionOffPostfix(VehicleSuspension __instance)
        {
            try
            {
                if (!releaseSetting.Value) return;
                Rigidbody rb = __instance.parentBody;
                if (rb == null) return;

                // Support never deduplicates and Unsupport removes only the first match, so count and remove every entry.
                var registry = PhysicsExtensions.UpdateController._rigidbodies;
                int matches = 0;
                for (int i = 0; i < registry.Count; i++)
                    if (registry[i].rigidbody == rb) matches++;
                if (matches == 0) return;

                for (int i = 0; i < matches; i++)
                    PhysicsExtensions.UpdateController.UnsupportRigidbody(rb);

                Plugin.Log?.LogInfo("BTR suspension release pos=" + rb.position.ToString("F1")
                    + " removed=" + matches
                    + " remainingRegistry=" + PhysicsExtensions.UpdateController._rigidbodies.Count
                    + " simulationEnabled=" + PhysicsExtensions.Simulation.UpdateEnabled);
            }
            catch (Exception error)
            {
                if (failureLogged) return;
                failureLogged = true;
                Plugin.Log?.LogWarning("BTR suspension release: SuspensionOff postfix failed (logged once): " + error);
            }
        }
    }
}
