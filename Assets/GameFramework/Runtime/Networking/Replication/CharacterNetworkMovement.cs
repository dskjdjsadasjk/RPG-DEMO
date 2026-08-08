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
        public const ushort MovementTickRate = GameNetDriver.DefaultTickRate;
        public const float FixedMoveDeltaTime = 1f / MovementTickRate;

        private const float FixedMoveDeltaTimeTolerance = 0.0001f;
        private const float OwnerCorrectionDistance = 0.05f;
        private const int MaxSavedMoves = 256;
        private const int MaxPendingServerMoves = 256;
        private const int MaxCatchUpTicksPerFrame = 8;
        private const int MaxSnapshots = 32;
        private const uint InterpolationDelayTicks = 6;

        private readonly List<SavedMove> savedMoves = new List<SavedMove>(64);
        private readonly Queue<CharacterMoveMessage> pendingServerMoves
            = new Queue<CharacterMoveMessage>(64);
        private readonly List<CharacterSnapshotMessage> snapshots
            = new List<CharacterSnapshotMessage>(MaxSnapshots);

        private NetworkIdentity identity;
        private CharacterMovementComponent movement;
        private NetworkRole configuredRole = NetworkRole.None;
        private uint nextMoveSequence = 1;
        private uint clientTick;
        private uint lastQueuedServerMoveSequence;
        private uint lastServerMoveSequence;
        private uint lastAckedMoveSequence;
        private uint lastSnapshotTick;
        private int correctionCount;
        private int sentMovesInWindow;
        private int receivedAcksInWindow;
        private int correctionsInWindow;
        private int processedServerMovesInWindow;
        private float clientDiagnosticWindowStart;
        private float serverDiagnosticWindowStart;
        private float lastPositionError;
        private Vector3 lastAckPosition;
        private Vector3 latestLocalInput;
        private double clientMoveAccumulator;
        private bool loggedServerMove;
        private bool loggedServerNonZeroMove;
        private bool loggedOwnerAck;
        private bool loggedSnapshot;
        private bool loggedLocalInput;
        private bool loggedExtremeCorrection;

        public uint LastAckedMoveSequence => lastAckedMoveSequence;
        public int PendingMoveCount => savedMoves.Count;
        public int PendingServerMoveCount => pendingServerMoves.Count;
        public int BufferedSnapshotCount => snapshots.Count;
        public int CorrectionCount => correctionCount;
        public float LastPositionError => lastPositionError;
        public Vector3 LastAckPosition => lastAckPosition;

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
                TickAutonomousProxy();
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
            pendingServerMoves.Clear();
            snapshots.Clear();
        }

        public bool TryQueueServerMove(
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

            if (Mathf.Abs(move.DeltaTime - FixedMoveDeltaTime) > FixedMoveDeltaTimeTolerance)
            {
                validationError = $"Move DeltaTime {move.DeltaTime:F6} does not match fixed step "
                    + $"{FixedMoveDeltaTime:F6}";
                return false;
            }

            if (move.ClientTick == 0)
            {
                validationError = "Move ClientTick must be non-zero";
                return false;
            }

            if (move.WorldInput.sqrMagnitude > 1.0001f)
            {
                validationError = "Move input magnitude exceeds 1";
                return false;
            }

            if (!IsNewerSequence(move.Sequence, lastQueuedServerMoveSequence))
            {
                return true;
            }

            if (pendingServerMoves.Count >= MaxPendingServerMoves)
            {
                validationError = $"Server move queue exceeded {MaxPendingServerMoves} entries";
                return false;
            }

            pendingServerMoves.Enqueue(move);
            lastQueuedServerMoveSequence = move.Sequence;
            return true;
        }

        public bool TryProcessServerMovementTick(
            uint serverTick,
            out CharacterMoveAckMessage ack)
        {
            ack = default;
            EnsureRoleConfiguration();
            if (identity == null
                || movement == null
                || !identity.IsSpawned
                || identity.Role != NetworkRole.Authority
                || pendingServerMoves.Count == 0)
            {
                return false;
            }

            CharacterMoveMessage move = pendingServerMoves.Dequeue();
            if (!IsNewerSequence(move.Sequence, lastServerMoveSequence))
            {
                return false;
            }

            Character character = movement.CharacterOwner;
            if (character != null && character.Controller != null)
            {
                character.Controller.SetControlRotation(
                    Quaternion.Euler(0f, move.ControlYaw, 0f));
            }

            movement.SimulateNetworkMove(move.WorldInput, FixedMoveDeltaTime);
            lastServerMoveSequence = move.Sequence;
            processedServerMovesInWindow++;
            LogServerDiagnosticsIfDue(move.WorldInput);
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

            ack = CreateServerAck(serverTick);
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
            receivedAcksInWindow++;
            lastAckPosition = ack.Position;
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
            lastPositionError = positionError;

            RemoveAcknowledgedMoves(ack.AcknowledgedSequence);
            if (positionError <= OwnerCorrectionDistance)
            {
                LogClientDiagnosticsIfDue();
                return;
            }

            movement.ApplyNetworkState(
                ack.Position,
                ack.Rotation,
                ack.Velocity,
                ack.MovementMode);
            Vector3 positionAfterApply = transform.position;

            for (int i = 0; i < savedMoves.Count; i++)
            {
                SavedMove savedMove = savedMoves[i];
                Vector3 positionBeforeReplay = transform.position;
                movement.SimulateNetworkMove(savedMove.WorldInput, savedMove.DeltaTime);
                if (!loggedExtremeCorrection && IsExtremePosition(transform.position))
                {
                    loggedExtremeCorrection = true;
                    Debug.LogError(
                        $"[Net][MoveDiag][Extreme] NetId={identity.NetId}, "
                        + $"ackSequence={ack.AcknowledgedSequence}, ackPosition={ack.Position}, "
                        + $"positionAfterApply={positionAfterApply}, "
                        + $"replayIndex={i}/{savedMoves.Count}, "
                        + $"replaySequence={savedMove.Sequence}, replayDt={savedMove.DeltaTime:F6}, "
                        + $"replayInput={savedMove.WorldInput}, "
                        + $"positionBeforeReplay={positionBeforeReplay}, "
                        + $"positionAfterReplay={transform.position}, velocity={movement.Velocity}.",
                        identity);
                }

                savedMove.PredictedPosition = transform.position;
                savedMove.PredictedRotation = transform.rotation;
                savedMove.PredictedVelocity = movement.Velocity;
            }

            correctionCount++;
            correctionsInWindow++;
            LogClientDiagnosticsIfDue();
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
            pendingServerMoves.Clear();
            snapshots.Clear();
            lastQueuedServerMoveSequence = 0;
            lastServerMoveSequence = 0;
            lastAckedMoveSequence = 0;
            lastSnapshotTick = 0;
            nextMoveSequence = 1;
            clientTick = 0;
            clientMoveAccumulator = 0d;
            latestLocalInput = Vector3.zero;
            sentMovesInWindow = 0;
            receivedAcksInWindow = 0;
            correctionsInWindow = 0;
            processedServerMovesInWindow = 0;
            clientDiagnosticWindowStart = Time.unscaledTime;
            serverDiagnosticWindowStart = Time.unscaledTime;
            lastPositionError = 0f;
            lastAckPosition = Vector3.zero;
            loggedServerMove = false;
            loggedServerNonZeroMove = false;
            loggedOwnerAck = false;
            loggedSnapshot = false;
            loggedLocalInput = false;
            loggedExtremeCorrection = false;

            if (movement != null)
            {
                movement.AutomaticTickEnabled = role == NetworkRole.None;
            }
        }

        private void TickAutonomousProxy()
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

            latestLocalInput = Vector3.ClampMagnitude(movement.ConsumeInputVector(), 1f);
            double maxAccumulatedTime = FixedMoveDeltaTime * MaxCatchUpTicksPerFrame;
            clientMoveAccumulator = System.Math.Min(
                clientMoveAccumulator + Mathf.Max(0f, Time.unscaledDeltaTime),
                maxAccumulatedTime);

            int elapsedTicks = (int)(clientMoveAccumulator / FixedMoveDeltaTime);
            for (int tickIndex = 0; tickIndex < elapsedTicks; tickIndex++)
            {
                SimulateAndSendLocalMove(driver, latestLocalInput);
            }

            clientMoveAccumulator -= elapsedTicks * FixedMoveDeltaTime;
            LogClientDiagnosticsIfDue();
        }

        private void SimulateAndSendLocalMove(GameNetDriver driver, Vector3 worldInput)
        {
            if (!loggedLocalInput && worldInput.sqrMagnitude > 0.0001f)
            {
                loggedLocalInput = true;
                Debug.Log(
                    $"[Net][Move][Client] Local movement input active for NetId={identity.NetId}; "
                    + $"input={worldInput}.",
                    identity);
            }

            movement.SimulateNetworkMove(worldInput, FixedMoveDeltaTime);
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
                FixedMoveDeltaTime,
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

            if (driver.SendCharacterMove(
                identity,
                sequence,
                clientTick,
                FixedMoveDeltaTime,
                worldInput,
                controlYaw))
            {
                sentMovesInWindow++;
            }

        }

        private void LogClientDiagnosticsIfDue()
        {
            float now = Time.unscaledTime;
            float elapsed = now - clientDiagnosticWindowStart;
            if (elapsed < 1f || identity == null || identity.Role != NetworkRole.AutonomousProxy)
            {
                return;
            }

            Debug.Log(
                $"[Net][MoveDiag][Client] NetId={identity.NetId}, "
                + $"fps={1f / Mathf.Max(Time.unscaledDeltaTime, 0.000001f):F0}, "
                + $"sent/s={sentMovesInWindow / elapsed:F1}, "
                + $"ack/s={receivedAcksInWindow / elapsed:F1}, "
                + $"corrections/s={correctionsInWindow / elapsed:F1}, "
                + $"input={movement.GetLastInputVector()}, velocity={movement.Velocity}, "
                + $"mode={movement.CurrentMovementMode}, block={movement.LastSimulationBlockReason}, "
                + $"attempted={movement.LastMoveAttempted}, request={movement.LastRequestedDisplacement}, "
                + $"moved={movement.LastMovementDelta}, collision={movement.LastCollisionFlags}, "
                + $"position={transform.position}, ackPosition={lastAckPosition}, "
                + $"error={lastPositionError:F4}, pending={savedMoves.Count}.",
                identity);

            sentMovesInWindow = 0;
            receivedAcksInWindow = 0;
            correctionsInWindow = 0;
            clientDiagnosticWindowStart = now;
        }

        private void LogServerDiagnosticsIfDue(Vector3 latestInput)
        {
            float now = Time.unscaledTime;
            float elapsed = now - serverDiagnosticWindowStart;
            if (elapsed < 1f || identity == null || identity.Role != NetworkRole.Authority)
            {
                return;
            }

            Debug.Log(
                $"[Net][MoveDiag][DS] NetId={identity.NetId}, "
                + $"processed/s={processedServerMovesInWindow / elapsed:F1}, "
                + $"input={latestInput}, velocity={movement.Velocity}, "
                + $"mode={movement.CurrentMovementMode}, block={movement.LastSimulationBlockReason}, "
                + $"attempted={movement.LastMoveAttempted}, request={movement.LastRequestedDisplacement}, "
                + $"moved={movement.LastMovementDelta}, collision={movement.LastCollisionFlags}, "
                + $"position={transform.position}, sequence={lastServerMoveSequence}, "
                + $"queued={pendingServerMoves.Count}.",
                identity);

            processedServerMovesInWindow = 0;
            serverDiagnosticWindowStart = now;
        }

        private static bool IsExtremePosition(Vector3 position)
        {
            const float diagnosticThreshold = 100000f;
            return !IsFinite(position.x)
                || !IsFinite(position.y)
                || !IsFinite(position.z)
                || Mathf.Abs(position.x) > diagnosticThreshold
                || Mathf.Abs(position.y) > diagnosticThreshold
                || Mathf.Abs(position.z) > diagnosticThreshold;
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
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
