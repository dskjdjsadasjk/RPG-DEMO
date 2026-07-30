using UnityEngine;

namespace RPGDemo.GameFramework
{
    public enum MovementMode
    {
        None,
        Walking
    }

    [RequireComponent(typeof(UnityEngine.CharacterController))]
    public class CharacterMovementComponent : PawnMovementComponent
    {
        private const float MinTickTime = 0.000001f;
        private const float BrakeToStopVelocity = 0.1f;

        [SerializeField]
        private MovementMode movementMode = MovementMode.Walking;

        [SerializeField]
        private float maxAcceleration = 20.48f;

        [SerializeField]
        private float maxWalkSpeed = 6f;

        [SerializeField]
        private float groundFriction = 8f;

        [SerializeField]
        private float brakingFrictionFactor = 2f;

        [SerializeField]
        private float brakingSubStepTime = 1f / 33f;

        [SerializeField]
        private float brakingDecelerationWalking = 20.48f;

        private Character characterOwner;
        private UnityEngine.CharacterController updatedComponent;
        private Vector3 acceleration;
        private Vector3 velocity;
        private float analogInputModifier;

        public Character CharacterOwner => characterOwner;
        public UnityEngine.CharacterController UpdatedComponent => updatedComponent;
        public MovementMode CurrentMovementMode => movementMode;
        public Vector3 Acceleration => acceleration;
        public Vector3 Velocity => velocity;
        public float AnalogInputModifier => analogInputModifier;

        protected override void Awake()
        {
            base.Awake();
            characterOwner = PawnOwner as Character;
            updatedComponent = GetComponent<UnityEngine.CharacterController>();
        }

        protected override void TickComponent(float deltaTime)
        {
            Vector3 inputVector = ConsumeInputVector();

            if (!HasValidData()
                || characterOwner == null
                || updatedComponent == null
                || !updatedComponent.enabled
                || deltaTime < MinTickTime)
            {
                return;
            }

            ControlledCharacterMove(inputVector, deltaTime);
        }

        public virtual void SetMovementMode(MovementMode newMovementMode)
        {
            movementMode = newMovementMode;
        }

        protected virtual void ControlledCharacterMove(
            Vector3 inputVector,
            float deltaTime)
        {
            acceleration = ScaleInputAcceleration(
                ConstrainInputAcceleration(inputVector));
            analogInputModifier = ComputeAnalogInputModifier();

            PerformMovement(deltaTime);
        }

        protected virtual Vector3 ConstrainInputAcceleration(Vector3 inputAcceleration)
        {
            return Vector3.ProjectOnPlane(inputAcceleration, Vector3.up);
        }

        protected virtual Vector3 ScaleInputAcceleration(Vector3 inputAcceleration)
        {
            return Vector3.ClampMagnitude(inputAcceleration, 1f)
                * Mathf.Max(0f, maxAcceleration);
        }

        protected virtual float ComputeAnalogInputModifier()
        {
            float currentMaxAcceleration = Mathf.Max(0f, maxAcceleration);
            if (acceleration.sqrMagnitude > 0f && currentMaxAcceleration > Mathf.Epsilon)
            {
                return Mathf.Clamp01(acceleration.magnitude / currentMaxAcceleration);
            }

            return 0f;
        }

        protected virtual void PerformMovement(float deltaTime)
        {
            if (movementMode == MovementMode.None)
            {
                return;
            }

            StartNewPhysics(deltaTime);
        }

        protected virtual void StartNewPhysics(float deltaTime)
        {
            if (deltaTime < MinTickTime || !HasValidData())
            {
                return;
            }

            switch (movementMode)
            {
                case MovementMode.Walking:
                    PhysWalking(deltaTime);
                    break;

                case MovementMode.None:
                    break;
            }
        }

        protected virtual void PhysWalking(float deltaTime)
        {
            if (characterOwner.Controller == null)
            {
                acceleration = Vector3.zero;
                velocity = Vector3.zero;
                return;
            }

            acceleration = Vector3.ProjectOnPlane(acceleration, Vector3.up);
            velocity = Vector3.ProjectOnPlane(velocity, Vector3.up);

            CalcVelocity(
                deltaTime,
                groundFriction,
                brakingDecelerationWalking);

            Vector3 oldLocation = transform.position;
            updatedComponent.Move(velocity * deltaTime);

            Vector3 actualVelocity = (transform.position - oldLocation) / deltaTime;
            velocity = Vector3.ProjectOnPlane(actualVelocity, Vector3.up);
        }

        protected virtual void CalcVelocity(
            float deltaTime,
            float friction,
            float brakingDeceleration)
        {
            if (deltaTime < MinTickTime)
            {
                return;
            }

            friction = Mathf.Max(0f, friction);
            float maxSpeed = Mathf.Max(0f, maxWalkSpeed);
            float maxInputSpeed = maxSpeed * analogInputModifier;

            bool zeroAcceleration = acceleration.sqrMagnitude <= Mathf.Epsilon;
            bool velocityOverMax = IsExceedingMaxSpeed(maxInputSpeed);

            if (zeroAcceleration || velocityOverMax)
            {
                Vector3 oldVelocity = velocity;

                ApplyVelocityBraking(
                    deltaTime,
                    friction,
                    brakingDeceleration);

                if (velocityOverMax
                    && velocity.sqrMagnitude < maxInputSpeed * maxInputSpeed
                    && Vector3.Dot(acceleration, oldVelocity) > 0f)
                {
                    velocity = oldVelocity.normalized * maxInputSpeed;
                }
            }
            else
            {
                Vector3 accelerationDirection = acceleration.normalized;
                float velocitySize = velocity.magnitude;
                velocity -= (velocity - accelerationDirection * velocitySize)
                    * Mathf.Min(deltaTime * friction, 1f);
            }

            if (!zeroAcceleration)
            {
                float newMaxInputSpeed = IsExceedingMaxSpeed(maxInputSpeed)
                    ? velocity.magnitude
                    : maxInputSpeed;

                velocity += acceleration * deltaTime;
                velocity = Vector3.ClampMagnitude(velocity, newMaxInputSpeed);
            }
        }

        protected virtual void ApplyVelocityBraking(
            float deltaTime,
            float friction,
            float brakingDeceleration)
        {
            if (velocity.sqrMagnitude <= Mathf.Epsilon || deltaTime < MinTickTime)
            {
                return;
            }

            friction = Mathf.Max(0f, friction * Mathf.Max(0f, brakingFrictionFactor));
            brakingDeceleration = Mathf.Max(0f, brakingDeceleration);

            bool zeroFriction = friction == 0f;
            bool zeroBraking = brakingDeceleration == 0f;
            if (zeroFriction && zeroBraking)
            {
                return;
            }

            Vector3 oldVelocity = velocity;
            Vector3 reverseAcceleration = zeroBraking
                ? Vector3.zero
                : -brakingDeceleration * velocity.normalized;

            float remainingTime = deltaTime;
            float maxTimeStep = Mathf.Clamp(
                brakingSubStepTime,
                1f / 75f,
                1f / 20f);

            while (remainingTime >= MinTickTime)
            {
                float timeStep = remainingTime > maxTimeStep && !zeroFriction
                    ? Mathf.Min(maxTimeStep, remainingTime * 0.5f)
                    : remainingTime;

                remainingTime -= timeStep;
                velocity += (-friction * velocity + reverseAcceleration) * timeStep;

                if (Vector3.Dot(velocity, oldVelocity) <= 0f)
                {
                    velocity = Vector3.zero;
                    return;
                }
            }

            float velocitySizeSquared = velocity.sqrMagnitude;
            if (velocitySizeSquared <= Mathf.Epsilon
                || (!zeroBraking
                    && velocitySizeSquared <= BrakeToStopVelocity * BrakeToStopVelocity))
            {
                velocity = Vector3.zero;
            }
        }

        private bool IsExceedingMaxSpeed(float maxSpeed)
        {
            maxSpeed = Mathf.Max(0f, maxSpeed);
            return velocity.sqrMagnitude > maxSpeed * maxSpeed;
        }
    }
}
