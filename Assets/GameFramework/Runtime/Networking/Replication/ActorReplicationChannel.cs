using System;
using System.Collections.Generic;
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
        private const int MaxPendingReliableRpcs = 64;
        private readonly Dictionary<ushort, ObjectReplicator> replicatorsById
            = new Dictionary<ushort, ObjectReplicator>();
        private readonly Queue<PendingRpcCall> pendingReliableRpcs
            = new Queue<PendingRpcCall>();

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
            CreateObjectReplicators(actor);
        }

        public ushort ChannelId { get; }
        public GameNetConnection Connection { get; }
        public NetworkIdentity Actor { get; }
        public uint NetId => Actor != null ? Actor.NetId : 0;
        public bool SpawnAcked { get; private set; }
        public ActorChannelState State { get; private set; }
        public uint LastReplicatedTick { get; internal set; }
        public uint LastMovementReplicatedTick { get; internal set; }
        public int PendingReliableRpcCount => pendingReliableRpcs.Count;
        public IReadOnlyCollection<ObjectReplicator> ObjectReplicators => replicatorsById.Values;

        internal bool TryGetObjectReplicator(ushort replicationId, out ObjectReplicator replicator)
        {
            return replicatorsById.TryGetValue(replicationId, out replicator);
        }

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

        internal bool TryEnqueueReliableRpc(
            ushort replicationId,
            ushort functionId,
            byte[] payload)
        {
            if (State != ActorChannelState.Opening
                || SpawnAcked
                || replicationId == 0
                || functionId == 0
                || payload == null
                || payload.Length > RpcPayloadWriter.MaxPayloadBytes
                || pendingReliableRpcs.Count >= MaxPendingReliableRpcs)
            {
                return false;
            }

            byte[] payloadCopy = new byte[payload.Length];
            Buffer.BlockCopy(payload, 0, payloadCopy, 0, payload.Length);
            pendingReliableRpcs.Enqueue(
                new PendingRpcCall(replicationId, functionId, payloadCopy));
            return true;
        }

        internal bool TryDequeueReliableRpc(out PendingRpcCall call)
        {
            if (pendingReliableRpcs.Count == 0)
            {
                call = default;
                return false;
            }

            call = pendingReliableRpcs.Dequeue();
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
            pendingReliableRpcs.Clear();
            replicatorsById.Clear();
            State = ActorChannelState.Closed;
        }

        private void CreateObjectReplicators(NetworkIdentity actor)
        {
            NetworkBehaviour[] behaviours = actor.GetComponentsInChildren<NetworkBehaviour>(true);
            for (int i = 0; i < behaviours.Length; i++)
            {
                NetworkBehaviour behaviour = behaviours[i];
                if (behaviour == null)
                {
                    continue;
                }

                if (behaviour.ReplicationId == 0
                    || replicatorsById.ContainsKey(behaviour.ReplicationId))
                {
                    throw new InvalidOperationException(
                        $"NetworkIdentity '{actor.name}' has duplicate or invalid ReplicationId "
                        + $"{behaviour.ReplicationId}.");
                }

                replicatorsById.Add(
                    behaviour.ReplicationId,
                    new ObjectReplicator(behaviour));
            }
        }
    }
}
