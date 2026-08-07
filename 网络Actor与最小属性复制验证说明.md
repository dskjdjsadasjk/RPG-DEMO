# 网络 Actor 与最小属性复制验证说明

## 当前实现

连接层之后的第一段 Actor 复制链路已经接通：

```text
Client Ready
→ ServerGameMode 创建 ServerPlayerController + PlayerState
→ DS 根据 PrefabId 创建 Pawn
→ 服务端分配 NetId、OwnerConnectionId、AuthorityEpoch
→ 为每个连接创建 ActorReplicationChannel
→ Reliable ActorChannelOpen
→ 客户端实例化 Prefab 并确定 AutonomousProxy/SimulatedProxy
→ Reliable ActorChannelOpenAck
→ 服务端在 SpawnAcked 后发送初始组件状态
→ 客户端 ObjectReplicator 应用状态
→ AutonomousProxy 由本地 PlayerController Possess
```

已经包含：

- `Application.runInBackground = true`，客户端切到后台不会再停止 UTP 心跳。
- UTP 断开原因显示为 `Timeout`、`ClosedByRemote` 等名称。
- `NetworkBehaviour` 和每连接的 `ObjectReplicator`。
- Reliable 初始完整状态。
- 5 Hz 状态变化检查及 Unreliable 完整状态更新。
- 示例 `ReplicatedHealth`；服务端给连接 1/2 的角色分别设置 101/102。
- 客户端 AutonomousProxy 自动寻找本地 `PlayerController` 并 Possess。
- 客户端左上角 `NetworkDebugHud`。
- 一键生成测试 Prefab、Prefab Registry、测试场景和 Build Settings。

本阶段没有实现移动同步。Character 输入目前仍然只会产生客户端本地移动，不能作为最终网络移动方案。

## 一次性生成联调资源

Unity 编译完成且 Console 没有错误后，点击：

```text
RPG Demo
→ Networking
→ Create or Refresh Network Test Content
```

该工具生成或刷新：

```text
Assets/GameFramework/Demo/Networking/Prefabs/NetworkPlayer.prefab
Assets/Resources/NetworkPrefabRegistry.asset
Assets/Scenes/NetworkTestScene.unity
```

并将 `NetworkTestScene` 放到 Build Settings 的第一个启用位置。

生成的网络玩家配置为：

```text
PrefabId = 1
Character
CharacterMovementComponent
CharacterController
NetworkIdentity
ReplicatedHealth (ReplicationId = 1)
```

测试场景包含两个 `NetworkPlayerStart`、本地 `PlayerController`、Input Actions、Camera、Ground、`NetworkBootstrap` 和 `NetworkDebugHud`。

## 构建

先关闭仍在运行的旧 `RPGDemoClient.exe` 服务端和客户端，否则 Windows 可能锁定构建输出文件。

暂时使用普通 Windows Player 同时验证服务端和客户端：

```text
RPG Demo → Build → Client (Windows)
```

安装 Dedicated Server Build Support 后可改用：

```text
RPG Demo → Build → Server + Client (Windows)
```

## 启动

普通 Player 临时作为服务端：

```powershell
$server = Start-Process `
  -FilePath 'E:\RPG-DEMO\Builds\Client\RPGDemoClient.exe' `
  -ArgumentList @(
    '-server', '-batchmode', '-nographics',
    '-port', '7777',
    '-playerPrefab', '1',
    '-maxPlayers', '16',
    '-logFile', 'E:\RPG-DEMO\Logs\server.log'
  ) `
  -WindowStyle Hidden `
  -PassThru
```

客户端 A：

```powershell
Start-Process `
  -FilePath 'E:\RPG-DEMO\Builds\Client\RPGDemoClient.exe' `
  -ArgumentList @(
    '-connect', '127.0.0.1:7777',
    '-name', 'ClientA',
    '-logFile', 'E:\RPG-DEMO\Logs\client-a.log'
  )
```

客户端 B：

```powershell
Start-Process `
  -FilePath 'E:\RPG-DEMO\Builds\Client\RPGDemoClient.exe' `
  -ArgumentList @(
    '-connect', '127.0.0.1:7777',
    '-name', 'ClientB',
    '-logFile', 'E:\RPG-DEMO\Logs\client-b.log'
  )
```

## 正确结果

客户端 A 左上角应显示两个网络对象：

```text
自己的角色：Role=AutonomousProxy, Owner=1, Health=101
ClientB 角色：Role=SimulatedProxy, Owner=2, Health=102
```

客户端 B 应相反：自己的角色是 `AutonomousProxy`，ClientA 是 `SimulatedProxy`。

服务端日志应包含：

```text
[Net][DS] Spawned NetId=...
[Net][DS] Opening ActorChannel ...
[Net][DS] ActorChannel ... open acknowledged ...
[Net][DS] Initial state sent ... ReplicationId=1
```

客户端日志应包含：

```text
[Net][Client] Spawned NetId=..., Role=AutonomousProxy/SimulatedProxy
[Net][Rep] NetId=..., ReplicationId=1, Health=..., Initial=True
[Net][Client] Local PlayerController possessed AutonomousProxy NetId=...
```

关闭客户端 A 后，服务端应 Despawn A 的角色，客户端 B 的对象数量应从 2 变为 1。

## 下一阶段

下一阶段开始移动纵向链路：

```text
固定 60 Hz CharacterMovementSimulation
→ CharacterMoveCommand
→ 本地预测和 SavedMove
→ ClientMoveBatch
→ 服务端权威复演
→ Ack/Correction
→ Replay
→ 其他客户端 Snapshot 插值
```
