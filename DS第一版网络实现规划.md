# RPG-DEMO：Dedicated Server 第一版网络实现规划

## 1. 本版结论

第一版采用真正的 Dedicated Server：

```text
RPGDemoServer.exe
- 独立进程
- 没有本地玩家
- 不读取设备输入
- 不创建 Camera/UI
- 不执行视觉平滑
- 保存全部权威 Gameplay 状态
- 接受所有客户端连接
- 创建 PlayerController、PlayerState 和 Character
- 执行权威移动、Actor 复制和 RPC

RPGDemoClient.exe
- 独立进程
- 只有一条到 DS 的连接
- 只拥有自己的 PlayerController/Character
- 自己的 Character 做客户端预测
- 其他 Character 只消费服务端快照
```

服务端和客户端使用同一 Unity 工程、同一份共享 Gameplay/Movement 代码，但产出两个不同 Build。服务端不是“某个客户端兼任”，也不存在本地 Host 玩家。

第一版最终必须演示：

```text
启动一个 DS
→ 启动 Client A 和 Client B
→ 两个客户端完成握手和登录
→ DS 为两人分别创建 PlayerController、PlayerState、Character 并 Possess
→ A/B 本地移动立即响应
→ DS 权威复演两人的 Move
→ Owner 收到 Ack/Correction 并可 Replay
→ A/B 通过快照看到对方平滑移动
→ 任意客户端断开后，DS 清理其对象并通知其他客户端
```

---

## 2. UE 5.7 源码给出的 DS 主链路

### 2.1 进程和网络模式

UE 的 `ENetMode` 明确区分：

- `NM_DedicatedServer`：没有本地玩家的服务端。
- `NM_ListenServer`：同时具有本地玩家的服务端。
- `NM_Client`：连接远端服务端的客户端。
- `NM_Standalone`：无网络单机。

本项目第一版只实现 `DedicatedServer`、`Client`，保留 `Standalone` 供离线调试；不实现 Listen Server。

### 2.2 DS 启动

UE `UWorld::Listen` 的实际顺序是：

```text
检查 World 是否已有 NetDriver
→ CreateNamedNetDriver
→ NetDriver.SetWorld
→ NetDriver.InitListen
→ World 进入服务端网络模式
```

Unity 对应：

```text
DedicatedServerBootstrap
→ 创建 GameNetDriver
→ UtpTransport.Bind/Listen
→ GameNetDriver 绑定 ServerWorld
→ ServerGameMode 初始化
→ 进入 Accept/Poll 循环
```

### 2.3 玩家进入服务端

UE `AGameModeBase` 的主流程：

```text
PreLogin
→ Login：创建 PlayerController、初始化 PlayerState
→ PostLogin
→ HandleStartingNewPlayer
→ RestartPlayer
→ FindPlayerStart
→ SpawnDefaultPawn
→ PlayerController.Possess(Pawn)
```

Unity 第一版照此保留职责边界，但不照搬 UObject/反射。

### 2.4 世界 Tick 与网络 Tick

UE 世界 Tick 大致是：

```text
TickDispatch：先收包
→ World/Actor/Movement Tick
→ TickFlush：ServerReplicateActors、连接发送和 Flush
```

Unity DS 使用同样次序：

```text
EarlyUpdate：UTP Poll + 消息分发
→ 固定 60 Hz：服务端 Gameplay/Movement 模拟
→ PostSimulation：Actor 复制、MoveResponse、Snapshot
→ Transport Flush
```

### 2.5 网络对象关系

```text
GameNetDriver（≈ UNetDriver）
└── GameNetConnection（每个远端 Client 一份，≈ UNetConnection）
    └── ActorReplicationChannel（每个“Connection × Actor”一份，≈ UActorChannel）
        ├── Actor ObjectReplicator
        └── Component ObjectReplicators
```

UE `UActorChannel` 中确实有一个 `ActorReplicator`，并维护 SubObject 的 `ReplicationMap`。因此本项目的 ActorChannel/ObjectReplicator 也必须是每连接状态，不能作为全局状态直接挂在 GameObject 上。

---

## 3. 第一版范围

### 3.1 第一版实现

- Windows Dedicated Server Build 和普通 Client Build。
- UTP Bind/Listen/Connect/Accept/Disconnect。
- 版本握手、简单 Challenge、Login、ClientReady。
- 每连接一个 `GameNetConnection`。
- 服务端 `ServerGameMode` 管理登录、玩家创建、Spawn、Possess、Logout。
- `NetworkIdentity`、NetId、PrefabId、OwnerConnectionId、AuthorityEpoch。
- 每连接每 Actor 的精简 `ActorReplicationChannel`。
- 最小 `ObjectReplicator`：初始状态、低频完整状态、手工 OnRep。
- 最小 RPC 路由：Server Reliable 与 Client Reliable。
- Character 专用 Move、Ack/Correction、Snapshot 协议。
- Walking 固定 Tick、本地预测、服务端权威复演、Replay、远端插值。
- 简单复制选择：AlwaysRelevant、OwnerOnly、所有已 Spawn Character。
- 断线清理、超时和网络指标。

### 3.2 第一版不实现

- Listen Server、Host Migration。
- 完整 ReplicationGraph、Spatial Grid、带宽优先级算法。
- UE Channel/Bunch 的逐位复刻。
- 通用反射扫描、Source Generator、自动生成 RPC Stub。
- Ack 驱动的复杂属性 Delta Baseline。
- Dormancy、动态 SubObject 生命周期、未解析 GUID 队列。
- Jump/Falling、移动平台、Root Motion、物理预测。
- 登录账号、Token 服务、加密、Relay、匹配。

---

## 4. 进程、Build 和场景

### 4.1 两个 Build

```text
Builds/Server/RPGDemoServer.exe
Builds/Client/RPGDemoClient.exe
```

服务端使用 Unity Dedicated Server/Server Subtarget，客户端使用普通 Standalone Windows Build。新增 Editor 构建脚本：

```text
Assets/GameFramework/Editor/Build/DedicatedServerBuild.cs
Assets/GameFramework/Editor/Build/ClientBuild.cs
```

服务端启动参数：

```text
RPGDemoServer.exe
  -batchmode
  -nographics
  -port 7777
  -tickrate 60
  -maxplayers 16
  -logFile Logs/server.log
```

客户端启动参数：

```text
RPGDemoClient.exe
  -connect 127.0.0.1:7777
  -logFile Logs/client-a.log
```

### 4.2 场景

新增一个明确的联调场景：

```text
Assets/Scenes/NetworkTestScene.unity
```

场景只包含：

- 静态地面和墙体。
- 多个 `PlayerStart`。
- `NetworkBootstrap`。
- `NetworkPrefabRegistry`。
- 服务端需要的 `ServerGameMode` Prefab/配置。

场景不能预放 Player Character 或 PlayerController；它们必须由 DS 登录流程创建。现有 SampleScene 保留为单机参考。

### 4.3 服务端裁剪

DS 启动时必须禁用或不创建：

- Camera、AudioListener、UI EventSystem。
- 本地 PlayerInput。
- VisualRoot 网络平滑。
- 客户端 HUD。

但服务端必须保留：

- CharacterController/碰撞场景。
- 服务端 Animator 所必需的纯 Gameplay 状态；第一版 Walking 不依赖动画。
- GameMode、GameState、PlayerController、PlayerState、Pawn。
- 网络、模拟、日志和指标。

---

## 5. 目录和类

```text
Assets/GameFramework/Runtime/Networking/
├── Bootstrap/
│   ├── NetworkBootstrap.cs
│   ├── NetworkRuntimeArguments.cs
│   └── NetworkProcessMode.cs
├── Transport/
│   ├── INetworkTransport.cs
│   ├── UtpTransport.cs
│   ├── TransportConnectionHandle.cs
│   └── TransportEvent.cs
├── Core/
│   ├── GameNetDriver.cs
│   ├── GameNetConnection.cs
│   ├── NetConnectionState.cs
│   ├── NetworkSettings.cs
│   ├── NetworkClock.cs
│   ├── NetworkSimulationLoop.cs
│   └── SequenceMath.cs
├── Protocol/
│   ├── NetworkMessageType.cs
│   ├── NetworkMessageRouter.cs
│   ├── ConnectionProtocol.cs
│   ├── ActorProtocol.cs
│   ├── RpcProtocol.cs
│   └── CharacterMovementProtocol.cs
├── Identity/
│   ├── NetworkIdentity.cs
│   ├── NetworkBehaviour.cs
│   ├── IReplicatedObject.cs
│   ├── NetworkObjectRegistry.cs
│   └── NetworkPrefabRegistry.cs
├── Replication/
│   ├── SimpleReplicationDriver.cs
│   ├── ActorReplicationChannel.cs
│   ├── ObjectReplicator.cs
│   ├── ReplicationDescriptor.cs
│   ├── RpcDescriptor.cs
│   └── ReplicationCondition.cs
├── Server/
│   ├── DedicatedServerRuntime.cs
│   ├── ServerWorld.cs
│   ├── ServerGameMode.cs
│   ├── ServerConnectionContext.cs
│   ├── ServerPlayerSpawner.cs
│   └── ServerCharacterMoveProcessor.cs
├── Client/
│   ├── ClientRuntime.cs
│   ├── ClientWorld.cs
│   ├── ClientCharacterPrediction.cs
│   └── RemoteCharacterInterpolator.cs
├── Movement/
│   ├── CharacterMoveCommand.cs
│   ├── CharacterMovementState.cs
│   ├── SavedCharacterMove.cs
│   ├── CharacterPredictionBuffer.cs
│   ├── ServerMoveQueue.cs
│   └── CharacterSnapshotBuffer.cs
└── Diagnostics/
    ├── NetworkMetrics.cs
    ├── NetworkEmulator.cs
    └── NetworkDebugHud.cs

Assets/GameFramework/Editor/Build/
├── DedicatedServerBuild.cs
└── ClientBuild.cs
```

---

## 6. 每个核心类如何实现

### 6.1 `NetworkBootstrap`

类型：场景中的 MonoBehaviour，只负责组装运行时。

```csharp
public sealed class NetworkBootstrap : MonoBehaviour
{
    [SerializeField] private NetworkSettings settings;
    [SerializeField] private NetworkPrefabRegistry prefabRegistry;

    private GameNetDriver netDriver;

    private void Awake();
    private void Update();
    private void LateUpdate();
    private void OnDestroy();
}
```

`Awake`：

```text
解析命令行
→ 判断 DedicatedServer 或 Client
→ 创建 UtpTransport
→ 创建 GameNetDriver
→ DS：创建 DedicatedServerRuntime 并 StartListen
→ Client：创建 ClientRuntime 并 Connect
```

禁止把玩家创建、移动处理或复制逻辑塞进 Bootstrap。

### 6.2 `UtpTransport`

类型：普通 C# 类，唯一直接依赖 Unity Transport 的模块。

接口：

```csharp
public interface INetworkTransport : IDisposable
{
    void StartServer(ushort port, int maxConnections);
    void StartClient(string address, ushort port);
    void PollEvents(ITransportEventSink sink);
    void SendReliable(TransportConnectionHandle connection, ArraySegment<byte> payload);
    void SendUnreliable(TransportConnectionHandle connection, ArraySegment<byte> payload);
    void Disconnect(TransportConnectionHandle connection);
}
```

UTP 的 `NetworkDriver` 只存在于这里，上层统一使用 `GameNetDriver`，避免命名和职责混淆。

第一版创建两条 Pipeline：

- Reliable Sequenced：控制、Spawn/Despawn、低频属性、可靠 RPC。
- Unreliable：Move、MoveResponse、Snapshot。

### 6.3 `GameNetDriver`

对应 UE：`UNetDriver`。

```csharp
public sealed class GameNetDriver : ITransportEventSink, IDisposable
{
    private readonly INetworkTransport transport;
    private readonly Dictionary<uint, GameNetConnection> clientConnections;
    private GameNetConnection serverConnection;
    private NetworkObjectRegistry objectRegistry;

    public void TickDispatch(float unscaledDeltaTime);
    public void TickSimulation(float fixedDeltaTime);
    public void TickFlush(float unscaledDeltaTime);
}
```

DS：

```text
clientConnections 中每个远端客户端一项
serverConnection 必须为 null
```

Client：

```text
serverConnection 只有一项
clientConnections 为空
```

职责：

- 接收 Transport Event。
- 创建/销毁 `GameNetConnection`。
- 把消息路由给 Connection。
- 驱动固定模拟。
- 调用 `SimpleReplicationDriver`。
- 统一 Flush。

### 6.4 `GameNetConnection`

对应 UE：`UNetConnection`。

```csharp
public sealed class GameNetConnection
{
    public uint ConnectionId { get; }
    public NetConnectionState State { get; private set; }
    public TransportConnectionHandle TransportHandle { get; }
    public PlayerController OwningController { get; private set; }

    private readonly Dictionary<uint, ActorReplicationChannel> actorChannels;

    public void HandleReliableMessage(ref NetworkReader reader);
    public void HandleUnreliableMessage(ref NetworkReader reader);
    public void SendReliable(NetworkMessage message);
    public void SendUnreliable(NetworkMessage message);
    public void Close(DisconnectReason reason);
}
```

状态机：

```text
TransportConnected
→ AwaitHello
→ AwaitLogin
→ AwaitClientReady
→ Playing
→ Closing
→ Closed
```

DS 每条 Connection 绑定一个服务端 PlayerController，等价于 UE `UNetConnection::OwningActor` 通常指向 PlayerController。

### 6.5 `NetworkIdentity`

类型：仅挂在需要网络生命周期的 GameObject 根节点上。

```csharp
public sealed class NetworkIdentity : MonoBehaviour
{
    public uint NetId { get; internal set; }
    public ushort PrefabId { get; private set; }
    public uint OwnerConnectionId { get; internal set; }
    public ushort AuthorityEpoch { get; internal set; }
    public NetworkRole Role { get; internal set; }
    public bool IsSpawned { get; internal set; }
}
```

NetId 只由 DS 分配。AuthorityEpoch 在重生或所有权世代变化时递增，旧世代消息一律丢弃。

普通环境 GameObject、Renderer、Collider 不需要 NetworkIdentity。

### 6.6 `NetworkBehaviour`

`NetworkIdentity` 根对象和 `NetworkBehaviour` 统一实现一个不依赖 Unity 反射的复制接口：

```csharp
public interface IReplicatedObject
{
    uint ReplicationVersion { get; }
    void WriteReplicationState(ref NetworkWriter writer, in ReplicationContext context);
    void ReadReplicationState(ref NetworkReader reader, in ReplicationContext context);
}
```

只让需要同步属性或 RPC 的 Component 继承 `NetworkBehaviour`：

```csharp
public abstract class NetworkBehaviour : MonoBehaviour, IReplicatedObject
{
    public NetworkIdentity Identity { get; private set; }
    public ushort BehaviourId { get; private set; }
    public uint ReplicationVersion { get; private set; }

    protected void MarkReplicationDirty();
    public abstract void WriteReplicationState(ref NetworkWriter writer, in ReplicationContext context);
    public abstract void ReadReplicationState(ref NetworkReader reader, in ReplicationContext context);
}
```

BehaviourId 必须由 Prefab 配置固定，不能依赖运行时 `GetComponents` 顺序。

### 6.7 `ActorReplicationChannel`

对应 UE：`UActorChannel`。

它不是 MonoBehaviour，而是每个 Connection 针对某个 NetworkIdentity 的运行时对象：

```csharp
public sealed class ActorReplicationChannel
{
    public ushort ChannelId { get; }
    public GameNetConnection Connection { get; }
    public NetworkIdentity Actor { get; }
    public bool SpawnAcked { get; private set; }
    public uint LastReplicatedTick { get; private set; }

    private ObjectReplicator actorReplicator;
    private Dictionary<ushort, ObjectReplicator> componentReplicators;

    public void Open();
    public void Replicate(uint serverTick);
    public void HandleSpawnAck();
    public void Close(ActorChannelCloseReason reason);
}
```

`Open` 用 Reliable Pipeline 发送：

```text
ActorChannelOpen
- ChannelId
- NetId
- PrefabId
- OwnerConnectionId
- AuthorityEpoch
- Spawn Transform
- Initial Actor/Component State
```

只有 SpawnAck 后，才允许向该连接发送引用此 NetId 的普通属性和 RPC。Character 快照也必须等 Channel Open 已送达；第一版依赖同一连接的可靠 Spawn 先行，并在客户端无法解析 NetId 时丢弃快照而不是缓存无限数据。

### 6.8 `ObjectReplicator`

对应 UE：`FObjectReplicator`，每个“Connection × Actor/Component”一份。

第一版不做复杂字段 Delta，采用按对象版本的完整低频状态：

```csharp
public sealed class ObjectReplicator
{
    private readonly IReplicatedObject target;
    private readonly ReplicationDescriptor descriptor;
    private uint lastSentVersion;

    public void WriteInitialState(ref NetworkWriter writer, in ReplicationContext context);
    public bool WriteUpdateIfDirty(ref NetworkWriter writer, in ReplicationContext context);
    public void ReadAndApply(ref NetworkReader reader, in ReplicationContext context);
}
```

流程：

```text
NetworkBehaviour.MarkReplicationDirty
→ ReplicationVersion++
→ 每条 Connection 的 ObjectReplicator 比较 lastSentVersion
→ 符合 ReplicationCondition 时发送完整对象状态
→ Client ReadAndApply
→ 比较旧值并调用手工 OnRep
```

第一版普通属性使用 Reliable Pipeline，因此写入 Transport 后可以推进 `lastSentVersion`。这些属性只允许低频状态，例如 PlayerName、TeamId、Score、PossessedPawnNetId；Character Position 不走这里。

### 6.9 `SimpleReplicationDriver`

对应 UE 传统 `ServerReplicateActors` 的最小子集；第一版不实现 ReplicationGraph。

```csharp
public sealed class SimpleReplicationDriver
{
    public void ReplicateConnection(
        GameNetConnection connection,
        IReadOnlyList<NetworkIdentity> networkObjects,
        uint serverTick);
}
```

第一版规则：

```text
未 Spawn → 跳过
OwnerOnly 且不是 Owner → 跳过
AlwaysRelevant → 同步
Player Character → 对所有 Playing Connection 同步
```

对 Relevant Actor：

```text
FindOrCreate ActorChannel
→ 新 Channel：Open
→ 已 Open：Replicate ObjectReplicators
```

以后 `ReplicationGraph` 只替换“如何得到 Relevant Actor”，不替换 Connection、Channel、Replicator。

### 6.10 RPC

第一版 RPC 不使用反射，使用手工 `RpcDescriptor`：

```text
RpcMessage
- NetId
- AuthorityEpoch
- BehaviourId
- RpcId
- PayloadLength
- Payload
```

支持：

- `ServerReliable`：Owner Client → DS。
- `ClientReliable`：DS → 指定 Owner Client。

接收校验：

- NetId/Epoch 存在。
- BehaviourId/RpcId 已注册。
- RPC 方向正确。
- Server RPC 的发送 Connection 拥有该 Actor。
- Payload 长度合法。

第一版不支持 Multicast、Unreliable Gameplay RPC、反射调用。Character Move 不走通用 RPC，而走专用不可靠协议。

### 6.11 `ServerGameMode`

对应 UE：`AGameModeBase`，只存在并运行在 DS。

```csharp
public sealed class ServerGameMode : MonoBehaviour
{
    public LoginResult PreLogin(in LoginRequest request, GameNetConnection connection);
    public PlayerController Login(in LoginRequest request, GameNetConnection connection);
    public void PostLogin(PlayerController newPlayer);
    public void HandleStartingNewPlayer(PlayerController newPlayer);
    public void RestartPlayer(PlayerController player);
    public void Logout(PlayerController exitingPlayer);
}
```

职责：

- 校验人数、协议/Schema、重复登录。
- 创建服务端 PlayerController。
- 创建 PlayerState 并绑定 Connection。
- 选择 PlayerStart。
- Spawn Character。
- Authority Possess。
- 注册网络对象并为所有客户端开 ActorChannel。
- 断线时 UnPossess、Despawn、清理。

客户端不能包含或调用 ServerGameMode。

PlayerController、PlayerState、Character 都是带 `NetworkIdentity` 的网络 Prefab，但相关性不同：

```text
PlayerController：OwnerOnly，只复制给所属客户端
PlayerState：AlwaysRelevant，复制给所有 Playing 客户端
Character：第一版复制给所有 Playing 客户端
ServerGameMode：ServerOnly，永不复制到客户端
```

客户端不能创建其他玩家的 PlayerController。它只通过 OwnerOnly ActorChannel 得到自己的 PlayerController；其他玩家身份通过 PlayerState 表示。

### 6.12 `ServerWorld` / `ClientWorld`

`ServerWorld`：

- 保存所有 Authority NetworkIdentity。
- 保存服务端 Controller、PlayerState、Character。
- 提供 NetId 分配和 Spawn/Despawn。
- 驱动服务端固定模拟。

`ClientWorld`：

- 保存服务端已复制过来的对象。
- 处理 ActorChannelOpen/Close。
- 识别 OwnerConnectionId。
- Owner Character 设置 AutonomousProxy。
- 非 Owner Character 设置 SimulatedProxy。

### 6.13 现有 Gameplay Framework 类的改造

`Controller.cs`：

- 删除 `hasAuthority = true` 这种固定假设。
- `HasAuthority` 从其 `NetworkIdentity.Role == Authority` 得出。
- 公共 `Possess/UnPossess` 只能由 DS Authority 调用。
- 客户端通过可靠 `PossessionChanged` 应用服务端结果，不能自行决定所有权。

`PlayerController.cs`：

- DS 为每条 Playing Connection 创建一份，但不创建 `UnityPlayerInput`。
- 客户端只创建自己的 OwnerOnly PlayerController，并启用 `UnityPlayerInput`。
- `ControlRotation` 由 ClientMoveCommand 发往 DS，不通过普通属性每帧复制。

`PlayerState.cs`：

- 改成 `NetworkBehaviour` 或增加专用 NetworkBehaviour Adapter。
- 第一版复制 PlayerId、DisplayName、Score、PossessedPawnNetId。
- PlayerState 对所有 Playing Connection 为 AlwaysRelevant。

`Pawn.cs` / `Character.cs`：

- 根节点增加 NetworkIdentity。
- DS Authority 执行 PossessedBy/UnPossessed。
- 客户端收到 `PossessionChanged(ControllerNetId, PawnNetId, Epoch)` 后建立本地引用。
- 只有 Local Owner 的 AutonomousProxy 创建 Pawn InputComponent。

`PawnMovementComponent.cs` / `CharacterMovementComponent.cs`：

- 停止自主渲染帧 Update 移动。
- 由 NetworkSimulationLoop 按 Role 调度。
- Authority、AutonomousProxy、SimulatedProxy 走不同分支。
- 共同复用固定 Tick 的 SimulateMove/CaptureState/RestoreState。

---

## 7. DS 登录协议和玩家生命周期

### 7.1 控制消息

全部使用 Reliable Sequenced：

| 消息 | 方向 | 作用 |
|---|---|---|
| `ClientHello` | C→S | ProtocolVersion、SchemaHash、ClientNonce |
| `ServerChallenge` | S→C | ServerNonce、期望版本 |
| `ClientLogin` | C→S | Challenge 回传、DisplayName |
| `LoginFailed` | S→C | 错误码和简短原因 |
| `ServerWelcome` | S→C | ConnectionId、ServerTick、TickRate、SceneId |
| `ClientReady` | C→S | 场景和 PrefabRegistry 已准备好 |
| `ActorChannelOpen` | S→C | Spawn 和初始状态 |
| `ActorChannelOpenAck` | C→S | Actor 已创建并注册 |
| `ActorChannelClose` | S→C | Despawn/不相关/销毁 |

### 7.2 完整顺序

```text
UTP Accept
→ DS 创建 GameNetConnection(State=AwaitHello)
→ ClientHello
→ 校验 ProtocolVersion + SchemaHash
→ ServerChallenge
→ ClientLogin
→ ServerGameMode.PreLogin
→ 通过后 ServerWelcome
→ Client 确认 NetworkTestScene/PrefabRegistry Ready
→ ClientReady
→ ServerGameMode.Login：创建 PlayerController + PlayerState
→ Connection.OwningController = PlayerController
→ ServerGameMode.PostLogin
→ HandleStartingNewPlayer
→ RestartPlayer：PlayerStart → Spawn Character → Possess
→ Connection.State = Playing
→ 向所有连接 Open 对应 ActorChannel
```

DS 不应在 Transport Accept 时立即 Spawn Pawn，因为客户端尚未完成版本校验和场景准备。

### 7.3 断线

```text
Transport Disconnect/Timeout
→ Connection.State = Closing
→ ServerGameMode.Logout
→ Character UnPossess
→ 向其他连接 ActorChannelClose
→ Despawn Character
→ Despawn/保留 PlayerState（第一版直接销毁）
→ Destroy PlayerController
→ 清空该 Connection 的所有 ActorChannel/ObjectReplicator
→ 移除 GameNetConnection
```

---

## 8. Character 移动在 DS 中如何实现

### 8.1 三端实例

以 Client A 的 Character 为例：

```text
DS 进程：Authority
- 不读取 A 的设备
- 接收 ClientMoveBatch
- 执行权威碰撞和移动

Client A：AutonomousProxy
- 读取本地输入
- 立即预测
- 保存未确认 Move
- 接收 Ack/Correction

Client B：SimulatedProxy
- 不读取输入
- 不发送 Move
- 接收 DS Snapshot 并插值
```

### 8.2 固定模拟入口

重构现有 `CharacterMovementComponent`：

```csharp
public void SimulateMove(
    in CharacterMoveCommand command,
    float fixedDeltaTime);

public CharacterMovementState CaptureState();
public void RestoreState(in CharacterMovementState state);
```

客户端预测、DS 权威执行、客户端 Replay 必须使用同一个 `SimulateMove`。

`PawnMovementComponent.Update → Time.deltaTime` 必须停止作为网络移动入口，改由 `NetworkSimulationLoop` 固定 60 Hz 调用。

### 8.3 Client Move

Client 每个 60 Hz Tick：

```text
采样输入
→ 生成 CharacterMoveCommand(ClientTick)
→ Capture Start
→ 本地 SimulateMove
→ Capture End
→ 放入 PredictionBuffer
```

每 2 Tick 发一个 `ClientMoveBatch`，包含：

- 两个新 Move。
- 最近一个冗余 Move。
- 最老未确认 Move。
- 最新 Move 的 ClientEndPosition/MovementMode。

使用 Unreliable Pipeline。

### 8.4 DS Move Processor

收到后：

```text
验证 Connection.State == Playing
→ 验证 Connection.OwningController.Pawn.NetId
→ 验证 OwnerConnectionId/Epoch
→ 验证 Tick、输入、Flags、MoveCount
→ 按 Tick 去重放入 ServerMoveQueue
→ 只连续处理 LastProcessedClientTick + 1
→ 使用服务端 MovementSettings 和固定 DeltaTime SimulateMove
→ 比较最新客户端位置/模式
→ 生成累计 Ack 或 Correction
```

DS 不信任客户端传来的位置、速度、DeltaTime、MaxSpeed；客户端位置只用于误差检查。

### 8.5 Owner Response

`CharacterMoveResponse` 使用 Unreliable，但 Correction 保存在 DS 并重复发送直到客户端回执 ResponseSequence。

Ack：

```text
Client 删除 <= AckClientTick 的 SavedMove
```

Correction：

```text
Client 恢复 DS 权威状态
→ 删除 <= AckClientTick
→ Replay 更晚的 SavedMove
→ 逻辑 Capsule 立即正确
→ VisualRoot 平滑 Offset 归零
```

### 8.6 Simulated Proxy Snapshot

DS 20 Hz 给非 Owner 客户端发送：

```text
ServerTick
NetId/Epoch
Position
Velocity
Yaw
MovementMode
Teleport Flag
```

客户端按 `EstimatedServerTick - 100ms` 插值。Movement Snapshot 不经过 ObjectReplicator 的可靠低频属性通道。

---

## 9. 协议划分

### Reliable Sequenced

- Hello/Challenge/Login/Welcome/Ready/Failure。
- ActorChannel Open/Ack/Close。
- 第一版低频 Object State。
- ServerReliable/ClientReliable RPC。
- Possession/Ownership 世代变化。

### Unreliable + 应用层 Sequence/Tick

- `ClientMoveBatch`。
- `CharacterMoveResponse`。
- `CharacterSnapshot`。

### 第一版序列化

直接使用固定字段和有界 Reader/Writer：

- 每条消息第一个字节 `MessageType`。
- 所有数组先读有上限的 Count。
- 所有 Payload 有最大字节数。
- Position/Velocity 第一阶段用 float，链路稳定后改定点量化。
- NetId/Epoch/BehaviourId/RpcId 必须显式。
- 收到未知 MessageType、越界长度、NaN/Infinity 时断开或丢弃并计数。

不先实现 UE 完整 `FBitWriter`、Bunch 和 PackageMap。

---

## 10. 服务端每帧执行顺序

```text
DedicatedServerRuntime.Update

1. GameNetDriver.TickDispatch
   - UTP Poll
   - Accept/Disconnect
   - Hello/Login/Ready
   - ActorChannel Ack
   - ClientMoveBatch/RPC 入队

2. NetworkSimulationLoop
   while accumulator >= 1/60s：
   - ServerGameMode/Gameplay 前置状态
   - ServerCharacterMoveProcessor 连续消费 Move
   - AI Controller 生成命令并直接 Authority Simulate
   - Character/Ability 固定 Tick
   - ServerTick++

3. GameNetDriver.TickFlush
   - 生成 Pending MoveResponse
   - SimpleReplicationDriver 遍历每条 Playing Connection
   - Open/Close ActorChannel
   - ObjectReplicator 发送低频状态
   - 20 Hz CharacterSnapshot
   - UTP Send/Flush
```

DS 不执行 Client Prediction、Remote Interpolation 或 VisualRoot Smoothing。

---

## 11. 客户端每帧执行顺序

```text
ClientRuntime.Update

1. GameNetDriver.TickDispatch
   - UTP Poll
   - Challenge/Welcome
   - ActorChannel Open/Close
   - Object State/RPC
   - MoveResponse/Snapshot

2. NetworkSimulationLoop
   while accumulator >= 1/60s：
   - 本地 PlayerController 生成 CharacterMoveCommand
   - AutonomousProxy 本地预测并保存 Move
   - 按 30 Hz 构建 ClientMoveBatch

3. LateUpdate
   - Owner Correction Visual Offset 衰减
   - SimulatedProxy Snapshot 插值
   - Camera/UI
```

客户端没有 ServerGameMode，也不能自行 Spawn 权威 Character 或决定 Possession。

---

## 12. 分阶段实现与提交

### 阶段 1：DS/Client 两个 Build 和 UTP 连接

新增：

- 在 `Packages/manifest.json` 加入 `com.unity.transport`，提交 Package Manager 实际解析出的锁定版本。
- 构建脚本。
- NetworkTestScene。
- NetworkBootstrap、Arguments。
- UtpTransport、GameNetDriver 骨架。

运行结果：DS Listen，两个 Client Connect/Disconnect。

提交：

```text
network: add dedicated server and client builds with UTP transport
```

### 阶段 2：Connection 和登录状态机

新增：

- GameNetConnection。
- ClientHello/Challenge/Login/Welcome/Ready。
- 超时、协议版本和 SchemaHash 校验。

运行结果：DS 为两客户端分配 ConnectionId，连接进入 Playing 前必须完成 Ready。

提交：

```text
network: add DS handshake login and connection state machine
```

### 阶段 3：服务端 Gameplay 登录生命周期

新增：

- ServerGameMode、ServerWorld、ServerPlayerSpawner。
- PlayerStart。
- DS 创建 PlayerController、PlayerState。

运行结果：每条 Connection 的 OwningController 正确，断开调用 Logout。

提交：

```text
framework: add server-authoritative login controller and player state lifecycle
```

### 阶段 4：NetworkIdentity、ActorChannel 和 Spawn

新增：

- NetworkIdentity、Registry、PrefabRegistry。
- ActorReplicationChannel Open/Ack/Close。
- NetId、OwnerConnectionId、AuthorityEpoch。

运行结果：DS Spawn/Possess；两个客户端创建相同 Character，只有 Owner 是 AutonomousProxy。

提交：

```text
network: add actor channels network identity spawn and possession
```

### 阶段 5：最小 ObjectReplicator 和 RPC 证明

新增：

- NetworkBehaviour、ObjectReplicator、Descriptor。
- PlayerState 的 PlayerId/DisplayName/Score 完整低频复制。
- 一个 ServerReliable 测试 RPC、一个 ClientReliable 测试 RPC。

运行结果：不同连接有独立 lastSentVersion；Server RPC 校验 Owner。

提交：

```text
network: add per-connection object replication and reliable RPC routing
```

### 阶段 6：Walking 固定 Tick

修改：

- PawnMovementComponent 不再自主使用 Time.deltaTime。
- CharacterMovementComponent 提供 Simulate/Capture/Restore。
- Standalone 和 DS 共用固定模拟。

运行结果：相同命令可恢复和重放。

提交：

```text
movement: make walking simulation fixed-tick and replayable
```

### 阶段 7：客户端预测和 DS 权威移动

新增：

- MoveCommand、SavedMove、PredictionBuffer。
- ClientMoveBatch、ServerMoveQueue、ServerMoveProcessor。

运行结果：Owner 即时移动，DS 连续权威执行，重复 Move 不重复模拟。

提交：

```text
network: add autonomous client prediction and authoritative DS movement
```

### 阶段 8：Ack、Correction、Replay

新增：

- MoveResponse。
- Pending Correction 重复与回执。
- Owner Restore/Replay/Visual Offset。

运行结果：篡改客户端位置后由 DS 拉回，未确认输入不丢。

提交：

```text
network: add movement ack correction replay and owner smoothing
```

### 阶段 9：SimulatedProxy Snapshot

新增：

- CharacterSnapshot、SnapshotBuffer、RemoteInterpolator。
- 20 Hz DS Snapshot。

运行结果：两个客户端平滑看到对方移动。

提交：

```text
network: add DS character snapshots and simulated proxy interpolation
```

### 阶段 10：断线和网络恶化

新增：

- Timeout、Logout、Despawn 清理。
- NetworkEmulator、Metrics、DebugHud。

运行结果：100 ms RTT、20 ms Jitter、5% Loss 下继续移动并收敛；断线不残留 ActorChannel。

提交：

```text
network: harden disconnect cleanup packet loss recovery and metrics
```

---

## 13. 每阶段验收测试的位置

测试跟在功能后面：

| 完成功能 | 随后补的验证 |
|---|---|
| Transport | DS 可接受两个进程，断开事件一次且清理 |
| Handshake | 版本错误、重复 Hello、Ready 前发 Move 被拒绝 |
| Login | Connection 与 PlayerController 一一对应，人数上限 |
| ActorChannel | 同 Actor 对两 Connection 有两份 Channel，Spawn Ack 后才发状态 |
| ObjectReplicator | A/B lastSentVersion 独立，OwnerOnly 正确 |
| RPC | 非 Owner Server RPC 被拒绝，Payload 越界被拒绝 |
| Fixed Movement | Capture/Restore/Replay，同 Tick 数结果一致 |
| ServerMoveQueue | 乱序、重复、缺口补齐、未来 Tick |
| Correction | 0/1/N 个未确认 Move，Response 重复幂等 |
| Snapshot | 乱序、插值、外推上限、Teleport |
| Logout | Controller/Pawn/Channel/Replicator 全部释放 |

---

## 14. 第一版完成后的对象数量关系

假设 DS 有 Client A、Client B，世界里有 Character A、Character B：

```text
DS GameNetDriver
├── Connection A
│   ├── OwningController = PlayerController A
│   ├── ActorChannel(Character A)
│   │   ├── ActorReplicator-A
│   │   └── ComponentReplicators-A
│   └── ActorChannel(Character B)
└── Connection B
    ├── OwningController = PlayerController B
    ├── ActorChannel(Character A)
    │   ├── ActorReplicator-B
    │   └── ComponentReplicators-B
    └── ActorChannel(Character B)
```

Character A 本体只有一个 Authority GameObject，但面向两个客户端有两套 Channel/Replicator。Client A 的 Character A 是 AutonomousProxy；Client B 的 Character A 是 SimulatedProxy。

---

## 15. 第一版之后再优化什么

按顺序：

1. float → 定点/位量化，记录每消息真实字节数。
2. ObjectReplicator 从完整对象状态改 ChangeMask/字段 Delta。
3. 属性 Ack/Baseline，不再全部依赖 Reliable Pipeline。
4. SimpleReplicationDriver → ReplicationGraph/Spatial Grid。
5. 更新优先级、带宽预算、Dormancy。
6. Jump/Falling，并扩展 SavedMove/协议/Replay。
7. 移动平台相对 Base。
8. Ability/RootMotionSource 预测。

ReplicationGraph 是优化“哪些 Actor 给哪条 Connection”，不应先于 Connection、ActorChannel、ObjectReplicator 和 Character 权威闭环。

---

## 16. UE 5.7 源码索引

根目录：

```text
C:\Program Files\Epic Games\UE_5.7\Engine\Source
```

- `Runtime/Engine/Classes/Engine/EngineBaseTypes.h`
  - `ENetMode`：DedicatedServer/ListenServer/Client/Standalone。
- `Runtime/Engine/Private/World.cpp`
  - `UWorld::Listen`：CreateNamedNetDriver、SetWorld、InitListen。
- `Runtime/Engine/Classes/Engine/NetDriver.h`
  - NetDriver 声明、ClientConnections、TickDispatch/TickFlush、ServerReplicateActors。
- `Runtime/Engine/Private/NetDriver.cpp`
  - 收包、复制、连接 Flush 的实际流程。
- `Runtime/Engine/Classes/Engine/NetConnection.h`
  - OwningActor、Channel 表、ActorChannel Map、连接清理。
- `Runtime/Engine/Classes/Engine/ActorChannel.h`
  - ActorReplicator、ReplicationMap、SpawnAcked、ReplicateActor。
- `Runtime/Engine/Private/DataChannel.cpp`
  - Control/Actor Channel、属性和 RPC 数据处理。
- `Runtime/Engine/Private/LevelActor.cpp`
  - `UWorld::SpawnPlayActor`。
- `Runtime/Engine/Classes/GameFramework/GameModeBase.h`
  - PreLogin/Login/PostLogin/HandleStartingNewPlayer/RestartPlayer。
- `Runtime/Engine/Private/GameModeBase.cpp`
  - 创建 PlayerController、PlayerState 初始化、选择 PlayerStart、Spawn Pawn、Possess。
- `Runtime/Engine/Private/LevelTick.cpp`
  - TickDispatch 在世界 Tick 前、TickFlush 在世界 Tick 后。
- `Runtime/Engine/Classes/GameFramework/CharacterMovementComponent.h`
  - SavedMove、Client/Server Prediction Data、移动纠错声明。
- `Runtime/Engine/Classes/GameFramework/CharacterMovementReplication.h`
  - MoveData、MoveDataContainer、MoveResponse。
- `Runtime/Engine/Private/Components/CharacterMovementComponent.cpp`
  - AutonomousProxy、Authority、SimulatedProxy 分支；ServerMove、Ack/Correction、Replay、Smoothing。

开发时按符号名搜索，不依赖固定行号。
