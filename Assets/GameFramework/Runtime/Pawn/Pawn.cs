using UnityEngine;

namespace RPGDemo.GameFramework
{
    public class Pawn : MonoBehaviour
    {
        private Controller controller;
        private PlayerState playerState;
        private InputComponent inputComponent;
        private Vector3 controlInputVector;
        private Vector3 lastControlInputVector;
        private bool isDestroying;

        public Controller Controller => controller;
        public PlayerState PlayerState => playerState;
        public InputComponent InputComponent => inputComponent;
        public Vector3 PendingMovementInputVector => controlInputVector;
        public Vector3 LastMovementInputVector => lastControlInputVector;
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
            DestroyPlayerInputComponent();
            controller = null;
            OnUnpossessed(oldController);

            if (oldController != null)
            {
                OnControllerChanged(oldController, null);
            }

            ConsumeMovementInputVector();
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

        internal void InitializePlayerInputComponent()
        {
            if (!(controller is PlayerController playerController)
                || !playerController.IsLocalController
                || inputComponent != null)
            {
                return;
            }

            inputComponent = CreatePlayerInputComponent();
            if (inputComponent != null)
            {
                SetupPlayerInputComponent(inputComponent);
            }
        }

        protected virtual InputComponent CreatePlayerInputComponent()
        {
            return gameObject.AddComponent<InputComponent>();
        }

        protected virtual void SetupPlayerInputComponent(InputComponent component)
        {
        }

        public virtual void AddMovementInput(
            Vector3 worldDirection,
            float scaleValue = 1f,
            bool force = false)
        {
            if (force || controller == null || !controller.IsMoveInputIgnored)
            {
                controlInputVector += worldDirection * scaleValue;
            }
        }

        public virtual Vector3 GetPendingMovementInputVector()
        {
            return controlInputVector;
        }

        public virtual Vector3 GetLastMovementInputVector()
        {
            return lastControlInputVector;
        }

        public virtual Vector3 ConsumeMovementInputVector()
        {
            lastControlInputVector = controlInputVector;
            controlInputVector = Vector3.zero;
            return lastControlInputVector;
        }

        public virtual void AddControllerPitchInput(float value)
        {
            if (value != 0f
                && controller is PlayerController playerController
                && playerController.IsLocalController)
            {
                playerController.AddPitchInput(value);
            }
        }

        public virtual void AddControllerYawInput(float value)
        {
            if (value != 0f
                && controller is PlayerController playerController
                && playerController.IsLocalController)
            {
                playerController.AddYawInput(value);
            }
        }

        public virtual void AddControllerRollInput(float value)
        {
            if (value != 0f
                && controller is PlayerController playerController
                && playerController.IsLocalController)
            {
                playerController.AddRollInput(value);
            }
        }

        public virtual void FaceRotation(Quaternion newRotation, float deltaTime)
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

        private void DestroyPlayerInputComponent()
        {
            if (inputComponent == null)
            {
                return;
            }

            inputComponent.ClearBindings();
            Destroy(inputComponent);
            inputComponent = null;
        }
    }
}
