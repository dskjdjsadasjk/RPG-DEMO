using System;
using System.Collections.Generic;
using RPGDemo.GameFramework.Networking.Identity;
using RPGDemo.GameFramework.Networking.Replication;
using RPGDemo.GameFramework.Networking.Transport;

namespace RPGDemo.GameFramework.Networking
{
    public sealed class GameNetConnection
    {
        private readonly Dictionary<ushort, ActorReplicationChannel> actorChannelsById
            = new Dictionary<ushort, ActorReplicationChannel>();
        private readonly Dictionary<uint, ActorReplicationChannel> actorChannelsByNetId
            = new Dictionary<uint, ActorReplicationChannel>();
        private ushort nextActorChannelId = 1;

        internal GameNetConnection(
            TransportConnectionHandle transportHandle,
            NetConnectionState initialState,
            string remoteEndpoint)
        {
            TransportHandle = transportHandle;
            State = initialState;
            RemoteEndpoint = remoteEndpoint;
        }

        public uint ConnectionId { get; internal set; }
        public NetConnectionState State { get; internal set; }
        public string RemoteEndpoint { get; }
        public string DisplayName { get; internal set; }
        public uint ServerTickAtWelcome { get; internal set; }
        public ushort ServerTickRate { get; internal set; }
        public bool IsReady => State == NetConnectionState.Ready;
        public IReadOnlyCollection<ActorReplicationChannel> ActorChannels => actorChannelsById.Values;
        public Controller OwningController { get; private set; }

        internal TransportConnectionHandle TransportHandle { get; }
        internal ulong ClientNonce { get; set; }
        internal ulong ServerNonce { get; set; }
        internal float StateElapsedSeconds { get; set; }

        internal void TransitionTo(NetConnectionState nextState)
        {
            State = nextState;
            StateElapsedSeconds = 0f;
        }

        internal void SetOwningController(Controller controller)
        {
            OwningController = controller;
        }

        internal ActorReplicationChannel FindOrCreateLocalActorChannel(NetworkIdentity actor)
        {
            if (actor == null || !actor.IsSpawned)
            {
                throw new ArgumentException("Actor must be a spawned NetworkIdentity.", nameof(actor));
            }

            if (actorChannelsByNetId.TryGetValue(actor.NetId, out ActorReplicationChannel existing))
            {
                return existing;
            }

            ushort channelId = AllocateActorChannelId();
            ActorReplicationChannel channel = new ActorReplicationChannel(
                channelId,
                this,
                actor,
                remoteOpenIsComplete: false);
            AddActorChannel(channel);
            return channel;
        }

        internal bool TryCreateRemoteActorChannel(
            ushort channelId,
            NetworkIdentity actor,
            out ActorReplicationChannel channel)
        {
            channel = null;
            if (channelId == 0
                || actor == null
                || !actor.IsSpawned
                || actorChannelsById.ContainsKey(channelId)
                || actorChannelsByNetId.ContainsKey(actor.NetId))
            {
                return false;
            }

            channel = new ActorReplicationChannel(
                channelId,
                this,
                actor,
                remoteOpenIsComplete: true);
            AddActorChannel(channel);
            return true;
        }

        internal bool TryGetActorChannel(ushort channelId, out ActorReplicationChannel channel)
        {
            return actorChannelsById.TryGetValue(channelId, out channel);
        }

        internal bool TryGetActorChannelByNetId(uint netId, out ActorReplicationChannel channel)
        {
            return actorChannelsByNetId.TryGetValue(netId, out channel);
        }

        internal bool RemoveActorChannel(ushort channelId)
        {
            if (!actorChannelsById.TryGetValue(channelId, out ActorReplicationChannel channel))
            {
                return false;
            }

            actorChannelsById.Remove(channelId);
            actorChannelsByNetId.Remove(channel.NetId);
            channel.Close();
            return true;
        }

        internal void CloseAllActorChannels()
        {
            foreach (ActorReplicationChannel channel in actorChannelsById.Values)
            {
                channel.Close();
            }

            actorChannelsById.Clear();
            actorChannelsByNetId.Clear();
            nextActorChannelId = 1;
        }

        private void AddActorChannel(ActorReplicationChannel channel)
        {
            actorChannelsById.Add(channel.ChannelId, channel);
            actorChannelsByNetId.Add(channel.NetId, channel);
        }

        private ushort AllocateActorChannelId()
        {
            ushort firstCandidate = nextActorChannelId;
            do
            {
                ushort candidate = nextActorChannelId++;
                if (nextActorChannelId == 0)
                {
                    nextActorChannelId = 1;
                }

                if (candidate != 0 && !actorChannelsById.ContainsKey(candidate))
                {
                    return candidate;
                }
            }
            while (nextActorChannelId != firstCandidate);

            throw new InvalidOperationException("Actor channel id space is exhausted for this connection.");
        }

        public override string ToString()
        {
            string id = ConnectionId == 0 ? "pending" : ConnectionId.ToString();
            return $"Connection {id} ({RemoteEndpoint}, {State})";
        }
    }
}
