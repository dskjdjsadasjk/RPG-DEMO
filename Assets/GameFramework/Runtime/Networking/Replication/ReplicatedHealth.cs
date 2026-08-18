using UnityEngine;

namespace RPGDemo.GameFramework.Networking.Replication
{
    [DisallowMultipleComponent]
    public sealed class ReplicatedHealth : NetworkBehaviour
    {
        private const ushort ServerRequestHealthFunctionId = 1;
        private const ushort OwningClientResultFunctionId = 2;
        private const ushort MulticastHealthChangedFunctionId = 3;

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

        public bool RequestHealthChange(int requestedHealth)
        {
            if (!IsNetworkSpawned)
            {
                return SetHealth(requestedHealth);
            }

            if (!HasAuthority && !HasLocalOwnership)
            {
                return false;
            }

            return CallRemoteProcedure(
                ServerRequestHealthFunctionId,
                writer => writer.WriteInt32(requestedHealth));
        }

        protected override void RegisterRemoteProcedures(RpcRegistry registry)
        {
            registry.Register(
                ServerRequestHealthFunctionId,
                RpcTarget.Server,
                RpcDelivery.Reliable,
                HandleServerRequestHealth);
            registry.Register(
                OwningClientResultFunctionId,
                RpcTarget.OwningClient,
                RpcDelivery.Reliable,
                HandleOwningClientHealthResult);
            registry.Register(
                MulticastHealthChangedFunctionId,
                RpcTarget.Multicast,
                RpcDelivery.Unreliable,
                HandleMulticastHealthChanged);
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

        private bool HandleServerRequestHealth(RpcPayloadReader reader)
        {
            if (!reader.TryReadInt32(out int requestedHealth))
            {
                return false;
            }

            // This sample command can only reduce the caller's own health. Real gameplay
            // should derive damage on the server instead of trusting a requested value.
            bool accepted = requestedHealth >= 0 && requestedHealth <= health;
            bool changed = accepted && SetHealth(requestedHealth);

            CallRemoteProcedure(
                OwningClientResultFunctionId,
                writer =>
                {
                    writer.WriteBoolean(accepted);
                    writer.WriteInt32(health);
                });

            if (changed)
            {
                CallRemoteProcedure(
                    MulticastHealthChangedFunctionId,
                    writer => writer.WriteInt32(health));
            }

            Debug.Log(
                $"[Net][RPC][DS] Health request NetId={Identity.NetId}, "
                + $"Requested={requestedHealth}, Accepted={accepted}, Current={health}.",
                this);
            return true;
        }

        private bool HandleOwningClientHealthResult(RpcPayloadReader reader)
        {
            if (!reader.TryReadBoolean(out bool accepted)
                || !reader.TryReadInt32(out int authoritativeHealth)
                || authoritativeHealth < 0)
            {
                return false;
            }

            Debug.Log(
                $"[Net][RPC][Client] Health result NetId={Identity.NetId}, "
                + $"Accepted={accepted}, AuthoritativeHealth={authoritativeHealth}.",
                this);
            return true;
        }

        private bool HandleMulticastHealthChanged(RpcPayloadReader reader)
        {
            if (!reader.TryReadInt32(out int authoritativeHealth) || authoritativeHealth < 0)
            {
                return false;
            }

            Debug.Log(
                $"[Net][RPC][Multicast] Health changed NetId={Identity.NetId}, "
                + $"Health={authoritativeHealth}, LocalRole={Identity.Role}.",
                this);
            return true;
        }
    }
}
