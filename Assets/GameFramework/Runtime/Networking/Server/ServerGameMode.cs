using System;
using System.Collections.Generic;
using RPGDemo.GameFramework.Networking.Identity;
using RPGDemo.GameFramework.Networking.Protocol;
using RPGDemo.GameFramework.Networking.Replication;
using UnityEngine;
using Object = UnityEngine.Object;

namespace RPGDemo.GameFramework.Networking.Server
{
    public sealed class ServerGameMode : IDisposable
    {
        private readonly GameNetDriver netDriver;
        private readonly ushort defaultPlayerPrefabId;
        private readonly int maxPlayers;
        private readonly Dictionary<uint, ServerConnectionContext> players
            = new Dictionary<uint, ServerConnectionContext>();
        private readonly List<ServerConnectionContext> cleanupPlayers
            = new List<ServerConnectionContext>();

        public ServerGameMode(
            GameNetDriver netDriver,
            ushort defaultPlayerPrefabId,
            int maxPlayers = 16)
        {
            this.netDriver = netDriver ?? throw new ArgumentNullException(nameof(netDriver));
            this.defaultPlayerPrefabId = defaultPlayerPrefabId;
            this.maxPlayers = Mathf.Max(1, maxPlayers);

            netDriver.ConnectionReady += HandleConnectionReady;
            netDriver.ConnectionClosed += HandleConnectionClosed;
        }

        public IReadOnlyCollection<ServerConnectionContext> Players => players.Values;

        public bool TryGetPlayer(uint connectionId, out ServerConnectionContext context)
        {
            return players.TryGetValue(connectionId, out context);
        }

        public void Dispose()
        {
            netDriver.ConnectionReady -= HandleConnectionReady;
            netDriver.ConnectionClosed -= HandleConnectionClosed;

            cleanupPlayers.Clear();
            cleanupPlayers.AddRange(players.Values);
            for (int i = 0; i < cleanupPlayers.Count; i++)
            {
                Logout(cleanupPlayers[i], ActorChannelCloseReason.ServerShutdown);
            }

            cleanupPlayers.Clear();
            players.Clear();
        }

        private void HandleConnectionReady(GameNetConnection connection)
        {
            if (connection == null || connection.ConnectionId == 0 || players.ContainsKey(connection.ConnectionId))
            {
                return;
            }

            if (players.Count >= maxPlayers)
            {
                netDriver.DisconnectConnection(connection, $"Server is full ({maxPlayers} players)");
                return;
            }

            GameObject controllerObject = new GameObject($"ServerPlayer_{connection.ConnectionId}");
            ServerPlayerController controller = controllerObject.AddComponent<ServerPlayerController>();
            PlayerState playerState = controllerObject.AddComponent<PlayerState>();
            controller.SetPlayerState(playerState);
            connection.SetOwningController(controller);

            ServerConnectionContext context = new ServerConnectionContext(
                connection,
                controller,
                playerState);
            players.Add(connection.ConnectionId, context);

            if (defaultPlayerPrefabId == 0)
            {
                Debug.LogWarning(
                    $"[Net][DS] Connection {connection.ConnectionId} logged in without a Pawn: "
                    + "DefaultPlayerPrefabId is 0.");
                return;
            }

            try
            {
                NetworkPlayerStart.TrySelect(connection.ConnectionId, out Pose spawnPose);
                NetworkIdentity pawnIdentity = netDriver.SpawnServerObject(
                    defaultPlayerPrefabId,
                    spawnPose.position,
                    spawnPose.rotation,
                    connection.ConnectionId,
                    authorityEpoch: 1);

                ReplicatedHealth replicatedHealth = pawnIdentity.GetComponent<ReplicatedHealth>();
                if (replicatedHealth != null)
                {
                    replicatedHealth.SetHealth(100 + (int)connection.ConnectionId);
                }

                Pawn pawn = pawnIdentity.GetComponent<Pawn>();
                if (pawn == null)
                {
                    netDriver.DespawnServerObject(
                        pawnIdentity.NetId,
                        ActorChannelCloseReason.ProtocolError);
                    throw new InvalidOperationException(
                        $"Default player PrefabId {defaultPlayerPrefabId} has no Pawn component.");
                }

                context.PawnIdentity = pawnIdentity;
                PossessionResult result = controller.Possess(pawn);
                if (result != PossessionResult.Succeeded)
                {
                    throw new InvalidOperationException($"Server Possess failed with {result}.");
                }

                Debug.Log(
                    $"[Net][DS] Player {connection.ConnectionId} logged in, "
                    + $"Pawn NetId={pawnIdentity.NetId} possessed.");
            }
            catch (Exception exception)
            {
                Debug.LogError($"[Net][DS] Player spawn failed: {exception.Message}");
                netDriver.DisconnectConnection(connection, "Server failed to spawn player");
            }
        }

        private void HandleConnectionClosed(GameNetConnection connection, string reason)
        {
            if (connection != null
                && players.TryGetValue(connection.ConnectionId, out ServerConnectionContext context))
            {
                Logout(context, ActorChannelCloseReason.OwnerDisconnected);
            }
        }

        private void Logout(ServerConnectionContext context, ActorChannelCloseReason reason)
        {
            if (context == null)
            {
                return;
            }

            players.Remove(context.Connection.ConnectionId);
            context.Connection.SetOwningController(null);

            if (context.PawnIdentity != null && context.PawnIdentity.IsSpawned)
            {
                netDriver.DespawnServerObject(context.PawnIdentity.NetId, reason);
            }

            if (context.PlayerController != null)
            {
                context.PlayerController.UnPossess();
                DestroyObject(context.PlayerController.gameObject);
            }
        }

        private static void DestroyObject(GameObject target)
        {
            if (target == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                Object.Destroy(target);
            }
            else
            {
                Object.DestroyImmediate(target);
            }
        }
    }
}
