using System;
using RPGDemo.GameFramework.Networking.Replication;

namespace RPGDemo.GameFramework.Networking.Protocol
{
    public enum RpcMessageType : byte
    {
        Invoke = 64
    }

    public readonly struct RpcInvocationMessage
    {
        public RpcInvocationMessage(
            ushort channelId,
            uint netId,
            ushort authorityEpoch,
            ushort replicationId,
            ushort functionId,
            byte[] payload)
        {
            ChannelId = channelId;
            NetId = netId;
            AuthorityEpoch = authorityEpoch;
            ReplicationId = replicationId;
            FunctionId = functionId;
            Payload = payload;
        }

        public ushort ChannelId { get; }
        public uint NetId { get; }
        public ushort AuthorityEpoch { get; }
        public ushort ReplicationId { get; }
        public ushort FunctionId { get; }
        public byte[] Payload { get; }
    }

    public static class RpcProtocol
    {
        private const int HeaderSize = 15;

        public static bool TryReadMessageType(byte[] packet, out RpcMessageType messageType)
        {
            messageType = default;
            if (packet == null || packet.Length == 0)
            {
                return false;
            }

            messageType = (RpcMessageType)packet[0];
            return Enum.IsDefined(typeof(RpcMessageType), messageType);
        }

        public static byte[] CreateInvocation(
            ushort channelId,
            uint netId,
            ushort authorityEpoch,
            ushort replicationId,
            ushort functionId,
            byte[] payload)
        {
            payload ??= Array.Empty<byte>();
            if (channelId == 0 || netId == 0 || authorityEpoch == 0
                || replicationId == 0 || functionId == 0
                || payload.Length > RpcPayloadWriter.MaxPayloadBytes)
            {
                throw new ArgumentOutOfRangeException(nameof(payload), "Invalid RPC invocation fields.");
            }

            NetworkPacketWriter writer = new NetworkPacketWriter(HeaderSize + payload.Length);
            writer.WriteByte((byte)RpcMessageType.Invoke);
            writer.WriteUInt16(channelId);
            writer.WriteUInt32(netId);
            writer.WriteUInt16(authorityEpoch);
            writer.WriteUInt16(replicationId);
            writer.WriteUInt16(functionId);
            writer.WriteUInt16((ushort)payload.Length);
            writer.WriteBytes(payload);
            return writer.ToArray();
        }

        public static bool TryReadInvocation(byte[] packet, out RpcInvocationMessage message)
        {
            message = default;
            NetworkPacketReader reader = new NetworkPacketReader(packet);
            if (!reader.TryReadExpectedByte((byte)RpcMessageType.Invoke)
                || !reader.TryReadUInt16(out ushort channelId)
                || !reader.TryReadUInt32(out uint netId)
                || !reader.TryReadUInt16(out ushort authorityEpoch)
                || !reader.TryReadUInt16(out ushort replicationId)
                || !reader.TryReadUInt16(out ushort functionId)
                || !reader.TryReadUInt16(out ushort payloadLength)
                || payloadLength > RpcPayloadWriter.MaxPayloadBytes
                || !reader.TryReadBytes(payloadLength, out byte[] payload)
                || !reader.IsAtEnd
                || channelId == 0 || netId == 0 || authorityEpoch == 0
                || replicationId == 0 || functionId == 0)
            {
                return false;
            }

            message = new RpcInvocationMessage(
                channelId,
                netId,
                authorityEpoch,
                replicationId,
                functionId,
                payload);
            return true;
        }
    }
}
