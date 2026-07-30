using UnityEngine;

namespace RPGDemo.GameFramework
{
    [RequireComponent(typeof(UnityEngine.CharacterController))]
    [RequireComponent(typeof(CharacterMovementComponent))]
    public class Character : Pawn
    {
        private const string MoveActionName = "Player/Move";
        private const string LookActionName = "Player/Look";

        public CharacterMovementComponent CharacterMovement
            => MovementComponent as CharacterMovementComponent;

        protected override void SetupPlayerInputComponent(InputComponent component)
        {
            component.BindValue<Vector2>(MoveActionName, HandleMoveInput);
            component.BindValue<Vector2>(LookActionName, HandleLookInput);
        }

        private void HandleMoveInput(Vector2 input)
        {
            if (!(Controller is PlayerController playerController))
            {
                return;
            }

            float controlYaw = playerController.ControlRotation.eulerAngles.y;
            Quaternion yawRotation = Quaternion.Euler(0f, controlYaw, 0f);

            AddMovementInput(yawRotation * Vector3.forward, input.y);
            AddMovementInput(yawRotation * Vector3.right, input.x);
        }

        private void HandleLookInput(Vector2 input)
        {
            AddControllerYawInput(input.x);
            AddControllerPitchInput(input.y);
        }
    }
}
