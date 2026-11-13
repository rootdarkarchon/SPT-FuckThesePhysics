using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using EFT.SynchronizableObjects;
using HarmonyLib;
using UnityEngine;

namespace FuckThesePhysics
{
    // Keeps planted tripwires out of EFT's physics registry (PhysicsExtensions.UpdateController._rigidbodies) and detects
    // player and bot walk-ins itself (docs/research/tripwire-gate.md section 9).
    //
    // BaseTripwire.Init registers the wire rigidbody through SupportRigidbody and only ReturnToPool (after Destroyed) removes
    // it again, so every planted wire (the Partisan boss plants up to ~30) holds PhysicsExtensions.Simulation.UpdateEnabled
    // true, and Physics.Simulate running every frame, for the rest of the raid. The step is only there so that the trigger
    // capsule _collisionCollider can report OnTriggerEnter to SyncObjectCollisionChecker, which relays to
    // BaseTripwire.CollisionEnter.
    //
    // Split of vanilla's single detector by what moves the entering collider:
    //  - Players and bots move their capsules by transform and never hold the gate. This class tests their capsules against
    //    the wire segment once per frame (prefix on PhysicsExtensions.Simulation.Update, where vanilla's Simulate and so its
    //    trigger callbacks would run) and calls vanilla BaseTripwire.CollisionEnter(playerCollider) on an enter edge, so the
    //    layer filter, GetPlayerByCollider, the bot ignore roll and the ISyncEnvironment.TriggerTripwire virtual (Fika
    //    authority) stay vanilla.
    //  - Everything else that can enter a wire (loot drops, grenades, ragdolls) registers its own rigidbody while it moves,
    //    so the step runs anyway and the vanilla relay handles it unchanged.
    // Accepted divergence: non-player colliders that move without their own registry entry (doors, animated scene colliders)
    // do not set a wire off unless something else happens to run the step.
    //
    // Transform math instead of Physics.OverlapCapsule: EFT runs with Physics.autoSyncTransforms = false, so PhysX poses of bot
    // and remote-player capsules are frame-order dependent while the step is off, and a query would need its own SyncTransforms.
    //
    // Resolved members (Assembly-CSharp 0.16.9.40743, MVID cc2d80b06d5b4cb1a5816d2cc901d4c7, tokens in brackets):
    //  EFT.SynchronizableObjects.BaseTripwire [02001592]: Init(int, Vector3, Vector3) [0600AF33], ReturnToPool() [0600AF3B],
    //    CollisionEnter(Collider) [0600AF39], SyncObjectRigidbody [17001ADB], _tripwireSyncObject [04008E0F]
    //  TripwireSynchronizableObject [0200159F]: _collisionCollider [04008E4F] (CapsuleCollider), TripwireState [17001AE2],
    //    PlacerPlayerId [17001AE7] (MongoID); SynchronizableObject [02001597]: ObjectId [04008E19], IsStatic [04008E1D]
    //  SyncObjectCollisionChecker [0200159A]: OnTriggerEnter(Collider) [0600AF6C] (relay to SynchronizableObject.CollisionEnter
    //    [0600AF56] -> _logic.CollisionEnter), Awake [0600AF6B], _synchronizableObject [04008E2B]
    //  EFT.GameWorld: Dispose() [06007B75], AllAlivePlayersList [040067F5], GetPlayerByCollider(Collider) [06007B84],
    //    PlayersColliders [170011F0]
    //  EFT.Player: CharacterController [1700131C] (ICharacterController.GetCollider() [060035F5], the collider RegisterPlayer
    //    [06007B7F] stores in PlayersColliders), IsAI [17001398], ProfileId [17001389]
    //  PhysicsExtensions.Simulation.Update() [060111C9]; PhysicsExtensions.UpdateController: SupportRigidbody [060111D1],
    //    UnsupportRigidbody [060111D2], _rigidbodies [0400D531]
    // TripwireSynchronizableObject derives from Sirenix's SerializedMonoBehaviour (an assembly this project does not reference),
    // so its members are read through compiled accessors built from reflection, as in AirdropRelease.
    //
    // Only calls EFT's own SupportRigidbody/UnsupportRigidbody and BaseTripwire.CollisionEnter. It never touches wire
    // colliders, rigidbodies, state or lifecycle.
    internal static class TripwireRelease
    {
        // Fault sites, so a repeating failure is logged once and never thrown into EFT.
        const int SiteInit = 1;
        const int SiteReturn = 2;
        const int SiteUpdate = 4;
        const int SiteRelay = 8;
        const int SiteDispose = 16;
        const int SiteEnter = 32;
        const int SiteCollider = 64;

        // A player counts as having left a wire once its capsule is this much further than touching (metres), so standing on
        // the wire does not roll CHANCE_TO_IGNORE_TRIPWIRE again.
        const float ExitHysteresis = 0.01f;
        // Sweep test between last frame's and this frame's capsule when the capsule moved more than its own radius. Moves
        // above MaxSweepDistance are teleports and are not swept.
        const float MaxSweepDistance = 25f;
        const int MaxSweepSteps = 16;
        const int PosePruneFrames = 600;
        const float LogIntervalSeconds = 0.5f;
        const int InitialSlots = 64;

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // Everything the detector needs about one planted wire. Pooled BaseTripwire instances get a fresh Wire per Init.
        sealed class Wire
        {
            public BaseTripwire Logic;
            public Component Sync;                  // the TripwireSynchronizableObject
            public Rigidbody Body;
            public CapsuleCollider Capsule;         // TripwireSynchronizableObject._collisionCollider
            public GameObject ArmedObject;          // Capsule.gameObject: active while the wire is armed (HideContent deactivates it)
            public SyncObjectCollisionChecker Checker;
            public bool Held;                       // registry entries removed; this class is the walk-in detector
            public bool GeometryValid;
            public Vector3 A, B, Mid;               // world-space segment of the trigger capsule axis
            public float Radius, Reach;             // Reach: bounding-sphere radius around Mid
            public readonly List<int> Inside = new List<int>(4); // collider instance ids currently touching
        }

        // One alive player's capsule this frame.
        struct Slot
        {
            public Player Player;
            public Collider Collider;
            public int Id;
            public Vector3 A, B, Mid;
            public float Radius, Reach;
            public bool HasPrev;
            public Vector3 PrevA, PrevB;
            public float Moved;
            public int Steps;
        }

        struct Pose
        {
            public int Frame;
            public Vector3 A, B;
        }

        static Harmony harmony;
        static ConfigEntry<bool> releaseSetting;
        static ConfigEntry<bool> debugLogging;
        static readonly List<KeyValuePair<MethodBase, MethodInfo>> applied = new List<KeyValuePair<MethodBase, MethodInfo>>(5);

        static readonly List<Wire> wires = new List<Wire>(32);
        static readonly List<Wire> armed = new List<Wire>(32);
        static Slot[] slots = new Slot[InitialSlots];
        static readonly Dictionary<int, Pose> poses = new Dictionary<int, Pose>(InitialSlots);
        static readonly List<int> pruneScratch = new List<int>(InitialSlots);
        static int heldCount;
        static int version;
        static int lastFrame = -1;
        static int lastPruneFrame;
        static bool faulted;
        static int failedSites;

        static float nextEnterLog, nextRelayLog;
        static int enterSuppressed, relaySuppressed;

        static Type syncType;
        static Func<BaseTripwire, Component> syncOf;
        static Func<Component, CapsuleCollider> collisionColliderOf;
        static Func<Component, ETripwireState> stateOf;
        static Func<Component, int> objectIdOf;
        static Func<Component, bool> isStaticOf;
        static Func<Component, string> placerOf;

        internal static void Install(string harmonyId, ConfigEntry<bool> releaseEnabled, ConfigEntry<bool> debugLog)
        {
            try
            {
                if (harmony != null) return;
                if (releaseEnabled == null)
                {
                    Plugin.Log?.LogWarning("Tripwire release: no config entry supplied; skipping patching.");
                    return;
                }

                // Resolve every target first; any miss means vanilla behaviour (no patch at all).
                MethodBase init = AccessTools.Method(typeof(BaseTripwire), "Init", new[] { typeof(int), typeof(Vector3), typeof(Vector3) });
                MethodBase returnToPool = AccessTools.Method(typeof(BaseTripwire), "ReturnToPool", Type.EmptyTypes);
                MethodBase simulationUpdate = AccessTools.Method(typeof(PhysicsExtensions.Simulation), "Update", Type.EmptyTypes);
                MethodBase triggerEnter = AccessTools.Method(typeof(SyncObjectCollisionChecker), "OnTriggerEnter", new[] { typeof(Collider) });
                MethodBase dispose = AccessTools.Method(typeof(GameWorld), "Dispose", Type.EmptyTypes);
                MethodInfo collisionEnter = AccessTools.Method(typeof(BaseTripwire), "CollisionEnter", new[] { typeof(Collider) });
                MethodInfo rigidbodyGetter = AccessTools.PropertyGetter(typeof(BaseTripwire), "SyncObjectRigidbody");
                FieldInfo syncField = AccessTools.Field(typeof(BaseTripwire), "_tripwireSyncObject");
                Type sync = syncField?.FieldType;
                FieldInfo collisionCollider = sync == null ? null : AccessTools.Field(sync, "_collisionCollider");
                PropertyInfo state = sync == null ? null : AccessTools.Property(sync, "TripwireState");
                PropertyInfo placer = sync == null ? null : AccessTools.Property(sync, "PlacerPlayerId");
                FieldInfo objectId = sync == null ? null : AccessTools.Field(sync, "ObjectId");
                FieldInfo isStatic = sync == null ? null : AccessTools.Field(sync, "IsStatic");
                MethodInfo getPlayerByCollider = AccessTools.Method(typeof(GameWorld), "GetPlayerByCollider", new[] { typeof(Collider) });
                FieldInfo alivePlayers = AccessTools.Field(typeof(GameWorld), "AllAlivePlayersList");
                MethodInfo characterController = AccessTools.PropertyGetter(typeof(Player), "CharacterController");
                MethodInfo getCollider = AccessTools.Method(typeof(ICharacterController), "GetCollider", Type.EmptyTypes);
                MethodInfo support = AccessTools.Method(typeof(PhysicsExtensions.UpdateController), "SupportRigidbody", new[] { typeof(Rigidbody), typeof(float), typeof(ObjectVisibility) });
                MethodInfo unsupport = AccessTools.Method(typeof(PhysicsExtensions.UpdateController), "UnsupportRigidbody", new[] { typeof(Rigidbody) });
                FieldInfo registry = AccessTools.Field(typeof(PhysicsExtensions.UpdateController), "_rigidbodies");

                string missing = FirstMissing(
                    "BaseTripwire.Init(int, Vector3, Vector3)", init,
                    "BaseTripwire.ReturnToPool()", returnToPool,
                    "PhysicsExtensions.Simulation.Update()", simulationUpdate,
                    "SyncObjectCollisionChecker.OnTriggerEnter(Collider)", triggerEnter,
                    "EFT.GameWorld.Dispose()", dispose,
                    "BaseTripwire.CollisionEnter(Collider)", collisionEnter,
                    "BaseTripwire.SyncObjectRigidbody", rigidbodyGetter,
                    "BaseTripwire._tripwireSyncObject", syncField,
                    "TripwireSynchronizableObject._collisionCollider", collisionCollider,
                    "TripwireSynchronizableObject.TripwireState", state,
                    "TripwireSynchronizableObject.PlacerPlayerId", placer,
                    "SynchronizableObject.ObjectId", objectId,
                    "SynchronizableObject.IsStatic", isStatic,
                    "EFT.GameWorld.GetPlayerByCollider(Collider)", getPlayerByCollider,
                    "EFT.GameWorld.AllAlivePlayersList", alivePlayers,
                    "EFT.Player.CharacterController", characterController,
                    "ICharacterController.GetCollider()", getCollider,
                    "PhysicsExtensions.UpdateController.SupportRigidbody(Rigidbody, float, ObjectVisibility)", support,
                    "PhysicsExtensions.UpdateController.UnsupportRigidbody(Rigidbody)", unsupport,
                    "PhysicsExtensions.UpdateController._rigidbodies", registry);
                if (missing != null)
                {
                    Plugin.Log?.LogWarning("Tripwire release: " + missing + " not found; skipping patching (vanilla behaviour).");
                    return;
                }
                if (collisionCollider.FieldType != typeof(CapsuleCollider) || state.PropertyType != typeof(ETripwireState)
                    || objectId.FieldType != typeof(int) || isStatic.FieldType != typeof(bool))
                {
                    Plugin.Log?.LogWarning("Tripwire release: tripwire members have unexpected types ("
                        + collisionCollider.FieldType + "/" + state.PropertyType + "/" + objectId.FieldType + "/" + isStatic.FieldType
                        + "); skipping patching (vanilla behaviour).");
                    return;
                }

                syncType = sync;
                syncOf = BuildSyncAccessor(syncField);
                collisionColliderOf = BuildAccessor<CapsuleCollider>(sync, collisionCollider);
                stateOf = BuildAccessor<ETripwireState>(sync, state);
                objectIdOf = BuildAccessor<int>(sync, objectId);
                isStaticOf = BuildAccessor<bool>(sync, isStatic);
                placerOf = BuildTextAccessor(sync, placer);
                releaseSetting = releaseEnabled;
                debugLogging = debugLog;
                faulted = false;

                var instance = new Harmony(harmonyId);
                harmony = instance;
                if (!Patch(instance, "BaseTripwire.Init", init, null, nameof(InitPostfix))
                    || !Patch(instance, "BaseTripwire.ReturnToPool", returnToPool, nameof(ReturnToPoolPrefix), null)
                    || !Patch(instance, "PhysicsExtensions.Simulation.Update", simulationUpdate, nameof(UpdatePrefix), null)
                    || !Patch(instance, "SyncObjectCollisionChecker.OnTriggerEnter", triggerEnter, nameof(RelayPrefix), null)
                    || !Patch(instance, "EFT.GameWorld.Dispose", dispose, null, nameof(DisposePostfix)))
                {
                    Rollback(instance);
                    harmony = null;
                    return;
                }
                Plugin.Log?.LogInfo("Tripwire release installed (setting " + (releaseEnabled.Value ? "on" : "off") + ").");
            }
            catch (Exception error)
            {
                Plugin.Log?.LogWarning("Tripwire release: install failed, running vanilla: " + error);
                var instance = harmony;
                harmony = null;
                if (instance != null) Rollback(instance);
            }
        }

        // The whole plugin is going away: hand every held wire back to the registry before the patches disappear.
        internal static void Uninstall()
        {
            var instance = harmony;
            harmony = null;
            try { ResupportHeld(); }
            catch (Exception error) { Plugin.Log?.LogWarning("Tripwire release: restoring registry entries failed: " + error.Message); }
            ClearState();
            if (instance == null) return;
            try { instance.UnpatchSelf(); }
            catch (Exception error) { Plugin.Log?.LogWarning("Tripwire release unpatch failed: " + error.Message); }
        }

        #region Install helpers

        static string FirstMissing(params object[] pairs)
        {
            for (int i = 0; i < pairs.Length; i += 2)
                if (pairs[i + 1] == null) return (string)pairs[i];
            return null;
        }

        static Func<BaseTripwire, Component> BuildSyncAccessor(FieldInfo syncField)
        {
            ParameterExpression wire = Expression.Parameter(typeof(BaseTripwire), "wire");
            Expression body = Expression.Convert(Expression.Field(wire, syncField), typeof(Component));
            return Expression.Lambda<Func<BaseTripwire, Component>>(body, wire).Compile();
        }

        static Func<Component, T> BuildAccessor<T>(Type sync, MemberInfo member)
        {
            ParameterExpression parameter = Expression.Parameter(typeof(Component), "sync");
            Expression body = Expression.MakeMemberAccess(Expression.Convert(parameter, sync), member);
            return Expression.Lambda<Func<Component, T>>(body, parameter).Compile();
        }

        // Boxes the value (MongoID is a struct) and calls its ToString override.
        static Func<Component, string> BuildTextAccessor(Type sync, MemberInfo member)
        {
            ParameterExpression parameter = Expression.Parameter(typeof(Component), "sync");
            Expression value = Expression.MakeMemberAccess(Expression.Convert(parameter, sync), member);
            Expression body = Expression.Call(Expression.Convert(value, typeof(object)), typeof(object).GetMethod(nameof(ToString), Type.EmptyTypes));
            return Expression.Lambda<Func<Component, string>>(body, parameter).Compile();
        }

        static bool Patch(Harmony instance, string label, MethodBase target, string prefix, string postfix)
        {
            string patchName = prefix ?? postfix;
            try
            {
                instance.Patch(target,
                    prefix: prefix == null ? null : new HarmonyMethod(typeof(TripwireRelease), prefix),
                    postfix: postfix == null ? null : new HarmonyMethod(typeof(TripwireRelease), postfix));
                applied.Add(new KeyValuePair<MethodBase, MethodInfo>(target, AccessTools.Method(typeof(TripwireRelease), patchName)));
                return true;
            }
            catch (Exception error)
            {
                Plugin.Log?.LogWarning("Tripwire release: patching " + label + " failed; reverting tripwire patches (vanilla behaviour): " + error.Message);
                return false;
            }
        }

        // Removes only this class's patches. UnpatchSelf is not used here: the Harmony owner id is shared with the other release classes.
        static void Rollback(Harmony instance)
        {
            foreach (var entry in applied)
            {
                try { instance.Unpatch(entry.Key, entry.Value); }
                catch (Exception error) { Plugin.Log?.LogWarning("Tripwire release: rollback of " + entry.Value.Name + " failed: " + error.Message); }
            }
            applied.Clear();
            ClearState();
        }

        static void ClearState()
        {
            wires.Clear();
            armed.Clear();
            poses.Clear();
            heldCount = 0;
            version++;
            lastFrame = -1;
            Array.Clear(slots, 0, slots.Length);
        }

        #endregion

        #region Harmony patches

        // BaseTripwire.Init(int objectId, Vector3 position, Vector3 rotation): vanilla has just registered the rigidbody. Track
        // the wire and, with the setting on, remove every registry entry for it (Support never deduplicates and Unsupport
        // removes only the first match). Tracked even with the setting off, so turning it on later can release the wire.
        static void InitPostfix(BaseTripwire __instance)
        {
            try
            {
                if (faulted) return;
                Untrack(__instance);

                Component sync = syncOf(__instance);
                CapsuleCollider capsule = sync == null ? null : collisionColliderOf(sync);
                Rigidbody body = __instance.SyncObjectRigidbody;
                if (capsule == null || body == null)
                    throw new InvalidOperationException("tripwire is missing its sync object, trigger capsule or rigidbody");

                // The relay lives on the component next to _collisionCollider (prefab child ObjectCollision).
                SyncObjectCollisionChecker checker = capsule.GetComponent<SyncObjectCollisionChecker>();
                if (checker == null) checker = capsule.GetComponentInParent<SyncObjectCollisionChecker>();
                if (checker == null)
                    throw new InvalidOperationException("tripwire trigger capsule has no SyncObjectCollisionChecker; leaving the wire to vanilla");

                var wire = new Wire
                {
                    Logic = __instance,
                    Sync = sync,
                    Body = body,
                    Capsule = capsule,
                    ArmedObject = capsule.gameObject,
                    Checker = checker
                };
                wires.Add(wire);
                version++;
                if (releaseSetting.Value) Release(wire);
            }
            catch (Exception error) { Failed(SiteInit, "Init postfix", error); }
        }

        // BaseTripwire.ReturnToPool: vanilla deactivates the object and calls SupportRigidbody once more on the inactive body,
        // which CheckRigidbodiesDone purges by itself.
        static void ReturnToPoolPrefix(BaseTripwire __instance)
        {
            try { Untrack(__instance); }
            catch (Exception error) { Failed(SiteReturn, "ReturnToPool prefix", error); }
        }

        // Runs once per frame (PhysicsExtensions.Update -> Simulation.Update), where vanilla would run Physics.Simulate and
        // deliver its trigger callbacks. The detector's frame work lives in Detect. Priority.First runs it before
        // SimulateTimer's prefix on the same method, so the report's step timing does not include the detector.
        [HarmonyPriority(Priority.First)]
        static void UpdatePrefix()
        {
            try
            {
                if (faulted || wires.Count == 0) return;
                int frame = Time.frameCount;
                if (frame == lastFrame) return;
                lastFrame = frame;

                if (!releaseSetting.Value)
                {
                    // Setting off live: vanilla from now on (re-registered wires hold the step again, the relay is not skipped).
                    if (heldCount > 0) ResupportHeld();
                    return;
                }
                if (heldCount < wires.Count) ReleaseUnheld();
                if (heldCount > 0) Detect(frame);
            }
            catch (Exception error)
            {
                // A repeating exception here would cost every frame: degrade to vanilla for the rest of the session.
                Failed(SiteUpdate, "Simulation.Update prefix", error);
                faulted = true;
                try { ResupportHeld(); }
                catch (Exception restoreError) { Plugin.Log?.LogWarning("Tripwire release: restoring registry entries failed: " + restoreError.Message); }
            }
        }

        // SyncObjectCollisionChecker.OnTriggerEnter(Collider colliderComponent). Skipping prefix: intentionally replaces the
        // vanilla relay for player and bot colliders on wires this class detects for, so a step that runs for another holder
        // (a corpse, a grenade) cannot roll the bot ignore chance or trigger the wire a second time next to the frame detector.
        // Non-player colliders pass through to vanilla. Disabling the component would not work: Unity delivers trigger
        // messages to disabled behaviours.
        static bool RelayPrefix(SyncObjectCollisionChecker __instance, Collider colliderComponent)
        {
            try
            {
                if (faulted || heldCount == 0) return true;
                Wire wire = FindByChecker(__instance);
                if (wire == null || !wire.Held) return true;
                // Skip only colliders the frame detector tested this frame; anything it could not test (disabled, unsupported
                // collider kind, not in AllAlivePlayersList) stays with the vanilla relay.
                if (!poses.TryGetValue(colliderComponent.GetInstanceID(), out Pose pose) || pose.Frame != lastFrame) return true;
                if (!Singleton<GameWorld>.Instantiated) return true;
                Player player = Singleton<GameWorld>.Instance.GetPlayerByCollider(colliderComponent);
                if (player == null) return true;

                if (LogDue(ref nextRelayLog, ref relaySuppressed, out int suppressed))
                    Plugin.Log?.LogInfo("Tripwire relay skipped id=" + IdOf(wire) + " player=" + player.ProfileId + " ai=" + player.IsAI
                        + " (frame detector owns player contacts)" + SuppressedText(suppressed));
                return false;
            }
            catch (Exception error)
            {
                Failed(SiteRelay, "OnTriggerEnter prefix", error);
                return true;
            }
        }

        // GameWorld.Dispose (ClientGameWorld.Dispose calls base.Dispose): the raid is over, forget every wire. The game tears
        // down the pooled objects and the registry itself.
        static void DisposePostfix()
        {
            try { ClearState(); }
            catch (Exception error) { Failed(SiteDispose, "GameWorld.Dispose postfix", error); }
        }

        #endregion

        #region Tracking and registry

        static void Untrack(BaseTripwire logic)
        {
            for (int i = 0; i < wires.Count; i++)
            {
                if (!ReferenceEquals(wires[i].Logic, logic)) continue;
                RemoveAt(i);
                return;
            }
        }

        static void RemoveAt(int index)
        {
            if (wires[index].Held) heldCount--;
            wires.RemoveAt(index);
            version++;
        }

        static Wire FindByChecker(SyncObjectCollisionChecker checker)
        {
            for (int i = 0; i < wires.Count; i++)
                if (ReferenceEquals(wires[i].Checker, checker)) return wires[i];
            return null;
        }

        // Drops every registry entry that references the wire rigidbody and makes this class the wire's detector.
        static void Release(Wire wire)
        {
            var registry = PhysicsExtensions.UpdateController._rigidbodies;
            int matches = 0;
            for (int i = 0; i < registry.Count; i++)
                if (registry[i].rigidbody == wire.Body) matches++;
            for (int i = 0; i < matches; i++)
                PhysicsExtensions.UpdateController.UnsupportRigidbody(wire.Body);

            wire.Held = true;
            heldCount++;
        }

        // Setting turned on while wires are tracked (planted while it was off, or re-enabled after being off).
        static void ReleaseUnheld()
        {
            for (int i = 0; i < wires.Count; i++)
            {
                if (!wires[i].Held) Release(wires[i]);
            }
        }

        // Back to vanilla: armed wires register again, once (what BaseTripwire.Init did). Hidden wires (triggered, disarmed,
        // inert) stay out: nothing needs them, and the game would purge them on pool return anyway.
        static void ResupportHeld()
        {
            for (int i = 0; i < wires.Count; i++)
            {
                Wire wire = wires[i];
                if (!wire.Held) continue;
                wire.Held = false;
                wire.Inside.Clear();
                wire.GeometryValid = false;
                if (wire.Body != null && wire.ArmedObject != null && wire.ArmedObject.activeInHierarchy)
                    PhysicsExtensions.UpdateController.SupportRigidbody(wire.Body);
            }
            heldCount = 0;
        }

        #endregion

        #region Detector

        // Never drives a hidden wire: HideContent deactivates ObjectCollision when the wire triggers, is disarmed or goes
        // inert, and a second SetGrenadeState(true) on such a wire would drop a duplicate grenade.
        static void Detect(int frame)
        {
            armed.Clear();
            for (int i = 0; i < wires.Count; i++)
            {
                Wire wire = wires[i];
                if (wire.ArmedObject == null)
                {
                    // Destroyed with its pool (not returned through ReturnToPool).
                    RemoveAt(i--);
                    continue;
                }
                if (!wire.Held) continue;
                if (!wire.ArmedObject.activeInHierarchy)
                {
                    wire.GeometryValid = false;
                    if (wire.Inside.Count > 0) wire.Inside.Clear();
                    continue;
                }
                if (!wire.GeometryValid) ComputeWireGeometry(wire);
                armed.Add(wire);
            }
            if (armed.Count == 0 || !Singleton<GameWorld>.Instantiated) return;

            GameWorld world = Singleton<GameWorld>.Instance;
            if (world == null) return;

            int slotCount = BuildSlots(world, frame);
            for (int i = 0; i < armed.Count; i++)
            {
                if (!TestWire(armed[i], slotCount, frame)) break;
            }
            PrunePoses(frame);
        }

        // The trigger capsule is static once SetupStakes has placed and activated it, so its segment is computed on the first
        // armed frame and kept until the wire is seen hidden.
        static void ComputeWireGeometry(Wire wire)
        {
            CapsuleSegment(wire.Capsule, out wire.A, out wire.B, out wire.Radius);
            wire.Mid = (wire.A + wire.B) * 0.5f;
            wire.Reach = (wire.B - wire.A).magnitude * 0.5f + wire.Radius;
            wire.GeometryValid = true;
        }

        static int BuildSlots(GameWorld world, int frame)
        {
            List<Player> players = world.AllAlivePlayersList;
            int count = 0;
            for (int i = 0; i < players.Count; i++)
            {
                Player player = players[i];
                if (player == null) continue;
                ICharacterController controller = player.CharacterController;
                if (controller == null) continue;
                Collider collider = controller.GetCollider();
                if (collider == null) continue;
                // PhysX reports nothing for a disabled collider (BTR passengers, bots created but not yet activated).
                if (!collider.enabled || !collider.gameObject.activeInHierarchy) continue;
                if (!TryCapsule(collider, out Vector3 a, out Vector3 b, out float radius)) continue;

                if (count == slots.Length) Array.Resize(ref slots, slots.Length * 2);
                int id = collider.GetInstanceID();
                ref Slot slot = ref slots[count++];
                slot.Player = player;
                slot.Collider = collider;
                slot.Id = id;
                slot.A = a;
                slot.B = b;
                slot.Mid = (a + b) * 0.5f;
                slot.Radius = radius;
                slot.Reach = (b - a).magnitude * 0.5f + radius;
                slot.HasPrev = false;
                slot.Moved = 0f;
                slot.Steps = 1;

                if (poses.TryGetValue(id, out Pose previous) && previous.Frame == frame - 1)
                {
                    float moved = Mathf.Sqrt(Mathf.Max((a - previous.A).sqrMagnitude, (b - previous.B).sqrMagnitude));
                    if (moved <= MaxSweepDistance)
                    {
                        slot.HasPrev = true;
                        slot.PrevA = previous.A;
                        slot.PrevB = previous.B;
                        slot.Moved = moved;
                        if (moved > radius) slot.Steps = Mathf.Min(MaxSweepSteps, Mathf.CeilToInt(moved / radius));
                    }
                }
                poses[id] = new Pose { Frame = frame, A = a, B = b };
            }
            return count;
        }

        // Returns false when the wire list changed while vanilla code ran (the frame's remaining work is abandoned).
        static bool TestWire(Wire wire, int slotCount, int frame)
        {
            List<int> inside = wire.Inside;
            // Colliders of players that are no longer alive or listed can not touch anything.
            for (int i = inside.Count - 1; i >= 0; i--)
            {
                if (!poses.TryGetValue(inside[i], out Pose pose) || pose.Frame != frame) inside.RemoveAt(i);
            }

            int startVersion = version;
            for (int s = 0; s < slotCount; s++)
            {
                ref Slot slot = ref slots[s];
                int index = inside.IndexOf(slot.Id);

                // Bounding spheres of both segments, widened by the exit margin and the distance moved since last frame.
                float reach = wire.Reach + slot.Reach + ExitHysteresis + (slot.HasPrev ? slot.Moved : 0f);
                if ((wire.Mid - slot.Mid).sqrMagnitude > reach * reach)
                {
                    if (index >= 0) inside.RemoveAt(index);
                    continue;
                }

                float limit = wire.Radius + slot.Radius;
                float distanceSquared = SegmentDistanceSquared(wire.A, wire.B, slot.A, slot.B);
                if (index >= 0)
                {
                    float exit = limit + ExitHysteresis;
                    if (distanceSquared > exit * exit) inside.RemoveAt(index);
                    continue;
                }

                bool overlapping = distanceSquared <= limit * limit;
                if (!overlapping && !(slot.HasPrev && slot.Steps > 1 && SweepHits(wire, ref slot, limit))) continue;

                // A capsule already touching when the wire arms counts as an enter, as PhysX reports on its first step.
                if (overlapping) inside.Add(slot.Id);
                Enter(wire, ref slot);
                if (version != startVersion) return false;
                if (!wire.ArmedObject.activeInHierarchy) break;
            }
            return true;
        }

        // Intermediate capsule poses between last frame and this frame, so a fast mover cannot skip a 6 cm wire.
        static bool SweepHits(Wire wire, ref Slot slot, float limit)
        {
            float limitSquared = limit * limit;
            for (int k = 1; k < slot.Steps; k++)
            {
                float t = (float)k / slot.Steps;
                Vector3 a = Vector3.Lerp(slot.PrevA, slot.A, t);
                Vector3 b = Vector3.Lerp(slot.PrevB, slot.B, t);
                if (SegmentDistanceSquared(wire.A, wire.B, a, b) <= limitSquared) return true;
            }
            return false;
        }

        // Vanilla's own filter and consequences: BaseTripwire.CollisionEnter rejects TripwireState None and triggers, applies
        // TripwireCheckLayerMask and the bot ignore roll, then calls the ISyncEnvironment.TriggerTripwire virtual.
        static void Enter(Wire wire, ref Slot slot)
        {
            bool log = LogDue(ref nextEnterLog, ref enterSuppressed, out int suppressed);
            ETripwireState before = log ? stateOf(wire.Sync) : default;
            try { wire.Logic.CollisionEnter(slot.Collider); }
            catch (Exception error) { Failed(SiteEnter, "BaseTripwire.CollisionEnter", error); }
            if (log)
                Plugin.Log?.LogInfo("Tripwire walk-in id=" + IdOf(wire) + " player=" + slot.Player.ProfileId + " ai=" + slot.Player.IsAI
                    + " state=" + before + "->" + stateOf(wire.Sync) + " (frame detector)" + SuppressedText(suppressed));
        }

        static void PrunePoses(int frame)
        {
            if (frame - lastPruneFrame < PosePruneFrames) return;
            lastPruneFrame = frame;
            pruneScratch.Clear();
            foreach (var pair in poses)
            {
                if (pair.Value.Frame != frame) pruneScratch.Add(pair.Key);
            }
            for (int i = 0; i < pruneScratch.Count; i++) poses.Remove(pruneScratch[i]);
        }

        #endregion

        #region Geometry

        // World-space capsule of a player collider: both runtime collider kinds registered in PlayersColliders. Computed from
        // fields and the transform, never from Collider.bounds, which comes from PhysX and lags without SyncTransforms.
        static bool TryCapsule(Collider collider, out Vector3 a, out Vector3 b, out float radius)
        {
            CapsuleCollider capsule = collider as CapsuleCollider;
            if (capsule != null)
            {
                CapsuleSegment(capsule, out a, out b, out radius);
                return true;
            }

            // PlayerCharacterController wraps Unity's CharacterController: an upright capsule that ignores rotation.
            UnityEngine.CharacterController controller = collider as UnityEngine.CharacterController;
            if (controller != null)
            {
                Transform transform = controller.transform;
                Vector3 scale = transform.lossyScale;
                radius = controller.radius * Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.z));
                float half = Mathf.Max(0f, controller.height * Mathf.Abs(scale.y) * 0.5f - radius);
                Vector3 center = transform.position + Vector3.Scale(controller.center, scale);
                a = center - Vector3.up * half;
                b = center + Vector3.up * half;
                return true;
            }

            if ((failedSites & SiteCollider) == 0)
                Failed(SiteCollider, "player collider type", new NotSupportedException("unsupported player collider " + collider.GetType().FullName + "; such players are not tested against tripwires"));
            a = b = default;
            radius = 0f;
            return false;
        }

        // Unity scales a capsule's radius by the larger of the two non-axis scales and its height by the axis scale. The
        // height includes both end caps, so the segment ends are half the height minus the radius from the centre.
        static void CapsuleSegment(CapsuleCollider capsule, out Vector3 a, out Vector3 b, out float radius)
        {
            Transform transform = capsule.transform;
            Vector3 scale = transform.lossyScale;
            Vector3 axis;
            float axisScale, crossScale;
            switch (capsule.direction)
            {
                case 0:
                    axis = transform.right;
                    axisScale = Mathf.Abs(scale.x);
                    crossScale = Mathf.Max(Mathf.Abs(scale.y), Mathf.Abs(scale.z));
                    break;
                case 1:
                    axis = transform.up;
                    axisScale = Mathf.Abs(scale.y);
                    crossScale = Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.z));
                    break;
                default:
                    axis = transform.forward;
                    axisScale = Mathf.Abs(scale.z);
                    crossScale = Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.y));
                    break;
            }
            radius = capsule.radius * crossScale;
            float half = Mathf.Max(0f, capsule.height * axisScale * 0.5f - radius);
            Vector3 center = transform.TransformPoint(capsule.center);
            a = center - axis * half;
            b = center + axis * half;
        }

        // Squared distance between segments p1-q1 and p2-q2 (closest points of two segments, Ericson, Real-Time Collision Detection 5.1.9).
        static float SegmentDistanceSquared(Vector3 p1, Vector3 q1, Vector3 p2, Vector3 q2)
        {
            const float Epsilon = 1e-8f;
            Vector3 d1 = q1 - p1;
            Vector3 d2 = q2 - p2;
            Vector3 r = p1 - p2;
            float a = Vector3.Dot(d1, d1);
            float e = Vector3.Dot(d2, d2);
            float f = Vector3.Dot(d2, r);
            float s, t;

            if (a <= Epsilon && e <= Epsilon) return r.sqrMagnitude;
            if (a <= Epsilon)
            {
                s = 0f;
                t = Mathf.Clamp01(f / e);
            }
            else
            {
                float c = Vector3.Dot(d1, r);
                if (e <= Epsilon)
                {
                    t = 0f;
                    s = Mathf.Clamp01(-c / a);
                }
                else
                {
                    float b = Vector3.Dot(d1, d2);
                    float denominator = a * e - b * b;
                    s = denominator > 1e-7f * a * e ? Mathf.Clamp01((b * f - c * e) / denominator) : 0f;
                    t = (b * s + f) / e;
                    if (t < 0f)
                    {
                        t = 0f;
                        s = Mathf.Clamp01(-c / a);
                    }
                    else if (t > 1f)
                    {
                        t = 1f;
                        s = Mathf.Clamp01((b - c) / a);
                    }
                }
            }
            return ((p1 + d1 * s) - (p2 + d2 * t)).sqrMagnitude;
        }

        #endregion

        #region Diagnostics

        // One line for a SimulationOwners registry row, or null when the object is not a tripwire. Report only: allocates freely.
        internal static string DescribeEntry(GameObject go)
        {
            if (syncType == null || go == null) return null;
            Component sync = go.GetComponentInParent(syncType, true);
            if (sync == null) return null;

            Wire wire = null;
            for (int i = 0; i < wires.Count; i++)
            {
                if (!ReferenceEquals(wires[i].Sync, sync)) continue;
                wire = wires[i];
                break;
            }

            var text = new StringBuilder("tripwire id=");
            text.Append(objectIdOf(sync).ToString(Inv))
                .Append(" TripwireState=").Append(stateOf(sync))
                .Append(" PlacerPlayerId=").Append(placerOf(sync))
                .Append(" IsStatic=").Append(isStaticOf(sync))
                .Append(" release=").Append(wire == null ? "untracked" : wire.Held ? "held" : "vanilla");
            return text.ToString();
        }

        // Rate limit for the debug lines: at most one per interval and kind, with the number of dropped lines in the next one.
        static bool LogDue(ref float nextTime, ref int suppressed, out int suppressedBefore)
        {
            suppressedBefore = 0;
            if (debugLogging == null || !debugLogging.Value) return false;
            float now = Time.realtimeSinceStartup;
            if (now < nextTime)
            {
                suppressed++;
                return false;
            }
            nextTime = now + LogIntervalSeconds;
            suppressedBefore = suppressed;
            suppressed = 0;
            return true;
        }

        static string SuppressedText(int suppressed) => suppressed > 0 ? " (+" + suppressed.ToString(Inv) + " similar lines suppressed)" : string.Empty;

        static string IdOf(Wire wire)
        {
            try { return objectIdOf(wire.Sync).ToString(Inv); }
            catch (Exception) { return "?"; }
        }

        static void Failed(int site, string where, Exception error)
        {
            if ((failedSites & site) != 0) return;
            failedSites |= site;
            Plugin.Log?.LogWarning("Tripwire release: " + where + " failed (logged once): " + error);
        }

        #endregion
    }
}
