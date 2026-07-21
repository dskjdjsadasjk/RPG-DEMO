using System;
using UnityEngine;

namespace RPGDemo.GameFramework
{
    public class Controller : MonoBehaviour
    {
        private Pawn pawn;
        private Character character;
        private PlayerState playerState;
        private Quaternion controlRotation = Quaternion.identity;
        private string stateName = ControllerStates.Inactive;
        private Transform startSpot;

        private bool hasAuthority = true;
        private bool canPossessWithoutAuthority;
        private bool isPlayerController;

        private int ignoreMoveInput;
        private int ignoreLookInput;

        public Pawn Pawn => pawn;
        public Character Character => character;
        public PlayerState PlayerState => playerState;
        public Quaternion ControlRotation => controlRotation;
        public string StateName => stateName;
        public Transform StartSpot => startSpot;

        public bool HasAuthority => hasAuthority;
        public bool CanPossessWithoutAuthority => canPossessWithoutAuthority;
        public bool IsPlayerController => isPlayerController;
        public virtual bool IsLocalController => true;

        public bool IsMoveInputIgnored => ignoreMoveInput > 0;
        public bool IsLookInputIgnored => ignoreLookInput > 0;

        public event Action<Pawn, Pawn> PossessedPawnChanged;
        public event Action<string, string> StateChanged;
        public event Action<Quaternion> ControlRotationChanged;

        protected void SetIsPlayerController(bool value)
        {
            isPlayerController = value;
        }

        public PossessionResult Possess(Pawn inPawn)
        {
            if (!HasAuthority && !CanPossessWithoutAuthority)
            {
                return PossessionResult.RejectedNoAuthority;
            }

            Pawn oldPawn = pawn;

            if (inPawn == null)
            {
                if (oldPawn == null)
                {
                    return PossessionResult.InvalidPawn;
                }

                UnPossess();
                return PossessionResult.Succeeded;
            }

            if (oldPawn == inPawn)
            {
                return PossessionResult.AlreadyPossessed;
            }

            OnPossess(inPawn);

            if (oldPawn != pawn)
            {
                PossessedPawnChanged?.Invoke(oldPawn, pawn);
            }

            return pawn == inPawn ? PossessionResult.Succeeded : PossessionResult.InvalidPawn;
        }

        protected virtual void OnPossess(Pawn inPawn)
        {
            if (pawn != null && pawn != inPawn)
            {
                UnPossessInternal(false);
            }

            if (inPawn == null)
            {
                return;
            }

            if (inPawn.Controller != null && inPawn.Controller != this)
            {
                inPawn.Controller.UnPossess();
            }

            inPawn.PossessedBy(this);
            SetPawn(inPawn);
            SetControlRotation(inPawn.transform.rotation);
            inPawn.Restart();
        }

        public bool UnPossess()
        {
            return UnPossessInternal(true);
        }

        private bool UnPossessInternal(bool notifyPawnChanged)
        {
            if (pawn == null)
            {
                return false;
            }

            Pawn oldPawn = pawn;

            OnUnPossess();

            if (notifyPawnChanged)
            {
                PossessedPawnChanged?.Invoke(oldPawn, null);
            }

            return true;
        }

        protected virtual void OnUnPossess()
        {
            Pawn oldPawn = pawn;

            if (oldPawn == null)
            {
                return;
            }

            oldPawn.UnPossessed();
            SetPawn(null);
        }

        protected virtual void SetPawn(Pawn inPawn)
        {
            pawn = inPawn;
            character = inPawn as Character;

            if (playerState != null)
            {
                playerState.SetPawn(inPawn);
            }
        }

        public virtual void PawnPendingDestroy(Pawn inPawn)
        {
            if (inPawn != pawn)
            {
                return;
            }

            UnPossess();
            ChangeState(ControllerStates.Inactive);
        }

        public void SetPlayerState(PlayerState inPlayerState)
        {
            if (playerState == inPlayerState)
            {
                return;
            }

            CleanupPlayerState();

            if (inPlayerState != null)
            {
                InitPlayerState(inPlayerState);
            }
        }

        public virtual void InitPlayerState(PlayerState inPlayerState)
        {
            playerState = inPlayerState;
            playerState.SetOwningController(this);

            if (pawn != null)
            {
                pawn.SetPlayerState(playerState);
            }
        }

        public virtual void CleanupPlayerState()
        {
            if (playerState == null)
            {
                return;
            }

            if (pawn != null && pawn.PlayerState == playerState)
            {
                pawn.SetPlayerState(null);
            }

            if (playerState.OwningController == this)
            {
                playerState.SetOwningController(null);
            }

            if (playerState.Pawn == pawn)
            {
                playerState.SetPawn(null);
            }

            playerState = null;
        }

        public Quaternion GetControlRotation()
        {
            return controlRotation;
        }

        public virtual bool SetControlRotation(Quaternion newRotation)
        {
            if (!IsValidQuaternion(newRotation))
            {
                return false;
            }

            Quaternion normalizedRotation = NormalizeQuaternion(newRotation);
            if (Quaternion.Dot(controlRotation, normalizedRotation) > 0.999999f)
            {
                return false;
            }

            controlRotation = normalizedRotation;
            ControlRotationChanged?.Invoke(controlRotation);
            return true;
        }

        public virtual void ChangeState(string newStateName)
        {
            if (string.Equals(stateName, newStateName, StringComparison.Ordinal))
            {
                return;
            }

            string oldState = stateName;
            stateName = newStateName;
            StateChanged?.Invoke(oldState, stateName);
        }

        public void SetIgnoreMoveInput(bool ignore)
        {
            ignoreMoveInput = ignore ? ignoreMoveInput + 1 : Mathf.Max(0, ignoreMoveInput - 1);
        }

        public void ResetIgnoreMoveInput()
        {
            ignoreMoveInput = 0;
        }

        public void SetIgnoreLookInput(bool ignore)
        {
            ignoreLookInput = ignore ? ignoreLookInput + 1 : Mathf.Max(0, ignoreLookInput - 1);
        }

        public void ResetIgnoreLookInput()
        {
            ignoreLookInput = 0;
        }

        protected virtual void OnDestroy()
        {
            UnPossess();
            CleanupPlayerState();
        }

        private static bool IsValidQuaternion(Quaternion rotation)
        {
            return IsFinite(rotation.x)
                && IsFinite(rotation.y)
                && IsFinite(rotation.z)
                && IsFinite(rotation.w)
                && rotation != new Quaternion(0f, 0f, 0f, 0f);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static Quaternion NormalizeQuaternion(Quaternion rotation)
        {
            float magnitude = Mathf.Sqrt(
                rotation.x * rotation.x
                + rotation.y * rotation.y
                + rotation.z * rotation.z
                + rotation.w * rotation.w);

            return new Quaternion(
                rotation.x / magnitude,
                rotation.y / magnitude,
                rotation.z / magnitude,
                rotation.w / magnitude);
        }
    }
}
