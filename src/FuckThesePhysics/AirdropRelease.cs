using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using BepInEx.Configuration;
using EFT.Airdrop;
using HarmonyLib;
using UnityEngine;

namespace FuckThesePhysics
{
    // Releases a landed airdrop crate from EFT's physics registry (PhysicsExtensions.UpdateController._rigidbodies).
    //
    // ServerAirDrop.Init registers the crate rigidbody through SupportRigidbody and nothing ever unregisters it, so the crate
    // keeps PhysicsExtensions.Simulation.UpdateEnabled true (and Physics.Simulate running every frame) until raid end.
    // The crate is kinematic and, once EAirdropFallingStage.Landed is reached, nothing writes its transform or consumes physics
    // events any more, so the registration is no longer needed (docs/design.md 3).
    //
    // This only calls EFT's own UpdateController.UnsupportRigidbody. It never destroys the rigidbody, deactivates the
    // GameObject, toggles isKinematic, or writes simulationMode / UpdateEnabled.
    internal static class AirdropRelease
    {
        // Per-crate latch bits.
        const int LandedSeen = 1;   // 'Landed' lifecycle line written
        const int Released = 2;     // release attempted (setting was on) - never repeated for this drop
        const int StaticSeen = 4;   // 'IsStatic' lifecycle line written

        // Fault sites, so a repeating failure is logged once and never thrown into EFT.
        const int SiteInit = 1;
        const int SiteUpdate = 2;
        const int SiteDispose = 4;

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        static Harmony harmony;
        static ConfigEntry<bool> releaseSetting;
        static readonly List<KeyValuePair<MethodBase, MethodInfo>> applied = new List<KeyValuePair<MethodBase, MethodInfo>>(3);

        // ServerAirDrop instances are pooled and reused; Init clears an instance's latch and GameWorld.Dispose clears all.
        static readonly Dictionary<ServerAirDrop, int> latch = new Dictionary<ServerAirDrop, int>();
        static int failedSites;

        // ServerAirDrop._syncObject is an AirdropSynchronizableObject, whose base chain ends in Sirenix's SerializedMonoBehaviour
        // (an assembly this project does not reference). Its fields are therefore read through compiled accessors built from
        // reflection instead of being bound at compile time. ObjectId and IsStatic are declared on SynchronizableObject.
        static Func<ServerAirDrop, int> objectIdOf;
        static Func<ServerAirDrop, bool> isStaticOf;

        internal static void Install(string harmonyId, ConfigEntry<bool> releaseEnabled)
        {
            try
            {
                if (harmony != null) return;
                if (releaseEnabled == null)
                {
                    Plugin.Log?.LogWarning("Airdrop release: no config entry supplied; skipping patching.");
                    return;
                }

                // Resolve every target first; any miss means vanilla behaviour (no patch at all).
                MethodBase init = AccessTools.Method(typeof(ServerAirDrop), "Init", new[] { typeof(int), typeof(Vector3), typeof(Vector3) });
                MethodBase manualUpdate = AccessTools.Method(typeof(ServerAirDrop), "ManualUpdate", new[] { typeof(float) });
                Type gameWorld = AccessTools.TypeByName("EFT.GameWorld");
                MethodBase dispose = gameWorld == null ? null : AccessTools.Method(gameWorld, "Dispose", Type.EmptyTypes);
                FieldInfo fallingStage = AccessTools.Field(typeof(ServerAirDrop), "_fallingStage");
                FieldInfo syncObject = AccessTools.Field(typeof(ServerAirDrop), "_syncObject");
                FieldInfo objectId = syncObject == null ? null : AccessTools.Field(syncObject.FieldType, "ObjectId");
                FieldInfo isStatic = syncObject == null ? null : AccessTools.Field(syncObject.FieldType, "IsStatic");
                MethodInfo rigidbodyGetter = AccessTools.PropertyGetter(typeof(ServerAirDrop), "SyncObjectRigidbody");
                MethodInfo transformGetter = AccessTools.PropertyGetter(typeof(ServerAirDrop), "SyncObjectTransform");
                MethodInfo unsupport = AccessTools.Method(typeof(PhysicsExtensions.UpdateController), "UnsupportRigidbody", new[] { typeof(Rigidbody) });
                FieldInfo registry = AccessTools.Field(typeof(PhysicsExtensions.UpdateController), "_rigidbodies");
                MethodInfo syncTransforms = AccessTools.Method(typeof(PhysicsExtensions), "SyncTransforms", Type.EmptyTypes);

                string missing = FirstMissing(
                    "ServerAirDrop.Init(int, Vector3, Vector3)", init,
                    "ServerAirDrop.ManualUpdate(float)", manualUpdate,
                    "EFT.GameWorld.Dispose()", dispose,
                    "ServerAirDrop._fallingStage", fallingStage,
                    "ServerAirDrop._syncObject", syncObject,
                    "SynchronizableObject.ObjectId", objectId,
                    "SynchronizableObject.IsStatic", isStatic,
                    "ServerAirDrop.SyncObjectRigidbody", rigidbodyGetter,
                    "ServerAirDrop.SyncObjectTransform", transformGetter,
                    "PhysicsExtensions.UpdateController.UnsupportRigidbody(Rigidbody)", unsupport,
                    "PhysicsExtensions.UpdateController._rigidbodies", registry,
                    "PhysicsExtensions.SyncTransforms()", syncTransforms);
                if (missing != null)
                {
                    Plugin.Log?.LogWarning("Airdrop release: " + missing + " not found; skipping patching (vanilla behaviour).");
                    return;
                }
                if (objectId.FieldType != typeof(int) || isStatic.FieldType != typeof(bool))
                {
                    Plugin.Log?.LogWarning("Airdrop release: SynchronizableObject.ObjectId/IsStatic have unexpected types ("
                        + objectId.FieldType + "/" + isStatic.FieldType + "); skipping patching (vanilla behaviour).");
                    return;
                }

                objectIdOf = BuildAccessor<int>(syncObject, objectId);
                isStaticOf = BuildAccessor<bool>(syncObject, isStatic);
                releaseSetting = releaseEnabled;

                var instance = new Harmony(harmonyId);
                harmony = instance;
                if (!Patch(instance, "ServerAirDrop.Init", init, nameof(InitPrefix), null)
                    || !Patch(instance, "ServerAirDrop.ManualUpdate", manualUpdate, null, nameof(ManualUpdatePostfix))
                    || !Patch(instance, "EFT.GameWorld.Dispose", dispose, null, nameof(DisposePostfix)))
                {
                    Rollback(instance);
                    harmony = null;
                    return;
                }
                Plugin.Log?.LogInfo("Airdrop release installed (setting " + (releaseEnabled.Value ? "on" : "off") + ").");
            }
            catch (Exception error)
            {
                Plugin.Log?.LogWarning("Airdrop release: install failed, running vanilla: " + error);
                var instance = harmony;
                harmony = null;
                if (instance != null) Rollback(instance);
            }
        }

        internal static void Uninstall()
        {
            var instance = harmony;
            harmony = null;
            latch.Clear();
            if (instance == null) return;
            try { instance.UnpatchSelf(); }
            catch (Exception error) { Plugin.Log?.LogWarning("Airdrop release unpatch failed: " + error.Message); }
        }

        static string FirstMissing(params object[] pairs)
        {
            for (int i = 0; i < pairs.Length; i += 2)
                if (pairs[i + 1] == null) return (string)pairs[i];
            return null;
        }

        static Func<ServerAirDrop, T> BuildAccessor<T>(FieldInfo syncObject, FieldInfo leaf)
        {
            ParameterExpression drop = Expression.Parameter(typeof(ServerAirDrop), "drop");
            Expression body = Expression.Field(Expression.Field(drop, syncObject), leaf);
            return Expression.Lambda<Func<ServerAirDrop, T>>(body, drop).Compile();
        }

        static bool Patch(Harmony instance, string label, MethodBase target, string prefix, string postfix)
        {
            string patchName = prefix ?? postfix;
            try
            {
                instance.Patch(target,
                    prefix: prefix == null ? null : new HarmonyMethod(typeof(AirdropRelease), prefix),
                    postfix: postfix == null ? null : new HarmonyMethod(typeof(AirdropRelease), postfix));
                applied.Add(new KeyValuePair<MethodBase, MethodInfo>(target, AccessTools.Method(typeof(AirdropRelease), patchName)));
                return true;
            }
            catch (Exception error)
            {
                Plugin.Log?.LogWarning("Airdrop release: patching " + label + " failed; reverting airdrop patches (vanilla behaviour): " + error.Message);
                return false;
            }
        }

        // Removes only this class's patches. UnpatchSelf is not used here: the Harmony owner id is shared with SimulateTimer.
        static void Rollback(Harmony instance)
        {
            foreach (var entry in applied)
            {
                try { instance.Unpatch(entry.Key, entry.Value); }
                catch (Exception error) { Plugin.Log?.LogWarning("Airdrop release: rollback of " + entry.Value.Name + " failed: " + error.Message); }
            }
            applied.Clear();
            latch.Clear();
        }

        // ServerAirDrop.Init(int objectId, Vector3 position, Vector3 rotation): the instance may be a pooled one that has
        // landed before, so its latch must start over. Parameter names match the original for Harmony injection.
        static void InitPrefix(ServerAirDrop __instance, int objectId, Vector3 position)
        {
            try
            {
                latch.Remove(__instance);
                Plugin.Log?.LogInfo("Airdrop crate Init id=" + objectId.ToString(Inv) + " pos=" + position.ToString("F1"));
            }
            catch (Exception error) { Failed(SiteInit, "Init prefix", error); }
        }

        // Runs every frame for every ServerAirDrop (and keeps running after landing until IsStatic), so everything before the
        // first latch change must stay allocation-free: one enum compare, one dictionary lookup, bit tests. The latch is
        // committed before any action runs, so a failing action is attempted (and logged) once per drop, never every frame.
        static void ManualUpdatePostfix(ServerAirDrop __instance)
        {
            try
            {
                if (__instance._fallingStage != EAirdropFallingStage.Landed) return;

                latch.TryGetValue(__instance, out int state);
                if (state == (LandedSeen | Released | StaticSeen)) return;

                bool release = (state & Released) == 0 && releaseSetting.Value;
                bool landed = (state & LandedSeen) == 0;
                bool becameStatic = (state & StaticSeen) == 0 && isStaticOf(__instance);
                if (!landed && !release && !becameStatic) return;

                if (landed) state |= LandedSeen;
                if (release) state |= Released;
                if (becameStatic) state |= StaticSeen;
                latch[__instance] = state;

                if (landed)
                    Plugin.Log?.LogInfo("Airdrop crate Landed id=" + IdOf(__instance) + " pos=" + PositionOf(__instance)
                        + " release=" + (releaseSetting.Value ? "on" : "off"));
                if (release) Release(__instance);
                if (becameStatic)
                    Plugin.Log?.LogInfo("Airdrop crate IsStatic id=" + IdOf(__instance) + " pos=" + PositionOf(__instance));
            }
            catch (Exception error) { Failed(SiteUpdate, "ManualUpdate postfix", error); }
        }

        // Once per drop: drop every registry entry that references the crate rigidbody (Support never deduplicates and
        // Unsupport removes only the first match), after flushing the final pose to PhysX.
        static void Release(ServerAirDrop drop)
        {
            Rigidbody rb = drop.SyncObjectRigidbody;
            var registry = PhysicsExtensions.UpdateController._rigidbodies;

            int matches = 0;
            if (rb != null)
            {
                for (int i = 0; i < registry.Count; i++)
                    if (registry[i].rigidbody == rb) matches++;
            }

            if (matches > 0)
            {
                PhysicsExtensions.SyncTransforms();
                for (int i = 0; i < matches; i++)
                    PhysicsExtensions.UpdateController.UnsupportRigidbody(rb);
            }

            Plugin.Log?.LogInfo("Airdrop crate release id=" + IdOf(drop) + " pos=" + PositionOf(drop)
                + " removed=" + matches.ToString(Inv)
                + " remainingRegistry=" + PhysicsExtensions.UpdateController._rigidbodies.Count.ToString(Inv)
                + " simulationEnabled=" + PhysicsExtensions.Simulation.UpdateEnabled
                + (rb == null ? " (crate rigidbody missing)" : string.Empty));
        }

        // GameWorld.Dispose (ClientGameWorld.Dispose calls base.Dispose): the raid is over, drop every latch entry.
        static void DisposePostfix()
        {
            try { latch.Clear(); }
            catch (Exception error) { Failed(SiteDispose, "GameWorld.Dispose postfix", error); }
        }

        static string IdOf(ServerAirDrop drop)
        {
            try { return objectIdOf(drop).ToString(Inv); }
            catch (Exception) { return "?"; }
        }

        static string PositionOf(ServerAirDrop drop)
        {
            try { return drop.SyncObjectTransform.position.ToString("F1"); }
            catch (Exception) { return "?"; }
        }

        static void Failed(int site, string where, Exception error)
        {
            if ((failedSites & site) != 0) return;
            failedSites |= site;
            Plugin.Log?.LogWarning("Airdrop release: " + where + " failed (logged once): " + error);
        }
    }
}
