using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace RPGDemo.GameFramework.Networking.Replication
{
    public sealed class ReplicationStateWriter
    {
        public const int MaxStateBytes = 1024;

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
            StateFloatUInt32 converter = new StateFloatUInt32 { Float = value };
            WriteUInt32(converter.UInt32);
        }

        public void WriteVector3(Vector3 value)
        {
            WriteSingle(value.x);
            WriteSingle(value.y);
            WriteSingle(value.z);
        }

        public byte[] ToArray() => bytes.ToArray();

        private void EnsureCapacity(int additionalBytes)
        {
            if (additionalBytes < 0 || bytes.Count > MaxStateBytes - additionalBytes)
            {
                throw new InvalidOperationException(
                    $"Replicated component state exceeds {MaxStateBytes} bytes.");
            }
        }
    }

    public sealed class ReplicationStateReader
    {
        private readonly byte[] bytes;
        private int position;

        public ReplicationStateReader(byte[] bytes)
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
            if (!TryReadByte(out byte rawValue) || rawValue > 1)
            {
                return false;
            }

            value = rawValue != 0;
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
            bool success = TryReadUInt32(out uint rawValue);
            value = unchecked((int)rawValue);
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
            if (!TryReadUInt32(out uint rawValue))
            {
                return false;
            }

            StateFloatUInt32 converter = new StateFloatUInt32 { UInt32 = rawValue };
            value = converter.Float;
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        public bool TryReadVector3(out Vector3 value)
        {
            value = default;
            if (!TryReadSingle(out float x)
                || !TryReadSingle(out float y)
                || !TryReadSingle(out float z))
            {
                return false;
            }

            value = new Vector3(x, y, z);
            return true;
        }

        private bool HasRemaining(int count) => count >= 0 && position <= bytes.Length - count;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct StateFloatUInt32
    {
        [FieldOffset(0)] public float Float;
        [FieldOffset(0)] public uint UInt32;
    }
}
