using System;

namespace RPGDemo.GameFramework.Networking.Replication
{
    public sealed class ObjectReplicator
    {
        private byte[] lastSentState;
        private ushort nextSequence = 1;
        private ushort lastAppliedSequence;
        private bool hasAppliedSequence;

        internal ObjectReplicator(NetworkBehaviour target)
        {
            Target = target != null ? target : throw new ArgumentNullException(nameof(target));
        }

        public NetworkBehaviour Target { get; }
        public ushort ReplicationId => Target.ReplicationId;

        internal bool TryCaptureState(
            bool force,
            out ushort sequence,
            out byte[] state)
        {
            sequence = 0;
            state = Target.CaptureReplicatedState();
            if (!force && StatesEqual(lastSentState, state))
            {
                return false;
            }

            sequence = nextSequence++;
            if (nextSequence == 0)
            {
                nextSequence = 1;
            }

            lastSentState = state;
            return true;
        }

        internal bool TryApplyState(
            ushort sequence,
            byte[] state,
            bool isInitialState,
            out bool applied)
        {
            applied = false;
            if (sequence == 0 || state == null)
            {
                return false;
            }

            if (hasAppliedSequence && !IsNewerSequence(sequence, lastAppliedSequence))
            {
                return true;
            }

            if (!Target.TryApplyReplicatedState(state, isInitialState))
            {
                return false;
            }

            lastAppliedSequence = sequence;
            hasAppliedSequence = true;
            applied = true;
            return true;
        }

        private static bool StatesEqual(byte[] left, byte[] right)
        {
            if (ReferenceEquals(left, right))
            {
                return true;
            }

            if (left == null || right == null || left.Length != right.Length)
            {
                return false;
            }

            for (int i = 0; i < left.Length; i++)
            {
                if (left[i] != right[i])
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsNewerSequence(ushort candidate, ushort current)
        {
            ushort delta = (ushort)(candidate - current);
            return delta != 0 && delta < 0x8000;
        }
    }
}
