using System;
using System.Collections.Generic;
using RPGDemo.GameFramework.Networking.Identity;
using RPGDemo.GameFramework.Networking.Protocol;
using RPGDemo.GameFramework.Networking.Replication;
using RPGDemo.GameFramework.Networking.Transport;
using UnityEngine;

namespace RPGDemo.GameFramework.Networking
{
    public sealed class GameNetDriver : IDisposable
    {
        public const ushort DefaultPort = 7777;
        public const ushort DefaultTickRate = 60;
        public const ushort DefaultStateReplicationRate = 5;
        public const ushort DefaultMovementSnapshotRate = 20;
        public const float HandshakeTimeoutSeconds = 10f;

        private readonly INetworkTransport transport;
        private readonly NetworkPrefabRegistry prefabRegistry;
        private readonly NetworkObjectRegistry objectRegistry = new NetworkObjectRegistry();
        private readonly Dictionary<int, GameNetConnection> connections = new Dictionary<int, GameNetConnection>();
        private readonly List<TransportEvent> transportEvents = new List<TransportEvent>(32);
        private readonly List<GameNetConnection> timedOutConnections = new List<GameNetConnection>();

        private uint nextConnectionId = 1;
        private uint serverTick;
        private double serverTickAccumulator;
        private string clientDisplayName = "Player";

        public GameNetDriver(
            INetworkTransport transport = null,
            NetworkPrefabRegistry prefabRegistry = null)
        {
            this.transport = transport ?? new UtpTransport();
            this.prefabRegistry = prefabRegistry;
        }

        public NetworkProcessMode Mode { get; private set; }
        public bool IsRunning => Mode != NetworkProcessMode.None && transport.IsCreated;
        public IReadOnlyCollection<GameNetConnection> Connections => connections.Values;
        public GameNetConnection ServerConnection { get; private set; }
        public uint ServerTick => serverTick;
        public NetworkObjectRegistry ObjectRegistry => objectRegistry;
        public NetworkPrefabRegistry PrefabRegistry => prefabRegistry;

        public event Action<GameNetConnection> ConnectionOpened;
        public event Action<GameNetConnection> ConnectionReady;
        public event Action<GameNetConnection, string> ConnectionClosed;
        public event Action<NetworkIdentity> NetworkObjectSpawned;
        public event Action<uint, ActorChannelCloseReason> NetworkObjectDespawned;

        public void StartDedicatedServer(ushort port = DefaultPort)
        {
            EnsureNotRunning();
            transport.StartServer(port);
            Mode = NetworkProcessMode.DedicatedServer;
            Debug.Log($"[Net][DS] Listening on UDP 0.0.0.0:{port} (protocol {ConnectionProtocol.ProtocolVersion}).");
        }

        public void StartClient(string address, ushort port = DefaultPort, string displayName = "Player")
        {
            EnsureNotRunning();

            clientDisplayName = string.IsNullOrWhiteSpace(displayName) ? "Player" : displayName.Trim();
            TransportConnectionHandle handle = transport.StartClient(address, port);
            Mode = NetworkProcessMode.Client;

            ServerConnection = new GameNetConnection(
                handle,
                NetConnectionState.Connecting,
                $"{address}:{port}");
            connections.Add(handle.Value, ServerConnection);

            Debug.Log($"[Net][Client] Connecting to {address}:{port} as '{clientDisplayName}'.");
        }

        public void Tick(float unscaledDeltaTime)
        {
            if (!IsRunning)
            {
                return;
            }

            transportEvents.Clear();
            transport.PollEvents(transportEvents);

            for (int i = 0; i < transportEvents.Count; i++)
            {
                ProcessTransportEvent(transportEvents[i]);
            }

            float safeDeltaTime = Mathf.Max(0f, unscaledDeltaTime);
            TickHandshakeTimeouts(safeDeltaTime);
            AdvanceServerClock(safeDeltaTime);
            ReplicateActorStates();
            ReplicateCharacterSnapshots();
            transport.Flush();
        }

        public bool SendCharacterMove(
            NetworkIdentity identity,
            uint sequence,
            uint clientTick,
            float deltaTime,
            Vector3 worldInput,
            float controlYaw)
        {
            if (Mode != NetworkProcessMode.Client
                || ServerConnection == null
                || !ServerConnection.IsReady
                || identity == null
                || !identity.IsSpawned
                || identity.Role != NetworkRole.AutonomousProxy)
            {
                return false;
            }

            byte[] packet = CharacterMovementProtocol.CreateClientMove(
                identity.NetId,
                identity.AuthorityEpoch,
                sequence,
                clientTick,
                deltaTime,
                worldInput,
                controlYaw);
            return SendUnreliable(ServerConnection, packet);
        }

        public void DisconnectConnection(GameNetConnection connection, string reason)
        {
            if (connection == null
                || !connections.TryGetValue(connection.TransportHandle.Value, out GameNetConnection registered)
                || !ReferenceEquals(connection, registered))
            {
                return;
            }

            CloseConnection(connection, string.IsNullOrWhiteSpace(reason) ? "Disconnected by server" : reason);
        }

        public NetworkIdentity SpawnServerObject(
            ushort prefabId,
            Vector3 position,
            Quaternion rotation,
            uint ownerConnectionId = 0,
            ushort authorityEpoch = 1)
        {
            EnsureDedicatedServer();
            if (prefabRegistry == null
                || !prefabRegistry.TryInstantiate(prefabId, position, rotation, out NetworkIdentity identity))
            {
                throw new InvalidOperationException($"Network PrefabId {prefabId} is not registered.");
            }

            try
            {
                return RegisterServerObject(identity, ownerConnectionId, authorityEpoch);
            }
            catch
            {
                if (identity != null)
                {
                    UnityEngine.Object.Destroy(identity.gameObject);
                }

                throw;
            }
        }

        public NetworkIdentity RegisterServerObject(
            NetworkIdentity identity,
            uint ownerConnectionId = 0,
            ushort authorityEpoch = 1)
        {
            EnsureDedicatedServer();
            NetworkIdentity registered = objectRegistry.RegisterServerObject(
                identity,
                ownerConnectionId,
                authorityEpoch);

            List<GameNetConnection> targets = GetReadyConnections();
            for (int i = 0; i < targets.Count; i++)
            {
                OpenActorChannel(targets[i], registered);
            }

            Debug.Log(
                $"[Net][DS] Spawned NetId={registered.NetId}, PrefabId={registered.PrefabId}, "
                + $"Owner={registered.OwnerConnectionId}, Epoch={registered.AuthorityEpoch}.");
            NetworkObjectSpawned?.Invoke(registered);
            return registered;
        }

        public bool DespawnServerObject(
            uint netId,
            ActorChannelCloseReason reason = ActorChannelCloseReason.Destroyed)
        {
            EnsureDedicatedServer();
            if (!objectRegistry.TryGet(netId, out NetworkIdentity identity))
            {
                return false;
            }

            ushort authorityEpoch = identity.AuthorityEpoch;
            List<GameNetConnection> targets = GetReadyConnections();
            for (int i = 0; i < targets.Count; i++)
            {
                GameNetConnection connection = targets[i];
                if (!connection.TryGetActorChannelByNetId(netId, out ActorReplicationChannel channel))
                {
                    continue;
                }

                byte[] closePacket = ActorProtocol.CreateActorChannelClose(
                    channel.ChannelId,
                    netId,
                    authorityEpoch,
                    reason);
                TrySendReliable(connection, closePacket, "ActorChannelClose");
                connection.RemoveActorChannel(channel.ChannelId);
            }

            objectRegistry.Unregister(netId, destroyGameObject: true);
            Debug.Log($"[Net][DS] Despawned NetId={netId}, reason={reason}.");
            NetworkObjectDespawned?.Invoke(netId, reason);
            return true;
        }

        public void Stop()
        {
            if (Mode == NetworkProcessMode.None)
            {
                return;
            }

            foreach (GameNetConnection connection in connections.Values)
            {
                connection.State = NetConnectionState.Disconnected;
                connection.CloseAllActorChannels();
            }

            connections.Clear();
            objectRegistry.Clear(destroyGameObjects: true);
            transportEvents.Clear();
            timedOutConnections.Clear();
            ServerConnection = null;
            transport.Dispose();
            Mode = NetworkProcessMode.None;
            serverTick = 0;
            serverTickAccumulator = 0d;
            nextConnectionId = 1;
        }

        public void Dispose() => Stop();

        private void ProcessTransportEvent(TransportEvent transportEvent)
        {
            switch (transportEvent.Type)
            {
                case TransportEventType.Connected:
                    HandleTransportConnected(transportEvent.Connection);
                    break;

                case TransportEventType.Data:
                    if (connections.TryGetValue(transportEvent.Connection.Value, out GameNetConnection dataConnection))
                    {
                        HandlePacket(dataConnection, transportEvent.Payload);
                    }
                    break;

                case TransportEventType.Disconnected:
                    HandleTransportDisconnected(transportEvent.Connection, transportEvent.Reason);
                    break;
            }
        }

        private void HandleTransportConnected(TransportConnectionHandle handle)
        {
            if (Mode == NetworkProcessMode.DedicatedServer)
            {
                GameNetConnection connection = new GameNetConnection(
                    handle,
                    NetConnectionState.AwaitHello,
                    transport.GetRemoteEndpoint(handle));
                connections.Add(handle.Value, connection);
                Debug.Log($"[Net][DS] Transport accepted {connection.RemoteEndpoint}; awaiting ClientHello.");
                ConnectionOpened?.Invoke(connection);
                return;
            }

            if (Mode != NetworkProcessMode.Client
                || !connections.TryGetValue(handle.Value, out GameNetConnection serverConnection)
                || serverConnection.State != NetConnectionState.Connecting)
            {
                return;
            }

            serverConnection.ClientNonce = CreateNonce();
            if (!TrySendReliable(
                    serverConnection,
                    ConnectionProtocol.CreateClientHello(serverConnection.ClientNonce),
                    "ClientHello"))
            {
                return;
            }

            serverConnection.TransitionTo(NetConnectionState.AwaitChallenge);
            Debug.Log("[Net][Client] Transport connected; ClientHello sent.");
            ConnectionOpened?.Invoke(serverConnection);
        }

        private void HandlePacket(GameNetConnection connection, byte[] packet)
        {
            if (ConnectionProtocol.TryReadMessageType(packet, out ConnectionMessageType connectionMessageType))
            {
                if (Mode == NetworkProcessMode.DedicatedServer)
                {
                    HandleServerPacket(connection, connectionMessageType, packet);
                }
                else if (Mode == NetworkProcessMode.Client)
                {
                    HandleClientPacket(connection, connectionMessageType, packet);
                }

                return;
            }

            if (ActorProtocol.TryReadMessageType(packet, out ActorMessageType actorMessageType))
            {
                if (!connection.IsReady)
                {
                    RejectOrClose(
                        connection,
                        ConnectionRejectReason.UnexpectedMessage,
                        $"Actor message {actorMessageType} received before Ready");
                    return;
                }

                HandleActorPacket(connection, actorMessageType, packet);
                return;
            }

            if (ObjectReplicationProtocol.TryReadMessageType(
                    packet,
                    out ObjectReplicationMessageType objectMessageType))
            {
                if (!connection.IsReady)
                {
                    RejectOrClose(
                        connection,
                        ConnectionRejectReason.UnexpectedMessage,
                        $"Object message {objectMessageType} received before Ready");
                    return;
                }

                HandleObjectReplicationPacket(connection, objectMessageType, packet);
                return;
            }

            if (CharacterMovementProtocol.TryReadMessageType(
                    packet,
                    out CharacterMovementMessageType movementMessageType))
            {
                if (!connection.IsReady)
                {
                    RejectOrClose(
                        connection,
                        ConnectionRejectReason.UnexpectedMessage,
                        $"Movement message {movementMessageType} received before Ready");
                    return;
                }

                HandleCharacterMovementPacket(connection, movementMessageType, packet);
                return;
            }

            RejectOrClose(connection, ConnectionRejectReason.InvalidPacket, "Unknown or empty network packet");
        }

        private void HandleServerPacket(
            GameNetConnection connection,
            ConnectionMessageType messageType,
            byte[] packet)
        {
            switch (messageType)
            {
                case ConnectionMessageType.ClientHello when connection.State == NetConnectionState.AwaitHello:
                    if (!ConnectionProtocol.TryReadClientHello(
                            packet,
                            out ushort protocolVersion,
                            out uint schemaHash,
                            out ulong clientNonce))
                    {
                        RejectOrClose(connection, ConnectionRejectReason.InvalidPacket, "Malformed ClientHello");
                        return;
                    }

                    if (protocolVersion != ConnectionProtocol.ProtocolVersion)
                    {
                        RejectOrClose(
                            connection,
                            ConnectionRejectReason.ProtocolMismatch,
                            $"Protocol {protocolVersion} is not supported; expected {ConnectionProtocol.ProtocolVersion}");
                        return;
                    }

                    if (schemaHash != ConnectionProtocol.SchemaHash)
                    {
                        RejectOrClose(connection, ConnectionRejectReason.SchemaMismatch, "Network schema hash mismatch");
                        return;
                    }

                    connection.ClientNonce = clientNonce;
                    connection.ServerNonce = CreateNonce();
                    if (!TrySendReliable(
                            connection,
                            ConnectionProtocol.CreateServerChallenge(connection.ClientNonce, connection.ServerNonce),
                            "ServerChallenge"))
                    {
                        return;
                    }

                    connection.TransitionTo(NetConnectionState.AwaitLogin);
                    Debug.Log($"[Net][DS] ClientHello accepted from {connection.RemoteEndpoint}; challenge sent.");
                    break;

                case ConnectionMessageType.ClientLogin when connection.State == NetConnectionState.AwaitLogin:
                    if (!ConnectionProtocol.TryReadClientLogin(packet, out ulong serverNonce, out string displayName)
                        || serverNonce != connection.ServerNonce
                        || string.IsNullOrWhiteSpace(displayName))
                    {
                        RejectOrClose(connection, ConnectionRejectReason.InvalidLogin, "Invalid login or challenge nonce");
                        return;
                    }

                    connection.ConnectionId = nextConnectionId++;
                    connection.DisplayName = displayName;
                    if (!TrySendReliable(
                            connection,
                            ConnectionProtocol.CreateServerWelcome(
                                connection.ConnectionId,
                                serverTick,
                                DefaultTickRate),
                            "ServerWelcome"))
                    {
                        return;
                    }

                    connection.TransitionTo(NetConnectionState.AwaitReady);
                    Debug.Log($"[Net][DS] Login accepted for '{displayName}' as connection {connection.ConnectionId}; Welcome sent.");
                    break;

                case ConnectionMessageType.ClientReady when connection.State == NetConnectionState.AwaitReady:
                    if (!ConnectionProtocol.TryReadClientReady(packet, out uint connectionId)
                        || connectionId != connection.ConnectionId)
                    {
                        RejectOrClose(connection, ConnectionRejectReason.InvalidPacket, "Ready connection id mismatch");
                        return;
                    }

                    connection.TransitionTo(NetConnectionState.Ready);
                    Debug.Log($"[Net][DS] READY connection={connection.ConnectionId}, player='{connection.DisplayName}', remote={connection.RemoteEndpoint}.");
                    OpenExistingObjectsForConnection(connection);
                    ConnectionReady?.Invoke(connection);
                    break;

                default:
                    RejectOrClose(
                        connection,
                        ConnectionRejectReason.UnexpectedMessage,
                        $"Unexpected {messageType} while in {connection.State}");
                    break;
            }
        }

        private void HandleClientPacket(
            GameNetConnection connection,
            ConnectionMessageType messageType,
            byte[] packet)
        {
            if (messageType == ConnectionMessageType.ServerReject)
            {
                string reason = ConnectionProtocol.TryReadServerReject(packet, out ConnectionRejectReason rejectReason, out string message)
                    ? $"Server rejected connection ({rejectReason}): {message}"
                    : "Server sent a malformed rejection";
                CloseConnection(connection, reason);
                return;
            }

            switch (messageType)
            {
                case ConnectionMessageType.ServerChallenge when connection.State == NetConnectionState.AwaitChallenge:
                    if (!ConnectionProtocol.TryReadServerChallenge(
                            packet,
                            out ushort protocolVersion,
                            out ulong clientNonce,
                            out ulong serverNonce)
                        || protocolVersion != ConnectionProtocol.ProtocolVersion
                        || clientNonce != connection.ClientNonce)
                    {
                        CloseConnection(connection, "Invalid ServerChallenge");
                        return;
                    }

                    connection.ServerNonce = serverNonce;
                    if (!TrySendReliable(
                            connection,
                            ConnectionProtocol.CreateClientLogin(serverNonce, clientDisplayName),
                            "ClientLogin"))
                    {
                        return;
                    }

                    connection.TransitionTo(NetConnectionState.AwaitWelcome);
                    Debug.Log("[Net][Client] ServerChallenge accepted; ClientLogin sent.");
                    break;

                case ConnectionMessageType.ServerWelcome when connection.State == NetConnectionState.AwaitWelcome:
                    if (!ConnectionProtocol.TryReadServerWelcome(
                            packet,
                            out uint connectionId,
                            out uint serverTickAtWelcome,
                            out ushort tickRate)
                        || connectionId == 0
                        || tickRate == 0)
                    {
                        CloseConnection(connection, "Invalid ServerWelcome");
                        return;
                    }

                    connection.ConnectionId = connectionId;
                    connection.DisplayName = clientDisplayName;
                    connection.ServerTickAtWelcome = serverTickAtWelcome;
                    connection.ServerTickRate = tickRate;
                    serverTick = serverTickAtWelcome;
                    serverTickAccumulator = 0d;
                    if (!TrySendReliable(
                            connection,
                            ConnectionProtocol.CreateClientReady(connectionId),
                            "ClientReady"))
                    {
                        return;
                    }

                    connection.TransitionTo(NetConnectionState.Ready);
                    Debug.Log($"[Net][Client] READY connection={connectionId}, serverTick={serverTickAtWelcome}, tickRate={tickRate}.");
                    ConnectionReady?.Invoke(connection);
                    break;

                default:
                    CloseConnection(connection, $"Unexpected {messageType} while in {connection.State}");
                    break;
            }
        }

        private void HandleActorPacket(
            GameNetConnection connection,
            ActorMessageType messageType,
            byte[] packet)
        {
            if (Mode == NetworkProcessMode.DedicatedServer)
            {
                if (messageType != ActorMessageType.ActorChannelOpenAck)
                {
                    RejectOrClose(
                        connection,
                        ConnectionRejectReason.UnexpectedMessage,
                        $"Client cannot send {messageType}");
                    return;
                }

                HandleActorChannelOpenAck(connection, packet);
                return;
            }

            switch (messageType)
            {
                case ActorMessageType.ActorChannelOpen:
                    HandleActorChannelOpen(connection, packet);
                    break;

                case ActorMessageType.ActorChannelClose:
                    HandleActorChannelClose(connection, packet);
                    break;

                default:
                    CloseConnection(connection, $"Server sent unexpected {messageType}");
                    break;
            }
        }

        private void HandleActorChannelOpenAck(GameNetConnection connection, byte[] packet)
        {
            if (!ActorProtocol.TryReadActorChannelOpenAck(
                    packet,
                    out ushort channelId,
                    out uint netId,
                    out ushort authorityEpoch)
                || !connection.TryGetActorChannel(channelId, out ActorReplicationChannel channel)
                || !channel.TryMarkSpawnAcked(netId, authorityEpoch))
            {
                RejectOrClose(
                    connection,
                    ConnectionRejectReason.InvalidPacket,
                    "Invalid ActorChannelOpenAck");
                return;
            }

            Debug.Log(
                $"[Net][DS] ActorChannel {channelId} open acknowledged by connection "
                + $"{connection.ConnectionId} for NetId={netId}.");
            SendInitialObjectStates(connection, channel);
        }

        private void HandleActorChannelOpen(GameNetConnection connection, byte[] packet)
        {
            if (!ActorProtocol.TryReadActorChannelOpen(packet, out ActorSpawnMessage spawn))
            {
                CloseConnection(connection, "Malformed ActorChannelOpen");
                return;
            }

            if (connection.TryGetActorChannel(spawn.ChannelId, out ActorReplicationChannel existingChannel))
            {
                if (existingChannel.NetId == spawn.NetId
                    && existingChannel.Actor.AuthorityEpoch == spawn.AuthorityEpoch)
                {
                    TrySendReliable(
                        connection,
                        ActorProtocol.CreateActorChannelOpenAck(
                            spawn.ChannelId,
                            spawn.NetId,
                            spawn.AuthorityEpoch),
                        "ActorChannelOpenAck");
                    return;
                }

                CloseConnection(connection, $"Actor channel id {spawn.ChannelId} was reused");
                return;
            }

            if (objectRegistry.TryGet(spawn.NetId, out _))
            {
                CloseConnection(connection, $"Duplicate NetId {spawn.NetId} in ActorChannelOpen");
                return;
            }

            if (prefabRegistry == null
                || !prefabRegistry.TryInstantiate(
                    spawn.PrefabId,
                    spawn.Position,
                    spawn.Rotation,
                    out NetworkIdentity identity))
            {
                CloseConnection(connection, $"Unknown network PrefabId {spawn.PrefabId}");
                return;
            }

            NetworkRole role = spawn.OwnerConnectionId == connection.ConnectionId
                ? NetworkRole.AutonomousProxy
                : NetworkRole.SimulatedProxy;

            if (!objectRegistry.TryRegisterClientObject(
                    identity,
                    spawn.NetId,
                    spawn.PrefabId,
                    spawn.OwnerConnectionId,
                    spawn.AuthorityEpoch,
                    role)
                || !connection.TryCreateRemoteActorChannel(spawn.ChannelId, identity, out _))
            {
                if (identity != null && identity.IsSpawned)
                {
                    objectRegistry.Unregister(identity.NetId, destroyGameObject: true);
                }
                else if (identity != null)
                {
                    UnityEngine.Object.Destroy(identity.gameObject);
                }

                CloseConnection(connection, "Could not register spawned network object");
                return;
            }

            if (!TrySendReliable(
                    connection,
                    ActorProtocol.CreateActorChannelOpenAck(
                        spawn.ChannelId,
                        spawn.NetId,
                        spawn.AuthorityEpoch),
                    "ActorChannelOpenAck"))
            {
                return;
            }

            Debug.Log(
                $"[Net][Client] Spawned NetId={spawn.NetId}, PrefabId={spawn.PrefabId}, "
                + $"Role={role}, Channel={spawn.ChannelId}.");
            NetworkObjectSpawned?.Invoke(identity);
        }

        private void HandleObjectReplicationPacket(
            GameNetConnection connection,
            ObjectReplicationMessageType messageType,
            byte[] packet)
        {
            if (Mode == NetworkProcessMode.DedicatedServer)
            {
                RejectOrClose(
                    connection,
                    ConnectionRejectReason.UnexpectedMessage,
                    $"Client cannot send {messageType}");
                return;
            }

            if (messageType != ObjectReplicationMessageType.ObjectState
                || !ObjectReplicationProtocol.TryReadObjectState(packet, out ObjectStateMessage state)
                || !connection.TryGetActorChannel(state.ChannelId, out ActorReplicationChannel channel)
                || channel.NetId != state.NetId
                || channel.Actor == null
                || channel.Actor.AuthorityEpoch != state.AuthorityEpoch
                || !channel.TryGetObjectReplicator(state.ReplicationId, out ObjectReplicator replicator)
                || !replicator.TryApplyState(
                    state.Sequence,
                    state.State,
                    state.IsInitialState,
                    out _))
            {
                CloseConnection(connection, "Invalid ObjectState packet");
            }
        }

        private void HandleCharacterMovementPacket(
            GameNetConnection connection,
            CharacterMovementMessageType messageType,
            byte[] packet)
        {
            if (Mode == NetworkProcessMode.DedicatedServer)
            {
                HandleServerCharacterMove(connection, messageType, packet);
                return;
            }

            switch (messageType)
            {
                case CharacterMovementMessageType.ServerMoveAck:
                    HandleClientMoveAck(connection, packet);
                    break;

                case CharacterMovementMessageType.TransformSnapshot:
                    HandleClientTransformSnapshot(connection, packet);
                    break;

                default:
                    CloseConnection(connection, $"Server sent unexpected {messageType}");
                    break;
            }
        }

        private void HandleServerCharacterMove(
            GameNetConnection connection,
            CharacterMovementMessageType messageType,
            byte[] packet)
        {
            if (messageType != CharacterMovementMessageType.ClientMove
                || !CharacterMovementProtocol.TryReadClientMove(packet, out CharacterMoveMessage move)
                || !objectRegistry.TryGet(move.NetId, out NetworkIdentity identity)
                || identity.AuthorityEpoch != move.AuthorityEpoch
                || identity.OwnerConnectionId != connection.ConnectionId
                || !connection.TryGetActorChannelByNetId(move.NetId, out ActorReplicationChannel channel)
                || !channel.SpawnAcked)
            {
                RejectOrClose(
                    connection,
                    ConnectionRejectReason.InvalidPacket,
                    "Invalid or unauthorized ClientMove");
                return;
            }

            CharacterNetworkMovement networkMovement
                = identity.GetComponent<CharacterNetworkMovement>();
            string validationError = null;
            if (networkMovement == null
                || !networkMovement.TryProcessServerMove(move, out validationError))
            {
                RejectOrClose(
                    connection,
                    ConnectionRejectReason.InvalidPacket,
                    validationError ?? "Target has no CharacterNetworkMovement");
                return;
            }

            CharacterMoveAckMessage ack = networkMovement.CreateServerAck(serverTick);
            byte[] ackPacket = CharacterMovementProtocol.CreateServerMoveAck(
                ack.NetId,
                ack.AuthorityEpoch,
                ack.AcknowledgedSequence,
                ack.ServerTick,
                ack.Position,
                ack.Rotation,
                ack.Velocity,
                ack.MovementMode);
            SendUnreliable(connection, ackPacket);
        }

        private void HandleClientMoveAck(GameNetConnection connection, byte[] packet)
        {
            if (!CharacterMovementProtocol.TryReadServerMoveAck(
                    packet,
                    out CharacterMoveAckMessage ack)
                || !objectRegistry.TryGet(ack.NetId, out NetworkIdentity identity)
                || identity.Role != NetworkRole.AutonomousProxy
                || identity.AuthorityEpoch != ack.AuthorityEpoch)
            {
                CloseConnection(connection, "Invalid ServerMoveAck");
                return;
            }

            SynchronizeClientServerTick(ack.ServerTick);
            CharacterNetworkMovement networkMovement
                = identity.GetComponent<CharacterNetworkMovement>();
            if (networkMovement == null)
            {
                CloseConnection(connection, "Autonomous character has no CharacterNetworkMovement");
                return;
            }

            networkMovement.ReceiveServerMoveAck(ack);
        }

        private void HandleClientTransformSnapshot(GameNetConnection connection, byte[] packet)
        {
            if (!CharacterMovementProtocol.TryReadTransformSnapshot(
                    packet,
                    out CharacterSnapshotMessage snapshot)
                || !objectRegistry.TryGet(snapshot.NetId, out NetworkIdentity identity)
                || identity.Role != NetworkRole.SimulatedProxy
                || identity.AuthorityEpoch != snapshot.AuthorityEpoch)
            {
                CloseConnection(connection, "Invalid TransformSnapshot");
                return;
            }

            SynchronizeClientServerTick(snapshot.ServerTick);
            CharacterNetworkMovement networkMovement
                = identity.GetComponent<CharacterNetworkMovement>();
            if (networkMovement == null)
            {
                CloseConnection(connection, "Simulated character has no CharacterNetworkMovement");
                return;
            }

            networkMovement.ReceiveSnapshot(snapshot);
        }

        private void HandleActorChannelClose(GameNetConnection connection, byte[] packet)
        {
            if (!ActorProtocol.TryReadActorChannelClose(
                    packet,
                    out ushort channelId,
                    out uint netId,
                    out ushort authorityEpoch,
                    out ActorChannelCloseReason reason)
                || !connection.TryGetActorChannel(channelId, out ActorReplicationChannel channel)
                || channel.NetId != netId
                || channel.Actor == null
                || channel.Actor.AuthorityEpoch != authorityEpoch)
            {
                CloseConnection(connection, "Invalid ActorChannelClose");
                return;
            }

            connection.RemoveActorChannel(channelId);
            objectRegistry.Unregister(netId, destroyGameObject: true);
            Debug.Log($"[Net][Client] Despawned NetId={netId}, reason={reason}.");
            NetworkObjectDespawned?.Invoke(netId, reason);
        }

        private void OpenExistingObjectsForConnection(GameNetConnection connection)
        {
            foreach (NetworkIdentity identity in objectRegistry.Objects)
            {
                if (identity != null && identity.IsSpawned)
                {
                    OpenActorChannel(connection, identity);
                }
            }
        }

        private bool OpenActorChannel(GameNetConnection connection, NetworkIdentity identity)
        {
            if (connection == null || !connection.IsReady || identity == null || !identity.IsSpawned)
            {
                return false;
            }

            if (connection.TryGetActorChannelByNetId(identity.NetId, out _))
            {
                return true;
            }

            ActorReplicationChannel channel = connection.FindOrCreateLocalActorChannel(identity);
            byte[] packet = ActorProtocol.CreateActorChannelOpen(
                channel.ChannelId,
                identity.NetId,
                identity.PrefabId,
                identity.OwnerConnectionId,
                identity.AuthorityEpoch,
                identity.transform.position,
                identity.transform.rotation);

            if (!TrySendReliable(connection, packet, "ActorChannelOpen"))
            {
                return false;
            }

            Debug.Log(
                $"[Net][DS] Opening ActorChannel {channel.ChannelId} for NetId={identity.NetId} "
                + $"to connection {connection.ConnectionId}.");
            return true;
        }

        private void SendInitialObjectStates(
            GameNetConnection connection,
            ActorReplicationChannel channel)
        {
            if (connection == null || channel == null || !channel.SpawnAcked || channel.Actor == null)
            {
                return;
            }

            foreach (ObjectReplicator replicator in channel.ObjectReplicators)
            {
                if (!replicator.TryCaptureState(
                        force: true,
                        out ushort sequence,
                        out byte[] state))
                {
                    continue;
                }

                byte[] packet = ObjectReplicationProtocol.CreateObjectState(
                    channel.ChannelId,
                    channel.NetId,
                    channel.Actor.AuthorityEpoch,
                    replicator.ReplicationId,
                    sequence,
                    ObjectStateFlags.Initial,
                    state);
                if (!TrySendReliable(connection, packet, "InitialObjectState"))
                {
                    return;
                }

                Debug.Log(
                    $"[Net][DS] Initial state sent: connection={connection.ConnectionId}, "
                    + $"NetId={channel.NetId}, ReplicationId={replicator.ReplicationId}.");
            }

            channel.LastReplicatedTick = serverTick;
        }

        private void ReplicateActorStates()
        {
            if (Mode != NetworkProcessMode.DedicatedServer
                || serverTick == 0
                || DefaultStateReplicationRate == 0)
            {
                return;
            }

            uint replicationInterval = Math.Max(
                1u,
                (uint)DefaultTickRate / DefaultStateReplicationRate);
            List<GameNetConnection> readyConnections = GetReadyConnections();
            for (int connectionIndex = 0; connectionIndex < readyConnections.Count; connectionIndex++)
            {
                GameNetConnection connection = readyConnections[connectionIndex];
                List<ActorReplicationChannel> channels
                    = new List<ActorReplicationChannel>(connection.ActorChannels);
                for (int channelIndex = 0; channelIndex < channels.Count; channelIndex++)
                {
                    ActorReplicationChannel channel = channels[channelIndex];
                    if (channel == null
                        || !channel.SpawnAcked
                        || channel.Actor == null
                        || serverTick - channel.LastReplicatedTick < replicationInterval)
                    {
                        continue;
                    }

                    channel.LastReplicatedTick = serverTick;
                    foreach (ObjectReplicator replicator in channel.ObjectReplicators)
                    {
                        if (!replicator.TryCaptureState(
                                force: false,
                                out ushort sequence,
                                out byte[] state))
                        {
                            continue;
                        }

                        byte[] packet = ObjectReplicationProtocol.CreateObjectState(
                            channel.ChannelId,
                            channel.NetId,
                            channel.Actor.AuthorityEpoch,
                            replicator.ReplicationId,
                            sequence,
                            ObjectStateFlags.None,
                            state);
                        SendUnreliable(connection, packet);
                    }
                }
            }
        }

        private void ReplicateCharacterSnapshots()
        {
            if (Mode != NetworkProcessMode.DedicatedServer
                || serverTick == 0
                || DefaultMovementSnapshotRate == 0)
            {
                return;
            }

            uint snapshotInterval = Math.Max(
                1u,
                (uint)DefaultTickRate / DefaultMovementSnapshotRate);
            List<GameNetConnection> readyConnections = GetReadyConnections();
            for (int connectionIndex = 0; connectionIndex < readyConnections.Count; connectionIndex++)
            {
                GameNetConnection connection = readyConnections[connectionIndex];
                List<ActorReplicationChannel> channels
                    = new List<ActorReplicationChannel>(connection.ActorChannels);
                for (int channelIndex = 0; channelIndex < channels.Count; channelIndex++)
                {
                    ActorReplicationChannel channel = channels[channelIndex];
                    if (channel == null
                        || !channel.SpawnAcked
                        || channel.Actor == null
                        || channel.Actor.OwnerConnectionId == connection.ConnectionId
                        || serverTick - channel.LastMovementReplicatedTick < snapshotInterval)
                    {
                        continue;
                    }

                    CharacterNetworkMovement networkMovement
                        = channel.Actor.GetComponent<CharacterNetworkMovement>();
                    if (networkMovement == null)
                    {
                        continue;
                    }

                    channel.LastMovementReplicatedTick = serverTick;
                    CharacterSnapshotMessage snapshot
                        = networkMovement.CreateServerSnapshot(serverTick);
                    byte[] packet = CharacterMovementProtocol.CreateTransformSnapshot(
                        snapshot.NetId,
                        snapshot.AuthorityEpoch,
                        snapshot.ServerTick,
                        snapshot.Position,
                        snapshot.Rotation,
                        snapshot.Velocity,
                        snapshot.MovementMode);
                    SendUnreliable(connection, packet);
                }
            }
        }

        private void HandleTransportDisconnected(TransportConnectionHandle handle, string reason)
        {
            if (!connections.TryGetValue(handle.Value, out GameNetConnection connection))
            {
                return;
            }

            connections.Remove(handle.Value);
            connection.State = NetConnectionState.Disconnected;
            if (ReferenceEquals(ServerConnection, connection))
            {
                ServerConnection = null;
            }

            CleanupNetworkStateForConnection(connection);

            Debug.LogWarning($"[Net][{Mode}] Disconnected {connection.RemoteEndpoint}: {reason}.");
            ConnectionClosed?.Invoke(connection, reason);
        }

        private void TickHandshakeTimeouts(float deltaTime)
        {
            timedOutConnections.Clear();

            foreach (GameNetConnection connection in connections.Values)
            {
                if (connection.State == NetConnectionState.Ready
                    || connection.State == NetConnectionState.Disconnected
                    || connection.State == NetConnectionState.Disconnecting)
                {
                    continue;
                }

                connection.StateElapsedSeconds += deltaTime;
                if (connection.StateElapsedSeconds >= HandshakeTimeoutSeconds)
                {
                    timedOutConnections.Add(connection);
                }
            }

            for (int i = 0; i < timedOutConnections.Count; i++)
            {
                GameNetConnection connection = timedOutConnections[i];
                RejectOrClose(
                    connection,
                    ConnectionRejectReason.HandshakeTimeout,
                    $"Handshake timed out in {connection.State}");
            }
        }

        private void RejectOrClose(
            GameNetConnection connection,
            ConnectionRejectReason rejectReason,
            string message)
        {
            if (Mode == NetworkProcessMode.DedicatedServer)
            {
                SendReliable(connection, ConnectionProtocol.CreateServerReject(rejectReason, message));
                transport.Flush();
            }

            CloseConnection(connection, message);
        }

        private void CloseConnection(GameNetConnection connection, string reason)
        {
            if (connection.State == NetConnectionState.Disconnected)
            {
                return;
            }

            connection.State = NetConnectionState.Disconnecting;
            transport.Disconnect(connection.TransportHandle);
            connections.Remove(connection.TransportHandle.Value);
            connection.State = NetConnectionState.Disconnected;

            if (ReferenceEquals(ServerConnection, connection))
            {
                ServerConnection = null;
            }

            CleanupNetworkStateForConnection(connection);

            Debug.LogWarning($"[Net][{Mode}] Closing {connection.RemoteEndpoint}: {reason}.");
            ConnectionClosed?.Invoke(connection, reason);
        }

        private bool SendReliable(GameNetConnection connection, byte[] packet)
        {
            return transport.Send(
                connection.TransportHandle,
                new ArraySegment<byte>(packet),
                TransportDelivery.Reliable);
        }

        private bool SendUnreliable(GameNetConnection connection, byte[] packet)
        {
            return transport.Send(
                connection.TransportHandle,
                new ArraySegment<byte>(packet),
                TransportDelivery.Unreliable);
        }

        private bool TrySendReliable(GameNetConnection connection, byte[] packet, string packetName)
        {
            if (SendReliable(connection, packet))
            {
                return true;
            }

            CloseConnection(connection, $"Failed to send {packetName}");
            return false;
        }

        private void AdvanceServerClock(float deltaTime)
        {
            if (deltaTime <= 0f)
            {
                return;
            }

            ushort tickRate = Mode == NetworkProcessMode.DedicatedServer
                ? DefaultTickRate
                : ServerConnection != null && ServerConnection.IsReady
                    ? ServerConnection.ServerTickRate
                    : (ushort)0;
            if (tickRate == 0)
            {
                return;
            }

            double tickInterval = 1d / tickRate;
            serverTickAccumulator += deltaTime;
            uint elapsedTicks = (uint)(serverTickAccumulator / tickInterval);
            if (elapsedTicks == 0)
            {
                return;
            }

            serverTick += elapsedTicks;
            serverTickAccumulator -= elapsedTicks * tickInterval;
        }

        private void SynchronizeClientServerTick(uint receivedServerTick)
        {
            if (Mode == NetworkProcessMode.Client
                && (serverTick == 0 || unchecked(receivedServerTick - serverTick) < 0x80000000u))
            {
                serverTick = Math.Max(serverTick, receivedServerTick);
            }
        }

        private void CleanupNetworkStateForConnection(GameNetConnection connection)
        {
            connection.CloseAllActorChannels();

            if (Mode == NetworkProcessMode.Client)
            {
                objectRegistry.Clear(destroyGameObjects: true);
                return;
            }

            if (Mode != NetworkProcessMode.DedicatedServer || connection.ConnectionId == 0)
            {
                return;
            }

            List<uint> connectionOwnedNetIds = new List<uint>();
            foreach (NetworkIdentity identity in objectRegistry.Objects)
            {
                if (identity != null && identity.OwnerConnectionId == connection.ConnectionId)
                {
                    connectionOwnedNetIds.Add(identity.NetId);
                }
            }

            for (int i = 0; i < connectionOwnedNetIds.Count; i++)
            {
                DespawnServerObject(
                    connectionOwnedNetIds[i],
                    ActorChannelCloseReason.OwnerDisconnected);
            }
        }

        private List<GameNetConnection> GetReadyConnections()
        {
            List<GameNetConnection> readyConnections = new List<GameNetConnection>();
            foreach (GameNetConnection connection in connections.Values)
            {
                if (connection.IsReady)
                {
                    readyConnections.Add(connection);
                }
            }

            return readyConnections;
        }

        private static ulong CreateNonce()
        {
            byte[] bytes = Guid.NewGuid().ToByteArray();
            return BitConverter.ToUInt64(bytes, 0);
        }

        private void EnsureNotRunning()
        {
            if (IsRunning)
            {
                throw new InvalidOperationException($"Network driver is already running as {Mode}.");
            }
        }

        private void EnsureDedicatedServer()
        {
            if (Mode != NetworkProcessMode.DedicatedServer || !IsRunning)
            {
                throw new InvalidOperationException("This operation requires a running Dedicated Server.");
            }
        }
    }
}
