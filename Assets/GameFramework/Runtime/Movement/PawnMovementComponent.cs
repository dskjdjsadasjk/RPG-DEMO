using UnityEngine;

namespace RPGDemo.GameFramework
{
    [DefaultExecutionOrder(0)]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Pawn))]
    public class PawnMovementComponent : MonoBehaviour
    {
        private Pawn pawnOwner;

        public bool AutomaticTickEnabled { get; set; } = true;

        public Pawn PawnOwner => pawnOwner;

        protected virtual void Awake()
        {
            pawnOwner = GetComponent<Pawn>();
            if (pawnOwner != null)
            {
                pawnOwner.SetMovementComponent(this);
            }
        }

        protected virtual void Update()
        {
            if (AutomaticTickEnabled)
            {
                TickComponent(Time.deltaTime);
            }
        }

        public virtual void AddInputVector(Vector3 worldInput, bool force = false)
        {
            if (pawnOwner != null)
            {
                pawnOwner.InternalAddMovementInput(worldInput, force);
            }
        }

        public virtual Vector3 GetPendingInputVector()
        {
            return pawnOwner != null
                ? pawnOwner.InternalGetPendingMovementInputVector()
                : Vector3.zero;
        }

        public virtual Vector3 GetLastInputVector()
        {
            return pawnOwner != null
                ? pawnOwner.InternalGetLastMovementInputVector()
                : Vector3.zero;
        }

        public virtual Vector3 ConsumeInputVector()
        {
            return pawnOwner != null
                ? pawnOwner.InternalConsumeMovementInputVector()
                : Vector3.zero;
        }

        protected virtual void TickComponent(float deltaTime)
        {
        }

        protected bool HasValidData()
        {
            return pawnOwner != null;
        }

        protected virtual void OnDestroy()
        {
            if (pawnOwner != null && pawnOwner.MovementComponent == this)
            {
                pawnOwner.SetMovementComponent(null);
            }
        }
    }
}
