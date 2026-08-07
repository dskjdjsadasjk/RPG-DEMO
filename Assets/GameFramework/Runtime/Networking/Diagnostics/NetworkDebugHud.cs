using RPGDemo.GameFramework.Networking.Bootstrap;
using RPGDemo.GameFramework.Networking.Identity;
using RPGDemo.GameFramework.Networking.Replication;
using UnityEngine;

namespace RPGDemo.GameFramework.Networking.Diagnostics
{
    public sealed class NetworkDebugHud : MonoBehaviour
    {
        [SerializeField] private bool visible = true;

        private void OnGUI()
        {
            if (!visible)
            {
                return;
            }

            NetworkBootstrap bootstrap = NetworkBootstrap.Instance;
            GameNetDriver driver = bootstrap != null ? bootstrap.NetDriver : null;

            GUILayout.BeginArea(new Rect(12f, 12f, 520f, 500f), GUI.skin.box);
            GUILayout.Label("RPG Demo Network");
            if (driver == null || !driver.IsRunning)
            {
                GUILayout.Label("Network: not started");
                GUILayout.EndArea();
                return;
            }

            GUILayout.Label(
                $"Mode: {driver.Mode}   Connections: {driver.Connections.Count}   "
                + $"Objects: {driver.ObjectRegistry.Count}   ServerTick: {driver.ServerTick}");

            foreach (GameNetConnection connection in driver.Connections)
            {
                GUILayout.Label(
                    $"Connection {connection.ConnectionId}: {connection.State} "
                    + $"({connection.RemoteEndpoint})");
            }

            foreach (NetworkIdentity identity in driver.ObjectRegistry.Objects)
            {
                if (identity == null)
                {
                    continue;
                }

                ReplicatedHealth health = identity.GetComponent<ReplicatedHealth>();
                string healthText = health != null ? $", Health={health.Health}" : string.Empty;
                CharacterNetworkMovement networkMovement
                    = identity.GetComponent<CharacterNetworkMovement>();
                string movementText = string.Empty;
                if (networkMovement != null)
                {
                    movementText = identity.Role == NetworkRole.AutonomousProxy
                        ? $", Ack={networkMovement.LastAckedMoveSequence}, "
                            + $"Pending={networkMovement.PendingMoveCount}, "
                            + $"Corrections={networkMovement.CorrectionCount}"
                        : identity.Role == NetworkRole.SimulatedProxy
                            ? $", Snapshots={networkMovement.BufferedSnapshotCount}"
                            : string.Empty;
                }

                Vector3 position = identity.transform.position;
                GUILayout.Label(
                    $"NetId={identity.NetId}, PrefabId={identity.PrefabId}, "
                    + $"Role={identity.Role}, Owner={identity.OwnerConnectionId}{healthText}{movementText}\n"
                    + $"  Position=({position.x:F2}, {position.y:F2}, {position.z:F2})");
            }

            GUILayout.EndArea();
        }
    }
}
