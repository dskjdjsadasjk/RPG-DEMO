# RPG-DEMO：UE 风格位置同步、移动预测与网络复制实施规划

> 第一版的具体开工顺序、最小类集合、七种消息字段以及服务器/客户端逐步实现方式，见 [第一版网络移动具体实现顺序.md](第一版网络移动具体实现顺序.md)。本文件保留完整架构和后续演进规划。

## 1. 结论与实施边界

当前工程最合适的路线不是先做一个通用 `NetworkTransform`，也不是直接复刻完整 `UNetDriver` / Iris，而是先完成一条可验证的 Character 纵向切片：

```text
本地输入
→ 固定 Tick 生成 MoveCommand
→ 本地立即模拟并保存 SavedMove
→ 不可靠冗余发送 ClientMoveBatch
→ 服务端按同一规则权威复演
→ 服务端累计 Ack 或返回 Correction
→ 自主代理删除已确认移动；纠错时恢复权威状态并重放未确认移动
→ 服务端把权威快照发给其他客户端
→ 模拟代理按 ServerTick 插值显示
```

第一版只支持：

- Dedicated Server / Client 和本机回环测试。
- `Walking`、平地、静态障碍、平面 Yaw 转向。
- 一个玩家拥有一个 Character。
- 服务端权威碰撞与位置；客户端位置只用于误差判断和调试，不能成为最终权威。
- 自主代理预测、纠错重放；模拟代理快照插值。
- Spawn、Despawn、Possess、UnPossess 的最小可靠生命周期。
- 丢包、延迟、抖动、乱序模拟与可观测指标。

第一版明确不做：

- 通用反射式属性复制、Iris 风格 Fragment/Serializer 系统。
- Root Motion、移动平台/相对 Base、蹲伏、跳跃、下落、游泳、攀爬。
- 物理刚体预测、命中回溯、全世界回滚、技能状态回滚。
- 自研 UDP、拥塞控制、加密、NAT 穿透和底层可靠传输。
- 直接依赖通用 `NetworkTransform` 驱动拥有者角色。

完成第一版后，再按实际玩法逐项扩展。这样能先证明最难且最基础的闭环是正确的。

---

## 2. 当前工程事实与必须先改的地方

已检查当前仓库，结论如下：

- Unity 版本为 `2022.3.62f3c1`。
- 工程没有网络 Transport、网络对象、RPC、快照、预测或网络测试程序集。
- `Assets/GameFramework/Runtime/Movement/CharacterMovementComponent.cs` 只有 `None` 和 `Walking`，直接在 `Update()` 链路中调用 `CharacterController.Move()`。
- `PawnMovementComponent.Update()` 使用渲染帧 `Time.deltaTime`，模拟结果会受 30/60/144 FPS 影响，不能直接用于预测重放。
- 当前 `Controller.HasAuthority` 固定为 `true`，`IsLocalController` 默认固定为 `true`，还不是真正的网络角色判断。
- `Pawn` 的输入缓存是按渲染帧写入/消费，没有“输入属于哪个网络 Tick”的定义。
- 没有 `.asmdef`、Runtime/EditMode/PlayMode 网络测试边界。
- SampleScene 中已有 Character、`CharacterController` 和 PlayerController，可作为第一条联调场景，但不能承担网络世界管理职责。

因此，不能直接在现有 `CharacterMovementComponent` 外面套发包代码。必须先做下面三个结构性改造：

1. **移动模拟可由明确 Tick 调用。** 从 `MonoBehaviour.Update()` 中移出移动热路径，输入一次只对应一个 `SimulationTick`。
2. **模拟状态可捕获、恢复、重放。** 至少包含位置、旋转、速度、加速度、MovementMode 和本 Tick 需要的瞬时标志。
3. **逻辑根与显示根分离。** Capsule/`CharacterController` 立即接受权威纠正；模型子节点通过视觉 Offset 消化跳变，不能把逻辑碰撞体慢慢 Lerp 到正确位置。

> 说明：Unity `CharacterController` 和 PhysX 场景不是跨机器位级确定性的。目标是“相同命令通常得到足够接近的结果，并由服务端纠错收敛”，不是承诺浮点位级一致。预测模拟必须无随机数、无渲染帧时间、无网络外副作用。

---

## 3. 从 UE 5.7 应复刻的核心模型

### 3.1 三种角色不是同一套移动逻辑

| 本项目角色 | UE 对应 | 行为 |
|---|---|---|
| `Authority` | `ROLE_Authority` | 服务端消费客户端命令并执行权威模拟；AI 也直接在这里模拟 |
| `AutonomousProxy` | `ROLE_AutonomousProxy` | 本地拥有者立即预测、保存 Move、发送命令、接收 Ack/Correction、重放 |
| `SimulatedProxy` | `ROLE_SimulatedProxy` | 不读取玩家输入，不执行拥有者预测，只消费服务端快照并平滑显示 |

`NetworkRole` 必须来自 `NetworkIdentity` 和连接所有权，不能继续由 `Controller` 中的布尔常量伪造。

### 3.2 自主代理闭环

UE 的关键链路是：

- `ReplicateMoveToServer`：创建 `FSavedMove_Character`，先本地执行，放入未确认历史，再把 New/Pending/Old Move 打包发送。
- `ServerMove_HandleMoveData`：处理可选 Old Move、Pending Move，最后处理 New Move。
- `ServerMove_PerformMovement`：验证时间戳，恢复压缩标志和控制旋转，用同一个 `MoveAutonomous` / `PerformMovement` 做权威模拟。
- `ServerCheckClientError`：比较 MovementMode 与位置误差；客户端报告位置不是权威位置。
- `SendClientAdjustment`：发送累计 Ack 或权威 Correction，并对普通/大纠错节流。
- `ClientAdjustPosition`：确认对应 Move，设置权威位置、速度和 MovementMode。
- `ClientUpdatePositionAfterServerUpdate`：对仍未确认的 Move 逐个 `PrepMoveFor → MoveAutonomous → PostUpdate`。

本项目应该完整保留这个闭环，但使用 `uint` Tick/Sequence 代替 UE 的浮点时间戳。Tick 更容易比较、回绕和测试，也不需要周期性重置浮点时间。

### 3.3 模拟代理闭环

模拟代理不参与拥有者纠错重放。服务端以较低频率发送类似 `FRepMovement` 的权威状态：

- ServerTick。
- Position、Rotation、LinearVelocity。
- MovementMode。
- Teleport/状态断点标志。

客户端把快照放入按 ServerTick 排序的缓冲区，在“估计服务端时间 - 插值延迟”处渲染。初版做线性位置插值 + `Quaternion.Slerp`；稳定后再考虑 Hermite 和有限外推。

### 3.4 逻辑位置与视觉平滑分离

UE 的 `SmoothCorrection` 会把 Capsule 放到新的逻辑位置，同时保留 Mesh Offset，再由 `SmoothClientPosition` 将 Offset 衰减到零。本项目也应使用：

```text
CharacterRoot（逻辑、碰撞、权威位置）
└── VisualRoot（模型、Animator、网络平滑 Offset）
```

自主代理的纠错与模拟代理的新快照都可以使用视觉平滑，但二者的数据来源不同：

- 自主代理：权威纠正 + 未确认输入重放后的“显示差值”。
- 模拟代理：两个服务端快照之间的时间插值。

不要用同一个 `Lerp(transform.position, ...)` 同时处理这两种情况。

---

## 4. 推荐总体架构

### 4.1 目录与程序集

```text
Assets/GameFramework/Runtime/Networking/
├── Core/
│   ├── NetworkDriver.cs
│   ├── NetworkClock.cs
│   ├── NetworkSimulationLoop.cs
│   ├── NetworkTick.cs
│   ├── SequenceMath.cs
│   ├── NetworkMode.cs
│   └── NetworkRole.cs
├── Transport/
│   ├── INetworkTransport.cs
│   ├── TransportEvent.cs
│   └── UnityTransportAdapter.cs
├── Connection/
│   ├── NetworkConnection.cs
│   ├── ConnectionState.cs
│   └── ConnectionMetrics.cs
├── Serialization/
│   ├── NetBitWriter.cs
│   ├── NetBitReader.cs
│   ├── NetQuantization.cs
│   └── ProtocolVersion.cs
├── Replication/
│   ├── NetworkIdentity.cs
│   ├── NetworkObjectRegistry.cs
│   ├── NetworkEntityChannel.cs
│   ├── ReplicationDriver.cs
│   ├── ConnectionReplicationView.cs
│   ├── ReplicatedMovementState.cs
│   └── InterestManager.cs
├── Messages/
│   ├── MessageType.cs
│   ├── PacketHeader.cs
│   ├── LifecycleMessages.cs
│   ├── CharacterMoveMessages.cs
│   └── SnapshotMessages.cs
├── Prediction/
│   ├── CharacterMoveCommand.cs
│   ├── SavedCharacterMove.cs
│   ├── CharacterMovementState.cs
│   ├── ClientCharacterPrediction.cs
│   ├── ServerCharacterMoveProcessor.cs
│   ├── ClientCharacterPredictionData.cs
│   ├── ServerCharacterPredictionData.cs
│   ├── ServerMoveQueue.cs
│   └── CharacterPredictionBuffer.cs
├── Interpolation/
│   ├── SnapshotBuffer.cs
│   ├── SimulatedProxyInterpolator.cs
│   └── NetworkSmoothingState.cs
├── Diagnostics/
│   ├── NetworkEmulator.cs
│   ├── NetworkMetrics.cs
│   └── NetworkDebugHud.cs
└── RPGDemo.GameFramework.Networking.asmdef

Assets/GameFramework/Tests/
├── EditMode/Networking/
└── PlayMode/Networking/
```

`Movement` 目录保留实际 Motor：

```text
Assets/GameFramework/Runtime/Movement/
├── CharacterMovementComponent.cs       // Unity façade + 按角色分派
├── CharacterMovementSimulation.cs      // 单 Tick 模拟入口
├── CharacterCollisionMotor.cs           // CharacterController 适配层
└── CharacterMovementSettings.cs         // 客户端/服务端共同参数
```

### 4.2 各类和 UE 的对应关系

| 本项目类 | UE 对应 | 第一版职责 |
|---|---|---|
| `NetworkDriver` | `UNetDriver` | 收包、连接 Tick、固定模拟 Tick、复制调度、发包 |
| `NetworkConnection` | `UNetConnection` | 连接状态、拥有者、通道、收发统计、超时 |
| `NetworkEntityChannel` | `UActorChannel` | 单连接上的实体生命周期、可靠事件和复制基线 |
| `ReplicationDriver` | `ServerReplicateActors` / ReplicationDriver | 按连接筛选实体、预算、生成快照 |
| `NetworkIdentity` | Actor NetGUID/Role/Owner | `NetId`、PrefabId、OwnerConnectionId、AuthorityEpoch、Role |
| `ReplicatedMovementState` | `FRepMovement` | Tick、位置、旋转、速度、模式、Teleport 标志 |
| `NetBitWriter/Reader` | `FBitWriter/FBitReader` | 有界按位序列化、可选字段、变长整数 |
| `NetQuantization` | `FVector_NetQuantize*` / `SerializeQuantizedVector` | 位置、速度、输入、旋转量化 |
| `CharacterMoveCommand` | `FCharacterNetworkMoveData` | 一个 Tick 的输入、标志、控制旋转、客户端结果 |
| `CharacterMoveBatch` | `FCharacterNetworkMoveDataContainer` | New + 可选 Recent + 可选 Oldest Unacked；稳定后再优化 Important Old |
| `SavedCharacterMove` | `FSavedMove_Character` | 重放所需输入及 Tick 前/后状态 |
| `ClientCharacterPredictionData` | `FNetworkPredictionData_Client_Character` | 未确认环形缓冲、最后 Ack、待重放、视觉 Offset |
| `ServerCharacterPredictionData` | `FNetworkPredictionData_Server_Character` | 已处理 Tick、待发 Ack/Correction、误差和节流状态 |
| `ServerMoveQueue` | `ServerMove_HandleMoveData` 前的接收/去重职责 | 固定容量缓存乱序 Move，只向模拟器提交连续 Tick |
| `CharacterMoveResponse` | `FCharacterMoveResponseDataContainer` / `FClientAdjustment` | 累计 Ack 或权威纠正 |
| `SimulatedProxyInterpolator` | `SmoothCorrection` / `SmoothClientPosition` 的代理侧职责 | 快照排序、插值、有限外推、断点吸附 |

### 4.3 不要照搬的 UE 结构

- 不实现 UObject/UFunction 反射和 UHT RPC；使用显式 Message Handler 表。
- 不实现每个 Actor 一个重量级通道对象的全部行为；第一版 `NetworkEntityChannel` 只维护生命周期和每连接基线。
- 不实现 PackageMap/对象路径解析；网络引用一律是 `NetId + AuthorityEpoch`。
- 不先实现 Iris；待手写 Character 纵向切片稳定后，再抽象 Serializer/Fragment。
- 不把 `FSavedMove_Character` 的 RootMotion、MovementBase、Overlap Counter 等全部字段搬过来。只保存当前 Walking 重放真正需要的字段。

---

## 5. Tick 与执行顺序

### 5.1 初始频率建议

所有值都放入 `NetworkSettings`，不得散落成魔法数：

| 项目 | 初始值 | 说明 |
|---|---:|---|
| Simulation Tick | 60 Hz | 客户端与服务端使用相同固定步长 |
| Client Move Send | 30 Hz | 每包通常携带最近 2 个 60 Hz Move，并可带一个最老未确认 Move |
| Server Snapshot | 20 Hz | 发给模拟代理；后续按距离/优先级降频 |
| Interpolation Delay | 100 ms | 先换取稳定缓冲，再根据抖动自适应 |
| Prediction History | 256 Tick | 约 4.27 秒；固定容量环形缓冲 |
| Max Replay | 128 Tick | 超出时放弃重放并硬吸附，同时记录异常 |

不要在第一版做可变 `DeltaTime` Move 合并。固定 Tick 下，合并只是“一个包中装多个独立 Tick 命令”，每个命令仍按固定步长执行，调试和防作弊都更简单。

### 5.2 单帧执行顺序

```text
EarlyUpdate / NetworkDriver.Update
1. Transport.Poll：收取所有包
2. 解包并进入连接级消息队列
3. 时钟更新、RTT/offset 估计
4. 累加 unscaledDeltaTime
5. while accumulator >= fixedStep：
   a. 应用本 Tick 前必须生效的可靠生命周期消息
   b. AutonomousProxy 采样已缓存输入 → CharacterMoveCommand
   c. Client 本地预测 / Server 权威模拟 / AI 权威模拟
   d. 记录 SavedMove 或权威 Snapshot
   e. Server 处理误差、Ack/Correction、复制调度
6. 按发送频率组包并 Flush
7. LateUpdate：只更新 VisualRoot 平滑，不改逻辑状态
```

输入系统仍可在渲染帧收集设备状态，但只能写入 `PlayerInputAccumulator`。网络固定 Tick 从累加器生成一次命令；Pressed/Released 需要边沿锁存，直到某个 Tick 消费，不能依赖 `WasPressedThisFrame()` 在重放时再次查询设备。

---

## 6. 协议设计

### 6.1 基本规则

- 字节序固定为 Little Endian，协议版本必须显式写入握手。
- 所有高频消息都有最大字节数和最大元素数，读取前先做边界校验。
- Tick/Sequence 使用无符号回绕比较，统一经过 `SequenceMath`，禁止直接用 `a > b`。
- 高频移动消息不可靠但有序语义由应用层 Sequence/Tick 保证；旧包可安全丢弃。
- 关键生命周期使用 Transport 的 Reliable Ordered Pipeline；不要自己重写可靠 UDP。
- 客户端发来的 `NetId` 必须属于该连接，且 `AuthorityEpoch` 必须匹配，否则丢弃并计数。
- 不序列化 C# 类型名、场景路径、浮点 `Time.time` 或任意对象引用。

### 6.2 消息分类与可靠性

| 方向 | 消息 | 可靠性 | 说明 |
|---|---|---|---|
| 双向 | `Hello/Welcome/Disconnect` | Reliable Ordered | 版本、会话、配置摘要 |
| S→C | `Spawn/Despawn/Ownership/Possess` | Reliable Ordered | 生命周期和所有权切换 |
| C→S | `CharacterMoveBatch` | Unreliable Sequenced | New + 最近冗余 Move + 可选 Oldest Unacked |
| S→C Owner | `CharacterMoveResponse` | Unreliable Sequenced、重复直到客户端回执 | 累计 Ack 或 Correction，避免可靠队头阻塞移动 |
| S→C Others | `WorldSnapshot` | Unreliable Sequenced | 模拟代理位置快照 |
| 双向 | Gameplay RPC | 按语义选择 | 高频表现不可靠；关键状态可靠且幂等 |

### 6.3 包头

建议应用层包头：

```text
PacketHeader
- Magic                 : uint16
- ProtocolVersion       : uint8
- PacketSequence        : uint16
- RemotePacketAck       : uint16
- RemotePacketAckBits   : uint32
- SenderTick            : uint32
- MessageCount          : uint8
```

Packet Ack 位图主要用于 RTT、丢包估计和诊断，不替代 Transport 的可靠管线。每个子消息再包含 `MessageType + PayloadBits/Bytes`，未知消息按长度跳过或按版本策略断开。

### 6.4 `CharacterMoveBatch`（客户端到服务端）

```text
CharacterMoveBatch
- NetId                     : uint32
- AuthorityEpoch            : uint16
- BatchSequence             : uint16
- LastAppliedResponseSeq    : uint16
- MoveCount                 : 3 bits，范围 1..4
- Moves[MoveCount]

CharacterMoveCommand
- ClientTick                : uint32（包内后续项可写 varuint tick delta）
- InputX                    : int8，[-127, 127]
- InputY                    : int8，[-127, 127]
- ControlYaw                : uint16
- ControlPitch              : uint16，可选；第一版可省略
- MoveFlags                 : uint8
- ClaimedMovementMode       : 3~4 bits
- ClientEndPositionCm       : 3 × int32（只在 New Move 必填）
```

第一版 `MoveFlags`：

| Bit | 含义 |
|---:|---|
| 0 | JumpPressed，预留，第一版不执行 |
| 1 | WantsToCrouch，预留 |
| 2 | SprintWanted，若玩法尚未接入则固定 0 |
| 3 | ForceNoCombine / 状态断点 |
| 4..7 | 自定义预测标志 |

冗余策略先采用“最近 2 个未确认 Tick + 1 个最老未确认 Tick”。服务端把乱序命令放入固定容量 `ServerMoveQueue`，只从 `LastProcessedClientTick + 1` 开始连续执行和推进累计 Ack。这样，即使突发丢包超过近期冗余窗口，最老缺口仍会被后续包补回；每个 Tick 按 `ClientTick` 去重且只执行一次。

不能在第一版只补发“重要 Move”：固定 Tick 模型中，一个普通直线输入 Tick 丢失也会形成序列缺口。等连续 Ack 和缺口恢复稳定后，才可引入类似 UE `CanCombineWith/IsImportantMove` 的优化；届时必须明确定义同输入区间如何合并或重建，且不能悄悄改变逻辑 Tick 数。

### 6.5 `CharacterMoveResponse`（服务端到拥有者）

```text
CharacterMoveResponse
- NetId                  : uint32
- AuthorityEpoch         : uint16
- ResponseSequence       : uint16
- ServerTick             : uint32
- AckClientTick          : uint32
- Flags                   : IsCorrection / HasRotation / Teleport / ModeChanged

CorrectionPayload（IsCorrection 时）
- PositionCm             : 3 × int32
- VelocityCmPerSec       : 3 × int16
- RotationYaw            : uint16（按 Flags 可选）
- MovementMode           : 4 bits
```

`AckClientTick` 是累计确认：客户端删除所有 `<= AckClientTick` 的 SavedMove。Correction 必须描述“服务端执行完 AckClientTick 后的状态”，客户端才能从这个状态重放更晚的 Move。

Response 使用不可靠消息时，服务端保存最新未回执 Response，并在后续发包中重复。客户端在下一批 Move 中带回 `LastAppliedResponseSeq`。消息必须幂等：重复 Correction 不能重复消费 Gameplay 事件。

### 6.6 `WorldSnapshot`（服务端到模拟代理）

```text
WorldSnapshot
- SnapshotSequence       : uint16
- ServerTick             : uint32
- EntityCount            : varuint
- Entities[]

ReplicatedMovementState
- NetId                  : uint32
- AuthorityEpoch         : uint16
- ChangeMask             : bits
- PositionCm             : 3 × int32
- VelocityCmPerSec       : 3 × int16
- RotationYaw            : uint16
- MovementMode           : 4 bits
- Teleport               : 1 bit
```

第一版发送完整 Character Movement State，先把正确性和字节统计做出来。第二版才加“相对上次已确认基线”的 ChangeMask/Delta Compression。拥有者通常不消费自己的普通 Transform Snapshot，自己的收敛只走 MoveResponse。

### 6.7 量化策略

项目使用米，初版建议：

- Position：乘 100 后四舍五入成 `int32`，精度 1 cm。先求正确、范围安全。
- Velocity：乘 100 后写 `int16`，范围约 ±327.67 m/s；超界必须 Clamp 并记录。
- Input：单位圆 Clamp 后映射到 `int8`。
- Yaw/Pitch：`0..360°` 映射到 `uint16`。
- Flags/Mode/可选字段：按位写入。

稳定后再实现 UE 风格变长 Packed Vector，或使用“World Cell/Origin + int24 局部坐标”减少位置带宽。优化前必须有 Golden Packet 和往返误差测试，不能先凭感觉压位。

---

## 7. 移动状态、命令与保存历史

### 7.1 `CharacterMovementState`

第一版最低字段：

```csharp
public struct CharacterMovementState
{
    public uint Tick;
    public Vector3 Position;
    public Quaternion Rotation;
    public Vector3 Velocity;
    public Vector3 Acceleration;
    public MovementMode MovementMode;
    public bool JustTeleported;
}
```

以后加入 Jump/Falling 时，再添加 Grounded、FloorNormal、JumpHold、MovementBase NetId、相对 Base 状态等。不要提前复制 UE SavedMove 的所有字段。

### 7.2 `SavedCharacterMove`

```csharp
public struct SavedCharacterMove
{
    public uint ClientTick;
    public CharacterMoveCommand Command;
    public CharacterMovementState StartState;
    public CharacterMovementState EndState;
    public bool WasSent;
    public bool IsImportant;
}
```

历史放固定容量环形缓冲；禁止每 Tick `new`、LINQ、List 扩容。要提供：

- `Push`。
- `TryFind(tick)`。
- `DiscardThrough(ackTick)`。
- `EnumerateAfter(ackTick)`，按 Tick 升序且无分配。
- 回绕、满容量和断档检测。

### 7.3 唯一模拟入口

建议 API：

```csharp
CharacterMovementState SimulateTick(
    in CharacterMovementState start,
    in CharacterMoveCommand command,
    float fixedDeltaTime,
    SimulationContext context);
```

客户端预测、服务端权威模拟、纠错后重放、离线测试都必须调用同一入口。它可以通过 `CharacterCollisionMotor` 在主线程驱动 `CharacterController`，但下列行为必须放在模拟外层或变成幂等事件：

- 播音效、特效、Camera Shake。
- 发伤害、奖励、任务进度。
- 创建/销毁 GameObject。
- 读取真实设备输入。
- 读取 `Time.deltaTime`、`Time.time` 或随机数。

重放期间设置 `SimulationContext.IsReplay`，预测事件用 `(NetId, ClientTick, EventType)` 去重。

---

## 8. 客户端需要实现的代码

### 8.1 本地自主代理

`ClientCharacterPrediction` 每个固定 Tick：

1. 从 `PlayerInputAccumulator` 生成 `CharacterMoveCommand`。
2. 捕获 `StartState`。
3. 调用唯一 `SimulateTick`，立即响应输入。
4. 捕获 `EndState`，写入 `CharacterPredictionBuffer`。
5. 到发送时刻构建 MoveBatch，包含 New、近期冗余和 Oldest Unacked。
6. 收到 Ack 时累计删除历史。
7. 收到 Correction 时：
   - 验证 NetId、AuthorityEpoch、ResponseSequence。
   - 找到 `AckClientTick`；若历史已断档则硬吸附并清空。
   - 保存纠正前 VisualRoot 的世界姿态。
   - 把逻辑根恢复到权威 State。
   - 删除已确认 Move。
   - 按 Tick 重放其后的未确认 Move。
   - 计算视觉 Offset，让画面从纠正前视觉位置平滑到新的逻辑结果。
   - 回传 `LastAppliedResponseSeq`。

### 8.2 远端模拟代理

`SimulatedProxyInterpolator`：

- 按 `(AuthorityEpoch, ServerTick)` 接收和排序快照。
- 丢弃旧 Epoch 和过时 Tick。
- 目标渲染 Tick = 估计服务端 Tick - InterpolationDelayTicks。
- 有左右两帧时做位置线性插值和旋转 Slerp。
- 只有旧帧时最多外推 2~3 个 Snapshot 间隔；超出后保持最后状态。
- Teleport、Epoch 变化、模式断点或距离超过阈值时清空 Buffer 并吸附。
- Snapshot 不足、迟到、乱序、外推时长都写入指标。

### 8.3 客户端网络生命周期

- `Welcome` 后建立本地 ConnectionId 和网络时钟。
- `Spawn` 创建 Prefab，注册 NetId 后再处理引用它的消息。
- `Ownership/Possess` 变更 AuthorityEpoch，清空旧 Pawn 的输入、SavedMove、Response 和 Snapshot Buffer。
- 只有本地拥有的 Pawn 创建/启用玩家输入组件。
- `Despawn` 必须注销 NetId、解绑 Controller、清空所有预测/插值数据。

---

## 9. 服务端需要实现的代码

### 9.1 `ServerCharacterMoveProcessor`

收到 MoveBatch 后按以下顺序执行：

1. 验证连接已进入 Playing。
2. 验证 NetId 属于该连接且 AuthorityEpoch 一致。
3. 验证包长度、MoveCount、输入范围、BatchSequence。
4. 按 ClientTick 去重并写入固定容量 `ServerMoveQueue`，拒绝过旧或过度超前的 Tick。
5. 从 `LastProcessedClientTick + 1` 开始，仅对连续到达的 Move 逐个执行；中间有缺口时缓存后续 Move，等待客户端的 Oldest Unacked 冗余补齐。
6. 对每个可连续处理的 Move：
   - 反量化输入和控制旋转。
   - 服务端自己计算固定 `DeltaTime`，不接受客户端 DeltaTime。
   - Clamp 加速度、速度和玩法允许的标志。
   - 调用同一个 `CharacterMovementSimulation.SimulateTick`。
   - 更新 `LastProcessedClientTick`。
7. 只对最新完成 Move 的客户端报告结果做位置/模式误差检查。
8. 生成累计 Ack 或 Correction，保存为 PendingResponse。Correction 未被 `LastAppliedResponseSeq` 回执前不得被普通 Ack 覆盖。

服务端还要维护“已执行客户端 Tick 数 / 已流逝服务端时间”的预算，限制单帧补跑数量和客户端可领先窗口。丢包恢复时允许有上限的追赶，但不能因为客户端一次塞入大量未来 Tick 就在一个服务端帧内获得额外移动时间；超过预算的 Move 保留到后续服务端 Tick 或直接判为非法。

### 9.2 校验与防作弊基线

第一版至少校验：

- 发送者是否拥有 Pawn。
- AuthorityEpoch 是否有效，防止重生后旧包控制新 Pawn。
- Tick 是否单调、是否超过最大未来窗口、是否落后于历史窗口。
- 单包 Move 数和每秒消息数。
- 输入向量是否超单位圆、Flag 是否为允许集合。
- 服务端配置决定 MaxAcceleration/MaxSpeed，客户端值一律不可信。
- 客户端声明 MovementMode 是否与服务端一致。
- 客户端 EndPosition 与服务端结果误差。
- NaN、Infinity、量化溢出、畸形长度立即拒绝。

位置误差的初始可配置建议：

- `GoodMoveTolerance = 0.05 m`：以内累计 Ack。
- `LargeCorrectionDistance = 0.50 m`：大纠错提高发送优先级。
- `NoSmoothDistance = 2.00 m`：客户端视觉直接吸附。
- 普通 Correction 最小间隔 `0.10 s`，大纠错 `0.05 s`。

这些不是永久玩法常量。应通过网络矩阵收集误差分布后调整，避免容差太小导致持续抖动，或太大掩盖速度外挂。

### 9.3 `ReplicationDriver`

第一版每个服务端 Snapshot Tick：

1. 为每个连接构造 `ConnectionReplicationView`。
2. 从已 Spawn 实体中筛选 Relevant 集合。
3. 跳过未确认 Spawn 的实体状态，先保证生命周期有序。
4. 按优先级和字节预算写入 WorldSnapshot。
5. 对拥有者跳过普通 ReplicatedMovement，改走 MoveResponse。
6. 记录每实体/每消息字节数和被预算延迟的 Tick 数。

初始 Interest 只需“同一场景且距离小于固定半径”；稳定后再做网格 Spatial Hash、OwnerOnly、AlwaysRelevant、Dormancy 和动态更新频率。

---

## 10. 现有类的具体改造

### 10.1 `PawnMovementComponent`

- 移除或禁用其自主 `Update → TickComponent(Time.deltaTime)` 热路径。
- 暴露由 `NetworkSimulationLoop` 调用的 `SimulateTick`。
- 保留输入向量接口作为上层意图入口，但固定 Tick 生成命令后再消费。
- 对 Standalone 也走相同固定 Tick，保证离线和联网代码路径一致。

### 10.2 `CharacterMovementComponent`

- 保持其作为 UE `UCharacterMovementComponent` 的 Unity façade。
- 把 `CalcVelocity`、`ApplyVelocityBraking`、`PhysWalking` 整理到可测试的 `CharacterMovementSimulation`。
- 新增显式状态 Capture/Restore；Restore 必须同步 `CharacterController`、Transform、内部 velocity/mode。
- `TickComponent` 根据 Role 分派：Authority、AutonomousProxy、SimulatedProxy。
- 不在该类里实现 Socket、连接或通用复制。

### 10.3 `Controller` / `PlayerController`

- `HasAuthority`、`IsLocalController` 改为读取 `NetworkIdentity/NetworkContext`。
- `Possess/UnPossess` 只有 Authority 能决定；客户端只应用服务端可靠消息。
- Input Stack 只在 Local Autonomous Controller 启用。
- `ControlRotation` 进入 MoveCommand；服务端按规则应用并验证。

### 10.4 `Pawn` / `Character`

- 增加 `NetworkIdentity` 引用和 Role/Owner 变化回调。
- Possess/Epoch 切换时清空 Pending Input。
- 设备输入只产生意图，不直接修改 Transform。
- 给 Character 建立 `VisualRoot`；Animator 和 Mesh 放在其下，Capsule 留在逻辑根。

---

## 11. 分阶段执行路线

### 阶段 0：建立程序集、配置与测试基线

实现：

- Runtime/Tests `.asmdef`。
- `NetworkSettings`、`NetworkTick`、`SequenceMath`。
- 固定容量 Ring Buffer。
- 不依赖真实网络的 Loopback Transport 和 Network Emulator。

验收：

- Tick/Sequence 在 `uint` 回绕边界比较正确。
- 环形缓冲满、空、覆盖、断档行为有测试。
- 0/50/100/200 ms 延迟、抖动、丢包、乱序可确定性复现。

### 阶段 1：把单机移动变成可重放固定 Tick

实现：

- `CharacterMoveCommand`、`CharacterMovementState`。
- `CharacterMovementSimulation` 和 Capture/Restore。
- Standalone 也使用 60 Hz 固定模拟。
- 输入边沿锁存。

验收：

- 同一命令录制在 30/60/144 渲染 FPS 下执行相同 Tick 数后，状态在约定误差内一致。
- Restore 后重放同一命令序列得到一致结果。
- 重放不重复产生表现/GamePlay 副作用。

### 阶段 2：协议、量化与 Transport 适配

实现：

- `INetworkTransport` 和维护良好的底层 Transport 适配器。
- `NetBitWriter/Reader`、包头、版本握手。
- Move、Response、Snapshot、Lifecycle 序列化。

验收：

- 每种消息有 Round-trip、边界、截断、随机畸形输入测试。
- Golden Packet 固定，协议变更必须显式提升版本。
- 热路径每 Tick 0 GC Alloc；包大小可观测。

### 阶段 3：连接、NetId、Role 与生命周期

实现：

- `NetworkDriver`、`NetworkConnection`、`NetworkIdentity`、Registry。
- Hello/Welcome、Spawn/Despawn、Ownership/Possess。
- AuthorityEpoch。

验收：

- 两个客户端加入后只控制自己的 Pawn。
- 断线、Despawn、重生不会遗留旧 NetId 或旧预测历史。
- 重生前延迟到达的 Move 无法控制新 Pawn。

### 阶段 4：客户端预测与 Move 发送

实现：

- `SavedCharacterMove`、Client Prediction Data、固定环形历史。
- 本地先模拟后发送。
- New/Recent/Oldest Unacked 冗余 MoveBatch，服务端用固定队列缓存乱序命令。

验收：

- 100 ms RTT 下本地输入立即响应。
- 5% 丢包下服务端命令流不永久断档。
- 服务端按 Tick 去重，同一命令绝不执行两次。

### 阶段 5：服务端权威复演、Ack 与 Correction

实现：

- `ServerCharacterMoveProcessor`。
- 权限/Tick/输入/模式/位置校验。
- 累计 Ack、重复 Pending Correction、客户端回执。

验收：

- 0 ms 无扰动时纠错应接近零，而不是周期性抖动。
- 人工修改客户端位置后，服务端拒绝并使其收敛到权威状态。
- 丢失一次 Correction 后，后续重复响应仍能收敛。

### 阶段 6：客户端纠错重放与视觉平滑

实现：

- 按 AckTick 查找、删除、恢复、重放。
- VisualRoot Offset 平滑。
- 历史断档、超最大重放、Teleport 的硬恢复。

验收：

- Correction 后逻辑位置立即正确，未确认输入不丢失。
- 视觉没有把碰撞体慢慢拖过墙。
- ReplayCount、CorrectionDistance、CorrectionReason 可观测。

### 阶段 7：模拟代理快照与插值

实现：

- `ReplicatedMovementState`、WorldSnapshot。
- SnapshotBuffer、服务端时钟估计、插值/有限外推。
- Teleport 和 Epoch 断点。

验收：

- 20 Hz 快照在 60/144 FPS 渲染下连续。
- 乱序包不会让时间倒退。
- 100 ms RTT、20 ms 抖动、5% 丢包下不无限外推。

### 阶段 8：复制调度、相关性和预算

实现：

- `ReplicationDriver`、Connection View、距离相关性。
- Snapshot 字节预算、优先级、更新频率。
- 最小 `NetworkEntityChannel` 基线。

验收：

- 不相关实体不发包。
- 带宽饱和时近处玩家和拥有者响应优先。
- Spawn 尚未确认时不会先收到无法解析的状态。

### 阶段 9：玩法扩展

严格按以下顺序加入，每加入一种模式都扩展 SavedMove、协议和重放测试：

1. Jump + Falling + 落地。
2. Crouch/Sprint 和预测标志。
3. 移动平台与相对 Base（`BaseNetId + RelativePosition/Velocity`）。
4. Gameplay Ability 对移动参数的可序列化 Modifier。
5. Dash/Knockback/RootMotionSource。
6. 最后才考虑动画 Root Motion 和更广泛的 Gameplay Prediction。

---

## 12. 第一个可运行里程碑

第一个里程碑不要同时追求完整 Gameplay Framework。场景只保留：一块地面、几面墙、两个 Capsule、一个 Dedicated Server 和两个客户端。

必须演示：

1. Client A/B 各自 Possess 一个 Character。
2. 两端本地移动即时响应。
3. Server 以同一固定 Tick 权威复演。
4. Owner 收到累计 Ack，正常情况下历史长度稳定。
5. 注入客户端位置偏移后触发 Correction，恢复后重放未确认 Move。
6. 另一客户端看到该角色以 Snapshot 插值移动。
7. 开启 `100 ms RTT + 20 ms jitter + 5% loss` 后仍可移动并最终收敛。
8. HUD 显示 Tick、RTT、Loss、LastAck、PendingMoves、CorrectionCount、ReplayCount、SnapshotBuffer 和 bytes/s。

这个里程碑完成，才说明 UE 风格网络移动骨架成立。

---

## 13. 测试矩阵与完成定义

### 13.1 必须自动化的测试

| 类别 | 用例 |
|---|---|
| Tick | 回绕、追赶上限、暂停/卡顿、不同渲染 FPS |
| 序列化 | Round-trip、Golden Packet、最大/最小值、截断、未知版本、NaN/溢出 |
| 历史 | Ack 头/中/尾、重复 Ack、过期 Correction、满缓冲、断档 |
| 服务端 | 重复 Move、乱序 Move、未来 Tick、旧 Epoch、错误 Owner、非法 Flags |
| 重放 | 0/1/N 个未确认 Move、模式变化、Teleport、超过 MaxReplay |
| 插值 | 两帧、单帧、空缓冲、乱序、丢帧、有限外推、Teleport |
| 生命周期 | Spawn 前状态、Despawn 后旧包、重生 Epoch、断线清理 |

### 13.2 网络矩阵

| RTT | Jitter | Loss | 目标 |
|---:|---:|---:|---|
| 0 ms | 0 ms | 0% | 无周期性纠错，客户端/服务端稳定一致 |
| 50 ms | 10 ms | 0% | 本地即时，Ack 稳定推进 |
| 100 ms | 20 ms | 5% | 可持续移动、自动恢复、历史不无限增长 |
| 200 ms | 50 ms | 10% | 不崩溃、不失控、不无限重放；允许更多可见纠正 |

### 13.3 必须暴露的指标

- LocalClientTick、EstimatedServerTick、ServerSimulationTick。
- LastSentMoveTick、LastProcessedMoveTick、LastAckedMoveTick。
- SavedMoveCount、OldestSavedMoveAge、LastReplayCount。
- RTT、Jitter、PacketLoss、OutOfOrder、DuplicateMoveCount。
- CorrectionCount、CorrectionReason、Last/Average/MaxPositionError。
- SnapshotBufferCount、InterpolationDelay、ExtrapolationTime。
- 每种消息 packets/s、bytes/s、最大包、序列化失败数。
- 每 Tick GC Alloc、移动模拟耗时、复制调度耗时。

完成定义不是“看起来不卡”，而是上述指标能解释每次纠错和每个丢包场景，且自动化测试覆盖协议和状态机边界。

---

## 14. 哪些应自己实现，哪些应复用

### 必须自己实现

- NetworkRole、NetId、AuthorityEpoch 和 Gameplay Framework 所有权语义。
- 固定网络 Tick、客户端/服务端时钟映射。
- MoveCommand/SavedMove、预测历史、服务端权威复演。
- Ack/Correction、纠错后重放、视觉平滑。
- 模拟代理 Snapshot Buffer 和插值策略。
- Character 移动协议、量化、版本和边界校验。
- 复制相关性/优先级的游戏规则。
- 网络模拟、指标和测试。

### 应复用成熟库

- UDP Socket、连接握手底座、可靠有序 Pipeline、分片/MTU 处理。
- 平台网络适配、加密/认证、Relay/NAT（确有联网发布需求时）。
- Unity Test Framework 和 Profiler。

Transport 只负责“如何可靠/不可靠地把字节送到连接”，不能决定 Pawn 如何预测、服务端如何模拟或怎样纠错。用 `INetworkTransport` 隔离后，可以先用 Loopback 测核心，再接 Unity Transport 或其他成熟库。

---

## 15. 常见错误与禁止事项

- 禁止拥有者每帧把 Transform 发给服务端并让服务端照单全收。
- 禁止客户端和服务端使用不同的 Movement Settings 或不同模拟入口。
- 禁止在预测热路径读取 `Time.deltaTime`、真实输入、随机数或不可重放单例状态。
- 禁止 Correction 后只改位置、不清 Ack 历史、不重放后续输入。
- 禁止平滑逻辑碰撞体；只平滑 VisualRoot。
- 禁止用可靠有序消息承载每个高频 Move，避免丢一个包阻塞后续移动。
- 禁止把 Ack 当“这一帧状态可靠送达”；Ack 必须累计、幂等、可重复。
- 禁止用客户端传来的 DeltaTime、速度、最大速度作为权威。
- 禁止忽略 Tick 回绕和重生后的旧包；AuthorityEpoch 是必需字段。
- 禁止在没有协议 Golden Test 和字节统计前做激进位压缩。
- 禁止先做 Root Motion/移动平台，再补基础 Walking 重放。

---

## 16. UE 5.7 源码对照索引（已在本机复核）

根目录：

```text
C:\Program Files\Epic Games\UE_5.7\Engine\Source
```

### 通用复制

- `Runtime/Engine/Classes/Engine/NetDriver.h`
  - `TickDispatch`：入站网络处理入口。
  - `TickFlush`：复制与连接 Flush。
  - `ServerReplicateActors`：连接准备、Consider List、优先级、相关性、预算。
  - `ProcessRemoteFunction`：RPC 路由。
- `Runtime/Engine/Private/NetDriver.cpp`
  - `TickFlush`、`TickDispatch`、`ServerReplicateActors` 的实际流程。
- `Runtime/Engine/Classes/Engine/NetConnection.h`
  - Channel 管理、包确认、可靠/不可靠发送语义和连接状态。
- `Runtime/Engine/Classes/Engine/ActorChannel.h`
  - `ReceivedBunch`、`ReplicateActor`、`SetChannelActor`。
- `Runtime/Engine/Private/DataChannel.cpp`
  - Actor Channel 收包、Actor/属性/RPC 复制实现。
- `Runtime/Engine/Classes/Engine/ReplicatedState.h`
  - `FRepMovement` 字段与量化等级。
- `Runtime/Engine/Private/Engine/ReplicatedState.cpp`
  - `FRepMovement::SerializeQuantizedVector`、`NetSerialize`。
- `Runtime/Engine/Classes/Engine/NetSerialization.h`
  - `FVector_NetQuantize`、`NetQuantize10`、`NetQuantize100`、`NetQuantizeNormal`。
- `Runtime/Core/Public/Serialization/BitWriter.h`
  - 按位写入与有界序列化思路。

### Character 移动预测

- `Runtime/Engine/Classes/GameFramework/CharacterMovementComponent.h`
  - 约 2344 行：官方预测/复制/纠错链路说明。
  - `ReplicateMoveToServer`、`ClientUpdatePositionAfterServerUpdate`。
  - `ClientAckGoodMove`、`ClientAdjustPosition`、`ClientVeryShortAdjustPosition`。
  - `FSavedMove_Character`、`FNetworkPredictionData_Client_Character`、Server Prediction Data。
- `Runtime/Engine/Classes/GameFramework/CharacterMovementReplication.h`
  - `FCharacterNetworkMoveData`。
  - `FCharacterNetworkMoveDataContainer` 的 New/Pending/Old Move。
  - `FCharacterMoveResponseDataContainer`。
- `Runtime/Engine/Private/Components/CharacterMovementComponent.cpp`
  - `SmoothCorrection`、`SmoothClientPosition`。
  - `ClientUpdatePositionAfterServerUpdate`。
  - `ReplicateMoveToServer`、`CallServerMovePacked`。
  - `FCharacterNetworkMoveData::Serialize`。
  - `ServerMove_HandleMoveData`、`ServerMove_PerformMovement`。
  - `ServerCheckClientError`、`ServerExceedsAllowablePositionError`。
  - `SendClientAdjustment`、`ClientAdjustPosition_Implementation`。
- `Runtime/Net/Iris/`
  - 只用于后续理解复制系统演进；不进入第一版实现范围。

行号随引擎提交变化，实际开发时优先按符号名搜索。

---

## 17. 推荐的实际开工顺序

若下一步开始写代码，严格按下面的最小序列提交，每一步都可单独测试：

1. 新建 Networking Runtime/Test 程序集、`NetworkTick`、`SequenceMath`、Ring Buffer。
2. 新建 `CharacterMoveCommand` 与 `CharacterMovementState`。
3. 重构现有移动为固定 Tick 的唯一 `SimulateTick`，完成 Capture/Restore/Replay 离线测试。
4. 做 Loopback Transport 与 Network Emulator，不急着连真实 UDP。
5. 实现 Move/Response 的 Writer/Reader 和 Golden Tests。
6. 先在同进程跑 Autonomous Client + Authority Server，完成 Ack/Correction/Replay。
7. 再接成熟 Transport，跑独立 Server/Client 进程。
8. 加第二个客户端和 WorldSnapshot/SimulatedProxy 插值。
9. 最后补 Spawn/Possess 完整生命周期、相关性和带宽预算。

其中第 3 步是硬门槛：如果单机固定 Tick 的状态捕获与重放不稳定，任何网络代码都只会把问题隐藏成“网络抖动”。
