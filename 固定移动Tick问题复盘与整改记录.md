# 固定移动 Tick 问题复盘与整改记录

## 1. 结论

当前网络移动原先没有固定模拟频率：

- `PawnMovementComponent.Update()` 每个渲染帧执行一次本地移动。
- `CharacterNetworkMovement.LateUpdate()` 每个渲染帧生成并发送一个 `ClientMove`。
- DS 收到 `ClientMove` 后立即执行一次移动模拟。
- `GameNetDriver.DefaultTickRate = 60` 原先只推进网络时钟并调度属性复制、移动快照，没有驱动角色移动。

因此客户端帧率会直接决定每秒生成和处理的 Move 数量。无图形模式下实测超过 4,000 Move/s，已经不是 60Hz 网络移动。

整改目标是将网络移动统一为固定 60Hz：每 `1/60` 秒生成一个 MoveCommand，本地预测一次；DS 将收到的 MoveCommand 入队，并在固定服务端 Tick 中最多消费一个。渲染帧率、输入采样频率和网络移动模拟频率彼此解耦。

## 2. 已证实的问题

### 2.1 帧率驱动 Move 导致模拟时间膨胀

原实现每个渲染帧发送一次 Move，并使用：

```csharp
Mathf.Clamp(Time.deltaTime, 1f / 240f, 0.1f)
```

这不是固定 Tick。客户端高于 240 FPS 时，每一个包仍至少声明 `1/240` 秒。若客户端达到 5,000 FPS，DS 一秒会收到约 5,000 个 Move，并累计模拟约：

```text
5000 × 1/240 ≈ 20.8 秒游戏时间
```

隔离压力测试中，输入后的服务端位置约从 `z=0` 增长到 `z=584`，速度仍显示约 `6`，符合“单位时间内执行了过多次固定最小步长”的特征。

### 2.2 `CharacterController.minMoveDistance` 造成偶发起步后停住

Prefab 中 `CharacterController.minMoveDistance = 0.001`。高帧率下，从静止开始的单步请求位移可能小于该阈值，`CharacterController.Move()` 会忽略位移。移动代码随后通过实际位移反算速度，得到 0，并在下一帧重复同一过程。

同条件 A/B 结果：

| 条件 | 输入 | 客户端结果 | DS 结果 |
| --- | --- | --- | --- |
| `minMoveDistance=0.001` | 持续 `(0,0,1)` | 速度 0、位置不变 | 速度 0、位置不变 |
| `minMoveDistance=0` | 持续 `(0,0,1)` | 速度约 6、持续移动 | 速度约 6、持续移动 |

主项目已在 `CharacterMovementComponent.Awake()` 中把网络预测使用的 CharacterController 阈值设为 0。

### 2.3 纠错后立即重放导致极端坐标

直接调用 `Transform.SetPositionAndRotation()` 后立即执行 `CharacterController.Move()` 时，CharacterController 的原生胶囊内部位置可能仍是纠错前位置。隔离测试记录到：权威位置应用后仍正常，但第一次重放 Move 后坐标跳到约 `-70015`，随后扩大到约 `-8e16`，最终触发 `Invalid worldAABB`。

主项目已在应用权威状态时暂时禁用 CharacterController，设置 Transform 后重新启用，使原生胶囊状态与 Transform 同步。相同压力测试修复后没有再出现极端坐标。

## 3. 固定 Tick 设计

```text
渲染 Update
  └─ 读取输入并累计到 Pawn

CharacterNetworkMovement.LateUpdate
  ├─ 消费本渲染帧输入，保存为最新输入状态
  └─ 60Hz 累加器
       └─ 每个固定 Tick：
            1. 使用固定 DeltaTime=1/60 本地预测
            2. 保存预测后的 SavedMove
            3. 生成一个 ClientMove
            4. 发送给 DS

DS GameNetDriver
  ├─ 收包：验证所有权、序号、输入和固定 DeltaTime，只入队
  └─ 60Hz Server Tick：
       1. 每个角色最多消费一个排队 Move
       2. 使用固定 DeltaTime=1/60 权威模拟
       3. 返回累计 ACK
```

第一版保持一个 MoveCommand 对应一个不可靠 UDP 包，即最多约 60 包/s。后续可保持 60Hz 模拟不变，将两个 MoveCommand 合并进一个 30Hz 网络包。

## 4. 约束与保护

- 固定移动频率：60Hz。
- 固定移动步长：`1/60` 秒。
- 客户端单帧最多追赶 8 个固定 Tick，防止卡顿后进入无限追帧。
- 客户端 SavedMove 上限：256。
- DS 每角色待处理 Move 队列上限：256。
- DS 校验客户端上报的 DeltaTime 必须等于固定步长（允许很小的浮点误差）。
- Move 继续使用递增 `Sequence` 和 `ClientTick`；ACK 使用已执行的最新 Sequence。
- 连接协议版本由 1 升为 2，旧版按渲染帧发送 Move 的客户端不能与新版 DS 混连。

## 5. 本轮验收标准

- 无论客户端是 30、60、144 还是数千渲染 FPS，`sent/s` 应稳定在约 60。
- DS 的 `processed/s` 应稳定在约 60，不再随客户端渲染 FPS 增长。
- 持续输入时客户端和 DS 的速度上限约为 6，现实一秒内位移约为 6，而不是数百。
- 停止输入后可以正常制动到 0。
- 不出现 `Invalid worldAABB` 或 `[Net][MoveDiag][Extreme]`。
- ACK 能持续清理 SavedMove，正常本地环境下 Pending 不持续增长。

## 6. 本轮实测结果

使用无图形客户端执行自动输入，客户端先静止 5 秒，再持续输入 `(0,0,1)`。客户端渲染帧率约为 3,000–9,000 FPS，结果如下：

| 指标 | 实测结果 |
| --- | --- |
| 客户端 `sent/s` | 稳定 60.0 |
| 客户端 `ack/s` | 通常 60.0 |
| DS `processed/s` | 稳定 60.0 |
| 客户端 Pending SavedMove | 通常 2，偶尔 3 |
| 预测纠错 | 0 |
| 持续移动速度 | 约 6.00 |
| 持续移动位移 | 每秒约 6 |
| 极端坐标 / `Invalid worldAABB` | 未出现 |

该结果说明移动模拟次数已经与渲染帧率解耦，DS 不再因为客户端高 FPS 而加速模拟。

## 7. 后续工作

固定 Tick 解决的是模拟时间基准问题，不等于完整的公网移动协议。后续仍需实现：

1. Move 合并与冗余重发，降低不可靠包丢失造成的服务端少模拟。
2. ClientTick 与 ServerTick 的时间映射、超前/落后窗口校验。
3. 服务端队列抖动缓冲和受控追赶策略。
4. 更严格的速度、加速度、旋转和位置合法性验证。
5. 将诊断日志改为可配置开关，避免正式版本每秒输出。
