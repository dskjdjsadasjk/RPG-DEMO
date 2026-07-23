using System.Collections.Generic;
using UnityEngine;
using UnityPlayerInput = UnityEngine.InputSystem.PlayerInput;

namespace RPGDemo.GameFramework
{
    [RequireComponent(typeof(UnityPlayerInput))]
    public class PlayerController : Controller
    {
        private readonly List<InputComponent> currentInputStack = new List<InputComponent>();
        private readonly List<InputComponent> processingInputStack = new List<InputComponent>();

        private UnityPlayerInput playerInput;
        private InputComponent inputComponent;
        private Vector3 rotationInput;

        public override bool IsLocalController => true;
        public UnityPlayerInput PlayerInput => playerInput;
        public InputComponent InputComponent => inputComponent;
        public Vector3 RotationInput => rotationInput;

        protected override void OnPossess(Pawn inPawn)
        {
            base.OnPossess(inPawn);

            if (Pawn == inPawn && Pawn != null)
            {
                InitInputSystem();
                Pawn.InitializePlayerInputComponent();
                ChangeState(ControllerStates.Playing);
            }
        }

        protected override void OnUnPossess()
        {
            base.OnUnPossess();
            rotationInput = Vector3.zero;
            ChangeState(ControllerStates.Inactive);
        }

        protected override void TickActor(float deltaTime)
        {
            if (IsLocalController)
            {
                InitInputSystem();

                if (playerInput != null)
                {
                    PlayerTick(deltaTime);
                }
            }

            base.TickActor(deltaTime);
            rotationInput = Vector3.zero;
        }

        protected virtual void InitInputSystem()
        {
            if (playerInput == null)
            {
                playerInput = GetComponent<UnityPlayerInput>();
            }

            if (inputComponent == null)
            {
                SetupInputComponent();
            }
        }

        protected virtual void SetupInputComponent()
        {
            inputComponent = GetComponent<InputComponent>();
            if (inputComponent == null)
            {
                inputComponent = gameObject.AddComponent<InputComponent>();
            }
        }

        protected virtual void PlayerTick(float deltaTime)
        {
            TickPlayerInput(deltaTime);

            bool updateRotation = false;

            if (StateName == ControllerStates.Playing)
            {
                if (Pawn == null)
                {
                    ChangeState(ControllerStates.Inactive);
                }
                else
                {
                    updateRotation = true;
                }
            }
            else if (StateName == ControllerStates.Inactive
                || StateName == ControllerStates.Spectating)
            {
                updateRotation = true;
            }

            if (updateRotation)
            {
                UpdateRotation(deltaTime);
            }
        }

        protected virtual void TickPlayerInput(float deltaTime)
        {
            ProcessPlayerInput(deltaTime);
        }

        protected virtual void ProcessPlayerInput(float deltaTime)
        {
            PreProcessInput(deltaTime);

            processingInputStack.Clear();
            BuildInputStack(processingInputStack);

            for (int index = processingInputStack.Count - 1; index >= 0; index--)
            {
                InputComponent component = processingInputStack[index];
                if (component != null && component.ProcessInput(playerInput.actions))
                {
                    break;
                }
            }

            processingInputStack.Clear();
            PostProcessInput(deltaTime);
        }

        protected virtual void BuildInputStack(List<InputComponent> inputStack)
        {
            if (Pawn != null && Pawn.InputComponent != null)
            {
                AddInputComponentIfMissing(inputStack, Pawn.InputComponent);
            }

            if (inputComponent != null)
            {
                AddInputComponentIfMissing(inputStack, inputComponent);
            }

            for (int index = 0; index < currentInputStack.Count; index++)
            {
                InputComponent component = currentInputStack[index];
                if (component != null)
                {
                    AddInputComponentIfMissing(inputStack, component);
                }
                else
                {
                    currentInputStack.RemoveAt(index);
                    index--;
                }
            }

            StableSortByPriority(inputStack);
        }

        protected virtual void PreProcessInput(float deltaTime)
        {
        }

        protected virtual void PostProcessInput(float deltaTime)
        {
            if (IsLookInputIgnored)
            {
                rotationInput = Vector3.zero;
            }
        }

        public void PushInputComponent(InputComponent component)
        {
            if (component == null)
            {
                return;
            }

            currentInputStack.Remove(component);
            currentInputStack.Add(component);
        }

        public bool PopInputComponent(InputComponent component)
        {
            if (component == null)
            {
                return false;
            }

            return currentInputStack.Remove(component);
        }

        public virtual void AddPitchInput(float value)
        {
            if (!IsLookInputIgnored)
            {
                rotationInput.x += value;
            }
        }

        public virtual void AddYawInput(float value)
        {
            if (!IsLookInputIgnored)
            {
                rotationInput.y += value;
            }
        }

        public virtual void AddRollInput(float value)
        {
            if (!IsLookInputIgnored)
            {
                rotationInput.z += value;
            }
        }

        public virtual void UpdateRotation(float deltaTime)
        {
            Vector3 viewRotation = ControlRotation.eulerAngles;
            viewRotation += rotationInput;

            SetControlRotation(Quaternion.Euler(viewRotation));

            if (Pawn != null)
            {
                Pawn.FaceRotation(ControlRotation, deltaTime);
            }
        }

        private static void AddInputComponentIfMissing(
            List<InputComponent> inputStack,
            InputComponent component)
        {
            if (!inputStack.Contains(component))
            {
                inputStack.Add(component);
            }
        }

        private static void StableSortByPriority(List<InputComponent> inputStack)
        {
            for (int index = 1; index < inputStack.Count; index++)
            {
                InputComponent component = inputStack[index];
                int insertionIndex = index;

                while (insertionIndex > 0
                    && inputStack[insertionIndex - 1].Priority > component.Priority)
                {
                    inputStack[insertionIndex] = inputStack[insertionIndex - 1];
                    insertionIndex--;
                }

                inputStack[insertionIndex] = component;
            }
        }
    }
}
