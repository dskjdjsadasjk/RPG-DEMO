using System;
using RPGDemo.GameFramework.Networking.Bootstrap;
using RPGDemo.GameFramework.Networking.Identity;
using RPGDemo.GameFramework.Networking.Replication;
using UnityEngine;

namespace RPGDemo.GameFramework.Networking.Diagnostics
{
    internal sealed class RpcVerificationProbe : MonoBehaviour
    {
        private const int RequestedHealth = 77;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void CreateWhenRequested()
        {
            string[] arguments = Environment.GetCommandLineArgs();
            for (int i = 0; i < arguments.Length; i++)
            {
                if (!string.Equals(arguments[i], "-verifyRpc", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                GameObject probeObject = new GameObject("[RpcVerificationProbe]");
                DontDestroyOnLoad(probeObject);
                probeObject.AddComponent<RpcVerificationProbe>();
                return;
            }
        }

        private void Update()
        {
            NetworkBootstrap bootstrap = NetworkBootstrap.Instance;
            GameNetDriver driver = bootstrap != null ? bootstrap.NetDriver : null;
            if (driver == null
                || driver.Mode != NetworkProcessMode.Client
                || driver.ServerConnection == null
                || !driver.ServerConnection.IsReady)
            {
                return;
            }

            foreach (NetworkIdentity identity in driver.ObjectRegistry.Objects)
            {
                if (identity == null || identity.Role != NetworkRole.AutonomousProxy)
                {
                    continue;
                }

                ReplicatedHealth replicatedHealth = identity.GetComponent<ReplicatedHealth>();
                if (replicatedHealth == null)
                {
                    continue;
                }

                bool sent = replicatedHealth.RequestHealthChange(RequestedHealth);
                Debug.Log(
                    $"[Net][RPC][Verify] Request sent={sent}, NetId={identity.NetId}, "
                    + $"RequestedHealth={RequestedHealth}.");
                Destroy(gameObject);
                return;
            }
        }
    }
}
