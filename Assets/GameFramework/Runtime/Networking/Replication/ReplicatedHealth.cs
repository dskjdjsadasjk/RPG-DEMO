using UnityEngine;

namespace RPGDemo.GameFramework.Networking.Replication
{
    [DisallowMultipleComponent]
    public sealed class ReplicatedHealth : NetworkBehaviour
    {
        [SerializeField, Min(0)] private int health = 100;

        public int Health => health;

        public bool SetHealth(int value)
        {
            if (IsNetworkSpawned && !HasAuthority)
            {
                return false;
            }

            int clampedValue = Mathf.Max(0, value);
            if (clampedValue == health)
            {
                return false;
            }

            health = clampedValue;
            return true;
        }

        protected override void WriteReplicatedState(ReplicationStateWriter writer)
        {
            writer.WriteInt32(health);
        }

        protected override bool ReadReplicatedState(ReplicationStateReader reader)
        {
            if (!reader.TryReadInt32(out int replicatedHealth) || replicatedHealth < 0)
            {
                return false;
            }

            health = replicatedHealth;
            return true;
        }

        protected override void OnReplicatedStateApplied(bool isInitialState)
        {
            Debug.Log(
                $"[Net][Rep] NetId={Identity.NetId}, ReplicationId={ReplicationId}, "
                + $"Health={health}, Initial={isInitialState}.",
                this);
        }
    }
}
