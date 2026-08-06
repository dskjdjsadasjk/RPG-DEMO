using System;
using System.Runtime.InteropServices;
using System.Text;

namespace RPGDemo.GameFramework.Networking.Protocol
{
    internal sealed class NetworkPacketWriter
    {
        private readonly byte[] buffer;
        private int position;

        public NetworkPacketWriter(int capacity)
        {
            if (capacity < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity));
            }

            buffer = new byte[capacity];
        }

        public void WriteByte(byte value) => buffer[position++] = value;

        public void WriteUInt16(ushort value)
        {
            buffer[position++] = (byte)value;
            buffer[position++] = (byte)(value >> 8);
        }

        public void WriteUInt32(uint value)
        {
            for (int shift = 0; shift < 32; shift += 8)
            {
                buffer[position++] = (byte)(value >> shift);
            }
        }

        public void WriteUInt64(ulong value)
        {
            for (int shift = 0; shift < 64; shift += 8)
            {
                buffer[position++] = (byte)(value >> shift);
            }
        }

        public void WriteSingle(float value)
        {
            FloatUInt32 converter = new FloatUInt32 { Float = value };
            WriteUInt32(converter.UInt32);
        }

        public void WriteBytes(byte[] value)
        {
            if (value == null)
            {
                throw new ArgumentNullException(nameof(value));
            }

            Buffer.BlockCopy(value, 0, buffer, position, value.Length);
            position += value.Length;
        }

        public byte[] ToArray()
        {
            if (position == buffer.Length)
            {
                return buffer;
            }

            byte[] result = new byte[position];
            Buffer.BlockCopy(buffer, 0, result, 0, position);
            return result;
        }
    }

    internal sealed class NetworkPacketReader
    {
        private readonly byte[] buffer;
        private int position;

        public NetworkPacketReader(byte[] buffer)
        {
            this.buffer = buffer ?? Array.Empty<byte>();
        }

        public bool IsAtEnd => position == buffer.Length;

        public bool TryReadExpectedByte(byte expected)
        {
            return TryReadByte(out byte actual) && actual == expected;
        }

        public bool TryReadByte(out byte value)
        {
            value = 0;
            if (position >= buffer.Length)
            {
                return false;
            }

            value = buffer[position++];
            return true;
        }

        public bool TryReadUInt16(out ushort value)
        {
            value = 0;
            if (!HasRemaining(2))
            {
                return false;
            }

            value = (ushort)(buffer[position] | (buffer[position + 1] << 8));
            position += 2;
            return true;
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
                value |= (uint)buffer[position++] << shift;
            }

            return true;
        }

        public bool TryReadUInt64(out ulong value)
        {
            value = 0;
            if (!HasRemaining(8))
            {
                return false;
            }

            for (int shift = 0; shift < 64; shift += 8)
            {
                value |= (ulong)buffer[position++] << shift;
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

            FloatUInt32 converter = new FloatUInt32 { UInt32 = rawValue };
            value = converter.Float;
            return true;
        }

        public bool TryReadString(int maxByteCount, out string value)
        {
            value = null;
            if (!TryReadByte(out byte byteCount)
                || byteCount > maxByteCount
                || !HasRemaining(byteCount))
            {
                return false;
            }

            value = Encoding.UTF8.GetString(buffer, position, byteCount);
            position += byteCount;
            return true;
        }

        private bool HasRemaining(int count) => count >= 0 && position <= buffer.Length - count;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct FloatUInt32
    {
        [FieldOffset(0)] public float Float;
        [FieldOffset(0)] public uint UInt32;
    }
}
