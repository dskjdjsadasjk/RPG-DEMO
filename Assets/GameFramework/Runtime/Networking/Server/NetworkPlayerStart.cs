using System.Collections.Generic;
using UnityEngine;

namespace RPGDemo.GameFramework.Networking.Server
{
    [DisallowMultipleComponent]
    public sealed class NetworkPlayerStart : MonoBehaviour
    {
        private static readonly List<NetworkPlayerStart> activeStarts = new List<NetworkPlayerStart>();

        [SerializeField] private int spawnOrder;

        public int SpawnOrder => spawnOrder;

        private void OnEnable()
        {
            if (!activeStarts.Contains(this))
            {
                activeStarts.Add(this);
            }
        }

        private void OnDisable()
        {
            activeStarts.Remove(this);
        }

        internal static bool TrySelect(uint connectionId, out Pose pose)
        {
            RemoveMissingStarts();
            if (activeStarts.Count == 0)
            {
                pose = new Pose(Vector3.zero, Quaternion.identity);
                return false;
            }

            activeStarts.Sort(CompareStarts);
            int index = (int)((connectionId - 1) % (uint)activeStarts.Count);
            Transform selected = activeStarts[index].transform;
            pose = new Pose(selected.position, selected.rotation);
            return true;
        }

        private static int CompareStarts(NetworkPlayerStart left, NetworkPlayerStart right)
        {
            int orderComparison = left.spawnOrder.CompareTo(right.spawnOrder);
            if (orderComparison != 0)
            {
                return orderComparison;
            }

            int nameComparison = string.CompareOrdinal(left.name, right.name);
            return nameComparison != 0
                ? nameComparison
                : left.transform.GetSiblingIndex().CompareTo(right.transform.GetSiblingIndex());
        }

        private static void RemoveMissingStarts()
        {
            for (int i = activeStarts.Count - 1; i >= 0; i--)
            {
                if (activeStarts[i] == null)
                {
                    activeStarts.RemoveAt(i);
                }
            }
        }
    }
}
