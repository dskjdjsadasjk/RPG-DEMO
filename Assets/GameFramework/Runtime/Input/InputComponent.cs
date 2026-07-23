using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace RPGDemo.GameFramework
{
    public class InputComponent : MonoBehaviour
    {
        [SerializeField]
        private int priority;

        [SerializeField]
        private bool blockInput;

        private readonly List<InputBinding> bindings = new List<InputBinding>();

        public int Priority
        {
            get => priority;
            set => priority = value;
        }

        public bool BlockInput
        {
            get => blockInput;
            set => blockInput = value;
        }

        public void BindValue<TValue>(string actionName, Action<TValue> callback)
            where TValue : struct
        {
            if (callback == null)
            {
                throw new ArgumentNullException(nameof(callback));
            }

            bindings.Add(new ValueInputBinding<TValue>(actionName, callback));
        }

        public void BindPerformed(string actionName, Action callback)
        {
            if (callback == null)
            {
                throw new ArgumentNullException(nameof(callback));
            }

            bindings.Add(new TriggerInputBinding(actionName, InputTrigger.Performed, callback));
        }

        public void BindPressed(string actionName, Action callback)
        {
            if (callback == null)
            {
                throw new ArgumentNullException(nameof(callback));
            }

            bindings.Add(new TriggerInputBinding(actionName, InputTrigger.Pressed, callback));
        }

        public void BindReleased(string actionName, Action callback)
        {
            if (callback == null)
            {
                throw new ArgumentNullException(nameof(callback));
            }

            bindings.Add(new TriggerInputBinding(actionName, InputTrigger.Released, callback));
        }

        public void ClearBindings()
        {
            bindings.Clear();
        }

        internal bool ProcessInput(InputActionAsset actions)
        {
            if (!isActiveAndEnabled || actions == null)
            {
                return false;
            }

            for (int index = 0; index < bindings.Count; index++)
            {
                bindings[index].Process(actions);
            }

            return blockInput;
        }

        private abstract class InputBinding
        {
            private readonly string actionName;
            private InputActionAsset resolvedAsset;
            private InputAction resolvedAction;

            protected InputBinding(string actionName)
            {
                if (string.IsNullOrWhiteSpace(actionName))
                {
                    throw new ArgumentException("Action name cannot be null or empty.", nameof(actionName));
                }

                this.actionName = actionName;
            }

            public void Process(InputActionAsset actions)
            {
                if (resolvedAsset != actions || resolvedAction == null)
                {
                    resolvedAsset = actions;
                    resolvedAction = actions.FindAction(actionName, false);
                }

                if (resolvedAction != null && resolvedAction.enabled)
                {
                    Execute(resolvedAction);
                }
            }

            protected abstract void Execute(InputAction action);
        }

        private sealed class ValueInputBinding<TValue> : InputBinding
            where TValue : struct
        {
            private readonly Action<TValue> callback;

            public ValueInputBinding(string actionName, Action<TValue> callback)
                : base(actionName)
            {
                this.callback = callback;
            }

            protected override void Execute(InputAction action)
            {
                callback(action.ReadValue<TValue>());
            }
        }

        private sealed class TriggerInputBinding : InputBinding
        {
            private readonly InputTrigger trigger;
            private readonly Action callback;

            public TriggerInputBinding(string actionName, InputTrigger trigger, Action callback)
                : base(actionName)
            {
                this.trigger = trigger;
                this.callback = callback;
            }

            protected override void Execute(InputAction action)
            {
                bool shouldInvoke = trigger switch
                {
                    InputTrigger.Performed => action.WasPerformedThisFrame(),
                    InputTrigger.Pressed => action.WasPressedThisFrame(),
                    InputTrigger.Released => action.WasReleasedThisFrame(),
                    _ => false
                };

                if (shouldInvoke)
                {
                    callback();
                }
            }
        }

        private enum InputTrigger
        {
            Performed,
            Pressed,
            Released
        }
    }
}
