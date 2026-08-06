using System;
using UnityEngine;

namespace RPGDemo.GameFramework.Networking.Identity
{
    public enum NetworkRole : byte
    {
        None,
        Authority,
        AutonomousProxy,
        SimulatedProxy
    }

    [DisallowMultipleComponent]
    public sealed class NetworkIdentity : MonoBehaviour
    {
        [SerializeField] private ushort prefabId;
        [SerializeField] private bool destroyOnDespawn = true;

        public uint NetId { get; private set; }
        public ushort PrefabId => prefabId;
        public uint OwnerConnectionId { get; private set; }
        public ushort AuthorityEpoch { get; private set; }
        public NetworkRole Role { get; private set; }
        public bool IsSpawned { get; private set; }
        public bool DestroyOnDespawn => destroyOnDespawn;
        public bool HasLocalOwnership => Role == NetworkRole.AutonomousProxy;
        public bool HasAuthority => Role == NetworkRole.Authority;

        public event Action<NetworkIdentity> NetworkSpawned;
        public event Action<NetworkIdentity> NetworkDespawned;

        internal void InitializeNetworkSpawn(
            uint netId,
            ushort runtimePrefabId,
            uint ownerConnectionId,
            ushort authorityEpoch,
            NetworkRole role)
        {
            if (IsSpawned)
            {
                throw new InvalidOperationException($"'{name}' is already spawned as NetId {NetId}.");
            }

            if (netId == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(netId), "NetId 0 is reserved.");
            }

            if (runtimePrefabId == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(runtimePrefabId), "PrefabId 0 is reserved.");
            }

            if (authorityEpoch == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(authorityEpoch), "AuthorityEpoch 0 is reserved.");
            }

            NetId = netId;
            prefabId = runtimePrefabId;
            OwnerConnectionId = ownerConnectionId;
            AuthorityEpoch = authorityEpoch;
            Role = role;
            IsSpawned = true;
        }

        internal void NotifyNetworkSpawned()
        {
            NetworkSpawned?.Invoke(this);
        }

        internal void ResetNetworkSpawn()
        {
            if (!IsSpawned)
            {
                return;
            }

            IsSpawned = false;
            NetworkDespawned?.Invoke(this);
            NetId = 0;
            OwnerConnectionId = 0;
            AuthorityEpoch = 0;
            Role = NetworkRole.None;
        }
    }
}
