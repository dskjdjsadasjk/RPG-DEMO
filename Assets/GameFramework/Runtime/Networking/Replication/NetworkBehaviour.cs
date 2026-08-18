using System;
using RPGDemo.GameFramework.Networking.Bootstrap;
using RPGDemo.GameFramework.Networking.Identity;
using UnityEngine;

namespace RPGDemo.GameFramework.Networking.Replication
{
    public abstract class NetworkBehaviour : MonoBehaviour
    {
        [SerializeField, Min(1)] private ushort replicationId = 1;

        private NetworkIdentity identity;
        private RpcRegistry rpcRegistry;

        public ushort ReplicationId => replicationId;
        public NetworkIdentity Identity => identity != null ? identity : FindIdentity();
        public bool IsNetworkSpawned => Identity != null && Identity.IsSpawned;
        public bool HasAuthority => Identity != null && Identity.HasAuthority;
        public bool HasLocalOwnership => Identity != null && Identity.HasLocalOwnership;

        protected abstract void WriteReplicatedState(ReplicationStateWriter writer);
        protected abstract bool ReadReplicatedState(ReplicationStateReader reader);

        protected virtual void RegisterRemoteProcedures(RpcRegistry registry)
        {
        }

        protected virtual void OnNetworkSpawned()
        {
        }

        protected virtual void OnNetworkDespawned()
        {
        }

        protected virtual void OnReplicatedStateApplied(bool isInitialState)
        {
        }

        protected bool CallRemoteProcedure(
            ushort functionId,
            Action<RpcPayloadWriter> writePayload = null)
        {
            if (!IsNetworkSpawned
                || rpcRegistry == null
                || !rpcRegistry.TryGet(functionId, out RpcDescriptor descriptor))
            {
                return false;
            }

            RpcPayloadWriter writer = new RpcPayloadWriter();
            writePayload?.Invoke(writer);
            byte[] payload = writer.ToArray();

            if (HasAuthority && descriptor.Target == RpcTarget.Server)
            {
                return TryInvokeRemoteProcedure(functionId, RpcTarget.Server, payload);
            }

            if (HasAuthority && descriptor.Target == RpcTarget.Multicast)
            {
                if (!TryInvokeRemoteProcedure(functionId, RpcTarget.Multicast, payload))
                {
                    return false;
                }

                return NetworkBootstrap.Instance != null
                    && NetworkBootstrap.Instance.NetDriver != null
                    && NetworkBootstrap.Instance.NetDriver.SendRemoteProcedure(this, descriptor, payload);
            }

            return NetworkBootstrap.Instance != null
                && NetworkBootstrap.Instance.NetDriver != null
                && NetworkBootstrap.Instance.NetDriver.SendRemoteProcedure(this, descriptor, payload);
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

        internal bool TryGetRpcDescriptor(ushort functionId, out RpcDescriptor descriptor)
        {
            descriptor = null;
            return rpcRegistry != null && rpcRegistry.TryGet(functionId, out descriptor);
        }

        internal bool TryInvokeRemoteProcedure(
            ushort functionId,
            RpcTarget expectedTarget,
            byte[] payload)
        {
            if (!TryGetRpcDescriptor(functionId, out RpcDescriptor descriptor)
                || descriptor.Target != expectedTarget)
            {
                return false;
            }

            try
            {
                RpcPayloadReader reader = new RpcPayloadReader(payload);
                return descriptor.Handler(reader) && reader.IsAtEnd;
            }
            catch (Exception exception)
            {
                Debug.LogError(
                    $"[Net][RPC] Handler failed: NetId={Identity?.NetId ?? 0}, "
                    + $"ReplicationId={ReplicationId}, FunctionId={functionId}: {exception}",
                    this);
                return false;
            }
        }

        internal void NotifyNetworkSpawned(NetworkIdentity ownerIdentity)
        {
            identity = ownerIdentity != null
                ? ownerIdentity
                : throw new ArgumentNullException(nameof(ownerIdentity));

            rpcRegistry = new RpcRegistry();
            RegisterRemoteProcedures(rpcRegistry);
            rpcRegistry.Seal();
            OnNetworkSpawned();
        }

        internal void NotifyNetworkDespawned()
        {
            OnNetworkDespawned();
            rpcRegistry = null;
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
