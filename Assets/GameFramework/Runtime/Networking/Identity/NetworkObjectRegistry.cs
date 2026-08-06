using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace RPGDemo.GameFramework.Networking.Identity
{
    public sealed class NetworkObjectRegistry
    {
        private readonly Dictionary<uint, NetworkIdentity> objects = new Dictionary<uint, NetworkIdentity>();
        private readonly List<NetworkIdentity> cleanupObjects = new List<NetworkIdentity>();
        private uint nextNetId = 1;

        public IReadOnlyCollection<NetworkIdentity> Objects => objects.Values;
        public int Count => objects.Count;

        public event Action<NetworkIdentity> ObjectRegistered;
        public event Action<uint> ObjectUnregistered;

        public NetworkIdentity RegisterServerObject(
            NetworkIdentity identity,
            uint ownerConnectionId,
            ushort authorityEpoch = 1)
        {
            if (identity == null)
            {
                throw new ArgumentNullException(nameof(identity));
            }

            if (identity.PrefabId == 0)
            {
                throw new InvalidOperationException($"NetworkIdentity '{identity.name}' has PrefabId 0.");
            }

            uint netId = AllocateNetId();
            Register(
                identity,
                netId,
                identity.PrefabId,
                ownerConnectionId,
                authorityEpoch,
                NetworkRole.Authority);
            return identity;
        }

        public bool TryRegisterClientObject(
            NetworkIdentity identity,
            uint netId,
            ushort prefabId,
            uint ownerConnectionId,
            ushort authorityEpoch,
            NetworkRole role)
        {
            if (identity == null
                || netId == 0
                || prefabId == 0
                || authorityEpoch == 0
                || role == NetworkRole.None
                || role == NetworkRole.Authority
                || objects.ContainsKey(netId)
                || identity.IsSpawned)
            {
                return false;
            }

            Register(identity, netId, prefabId, ownerConnectionId, authorityEpoch, role);
            return true;
        }

        public bool TryGet(uint netId, out NetworkIdentity identity)
        {
            return objects.TryGetValue(netId, out identity) && identity != null;
        }

        public bool Unregister(uint netId, bool destroyGameObject)
        {
            if (!objects.TryGetValue(netId, out NetworkIdentity identity))
            {
                return false;
            }

            objects.Remove(netId);
            identity.ResetNetworkSpawn();
            ObjectUnregistered?.Invoke(netId);

            if (destroyGameObject && identity != null && identity.DestroyOnDespawn)
            {
                DestroyIdentity(identity);
            }

            return true;
        }

        public void Clear(bool destroyGameObjects)
        {
            cleanupObjects.Clear();
            cleanupObjects.AddRange(objects.Values);
            objects.Clear();

            for (int i = 0; i < cleanupObjects.Count; i++)
            {
                NetworkIdentity identity = cleanupObjects[i];
                if (identity == null)
                {
                    continue;
                }

                uint oldNetId = identity.NetId;
                identity.ResetNetworkSpawn();
                ObjectUnregistered?.Invoke(oldNetId);

                if (destroyGameObjects && identity.DestroyOnDespawn)
                {
                    DestroyIdentity(identity);
                }
            }

            cleanupObjects.Clear();
            nextNetId = 1;
        }

        private void Register(
            NetworkIdentity identity,
            uint netId,
            ushort prefabId,
            uint ownerConnectionId,
            ushort authorityEpoch,
            NetworkRole role)
        {
            identity.InitializeNetworkSpawn(
                netId,
                prefabId,
                ownerConnectionId,
                authorityEpoch,
                role);
            objects.Add(netId, identity);
            identity.NotifyNetworkSpawned();
            ObjectRegistered?.Invoke(identity);
        }

        private uint AllocateNetId()
        {
            uint firstCandidate = nextNetId;
            do
            {
                uint candidate = nextNetId++;
                if (nextNetId == 0)
                {
                    nextNetId = 1;
                }

                if (candidate != 0 && !objects.ContainsKey(candidate))
                {
                    return candidate;
                }
            }
            while (nextNetId != firstCandidate);

            throw new InvalidOperationException("Network NetId space is exhausted.");
        }

        private static void DestroyIdentity(NetworkIdentity identity)
        {
            if (Application.isPlaying)
            {
                Object.Destroy(identity.gameObject);
            }
            else
            {
                Object.DestroyImmediate(identity.gameObject);
            }
        }
    }
}
