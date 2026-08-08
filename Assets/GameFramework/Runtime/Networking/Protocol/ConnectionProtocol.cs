using System;
using System.Text;

namespace RPGDemo.GameFramework.Networking.Protocol
{
    public enum ConnectionMessageType : byte
    {
        ClientHello = 1,
        ServerChallenge = 2,
        ClientLogin = 3,
        ServerWelcome = 4,
        ClientReady = 5,
        ServerReject = 6
    }

    public enum ConnectionRejectReason : byte
    {
        InvalidPacket = 1,
        ProtocolMismatch = 2,
        SchemaMismatch = 3,
        InvalidChallenge = 4,
        InvalidLogin = 5,
        UnexpectedMessage = 6,
        HandshakeTimeout = 7
    }

    public static class ConnectionProtocol
    {
        public const ushort ProtocolVersion = 2;
        public const uint SchemaHash = 0x52504734;
        public const int MaxDisplayNameBytes = 32;

        public static byte[] CreateClientHello(ulong clientNonce)
        {
            NetworkPacketWriter writer = new NetworkPacketWriter(15);
            writer.WriteByte((byte)ConnectionMessageType.ClientHello);
            writer.WriteUInt16(ProtocolVersion);
            writer.WriteUInt32(SchemaHash);
            writer.WriteUInt64(clientNonce);
            return writer.ToArray();
        }

        public static bool TryReadClientHello(
            byte[] packet,
            out ushort protocolVersion,
            out uint schemaHash,
            out ulong clientNonce)
        {
            protocolVersion = 0;
            schemaHash = 0;
            clientNonce = 0;
            NetworkPacketReader reader = new NetworkPacketReader(packet);
            return reader.TryReadExpectedByte((byte)ConnectionMessageType.ClientHello)
                && reader.TryReadUInt16(out protocolVersion)
                && reader.TryReadUInt32(out schemaHash)
                && reader.TryReadUInt64(out clientNonce)
                && reader.IsAtEnd;
        }

        public static byte[] CreateServerChallenge(ulong clientNonce, ulong serverNonce)
        {
            NetworkPacketWriter writer = new NetworkPacketWriter(19);
            writer.WriteByte((byte)ConnectionMessageType.ServerChallenge);
            writer.WriteUInt16(ProtocolVersion);
            writer.WriteUInt64(clientNonce);
            writer.WriteUInt64(serverNonce);
            return writer.ToArray();
        }

        public static bool TryReadServerChallenge(
            byte[] packet,
            out ushort protocolVersion,
            out ulong clientNonce,
            out ulong serverNonce)
        {
            protocolVersion = 0;
            clientNonce = 0;
            serverNonce = 0;
            NetworkPacketReader reader = new NetworkPacketReader(packet);
            return reader.TryReadExpectedByte((byte)ConnectionMessageType.ServerChallenge)
                && reader.TryReadUInt16(out protocolVersion)
                && reader.TryReadUInt64(out clientNonce)
                && reader.TryReadUInt64(out serverNonce)
                && reader.IsAtEnd;
        }

        public static byte[] CreateClientLogin(ulong serverNonce, string displayName)
        {
            byte[] nameBytes = EncodeDisplayName(displayName);
            NetworkPacketWriter writer = new NetworkPacketWriter(10 + nameBytes.Length);
            writer.WriteByte((byte)ConnectionMessageType.ClientLogin);
            writer.WriteUInt64(serverNonce);
            writer.WriteByte((byte)nameBytes.Length);
            writer.WriteBytes(nameBytes);
            return writer.ToArray();
        }

        public static bool TryReadClientLogin(byte[] packet, out ulong serverNonce, out string displayName)
        {
            serverNonce = 0;
            displayName = null;
            NetworkPacketReader reader = new NetworkPacketReader(packet);
            return reader.TryReadExpectedByte((byte)ConnectionMessageType.ClientLogin)
                && reader.TryReadUInt64(out serverNonce)
                && reader.TryReadString(MaxDisplayNameBytes, out displayName)
                && reader.IsAtEnd;
        }

        public static byte[] CreateServerWelcome(uint connectionId, uint serverTick, ushort tickRate)
        {
            NetworkPacketWriter writer = new NetworkPacketWriter(11);
            writer.WriteByte((byte)ConnectionMessageType.ServerWelcome);
            writer.WriteUInt32(connectionId);
            writer.WriteUInt32(serverTick);
            writer.WriteUInt16(tickRate);
            return writer.ToArray();
        }

        public static bool TryReadServerWelcome(
            byte[] packet,
            out uint connectionId,
            out uint serverTick,
            out ushort tickRate)
        {
            connectionId = 0;
            serverTick = 0;
            tickRate = 0;
            NetworkPacketReader reader = new NetworkPacketReader(packet);
            return reader.TryReadExpectedByte((byte)ConnectionMessageType.ServerWelcome)
                && reader.TryReadUInt32(out connectionId)
                && reader.TryReadUInt32(out serverTick)
                && reader.TryReadUInt16(out tickRate)
                && reader.IsAtEnd;
        }

        public static byte[] CreateClientReady(uint connectionId)
        {
            NetworkPacketWriter writer = new NetworkPacketWriter(5);
            writer.WriteByte((byte)ConnectionMessageType.ClientReady);
            writer.WriteUInt32(connectionId);
            return writer.ToArray();
        }

        public static bool TryReadClientReady(byte[] packet, out uint connectionId)
        {
            connectionId = 0;
            NetworkPacketReader reader = new NetworkPacketReader(packet);
            return reader.TryReadExpectedByte((byte)ConnectionMessageType.ClientReady)
                && reader.TryReadUInt32(out connectionId)
                && reader.IsAtEnd;
        }

        public static byte[] CreateServerReject(ConnectionRejectReason reason, string message)
        {
            byte[] messageBytes = Encoding.UTF8.GetBytes(message ?? string.Empty);
            if (messageBytes.Length > byte.MaxValue)
            {
                Array.Resize(ref messageBytes, byte.MaxValue);
            }

            NetworkPacketWriter writer = new NetworkPacketWriter(3 + messageBytes.Length);
            writer.WriteByte((byte)ConnectionMessageType.ServerReject);
            writer.WriteByte((byte)reason);
            writer.WriteByte((byte)messageBytes.Length);
            writer.WriteBytes(messageBytes);
            return writer.ToArray();
        }

        public static bool TryReadServerReject(
            byte[] packet,
            out ConnectionRejectReason reason,
            out string message)
        {
            reason = default;
            message = null;
            NetworkPacketReader reader = new NetworkPacketReader(packet);
            if (!reader.TryReadExpectedByte((byte)ConnectionMessageType.ServerReject)
                || !reader.TryReadByte(out byte rawReason)
                || !reader.TryReadString(byte.MaxValue, out message)
                || !reader.IsAtEnd)
            {
                return false;
            }

            reason = (ConnectionRejectReason)rawReason;
            return true;
        }

        public static bool TryReadMessageType(byte[] packet, out ConnectionMessageType messageType)
        {
            messageType = default;
            if (packet == null || packet.Length == 0)
            {
                return false;
            }

            messageType = (ConnectionMessageType)packet[0];
            return Enum.IsDefined(typeof(ConnectionMessageType), messageType);
        }

        private static byte[] EncodeDisplayName(string displayName)
        {
            string value = string.IsNullOrWhiteSpace(displayName) ? "Player" : displayName.Trim();
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            if (bytes.Length > MaxDisplayNameBytes)
            {
                Array.Resize(ref bytes, MaxDisplayNameBytes);
            }

            return bytes;
        }

    }
}
