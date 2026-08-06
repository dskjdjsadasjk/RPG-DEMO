using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Networking.Transport;
using Unity.Networking.Transport.Utilities;

namespace RPGDemo.GameFramework.Networking.Transport
{
    public sealed class UtpTransport : INetworkTransport
    {
        private readonly Dictionary<int, NetworkConnection> connections = new Dictionary<int, NetworkConnection>();
        private readonly List<int> connectionIds = new List<int>();
        private readonly List<int> disconnectedIds = new List<int>();

        private NetworkDriver driver;
        private NetworkPipeline reliablePipeline;
        private int nextConnectionId = 1;

        public bool IsCreated => driver.IsCreated;
        public bool IsServer { get; private set; }
        public ushort BoundPort { get; private set; }

        public void StartServer(ushort port)
        {
            EnsureNotStarted();

            driver = NetworkDriver.Create();
            reliablePipeline = driver.CreatePipeline(typeof(ReliableSequencedPipelineStage));

            NetworkEndpoint endpoint = NetworkEndpoint.AnyIpv4.WithPort(port);
            int bindResult = driver.Bind(endpoint);
            if (bindResult != 0)
            {
                Dispose();
                throw new InvalidOperationException($"UTP bind failed on UDP port {port}, error {bindResult}.");
            }

            int listenResult = driver.Listen();
            if (listenResult != 0)
            {
                Dispose();
                throw new InvalidOperationException($"UTP listen failed on UDP port {port}, error {listenResult}.");
            }

            IsServer = true;
            BoundPort = port;
        }

        public TransportConnectionHandle StartClient(string address, ushort port)
        {
            EnsureNotStarted();

            if (!NetworkEndpoint.TryParse(address, port, out NetworkEndpoint endpoint))
            {
                throw new ArgumentException($"'{address}' is not a valid IPv4/IPv6 address.", nameof(address));
            }

            driver = NetworkDriver.Create();
            reliablePipeline = driver.CreatePipeline(typeof(ReliableSequencedPipelineStage));

            NetworkConnection connection = driver.Connect(endpoint);
            if (!connection.IsCreated)
            {
                Dispose();
                throw new InvalidOperationException($"UTP could not start a connection to {address}:{port}.");
            }

            IsServer = false;
            BoundPort = 0;
            return AddConnection(connection);
        }

        public void PollEvents(List<TransportEvent> output)
        {
            if (!driver.IsCreated)
            {
                return;
            }

            driver.ScheduleUpdate().Complete();

            if (IsServer)
            {
                NetworkConnection accepted;
                while ((accepted = driver.Accept()) != default)
                {
                    TransportConnectionHandle handle = AddConnection(accepted);
                    output.Add(new TransportEvent(TransportEventType.Connected, handle));
                }
            }

            connectionIds.Clear();
            connectionIds.AddRange(connections.Keys);
            disconnectedIds.Clear();

            for (int i = 0; i < connectionIds.Count; i++)
            {
                int id = connectionIds[i];
                if (!connections.TryGetValue(id, out NetworkConnection connection))
                {
                    continue;
                }

                TransportConnectionHandle handle = new TransportConnectionHandle(id);
                NetworkEvent.Type eventType;
                DataStreamReader reader;

                while ((eventType = connection.PopEvent(driver, out reader)) != NetworkEvent.Type.Empty)
                {
                    switch (eventType)
                    {
                        case NetworkEvent.Type.Connect:
                            if (!IsServer)
                            {
                                output.Add(new TransportEvent(TransportEventType.Connected, handle));
                            }
                            break;

                        case NetworkEvent.Type.Data:
                            byte[] payload = new byte[reader.Length];
                            for (int byteIndex = 0; byteIndex < payload.Length; byteIndex++)
                            {
                                payload[byteIndex] = reader.ReadByte();
                            }

                            output.Add(new TransportEvent(TransportEventType.Data, handle, payload));
                            break;

                        case NetworkEvent.Type.Disconnect:
                            string reason = reader.Length > 0
                                ? $"UTP disconnect reason {reader.ReadByte()}"
                                : "Remote closed the connection";
                            output.Add(new TransportEvent(TransportEventType.Disconnected, handle, reason: reason));
                            disconnectedIds.Add(id);
                            break;
                    }
                }
            }

            for (int i = 0; i < disconnectedIds.Count; i++)
            {
                connections.Remove(disconnectedIds[i]);
            }
        }

        public bool Send(
            TransportConnectionHandle connection,
            ArraySegment<byte> payload,
            TransportDelivery delivery)
        {
            if (!driver.IsCreated
                || payload.Array == null
                || !connections.TryGetValue(connection.Value, out NetworkConnection utpConnection))
            {
                return false;
            }

            NetworkPipeline pipeline = delivery == TransportDelivery.Reliable
                ? reliablePipeline
                : NetworkPipeline.Null;

            int beginResult = driver.BeginSend(pipeline, utpConnection, out DataStreamWriter writer, payload.Count);
            if (beginResult != 0)
            {
                return false;
            }

            for (int i = 0; i < payload.Count; i++)
            {
                writer.WriteByte(payload.Array[payload.Offset + i]);
            }

            return driver.EndSend(writer) >= 0;
        }

        public void Flush()
        {
            if (driver.IsCreated)
            {
                driver.ScheduleFlushSend().Complete();
            }
        }

        public void Disconnect(TransportConnectionHandle connection)
        {
            if (!driver.IsCreated
                || !connections.TryGetValue(connection.Value, out NetworkConnection utpConnection))
            {
                return;
            }

            utpConnection.Disconnect(driver);
            connections.Remove(connection.Value);
        }

        public string GetRemoteEndpoint(TransportConnectionHandle connection)
        {
            if (!driver.IsCreated
                || !connections.TryGetValue(connection.Value, out NetworkConnection utpConnection))
            {
                return "unknown";
            }

            return driver.GetRemoteEndpoint(utpConnection).ToString();
        }

        public void Dispose()
        {
            connections.Clear();
            connectionIds.Clear();
            disconnectedIds.Clear();

            if (driver.IsCreated)
            {
                driver.Dispose();
            }

            driver = default;
            reliablePipeline = default;
            IsServer = false;
            BoundPort = 0;
            nextConnectionId = 1;
        }

        private TransportConnectionHandle AddConnection(NetworkConnection connection)
        {
            int id = nextConnectionId++;
            connections.Add(id, connection);
            return new TransportConnectionHandle(id);
        }

        private void EnsureNotStarted()
        {
            if (driver.IsCreated)
            {
                throw new InvalidOperationException("Transport is already started.");
            }
        }
    }
}
