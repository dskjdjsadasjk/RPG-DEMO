using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace RPGDemo.GameFramework.Networking.Replication
{
    public enum RpcTarget : byte
    {
        Server = 1,
        OwningClient = 2,
        Multicast = 3
    }

    public enum RpcDelivery : byte
    {
        Unreliable = 1,
        Reliable = 2
    }

    public delegate bool RpcHandler(RpcPayloadReader reader);

    public sealed class RpcRegistry
    {
        private const int MaxFunctions = 256;
        private readonly Dictionary<ushort, RpcDescriptor> descriptors
            = new Dictionary<ushort, RpcDescriptor>();
        private bool isSealed;

        public void Register(
            ushort functionId,
            RpcTarget target,
            RpcDelivery delivery,
            RpcHandler handler)
        {
            if (isSealed)
            {
                throw new InvalidOperationException("RPC registry is already sealed.");
            }

            if (functionId == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(functionId), "RPC FunctionId 0 is reserved.");
            }

            if (handler == null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            if (descriptors.Count >= MaxFunctions)
            {
                throw new InvalidOperationException($"A NetworkBehaviour cannot register more than {MaxFunctions} RPCs.");
            }

            if (descriptors.ContainsKey(functionId))
            {
                throw new InvalidOperationException($"Duplicate RPC FunctionId {functionId}.");
            }

            descriptors.Add(functionId, new RpcDescriptor(functionId, target, delivery, handler));
        }

        internal bool TryGet(ushort functionId, out RpcDescriptor descriptor)
        {
            return descriptors.TryGetValue(functionId, out descriptor);
        }

        internal void Seal()
        {
            isSealed = true;
        }
    }

    internal sealed class RpcDescriptor
    {
        public RpcDescriptor(
            ushort functionId,
            RpcTarget target,
            RpcDelivery delivery,
            RpcHandler handler)
        {
            FunctionId = functionId;
            Target = target;
            Delivery = delivery;
            Handler = handler;
        }

        public ushort FunctionId { get; }
        public RpcTarget Target { get; }
        public RpcDelivery Delivery { get; }
        public RpcHandler Handler { get; }
    }

    internal readonly struct PendingRpcCall
    {
        public PendingRpcCall(ushort replicationId, ushort functionId, byte[] payload)
        {
            ReplicationId = replicationId;
            FunctionId = functionId;
            Payload = payload;
        }

        public ushort ReplicationId { get; }
        public ushort FunctionId { get; }
        public byte[] Payload { get; }
    }

    public sealed class RpcPayloadWriter
    {
        public const int MaxPayloadBytes = 1024;
        private readonly List<byte> bytes = new List<byte>(64);

        public int Length => bytes.Count;

        public void WriteByte(byte value)
        {
            EnsureCapacity(1);
            bytes.Add(value);
        }

        public void WriteBoolean(bool value) => WriteByte(value ? (byte)1 : (byte)0);

        public void WriteUInt16(ushort value)
        {
            EnsureCapacity(2);
            bytes.Add((byte)value);
            bytes.Add((byte)(value >> 8));
        }

        public void WriteInt32(int value) => WriteUInt32(unchecked((uint)value));

        public void WriteUInt32(uint value)
        {
            EnsureCapacity(4);
            for (int shift = 0; shift < 32; shift += 8)
            {
                bytes.Add((byte)(value >> shift));
            }
        }

        public void WriteSingle(float value)
        {
            RpcFloatUInt32 converter = new RpcFloatUInt32 { Float = value };
            WriteUInt32(converter.UInt32);
        }

        public void WriteVector3(Vector3 value)
        {
            WriteSingle(value.x);
            WriteSingle(value.y);
            WriteSingle(value.z);
        }

        public void WriteQuaternion(Quaternion value)
        {
            WriteSingle(value.x);
            WriteSingle(value.y);
            WriteSingle(value.z);
            WriteSingle(value.w);
        }

        public void WriteString(string value)
        {
            byte[] encoded = Encoding.UTF8.GetBytes(value ?? string.Empty);
            if (encoded.Length > ushort.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "RPC string is too large.");
            }

            WriteUInt16((ushort)encoded.Length);
            WriteRawBytes(encoded);
        }

        public byte[] ToArray() => bytes.ToArray();

        private void WriteRawBytes(byte[] value)
        {
            EnsureCapacity(value.Length);
            bytes.AddRange(value);
        }

        private void EnsureCapacity(int additionalBytes)
        {
            if (additionalBytes < 0 || bytes.Count > MaxPayloadBytes - additionalBytes)
            {
                throw new InvalidOperationException($"RPC payload exceeds {MaxPayloadBytes} bytes.");
            }
        }
    }

    public sealed class RpcPayloadReader
    {
        private readonly byte[] bytes;
        private int position;

        internal RpcPayloadReader(byte[] bytes)
        {
            this.bytes = bytes ?? Array.Empty<byte>();
        }

        public bool IsAtEnd => position == bytes.Length;

        public bool TryReadByte(out byte value)
        {
            value = 0;
            if (!HasRemaining(1))
            {
                return false;
            }

            value = bytes[position++];
            return true;
        }

        public bool TryReadBoolean(out bool value)
        {
            value = false;
            if (!TryReadByte(out byte raw) || raw > 1)
            {
                return false;
            }

            value = raw != 0;
            return true;
        }

        public bool TryReadUInt16(out ushort value)
        {
            value = 0;
            if (!HasRemaining(2))
            {
                return false;
            }

            value = (ushort)(bytes[position] | (bytes[position + 1] << 8));
            position += 2;
            return true;
        }

        public bool TryReadInt32(out int value)
        {
            bool success = TryReadUInt32(out uint raw);
            value = unchecked((int)raw);
            return success;
        }

        public bool TryReadUInt32(out uint value)
        {
            value = 0;
            if (!HasRemaining(4))
            {
                return false;
            }

            for (int shift = 0; shift < 32; shift += 8)
            {
                value |= (uint)bytes[position++] << shift;
            }

            return true;
        }

        public bool TryReadSingle(out float value)
        {
            value = 0f;
            if (!TryReadUInt32(out uint raw))
            {
                return false;
            }

            RpcFloatUInt32 converter = new RpcFloatUInt32 { UInt32 = raw };
            value = converter.Float;
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        public bool TryReadVector3(out Vector3 value)
        {
            value = default;
            return TryReadSingle(out value.x)
                && TryReadSingle(out value.y)
                && TryReadSingle(out value.z);
        }

        public bool TryReadQuaternion(out Quaternion value)
        {
            value = default;
            return TryReadSingle(out value.x)
                && TryReadSingle(out value.y)
                && TryReadSingle(out value.z)
                && TryReadSingle(out value.w)
                && value != new Quaternion(0f, 0f, 0f, 0f);
        }

        public bool TryReadString(out string value)
        {
            value = null;
            if (!TryReadUInt16(out ushort byteCount) || !HasRemaining(byteCount))
            {
                return false;
            }

            value = Encoding.UTF8.GetString(bytes, position, byteCount);
            position += byteCount;
            return true;
        }

        private bool HasRemaining(int count) => count >= 0 && position <= bytes.Length - count;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct RpcFloatUInt32
    {
        [FieldOffset(0)] public float Float;
        [FieldOffset(0)] public uint UInt32;
    }
}
