using System;
using RPGDemo.GameFramework.Networking.Identity;
using UnityEngine;

namespace RPGDemo.GameFramework.Networking.Replication
{
    public abstract class NetworkBehaviour : MonoBehaviour
    {
        [SerializeField, Min(1)] private ushort replicationId = 1;

        private NetworkIdentity identity;

        public ushort ReplicationId => replicationId;
        public NetworkIdentity Identity => identity != null ? identity : FindIdentity();
        public bool IsNetworkSpawned => Identity != null && Identity.IsSpawned;
        public bool HasAuthority => Identity != null && Identity.HasAuthority;
        public bool HasLocalOwnership => Identity != null && Identity.HasLocalOwnership;

        protected abstract void WriteReplicatedState(ReplicationStateWriter writer);
        protected abstract bool ReadReplicatedState(ReplicationStateReader reader);

        protected virtual void OnNetworkSpawned()
        {
        }

        protected virtual void OnNetworkDespawned()
        {
        }

        protected virtual void OnReplicatedStateApplied(bool isInitialState)
        {
        }

        internal byte[] CaptureReplicatedState()
        {
            ReplicationStateWriter writer = new ReplicationStateWriter();
            WriteReplicatedState(writer);
            return writer.ToArray();
        }

        internal bool TryApplyReplicatedState(byte[] state, bool isInitialState)
        {
            ReplicationStateReader reader = new ReplicationStateReader(state);
            if (!ReadReplicatedState(reader) || !reader.IsAtEnd)
            {
                return false;
            }

            OnReplicatedStateApplied(isInitialState);
            return true;
        }

        internal void NotifyNetworkSpawned(NetworkIdentity ownerIdentity)
        {
            identity = ownerIdentity != null
                ? ownerIdentity
                : throw new ArgumentNullException(nameof(ownerIdentity));
            OnNetworkSpawned();
        }

        internal void NotifyNetworkDespawned()
        {
            OnNetworkDespawned();
            identity = null;
        }

        private NetworkIdentity FindIdentity()
        {
            identity = GetComponentInParent<NetworkIdentity>();
            return identity;
        }

        protected virtual void OnValidate()
        {
            if (replicationId == 0)
            {
                replicationId = 1;
            }
        }
    }
}
