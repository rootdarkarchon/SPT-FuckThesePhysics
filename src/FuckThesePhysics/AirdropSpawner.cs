using System;
using Comfort.Common;
using EFT;
using UnityEngine;

namespace FuckThesePhysics
{
    // Debug/SpawnAirdropKey: asks EFT to start a real airdrop (plane -> /client/airdrop/loot -> drop -> landing) at the
    // drop point nearest the local player. Calls the public virtual GameWorld.InitAirdrop only; never touches the
    // ServerAirdropManager timers or counters. Every member used here is public in Assembly-CSharp (SPT 4.1), so no reflection.
    internal static class AirdropSpawner
    {
        // Main thread only (called from Plugin.Update). Never throws; logs the reason on every guard failure.
        internal static void Spawn()
        {
            try
            {
                SpawnCore();
            }
            catch (Exception error)
            {
                Plugin.Log.LogError("airdrop spawn failed: " + error);
            }
        }

        static void SpawnCore()
        {
            if (!Singleton<GameWorld>.Instantiated)
            {
                Plugin.Log.LogWarning("airdrop spawn skipped: not in raid (no GameWorld)");
                return;
            }

            var world = Singleton<GameWorld>.Instance;
            if (world == null)
            {
                Plugin.Log.LogWarning("airdrop spawn skipped: not in raid (GameWorld instance is null)");
                return;
            }

            // FikaHostGameWorld derives from ClientGameWorld, so this covers stock offline and the Fika host.
            if (!(world is ClientGameWorld clientWorld))
            {
                Plugin.Log.LogWarning("airdrop spawn skipped: not host (Fika client or non-ClientGameWorld: " + world.GetType().FullName + ")");
                return;
            }

            var processor = clientWorld.ClientSynchronizableObjectLogicProcessor;
            var manager = processor == null ? null : processor.ServerAirdropManager;
            if (manager == null)
            {
                Plugin.Log.LogWarning("airdrop spawn skipped: not host (no ServerAirdropManager; Fika client)");
                return;
            }

            if (!manager.IsInited)
            {
                Plugin.Log.LogWarning("airdrop spawn skipped: airdrop manager not inited");
                return;
            }

            if (manager.CachedAirdropParameters == null)
            {
                Plugin.Log.LogWarning("airdrop spawn skipped: no cached airdrop parameters");
                return;
            }

            // ClientGameWorld.TakeAirdropPoint -> ServerAirdropManager.TakeAirdropPoint reads _airdropPoints only
            // (deactivated points are re-activated inside it once the active list empties).
            var points = manager._airdropPoints;
            if (points == null || points.Count == 0)
            {
                int deactivated = manager._deactivatedAirdropPoints == null ? 0 : manager._deactivatedAirdropPoints.Count;
                Plugin.Log.LogWarning("airdrop spawn skipped: no drop points on this map (active=0 deactivated=" + deactivated + ")");
                return;
            }

            var player = world.MainPlayer;
            if (player == null)
            {
                Plugin.Log.LogWarning("airdrop spawn skipped: no main player");
                return;
            }

            Vector3 position = player.Position;
            int available = points.Count;
            try
            {
                world.InitAirdrop(null, true, position);
            }
            catch (Exception error)
            {
                Plugin.Log.LogError("airdrop spawn failed in GameWorld.InitAirdrop: " + error);
                return;
            }

            Plugin.Log.LogInfo("airdrop spawn requested at " + position.ToString("F1") + " (drop points available before take=" + available + ")");
        }
    }
}
