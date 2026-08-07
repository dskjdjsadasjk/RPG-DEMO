using System.Collections.Generic;
using RPGDemo.GameFramework.Networking.Bootstrap;
using RPGDemo.GameFramework.Networking.Identity;
using RPGDemo.GameFramework.Networking.Protocol;
using UnityEngine;

namespace RPGDemo.GameFramework.Networking.Replication
{
    [DefaultExecutionOrder(10000)]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NetworkIdentity))]
    [RequireComponent(typeof(CharacterMovementComponent))]
    public sealed class CharacterNetworkMovement : MonoBehaviour
    {
        private const float MaxMoveDeltaTime = 0.1f;
        private const float OwnerCorrectionDistance = 0.05f;
        private const int MaxSavedMoves = 256;
        private const int MaxSnapshots = 32;
        private const uint InterpolationDelayTicks = 6;

        private readonly List<SavedMove> savedMoves = new List<SavedMove>(64);
        private readonly List<CharacterSnapshotMessage> snapshots
            = new List<CharacterSnapshotMessage>(MaxSnapshots);

        private NetworkIdentity identity;
        private CharacterMovementComponent movement;
        private NetworkRole configuredRole = NetworkRole.None;
        private uint nextMoveSequence = 1;
        private uint clientTick;
        private uint lastServerMoveSequence;
        private uint lastAckedMoveSequence;
        private uint lastSnapshotTick;
        private int correctionCount;
        private bool loggedServerMove;
        private bool loggedServerNonZeroMove;
        private bool loggedOwnerAck;
        private bool loggedSnapshot;
        private bool loggedLocalInput;

        public uint LastAckedMoveSequence => lastAckedMoveSequence;
        public int PendingMoveCount => savedMoves.Count;
        public int BufferedSnapshotCount => snapshots.Count;
        public int CorrectionCount => correctionCount;

        private void Awake()
        {
            identity = GetComponent<NetworkIdentity>();
            movement = GetComponent<CharacterMovementComponent>();
        }

        private void LateUpdate()
        {
            if (identity == null || movement == null)
            {
                return;
            }

            EnsureRoleConfiguration();
            if (!identity.IsSpawned)
            {
                return;
            }

            if (identity.Role == NetworkRole.AutonomousProxy)
            {
                CaptureAndSendLocalMove();
            }
            else if (identity.Role == NetworkRole.SimulatedProxy)
            {
                InterpolateSimulatedProxy();
            }
        }

        private void OnDisable()
        {
            if (movement != null)
            {
                movement.AutomaticTickEnabled = true;
            }

            configuredRole = NetworkRole.None;
            savedMoves.Clear();
            snapshots.Clear();
        }

        public bool TryProcessServerMove(
            CharacterMoveMessage move,
            out string validationError)
        {
            validationError = null;
            EnsureRoleConfiguration();
            if (identity == null
                || movement == null
                || !identity.IsSpawned
                || identity.Role != NetworkRole.Authority)
            {
                validationError = "Target is not an authoritative network character";
                return false;
            }

            if (move.DeltaTime <= 0f || move.DeltaTime > MaxMoveDeltaTime)
            {
                validationError = $"Move DeltaTime {move.DeltaTime:F4} is outside the allowed range";
                return false;
            }

            if (move.WorldInput.sqrMagnitude > 1.0001f)
            {
                validationError = "Move input magnitude exceeds 1";
                return false;
            }

            if (!IsNewerSequence(move.Sequence, lastServerMoveSequence))
            {
                return true;
            }

            Character character = movement.CharacterOwner;
            if (character != null && character.Controller != null)
            {
                character.Controller.SetControlRotation(
                    Quaternion.Euler(0f, move.ControlYaw, 0f));
            }

            movement.SimulateNetworkMove(move.WorldInput, move.DeltaTime);
            lastServerMoveSequence = move.Sequence;
            if (!loggedServerMove)
            {
                loggedServerMove = true;
                Debug.Log(
                    $"[Net][Move][DS] Processing ClientMove for NetId={identity.NetId}; "
                    + $"firstSequence={move.Sequence}.",
                    identity);
            }

            if (!loggedServerNonZeroMove && move.WorldInput.sqrMagnitude > 0.0001f)
            {
                loggedServerNonZeroMove = true;
                Debug.Log(
                    $"[Net][Move][DS] First non-zero move for NetId={identity.NetId}; "
                    + $"position={transform.position}.",
                    identity);
            }

            return true;
        }

        public void ReceiveServerMoveAck(CharacterMoveAckMessage ack)
        {
            EnsureRoleConfiguration();
            if (identity == null
                || movement == null
                || identity.Role != NetworkRole.AutonomousProxy
                || ack.NetId != identity.NetId
                || ack.AuthorityEpoch != identity.AuthorityEpoch
                || !IsNewerSequence(ack.AcknowledgedSequence, lastAckedMoveSequence))
            {
                return;
            }

            lastAckedMoveSequence = ack.AcknowledgedSequence;
            if (!loggedOwnerAck)
            {
                loggedOwnerAck = true;
                Debug.Log(
                    $"[Net][Move][Client] Prediction acknowledged for NetId={identity.NetId}; "
                    + $"firstAck={ack.AcknowledgedSequence}.",
                    identity);
            }

            int acknowledgedIndex = FindSavedMoveIndex(ack.AcknowledgedSequence);
            float positionError = acknowledgedIndex >= 0
                ? Vector3.Distance(savedMoves[acknowledgedIndex].PredictedPosition, ack.Position)
                : Vector3.Distance(transform.position, ack.Position);

            RemoveAcknowledgedMoves(ack.AcknowledgedSequence);
            if (positionError <= OwnerCorrectionDistance)
            {
                return;
            }

            movement.ApplyNetworkState(
                ack.Position,
                ack.Rotation,
                ack.Velocity,
                ack.MovementMode);

            for (int i = 0; i < savedMoves.Count; i++)
            {
                SavedMove savedMove = savedMoves[i];
                movement.SimulateNetworkMove(savedMove.WorldInput, savedMove.DeltaTime);
                savedMove.PredictedPosition = transform.position;
                savedMove.PredictedRotation = transform.rotation;
                savedMove.PredictedVelocity = movement.Velocity;
            }

            correctionCount++;
        }

        public void ReceiveSnapshot(CharacterSnapshotMessage snapshot)
        {
            EnsureRoleConfiguration();
            if (identity == null
                || movement == null
                || identity.Role != NetworkRole.SimulatedProxy
                || snapshot.NetId != identity.NetId
                || snapshot.AuthorityEpoch != identity.AuthorityEpoch
                || (lastSnapshotTick != 0 && !IsNewerTick(snapshot.ServerTick, lastSnapshotTick)))
            {
                return;
            }

            lastSnapshotTick = snapshot.ServerTick;
            if (!loggedSnapshot)
            {
                loggedSnapshot = true;
                Debug.Log(
                    $"[Net][Move][Client] Snapshot interpolation active for NetId={identity.NetId}; "
                    + $"firstServerTick={snapshot.ServerTick}.",
                    identity);
            }

            snapshots.Add(snapshot);
            if (snapshots.Count > MaxSnapshots)
            {
                snapshots.RemoveAt(0);
            }

            if (snapshots.Count == 1)
            {
                ApplySnapshot(snapshot);
            }
        }

        public CharacterMoveAckMessage CreateServerAck(uint serverTick)
        {
            return new CharacterMoveAckMessage(
                identity.NetId,
                identity.AuthorityEpoch,
                lastServerMoveSequence,
                serverTick,
                transform.position,
                transform.rotation,
                movement.Velocity,
                movement.CurrentMovementMode);
        }

        public CharacterSnapshotMessage CreateServerSnapshot(uint serverTick)
        {
            return new CharacterSnapshotMessage(
                identity.NetId,
                identity.AuthorityEpoch,
                serverTick,
                transform.position,
                transform.rotation,
                movement.Velocity,
                movement.CurrentMovementMode);
        }

        private void EnsureRoleConfiguration()
        {
            NetworkRole role = identity != null && identity.IsSpawned
                ? identity.Role
                : NetworkRole.None;
            if (role == configuredRole)
            {
                return;
            }

            configuredRole = role;
            savedMoves.Clear();
            snapshots.Clear();
            lastServerMoveSequence = 0;
            lastAckedMoveSequence = 0;
            lastSnapshotTick = 0;
            nextMoveSequence = 1;
            clientTick = 0;
            loggedServerMove = false;
            loggedServerNonZeroMove = false;
            loggedOwnerAck = false;
            loggedSnapshot = false;
            loggedLocalInput = false;

            if (movement != null)
            {
                movement.AutomaticTickEnabled = role == NetworkRole.None
                    || role == NetworkRole.AutonomousProxy;
            }
        }

        private void CaptureAndSendLocalMove()
        {
            NetworkBootstrap bootstrap = NetworkBootstrap.Instance;
            GameNetDriver driver = bootstrap != null ? bootstrap.NetDriver : null;
            if (driver == null
                || driver.Mode != NetworkProcessMode.Client
                || driver.ServerConnection == null
                || !driver.ServerConnection.IsReady)
            {
                return;
            }

            float deltaTime = Mathf.Clamp(Time.deltaTime, 1f / 240f, MaxMoveDeltaTime);
            Vector3 worldInput = Vector3.ClampMagnitude(movement.GetLastInputVector(), 1f);
            if (!loggedLocalInput && worldInput.sqrMagnitude > 0.0001f)
            {
                loggedLocalInput = true;
                Debug.Log(
                    $"[Net][Move][Client] Local movement input active for NetId={identity.NetId}; "
                    + $"input={worldInput}.",
                    identity);
            }

            float controlYaw = movement.CharacterOwner != null
                && movement.CharacterOwner.Controller != null
                    ? movement.CharacterOwner.Controller.ControlRotation.eulerAngles.y
                    : transform.eulerAngles.y;
            uint sequence = nextMoveSequence++;
            if (nextMoveSequence == 0)
            {
                nextMoveSequence = 1;
            }

            SavedMove savedMove = new SavedMove(
                sequence,
                ++clientTick,
                deltaTime,
                worldInput,
                controlYaw,
                transform.position,
                transform.rotation,
                movement.Velocity);
            savedMoves.Add(savedMove);
            if (savedMoves.Count > MaxSavedMoves)
            {
                savedMoves.RemoveAt(0);
            }

            driver.SendCharacterMove(
                identity,
                sequence,
                clientTick,
                deltaTime,
                worldInput,
                controlYaw);
        }

        private void InterpolateSimulatedProxy()
        {
            if (snapshots.Count == 0)
            {
                return;
            }

            NetworkBootstrap bootstrap = NetworkBootstrap.Instance;
            GameNetDriver driver = bootstrap != null ? bootstrap.NetDriver : null;
            uint estimatedServerTick = driver != null ? driver.ServerTick : lastSnapshotTick;
            double renderTick = estimatedServerTick > InterpolationDelayTicks
                ? estimatedServerTick - InterpolationDelayTicks
                : 0d;

            while (snapshots.Count >= 2 && snapshots[1].ServerTick <= renderTick)
            {
                snapshots.RemoveAt(0);
            }

            if (snapshots.Count == 1)
            {
                ApplySnapshot(snapshots[0]);
                return;
            }

            CharacterSnapshotMessage from = snapshots[0];
            CharacterSnapshotMessage to = snapshots[1];
            uint tickSpan = to.ServerTick - from.ServerTick;
            float alpha = tickSpan > 0
                ? Mathf.Clamp01((float)((renderTick - from.ServerTick) / tickSpan))
                : 1f;
            Vector3 position = Vector3.Lerp(from.Position, to.Position, alpha);
            Quaternion rotation = Quaternion.Slerp(from.Rotation, to.Rotation, alpha);
            Vector3 velocity = Vector3.Lerp(from.Velocity, to.Velocity, alpha);
            movement.ApplyNetworkState(position, rotation, velocity, to.MovementMode);
        }

        private void ApplySnapshot(CharacterSnapshotMessage snapshot)
        {
            movement.ApplyNetworkState(
                snapshot.Position,
                snapshot.Rotation,
                snapshot.Velocity,
                snapshot.MovementMode);
        }

        private int FindSavedMoveIndex(uint sequence)
        {
            for (int i = 0; i < savedMoves.Count; i++)
            {
                if (savedMoves[i].Sequence == sequence)
                {
                    return i;
                }
            }

            return -1;
        }

        private void RemoveAcknowledgedMoves(uint acknowledgedSequence)
        {
            while (savedMoves.Count > 0
                && !IsNewerSequence(savedMoves[0].Sequence, acknowledgedSequence))
            {
                savedMoves.RemoveAt(0);
            }
        }

        private static bool IsNewerSequence(uint candidate, uint baseline)
        {
            return candidate != baseline && unchecked(candidate - baseline) < 0x80000000u;
        }

        private static bool IsNewerTick(uint candidate, uint baseline)
        {
            return candidate != baseline && unchecked(candidate - baseline) < 0x80000000u;
        }

        private sealed class SavedMove
        {
            public SavedMove(
                uint sequence,
                uint clientTick,
                float deltaTime,
                Vector3 worldInput,
                float controlYaw,
                Vector3 predictedPosition,
                Quaternion predictedRotation,
                Vector3 predictedVelocity)
            {
                Sequence = sequence;
                ClientTick = clientTick;
                DeltaTime = deltaTime;
                WorldInput = worldInput;
                ControlYaw = controlYaw;
                PredictedPosition = predictedPosition;
                PredictedRotation = predictedRotation;
                PredictedVelocity = predictedVelocity;
            }

            public uint Sequence { get; }
            public uint ClientTick { get; }
            public float DeltaTime { get; }
            public Vector3 WorldInput { get; }
            public float ControlYaw { get; }
            public Vector3 PredictedPosition { get; set; }
            public Quaternion PredictedRotation { get; set; }
            public Vector3 PredictedVelocity { get; set; }
        }
    }
}
