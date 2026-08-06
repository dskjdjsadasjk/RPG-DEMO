using System;
using UnityEngine;

namespace RPGDemo.GameFramework.Networking.Protocol
{
    public enum ActorMessageType : byte
    {
        ActorChannelOpen = 16,
        ActorChannelOpenAck = 17,
        ActorChannelClose = 18
    }

    public enum ActorChannelCloseReason : byte
    {
        Destroyed = 1,
        OwnerDisconnected = 2,
        ServerShutdown = 3,
        ProtocolError = 4
    }

    public readonly struct ActorSpawnMessage
    {
        public ActorSpawnMessage(
            ushort channelId,
            uint netId,
            ushort prefabId,
            uint ownerConnectionId,
            ushort authorityEpoch,
            Vector3 position,
            Quaternion rotation)
        {
            ChannelId = channelId;
            NetId = netId;
            PrefabId = prefabId;
            OwnerConnectionId = ownerConnectionId;
            AuthorityEpoch = authorityEpoch;
            Position = position;
            Rotation = rotation;
        }

        public ushort ChannelId { get; }
        public uint NetId { get; }
        public ushort PrefabId { get; }
        public uint OwnerConnectionId { get; }
        public ushort AuthorityEpoch { get; }
        public Vector3 Position { get; }
        public Quaternion Rotation { get; }
    }

    public static class ActorProtocol
    {
        public static byte[] CreateActorChannelOpen(
            ushort channelId,
            uint netId,
            ushort prefabId,
            uint ownerConnectionId,
            ushort authorityEpoch,
            Vector3 position,
            Quaternion rotation)
        {
            NetworkPacketWriter writer = new NetworkPacketWriter(43);
            writer.WriteByte((byte)ActorMessageType.ActorChannelOpen);
            writer.WriteUInt16(channelId);
            writer.WriteUInt32(netId);
            writer.WriteUInt16(prefabId);
            writer.WriteUInt32(ownerConnectionId);
            writer.WriteUInt16(authorityEpoch);
            writer.WriteSingle(position.x);
            writer.WriteSingle(position.y);
            writer.WriteSingle(position.z);
            writer.WriteSingle(rotation.x);
            writer.WriteSingle(rotation.y);
            writer.WriteSingle(rotation.z);
            writer.WriteSingle(rotation.w);
            return writer.ToArray();
        }

        public static bool TryReadActorChannelOpen(byte[] packet, out ActorSpawnMessage message)
        {
            message = default;
            NetworkPacketReader reader = new NetworkPacketReader(packet);
            if (!reader.TryReadExpectedByte((byte)ActorMessageType.ActorChannelOpen)
                || !reader.TryReadUInt16(out ushort channelId)
                || !reader.TryReadUInt32(out uint netId)
                || !reader.TryReadUInt16(out ushort prefabId)
                || !reader.TryReadUInt32(out uint ownerConnectionId)
                || !reader.TryReadUInt16(out ushort authorityEpoch)
                || !reader.TryReadSingle(out float positionX)
                || !reader.TryReadSingle(out float positionY)
                || !reader.TryReadSingle(out float positionZ)
                || !reader.TryReadSingle(out float rotationX)
                || !reader.TryReadSingle(out float rotationY)
                || !reader.TryReadSingle(out float rotationZ)
                || !reader.TryReadSingle(out float rotationW)
                || !reader.IsAtEnd)
            {
                return false;
            }

            Vector3 position = new Vector3(positionX, positionY, positionZ);
            Quaternion rotation = new Quaternion(rotationX, rotationY, rotationZ, rotationW);
            if (channelId == 0
                || netId == 0
                || prefabId == 0
                || authorityEpoch == 0
                || !IsFinite(position)
                || !IsValidRotation(rotation))
            {
                return false;
            }

            message = new ActorSpawnMessage(
                channelId,
                netId,
                prefabId,
                ownerConnectionId,
                authorityEpoch,
                position,
                rotation.normalized);
            return true;
        }

        public static byte[] CreateActorChannelOpenAck(
            ushort channelId,
            uint netId,
            ushort authorityEpoch)
        {
            NetworkPacketWriter writer = new NetworkPacketWriter(9);
            writer.WriteByte((byte)ActorMessageType.ActorChannelOpenAck);
            writer.WriteUInt16(channelId);
            writer.WriteUInt32(netId);
            writer.WriteUInt16(authorityEpoch);
            return writer.ToArray();
        }

        public static bool TryReadActorChannelOpenAck(
            byte[] packet,
            out ushort channelId,
            out uint netId,
            out ushort authorityEpoch)
        {
            channelId = 0;
            netId = 0;
            authorityEpoch = 0;
            NetworkPacketReader reader = new NetworkPacketReader(packet);
            return reader.TryReadExpectedByte((byte)ActorMessageType.ActorChannelOpenAck)
                && reader.TryReadUInt16(out channelId)
                && reader.TryReadUInt32(out netId)
                && reader.TryReadUInt16(out authorityEpoch)
                && channelId != 0
                && netId != 0
                && authorityEpoch != 0
                && reader.IsAtEnd;
        }

        public static byte[] CreateActorChannelClose(
            ushort channelId,
            uint netId,
            ushort authorityEpoch,
            ActorChannelCloseReason reason)
        {
            NetworkPacketWriter writer = new NetworkPacketWriter(10);
            writer.WriteByte((byte)ActorMessageType.ActorChannelClose);
            writer.WriteUInt16(channelId);
            writer.WriteUInt32(netId);
            writer.WriteUInt16(authorityEpoch);
            writer.WriteByte((byte)reason);
            return writer.ToArray();
        }

        public static bool TryReadActorChannelClose(
            byte[] packet,
            out ushort channelId,
            out uint netId,
            out ushort authorityEpoch,
            out ActorChannelCloseReason reason)
        {
            channelId = 0;
            netId = 0;
            authorityEpoch = 0;
            reason = default;
            NetworkPacketReader reader = new NetworkPacketReader(packet);
            if (!reader.TryReadExpectedByte((byte)ActorMessageType.ActorChannelClose)
                || !reader.TryReadUInt16(out channelId)
                || !reader.TryReadUInt32(out netId)
                || !reader.TryReadUInt16(out authorityEpoch)
                || !reader.TryReadByte(out byte rawReason)
                || channelId == 0
                || netId == 0
                || authorityEpoch == 0
                || !reader.IsAtEnd
                || !Enum.IsDefined(typeof(ActorChannelCloseReason), rawReason))
            {
                return false;
            }

            reason = (ActorChannelCloseReason)rawReason;
            return true;
        }

        public static bool TryReadMessageType(byte[] packet, out ActorMessageType messageType)
        {
            messageType = default;
            if (packet == null || packet.Length == 0)
            {
                return false;
            }

            messageType = (ActorMessageType)packet[0];
            return Enum.IsDefined(typeof(ActorMessageType), messageType);
        }

        private static bool IsFinite(Vector3 value)
        {
            return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
        }

        private static bool IsValidRotation(Quaternion value)
        {
            return IsFinite(value.x)
                && IsFinite(value.y)
                && IsFinite(value.z)
                && IsFinite(value.w)
                && value != new Quaternion(0f, 0f, 0f, 0f);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
