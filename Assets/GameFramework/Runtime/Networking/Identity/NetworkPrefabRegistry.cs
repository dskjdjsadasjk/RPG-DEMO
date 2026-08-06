using System;
using System.Collections.Generic;
using UnityEngine;

namespace RPGDemo.GameFramework.Networking.Identity
{
    [CreateAssetMenu(fileName = "NetworkPrefabRegistry", menuName = "RPG Demo/Networking/Prefab Registry")]
    public sealed class NetworkPrefabRegistry : ScriptableObject
    {
        public const string DefaultResourcesPath = "NetworkPrefabRegistry";

        [Serializable]
        private struct Entry
        {
            [Min(1)] public ushort prefabId;
            public NetworkIdentity prefab;
        }

        [SerializeField] private Entry[] entries = Array.Empty<Entry>();

        public bool TryGetPrefab(ushort prefabId, out NetworkIdentity prefab)
        {
            for (int i = 0; i < entries.Length; i++)
            {
                if (entries[i].prefabId == prefabId && entries[i].prefab != null)
                {
                    prefab = entries[i].prefab;
                    return true;
                }
            }

            prefab = null;
            return false;
        }

        public bool TryInstantiate(
            ushort prefabId,
            Vector3 position,
            Quaternion rotation,
            out NetworkIdentity instance)
        {
            if (!TryGetPrefab(prefabId, out NetworkIdentity prefab))
            {
                instance = null;
                return false;
            }

            instance = Instantiate(prefab, position, rotation);
            return instance != null;
        }

        private void OnValidate()
        {
            HashSet<ushort> usedIds = new HashSet<ushort>();
            for (int i = 0; i < entries.Length; i++)
            {
                Entry entry = entries[i];
                if (entry.prefabId == 0)
                {
                    Debug.LogError($"{name}: Network prefab entry {i} uses reserved PrefabId 0.", this);
                }
                else if (!usedIds.Add(entry.prefabId))
                {
                    Debug.LogError($"{name}: duplicate network PrefabId {entry.prefabId}.", this);
                }

                if (entry.prefab == null)
                {
                    Debug.LogError($"{name}: Network prefab entry {i} has no prefab.", this);
                }
                else if (entry.prefab.PrefabId != entry.prefabId)
                {
                    Debug.LogError(
                        $"{name}: entry PrefabId {entry.prefabId} does not match "
                        + $"'{entry.prefab.name}' NetworkIdentity PrefabId {entry.prefab.PrefabId}.",
                        this);
                }
            }
        }
    }
}
