using System;
using RPGDemo.GameFramework.Networking.Replication;

namespace RPGDemo.GameFramework.Networking.Protocol
{
    public enum ObjectReplicationMessageType : byte
    {
        ObjectState = 32
    }

    [Flags]
    public enum ObjectStateFlags : byte
    {
        None = 0,
        Initial = 1
    }

    public readonly struct ObjectStateMessage
    {
        public ObjectStateMessage(
            ushort channelId,
            uint netId,
            ushort authorityEpoch,
            ushort replicationId,
            ushort sequence,
            ObjectStateFlags flags,
            byte[] state)
        {
            ChannelId = channelId;
            NetId = netId;
            AuthorityEpoch = authorityEpoch;
            ReplicationId = replicationId;
            Sequence = sequence;
            Flags = flags;
            State = state;
        }

        public ushort ChannelId { get; }
        public uint NetId { get; }
        public ushort AuthorityEpoch { get; }
        public ushort ReplicationId { get; }
        public ushort Sequence { get; }
        public ObjectStateFlags Flags { get; }
        public byte[] State { get; }
        public bool IsInitialState => (Flags & ObjectStateFlags.Initial) != 0;
    }

    public static class ObjectReplicationProtocol
    {
        private const int HeaderSize = 16;

        public static bool TryReadMessageType(
            byte[] packet,
            out ObjectReplicationMessageType messageType)
        {
            messageType = default;
            if (packet == null || packet.Length == 0)
            {
                return false;
            }

            byte rawType = packet[0];
            if (rawType != (byte)ObjectReplicationMessageType.ObjectState)
            {
                return false;
            }

            messageType = (ObjectReplicationMessageType)rawType;
            return true;
        }

        public static byte[] CreateObjectState(
            ushort channelId,
            uint netId,
            ushort authorityEpoch,
            ushort replicationId,
            ushort sequence,
            ObjectStateFlags flags,
            byte[] state)
        {
            if (channelId == 0
                || netId == 0
                || authorityEpoch == 0
                || replicationId == 0
                || sequence == 0
                || state == null
                || state.Length > ReplicationStateWriter.MaxStateBytes)
            {
                throw new ArgumentException("Invalid replicated object state packet fields.");
            }

            NetworkPacketWriter writer = new NetworkPacketWriter(HeaderSize + state.Length);
            writer.WriteByte((byte)ObjectReplicationMessageType.ObjectState);
            writer.WriteUInt16(channelId);
            writer.WriteUInt32(netId);
            writer.WriteUInt16(authorityEpoch);
            writer.WriteUInt16(replicationId);
            writer.WriteUInt16(sequence);
            writer.WriteByte((byte)flags);
            writer.WriteUInt16((ushort)state.Length);
            writer.WriteBytes(state);
            return writer.ToArray();
        }

        public static bool TryReadObjectState(byte[] packet, out ObjectStateMessage message)
        {
            message = default;
            NetworkPacketReader reader = new NetworkPacketReader(packet);
            if (!reader.TryReadExpectedByte((byte)ObjectReplicationMessageType.ObjectState)
                || !reader.TryReadUInt16(out ushort channelId)
                || !reader.TryReadUInt32(out uint netId)
                || !reader.TryReadUInt16(out ushort authorityEpoch)
                || !reader.TryReadUInt16(out ushort replicationId)
                || !reader.TryReadUInt16(out ushort sequence)
                || !reader.TryReadByte(out byte rawFlags)
                || !reader.TryReadUInt16(out ushort stateLength)
                || stateLength > ReplicationStateWriter.MaxStateBytes
                || !reader.TryReadBytes(stateLength, out byte[] state)
                || !reader.IsAtEnd
                || channelId == 0
                || netId == 0
                || authorityEpoch == 0
                || replicationId == 0
                || sequence == 0
                || (rawFlags & ~(byte)ObjectStateFlags.Initial) != 0)
            {
                return false;
            }

            message = new ObjectStateMessage(
                channelId,
                netId,
                authorityEpoch,
                replicationId,
                sequence,
                (ObjectStateFlags)rawFlags,
                state);
            return true;
        }
    }
}
