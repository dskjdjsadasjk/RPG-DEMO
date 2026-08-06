using System;
using RPGDemo.GameFramework.Networking.Identity;

namespace RPGDemo.GameFramework.Networking.Replication
{
    public enum ActorChannelState : byte
    {
        Opening,
        Open,
        Closing,
        Closed
    }

    public sealed class ActorReplicationChannel
    {
        internal ActorReplicationChannel(
            ushort channelId,
            GameNetConnection connection,
            NetworkIdentity actor,
            bool remoteOpenIsComplete)
        {
            if (channelId == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(channelId), "Actor channel 0 is reserved for control.");
            }

            Connection = connection ?? throw new ArgumentNullException(nameof(connection));
            Actor = actor != null ? actor : throw new ArgumentNullException(nameof(actor));
            ChannelId = channelId;
            State = remoteOpenIsComplete ? ActorChannelState.Open : ActorChannelState.Opening;
            SpawnAcked = remoteOpenIsComplete;
        }

        public ushort ChannelId { get; }
        public GameNetConnection Connection { get; }
        public NetworkIdentity Actor { get; }
        public uint NetId => Actor != null ? Actor.NetId : 0;
        public bool SpawnAcked { get; private set; }
        public ActorChannelState State { get; private set; }
        public uint LastReplicatedTick { get; internal set; }

        internal bool TryMarkSpawnAcked(uint netId, ushort authorityEpoch)
        {
            if (State != ActorChannelState.Opening
                || Actor == null
                || Actor.NetId != netId
                || Actor.AuthorityEpoch != authorityEpoch)
            {
                return false;
            }

            SpawnAcked = true;
            State = ActorChannelState.Open;
            return true;
        }

        internal void Close()
        {
            if (State == ActorChannelState.Closed)
            {
                return;
            }

            State = ActorChannelState.Closing;
            SpawnAcked = false;
            State = ActorChannelState.Closed;
        }
    }
}
