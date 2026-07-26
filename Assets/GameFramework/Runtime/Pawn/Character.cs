using UnityEngine;

namespace RPGDemo.GameFramework
{
    [RequireComponent(typeof(UnityEngine.CharacterController))]
    [RequireComponent(typeof(CharacterMovementComponent))]
    public class Character : Pawn
    {
        public CharacterMovementComponent CharacterMovement
            => MovementComponent as CharacterMovementComponent;
    }
}
