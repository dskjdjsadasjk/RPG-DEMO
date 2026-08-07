using System;
using RPGDemo.GameFramework.Networking.Identity;
using UnityEngine;
using UnityEngine.InputSystem;
using Object = UnityEngine.Object;

namespace RPGDemo.GameFramework.Networking.Client
{
    public sealed class ClientPlayerSession : IDisposable
    {
        private readonly GameNetDriver netDriver;

        private PlayerController playerController;
        private NetworkIdentity possessedIdentity;
        private uint possessedNetId;
        private bool ownsControllerObject;

        public ClientPlayerSession(GameNetDriver netDriver)
        {
            this.netDriver = netDriver ?? throw new ArgumentNullException(nameof(netDriver));
            netDriver.NetworkObjectSpawned += HandleNetworkObjectSpawned;
            netDriver.NetworkObjectDespawned += HandleNetworkObjectDespawned;
        }

        public PlayerController PlayerController => playerController;
        public NetworkIdentity PossessedIdentity => possessedIdentity;

        public void Dispose()
        {
            netDriver.NetworkObjectSpawned -= HandleNetworkObjectSpawned;
            netDriver.NetworkObjectDespawned -= HandleNetworkObjectDespawned;

            if (playerController != null && playerController.Pawn != null)
            {
                playerController.UnPossess();
            }

            if (ownsControllerObject && playerController != null)
            {
                Object.Destroy(playerController.gameObject);
            }

            playerController = null;
            possessedIdentity = null;
            possessedNetId = 0;
            ownsControllerObject = false;
        }

        private void HandleNetworkObjectSpawned(NetworkIdentity identity)
        {
            if (identity == null || identity.Role != NetworkRole.AutonomousProxy)
            {
                return;
            }

            Pawn pawn = identity.GetComponent<Pawn>();
            if (pawn == null)
            {
                Debug.LogError(
                    $"[Net][Client] Autonomous NetId={identity.NetId} has no Pawn component.",
                    identity);
                return;
            }

            PlayerController controller = ResolvePlayerController();
            PlayerState playerState = controller.GetComponent<PlayerState>();
            if (playerState == null)
            {
                playerState = controller.gameObject.AddComponent<PlayerState>();
            }

            controller.SetPlayerState(playerState);
            if (controller.Pawn != null && controller.Pawn != pawn)
            {
                controller.UnPossess();
            }

            PossessionResult result = controller.Possess(pawn);
            if (result != PossessionResult.Succeeded
                && result != PossessionResult.AlreadyPossessed)
            {
                Debug.LogError(
                    $"[Net][Client] Possess failed for NetId={identity.NetId}: {result}.",
                    identity);
                return;
            }

            playerController = controller;
            possessedIdentity = identity;
            possessedNetId = identity.NetId;
            Debug.Log(
                $"[Net][Client] Local PlayerController possessed AutonomousProxy "
                + $"NetId={identity.NetId}.",
                identity);
        }

        private void HandleNetworkObjectDespawned(uint netId, Protocol.ActorChannelCloseReason reason)
        {
            if (possessedNetId == 0 || possessedNetId != netId)
            {
                return;
            }

            if (playerController != null)
            {
                playerController.UnPossess();
            }

            possessedIdentity = null;
            possessedNetId = 0;
            Debug.Log($"[Net][Client] Local Pawn NetId={netId} released ({reason}).");
        }

        private PlayerController ResolvePlayerController()
        {
            if (playerController != null)
            {
                return playerController;
            }

            PlayerController[] controllers = Object.FindObjectsByType<PlayerController>(
                FindObjectsInactive.Include);
            for (int i = 0; i < controllers.Length; i++)
            {
                if (controllers[i] != null && controllers[i].Pawn == null)
                {
                    playerController = controllers[i];
                    return playerController;
                }
            }

            GameObject controllerObject = new GameObject("[ClientPlayerController]");
            playerController = controllerObject.AddComponent<PlayerController>();
            ownsControllerObject = true;

            PlayerInput playerInput = playerController.GetComponent<PlayerInput>();
            if (playerInput == null || playerInput.actions == null)
            {
                Debug.LogWarning(
                    "[Net][Client] Runtime-created PlayerController has no InputActionAsset. "
                    + "Add a configured PlayerController to the network scene.",
                    playerController);
            }

            return playerController;
        }
    }
}
