using System;
using System.Collections.Generic;

namespace RPGDemo.GameFramework.Networking.Transport
{
    public interface INetworkTransport : IDisposable
    {
        bool IsCreated { get; }
        bool IsServer { get; }
        ushort BoundPort { get; }

        void StartServer(ushort port);
        TransportConnectionHandle StartClient(string address, ushort port);
        void PollEvents(List<TransportEvent> output);
        bool Send(TransportConnectionHandle connection, ArraySegment<byte> payload, TransportDelivery delivery);
        void Flush();
        void Disconnect(TransportConnectionHandle connection);
        string GetRemoteEndpoint(TransportConnectionHandle connection);
    }
}
