using System;
using RPGDemo.GameFramework;
using UnityEngine;

namespace RPGDemo.GameFramework.Networking.Protocol
{
    public enum CharacterMovementMessageType : byte
    {
        ClientMove = 48,
        ServerMoveAck = 49,
        TransformSnapshot = 50
    }

    public readonly struct CharacterMoveMessage
    {
        public CharacterMoveMessage(
            uint netId,
            ushort authorityEpoch,
            uint sequence,
            uint clientTick,
            float deltaTime,
            Vector3 worldInput,
            float controlYaw)
        {
            NetId = netId;
            AuthorityEpoch = authorityEpoch;
            Sequence = sequence;
            ClientTick = clientTick;
            DeltaTime = deltaTime;
            WorldInput = worldInput;
            ControlYaw = controlYaw;
        }

        public uint NetId { get; }
        public ushort AuthorityEpoch { get; }
        public uint Sequence { get; }
        public uint ClientTick { get; }
        public float DeltaTime { get; }
        public Vector3 WorldInput { get; }
        public float ControlYaw { get; }
    }

    public readonly struct CharacterMoveAckMessage
    {
        public CharacterMoveAckMessage(
            uint netId,
            ushort authorityEpoch,
            uint acknowledgedSequence,
            uint serverTick,
            Vector3 position,
            Quaternion rotation,
            Vector3 velocity,
            MovementMode movementMode)
        {
            NetId = netId;
            AuthorityEpoch = authorityEpoch;
            AcknowledgedSequence = acknowledgedSequence;
            ServerTick = serverTick;
            Position = position;
            Rotation = rotation;
            Velocity = velocity;
            MovementMode = movementMode;
        }

        public uint NetId { get; }
        public ushort AuthorityEpoch { get; }
        public uint AcknowledgedSequence { get; }
        public uint ServerTick { get; }
        public Vector3 Position { get; }
        public Quaternion Rotation { get; }
        public Vector3 Velocity { get; }
        public MovementMode MovementMode { get; }
    }

    public readonly struct CharacterSnapshotMessage
    {
        public CharacterSnapshotMessage(
            uint netId,
            ushort authorityEpoch,
            uint serverTick,
            Vector3 position,
            Quaternion rotation,
            Vector3 velocity,
            MovementMode movementMode)
        {
            NetId = netId;
            AuthorityEpoch = authorityEpoch;
            ServerTick = serverTick;
            Position = position;
            Rotation = rotation;
            Velocity = velocity;
            MovementMode = movementMode;
        }

        public uint NetId { get; }
        public ushort AuthorityEpoch { get; }
        public uint ServerTick { get; }
        public Vector3 Position { get; }
        public Quaternion Rotation { get; }
        public Vector3 Velocity { get; }
        public MovementMode MovementMode { get; }
    }

    public static class CharacterMovementProtocol
    {
        private const int ClientMoveSize = 35;
        private const int MoveAckSize = 56;
        private const int SnapshotSize = 52;

        public static bool TryReadMessageType(
            byte[] packet,
            out CharacterMovementMessageType messageType)
        {
            messageType = default;
            if (packet == null || packet.Length == 0)
            {
                return false;
            }

            messageType = (CharacterMovementMessageType)packet[0];
            return Enum.IsDefined(typeof(CharacterMovementMessageType), messageType);
        }

        public static byte[] CreateClientMove(
            uint netId,
            ushort authorityEpoch,
            uint sequence,
            uint clientTick,
            float deltaTime,
            Vector3 worldInput,
            float controlYaw)
        {
            NetworkPacketWriter writer = new NetworkPacketWriter(ClientMoveSize);
            writer.WriteByte((byte)CharacterMovementMessageType.ClientMove);
            writer.WriteUInt32(netId);
            writer.WriteUInt16(authorityEpoch);
            writer.WriteUInt32(sequence);
            writer.WriteUInt32(clientTick);
            writer.WriteSingle(deltaTime);
            WriteVector3(writer, worldInput);
            writer.WriteSingle(controlYaw);
            return writer.ToArray();
        }

        public static bool TryReadClientMove(byte[] packet, out CharacterMoveMessage message)
        {
            message = default;
            NetworkPacketReader reader = new NetworkPacketReader(packet);
            if (!reader.TryReadExpectedByte((byte)CharacterMovementMessageType.ClientMove)
                || !reader.TryReadUInt32(out uint netId)
                || !reader.TryReadUInt16(out ushort authorityEpoch)
                || !reader.TryReadUInt32(out uint sequence)
                || !reader.TryReadUInt32(out uint clientTick)
                || !reader.TryReadSingle(out float deltaTime)
                || !TryReadVector3(reader, out Vector3 worldInput)
                || !reader.TryReadSingle(out float controlYaw)
                || !reader.IsAtEnd
                || netId == 0
                || authorityEpoch == 0
                || sequence == 0
                || deltaTime <= 0f
                || !IsFinite(deltaTime)
                || !IsFinite(worldInput)
                || !IsFinite(controlYaw))
            {
                return false;
            }

            message = new CharacterMoveMessage(
                netId,
                authorityEpoch,
                sequence,
                clientTick,
                deltaTime,
                worldInput,
                controlYaw);
            return true;
        }

        public static byte[] CreateServerMoveAck(
            uint netId,
            ushort authorityEpoch,
            uint acknowledgedSequence,
            uint serverTick,
            Vector3 position,
            Quaternion rotation,
            Vector3 velocity,
            MovementMode movementMode)
        {
            NetworkPacketWriter writer = new NetworkPacketWriter(MoveAckSize);
            writer.WriteByte((byte)CharacterMovementMessageType.ServerMoveAck);
            writer.WriteUInt32(netId);
            writer.WriteUInt16(authorityEpoch);
            writer.WriteUInt32(acknowledgedSequence);
            writer.WriteUInt32(serverTick);
            WriteVector3(writer, position);
            WriteQuaternion(writer, rotation);
            WriteVector3(writer, velocity);
            writer.WriteByte((byte)movementMode);
            return writer.ToArray();
        }

        public static bool TryReadServerMoveAck(
            byte[] packet,
            out CharacterMoveAckMessage message)
        {
            message = default;
            NetworkPacketReader reader = new NetworkPacketReader(packet);
            if (!reader.TryReadExpectedByte((byte)CharacterMovementMessageType.ServerMoveAck)
                || !reader.TryReadUInt32(out uint netId)
                || !reader.TryReadUInt16(out ushort authorityEpoch)
                || !reader.TryReadUInt32(out uint acknowledgedSequence)
                || !reader.TryReadUInt32(out uint serverTick)
                || !TryReadVector3(reader, out Vector3 position)
                || !TryReadQuaternion(reader, out Quaternion rotation)
                || !TryReadVector3(reader, out Vector3 velocity)
                || !reader.TryReadByte(out byte rawMovementMode)
                || !reader.IsAtEnd
                || netId == 0
                || authorityEpoch == 0
                || acknowledgedSequence == 0
                || !IsValidState(position, rotation, velocity, rawMovementMode))
            {
                return false;
            }

            message = new CharacterMoveAckMessage(
                netId,
                authorityEpoch,
                acknowledgedSequence,
                serverTick,
                position,
                rotation.normalized,
                velocity,
                (MovementMode)rawMovementMode);
            return true;
        }

        public static byte[] CreateTransformSnapshot(
            uint netId,
            ushort authorityEpoch,
            uint serverTick,
            Vector3 position,
            Quaternion rotation,
            Vector3 velocity,
            MovementMode movementMode)
        {
            NetworkPacketWriter writer = new NetworkPacketWriter(SnapshotSize);
            writer.WriteByte((byte)CharacterMovementMessageType.TransformSnapshot);
            writer.WriteUInt32(netId);
            writer.WriteUInt16(authorityEpoch);
            writer.WriteUInt32(serverTick);
            WriteVector3(writer, position);
            WriteQuaternion(writer, rotation);
            WriteVector3(writer, velocity);
            writer.WriteByte((byte)movementMode);
            return writer.ToArray();
        }

        public static bool TryReadTransformSnapshot(
            byte[] packet,
            out CharacterSnapshotMessage message)
        {
            message = default;
            NetworkPacketReader reader = new NetworkPacketReader(packet);
            if (!reader.TryReadExpectedByte((byte)CharacterMovementMessageType.TransformSnapshot)
                || !reader.TryReadUInt32(out uint netId)
                || !reader.TryReadUInt16(out ushort authorityEpoch)
                || !reader.TryReadUInt32(out uint serverTick)
                || !TryReadVector3(reader, out Vector3 position)
                || !TryReadQuaternion(reader, out Quaternion rotation)
                || !TryReadVector3(reader, out Vector3 velocity)
                || !reader.TryReadByte(out byte rawMovementMode)
                || !reader.IsAtEnd
                || netId == 0
                || authorityEpoch == 0
                || !IsValidState(position, rotation, velocity, rawMovementMode))
            {
                return false;
            }

            message = new CharacterSnapshotMessage(
                netId,
                authorityEpoch,
                serverTick,
                position,
                rotation.normalized,
                velocity,
                (MovementMode)rawMovementMode);
            return true;
        }

        private static bool IsValidState(
            Vector3 position,
            Quaternion rotation,
            Vector3 velocity,
            byte rawMovementMode)
        {
            return IsFinite(position)
                && IsFinite(velocity)
                && IsFinite(rotation.x)
                && IsFinite(rotation.y)
                && IsFinite(rotation.z)
                && IsFinite(rotation.w)
                && rotation != new Quaternion(0f, 0f, 0f, 0f)
                && Enum.IsDefined(typeof(MovementMode), (MovementMode)rawMovementMode);
        }

        private static void WriteVector3(NetworkPacketWriter writer, Vector3 value)
        {
            writer.WriteSingle(value.x);
            writer.WriteSingle(value.y);
            writer.WriteSingle(value.z);
        }

        private static bool TryReadVector3(NetworkPacketReader reader, out Vector3 value)
        {
            value = default;
            if (!reader.TryReadSingle(out float x)
                || !reader.TryReadSingle(out float y)
                || !reader.TryReadSingle(out float z))
            {
                return false;
            }

            value = new Vector3(x, y, z);
            return true;
        }

        private static void WriteQuaternion(NetworkPacketWriter writer, Quaternion value)
        {
            writer.WriteSingle(value.x);
            writer.WriteSingle(value.y);
            writer.WriteSingle(value.z);
            writer.WriteSingle(value.w);
        }

        private static bool TryReadQuaternion(NetworkPacketReader reader, out Quaternion value)
        {
            value = default;
            if (!reader.TryReadSingle(out float x)
                || !reader.TryReadSingle(out float y)
                || !reader.TryReadSingle(out float z)
                || !reader.TryReadSingle(out float w))
            {
                return false;
            }

            value = new Quaternion(x, y, z, w);
            return true;
        }

        private static bool IsFinite(Vector3 value)
        {
            return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
