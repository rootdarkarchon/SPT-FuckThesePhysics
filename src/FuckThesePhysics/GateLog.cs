using UnityEngine;

namespace FuckThesePhysics
{
    // Debug/LogPhysicsGate: logs PhysicsExtensions.Simulation.UpdateEnabled (the flag that lets EFT run Physics.Simulate)
    // together with the PhysicsExtensions.UpdateController._rigidbodies count. Logs only on the first Tick after Reset
    // and on each UpdateEnabled change; allocation-free while nothing changes. Read-only.
    internal static class GateLog
    {
        static bool haveLast;
        static bool lastEnabled;

        internal static void Tick()
        {
            bool enabled = PhysicsExtensions.Simulation.UpdateEnabled;
            if (haveLast && enabled == lastEnabled) return;
            haveLast = true;
            lastEnabled = enabled;

            var entries = PhysicsExtensions.UpdateController._rigidbodies;
            int registry = entries == null ? 0 : entries.Count;
            Plugin.Log.LogInfo("physics gate UpdateEnabled=" + (enabled ? "True" : "False") + " registry=" + registry + " frame=" + Time.frameCount);
        }

        internal static void Reset() => haveLast = false;
    }
}
