# 通用 Actor RPC 第一版实现说明

## 1. 本版完成范围

本版在现有 `UTP -> GameNetDriver -> GameNetConnection -> ActorReplicationChannel -> ObjectReplicator -> NetworkBehaviour` 链路上增加通用 RPC，支持：

- `Server`：拥有该 Actor 的客户端调用 DS。
- `OwningClient`：DS 只调用该 Actor 的拥有者客户端。
- `Multicast`：DS 本地执行，并发送给当前已建立该 ActorChannel 的客户端。
- `Reliable` / `Unreliable`：复用 UTP 的可靠和不可靠管线。
- ActorChannel 尚在 Opening 时，服务端最多暂存 64 个发往该客户端的可靠 RPC；收到 SpawnAck 并发送初始属性后再按序发送。
- 最大 RPC 载荷 1024 字节。

角色移动仍使用专用的 `CharacterMovementProtocol`，没有改成通用 RPC。移动需要合包、冗余重发、预测、确认和纠错，继续保留专用协议更接近 UE 的做法。

## 2. 代码职责

| 代码 | 职责 | UE 中近似概念 |
|---|---|---|
| `RpcProtocol.cs` | RPC 网络包头的读写 | `UActorChannel` 上的函数数据封包 |
| `RpcTypes.cs` | RPC 方向、可靠性、注册表、载荷 Reader/Writer | `UFunction` 元数据、`FNetBitWriter/FNetBitReader` 的简化版 |
| `NetworkBehaviour.cs` | 声明稳定 FunctionId，发起并执行 RPC | 可复制 `UObject/ActorComponent` 的 RPC 入口 |
| `ObjectReplicator.cs` | 按 ReplicationId 定位具体 Behaviour 并分发 RPC | `FObjectReplicator` |
| `ActorReplicationChannel.cs` | Actor 级通道和 Opening 阶段可靠 RPC 队列 | `UActorChannel` |
| `GameNetDriver.cs` | 连接级收发、方向与 Owner 校验、目标路由 | `UNetDriver::ProcessRemoteFunction` + `UNetConnection` |

第一版使用手工指定的稳定 `ushort FunctionId`，不依赖反射顺序和方法名哈希。这能避免不同构建、代码裁剪和 IL2CPP 下的注册顺序变化。协议新增后连接协议版本已从 2 升为 3，旧客户端会在握手阶段被拒绝。

## 3. RPC 包格式

| 字段 | 大小 |
|---|---:|
| MessageType (`64`) | 1 byte |
| ActorChannelId | 2 bytes |
| NetId | 4 bytes |
| AuthorityEpoch | 2 bytes |
| ReplicationId | 2 bytes |
| FunctionId | 2 bytes |
| PayloadLength | 2 bytes |
| Payload | 0-1024 bytes |

包中没有传输 `RpcTarget` 和 `RpcDelivery`。接收端必须用本地 `(ReplicationId, FunctionId)` 注册表取得这两个属性，不能相信客户端自报的调用方向。

## 4. 权限与合法性校验

DS 接收 RPC 时依次验证：

1. 连接已 Ready。
2. ActorChannelId、NetId 和 AuthorityEpoch 全部匹配。
3. ReplicationId 和 FunctionId 已在本地注册。
4. RPC 本地定义必须是 `Server`。
5. `Actor.OwnerConnectionId` 必须等于发送连接的 ConnectionId。
6. Handler 必须完整消费载荷并返回成功。

客户端只接受本地定义为 `OwningClient` 或 `Multicast` 的 RPC；`OwningClient` 还会再次核对本地连接确实是 Owner。协议或权限违规当前采用关闭连接的严格策略。

## 5. 如何声明和调用

在 `NetworkBehaviour` 中使用固定 FunctionId 注册：

```csharp
private const ushort ServerUseItemFunctionId = 1;

protected override void RegisterRemoteProcedures(RpcRegistry registry)
{
    registry.Register(
        ServerUseItemFunctionId,
        RpcTarget.Server,
        RpcDelivery.Reliable,
        HandleServerUseItem);
}

public bool RequestUseItem(uint itemId)
{
    return CallRemoteProcedure(
        ServerUseItemFunctionId,
        writer => writer.WriteUInt32(itemId));
}

private bool HandleServerUseItem(RpcPayloadReader reader)
{
    if (!reader.TryReadUInt32(out uint itemId))
    {
        return false;
    }

    // 在 DS 上重新验证背包、冷却、距离等规则，再修改权威状态。
    return true;
}
```

同一个 `NetworkBehaviour` 内 FunctionId 不可重复，0 被保留。不同 Behaviour 可以复用 FunctionId，因为网络定位键是 `(NetId, ReplicationId, FunctionId)`。

## 6. 已完成的实际验证

`ReplicatedHealth` 现在包含一条完整示例链路：

1. AutonomousProxy 可靠调用 `ServerRequestHealth(FunctionId=1)`，请求将自身生命值降到 77。
2. DS 验证调用者是 Owner，且示例请求只能降低、不能提高生命值。
3. DS 可靠发送 `OwningClientResult(FunctionId=2)`。
4. DS 不可靠发送 `MulticastHealthChanged(FunctionId=3)`。
5. 权威 Health 属性随后通过既有属性复制同步为 77。

Unity 6 Development Player 已成功构建，并用同一个 EXE 分别作为 DS 和无界面客户端完成验证。关键日志为：

```text
[Net][RPC][DS] Health request NetId=1, Requested=77, Accepted=True, Current=77.
[Net][RPC][DedicatedServer] Received target=Server ... FunctionId=1
[Net][RPC][Client] Health result ... AuthoritativeHealth=77.
[Net][RPC][Client] Received target=OwningClient ... FunctionId=2
[Net][RPC][Client] Received target=Multicast ... FunctionId=3
[Net][Rep] NetId=1, ReplicationId=1, Health=77, Initial=False.
```

需要重新执行自动验证时：

```powershell
# DS
E:\RPG-DEMO\Builds\Client\RPGDemoClient.exe -server -batchmode -nographics -port 7790 -playerPrefab 1 -logFile E:\RPG-DEMO\Logs\rpc-server.log

# Client
E:\RPG-DEMO\Builds\Client\RPGDemoClient.exe -connect 127.0.0.1:7790 -name RpcProbe -verifyRpc -batchmode -nographics -logFile E:\RPG-DEMO\Logs\rpc-client.log
```

`-verifyRpc` 仅创建一次性的运行时诊断对象，不修改场景和 Prefab；发送一次请求后自动销毁。

## 7. 第一版明确未做

- 没有 UE HeaderTool 那样的 `[ServerRpc]` 代码生成；当前显式注册更容易检查协议稳定性。
- 没有自研可靠序号、确认和重传；当前可靠性由 UTP Reliable Pipeline 提供。
- 没有 RPC 限频、每连接字节预算和调用冷却；业务 Handler 仍必须做规则校验。
- 没有 ReplicationGraph / relevancy / dormancy；Multicast 目标暂时等于“该连接存在此 ActorChannel”。
- 没有 RPC schema 自动生成及碰撞检测工具；协议变化目前需要人工升级版本和 SchemaHash。
- 没有大载荷分片；超过 1024 字节会在写包阶段拒绝。

## 8. 后续顺序

下一阶段优先回到角色移动可靠性：

1. Move 合包：一个包携带多个固定 Tick 的 Move。
2. 冗余重发：新包附带尚未被 ServerAck 的若干历史 Move，抵抗不可靠 UDP 丢包。
3. ClientTick/ServerTick 时间窗口校验：拒绝过旧、过新和异常跳变的输入。
4. 再增加 RPC 限频和每连接预算，避免合法 FunctionId 被高频滥用。

