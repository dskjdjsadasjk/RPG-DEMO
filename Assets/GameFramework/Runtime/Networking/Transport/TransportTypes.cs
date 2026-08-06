using System;

namespace RPGDemo.GameFramework.Networking.Transport
{
    public readonly struct TransportConnectionHandle : IEquatable<TransportConnectionHandle>
    {
        public static readonly TransportConnectionHandle Invalid = new TransportConnectionHandle(0);

        public TransportConnectionHandle(int value)
        {
            Value = value;
        }

        public int Value { get; }
        public bool IsValid => Value > 0;

        public bool Equals(TransportConnectionHandle other) => Value == other.Value;
        public override bool Equals(object obj) => obj is TransportConnectionHandle other && Equals(other);
        public override int GetHashCode() => Value;
        public override string ToString() => IsValid ? Value.ToString() : "Invalid";
    }

    public enum TransportEventType : byte
    {
        Connected,
        Data,
        Disconnected
    }

    public enum TransportDelivery : byte
    {
        Unreliable,
        Reliable
    }

    public readonly struct TransportEvent
    {
        public TransportEvent(
            TransportEventType type,
            TransportConnectionHandle connection,
            byte[] payload = null,
            string reason = null)
        {
            Type = type;
            Connection = connection;
            Payload = payload;
            Reason = reason;
        }

        public TransportEventType Type { get; }
        public TransportConnectionHandle Connection { get; }
        public byte[] Payload { get; }
        public string Reason { get; }
    }
}
