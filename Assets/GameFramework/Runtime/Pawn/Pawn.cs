using UnityEngine;

namespace RPGDemo.GameFramework
{
    public class Pawn : MonoBehaviour
    {
        private Controller controller;
        private PlayerState playerState;
        private bool isDestroying;

        public Controller Controller => controller;
        public PlayerState PlayerState => playerState;
        public bool IsDestroying => isDestroying;

        internal void PossessedBy(Controller newController)
        {
            Controller oldController = controller;

            controller = newController;
            SetPlayerState(newController != null ? newController.PlayerState : null);
            OnPossessed(newController);

            if (oldController != newController)
            {
                OnControllerChanged(oldController, newController);
            }
        }

        internal void UnPossessed()
        {
            Controller oldController = controller;

            SetPlayerState(null);
            controller = null;
            OnUnpossessed(oldController);

            if (oldController != null)
            {
                OnControllerChanged(oldController, null);
            }
        }

        internal void SetPlayerState(PlayerState newPlayerState)
        {
            if (playerState == newPlayerState)
            {
                return;
            }

            PlayerState oldState = playerState;

            if (oldState != null && oldState.Pawn == this)
            {
                oldState.SetPawn(null);
            }

            playerState = newPlayerState;

            if (playerState != null)
            {
                playerState.SetPawn(this);
            }

            OnPlayerStateChanged(oldState, playerState);
        }

        public virtual void Restart()
        {
        }

        protected virtual void OnPossessed(Controller newController)
        {
        }

        protected virtual void OnUnpossessed(Controller oldController)
        {
        }

        protected virtual void OnControllerChanged(Controller oldController, Controller newController)
        {
        }

        protected virtual void OnPlayerStateChanged(PlayerState oldState, PlayerState newState)
        {
        }

        protected virtual void OnDestroy()
        {
            isDestroying = true;

            Controller currentController = controller;
            if (currentController != null)
            {
                currentController.PawnPendingDestroy(this);
            }
        }
    }
}
