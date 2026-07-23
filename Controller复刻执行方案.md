# UE Controller Unity 复刻执行方案

## 1. 实现范围

本阶段实现 UE `AController` 的基础框架，以及最小 `Pawn`、`Character`、`PlayerController`、`AIController` 和 `PlayerState`。

本阶段包含：

- `Controller` 独立 GameObject 与 MonoBehaviour。
- `Controller / PlayerController / AIController` 继承结构。
- `Pawn / Character` 继承结构。
- Controller 与 Pawn 双向引用。
- `Possess / OnPossess / UnPossess / OnUnPossess`。
- Pawn 销毁前主动通知 Controller。
- `Pawn`、`Character`、`PlayerState`、`ControlRotation`、`StateName`、`StartSpot`。
- 单机 Authority。
- Move/Look 输入忽略计数。
- Pawn 变化和 Controller 状态变化事件。

本阶段不包含：

- Character 移动、跳跃、重力和碰撞。
- Unity Input System 接入。
- 相机系统。
- AI 寻路、感知、黑板和行为树。
- 网络同步、RPC、预测和纠错。
- Controller 与 Pawn 的 Transform 父子关系。
- Controller 与 Pawn 的 Tick 依赖。
- 场景迁移和无缝旅行。

## 2. 固定约束

- Controller 使用独立 GameObject，不挂在 Pawn 或 Character 的 Transform 下。
- Controller 不包含 Collider、Rigidbody、Renderer 或 Animator。
- Controller 和 Pawn 互相持有对方引用。
- 一个 Controller 同时最多控制一个 Pawn。
- 一个 Pawn 同时最多被一个 Controller 控制。
- 双向关系只能通过 `Controller.Possess()` 和 `Controller.UnPossess()` 修改。
- Pawn 销毁时主动调用当前 Controller 的 `PawnPendingDestroy()`。
- Controller 的 `ControlRotation` 与 Pawn Transform rotation 相互独立。
- 单机阶段 Controller 默认具有 Authority。
- `Possess()` 保留 Authority 检查。
- 公共 `Possess()` 和 `UnPossess()` 不允许派生类覆盖；派生类只覆盖保护级生命周期方法。
- 不增加 `IController`、`IPawn`、`PossessionCoordinator`、Authority 接口或网络适配接口。

## 3. 目录结构

```text
Assets/GameFramework/
├── Runtime/
│   ├── Controller/
│   │   ├── Controller.cs
│   │   ├── PlayerController.cs
│   │   ├── AIController.cs
│   │   ├── ControllerStates.cs
│   │   └── PossessionResult.cs
│   ├── Pawn/
│   │   ├── Pawn.cs
│   │   └── Character.cs
│   └── PlayerState/
│       └── PlayerState.cs
```

本阶段不创建 asmdef。使用项目现有的 `Assembly-CSharp`。

命名空间统一使用：

```csharp
RPGDemo.GameFramework
```

## 4. 类结构

```text
MonoBehaviour
├── Controller
│   ├── PlayerController
│   └── AIController
├── Pawn
│   └── Character
└── PlayerState
```

引用关系：

```text
Controller.Pawn ───────────────► Pawn
Pawn.Controller ───────────────► Controller

Controller.Character ──────────► Pawn 是 Character 时的缓存
Controller.PlayerState ────────► PlayerState
Pawn.PlayerState ──────────────► 当前 Controller 的 PlayerState
```

## 5. Controller.cs

### 5.1 职责

- 保存当前 Pawn。
- 缓存当前 Character。
- 保存 PlayerState。
- 保存 ControlRotation。
- 保存 Controller 状态。
- 执行 Possess/UnPossess 固定流程。
- 处理 Pawn 销毁通知。
- 保存输入忽略计数。
- 发出 Pawn 和状态变化事件。

### 5.2 字段

```csharp
private Pawn pawn;
private Character character;
private PlayerState playerState;
private Quaternion controlRotation = Quaternion.identity;
private string stateName = ControllerStates.Inactive;
private Transform startSpot;

private bool hasAuthority = true;
private bool canPossessWithoutAuthority;

private int ignoreMoveInput;
private int ignoreLookInput;
```

### 5.3 只读属性

```csharp
public Pawn Pawn { get; }
public Character Character { get; }
public PlayerState PlayerState { get; }
public Quaternion ControlRotation { get; }
public string StateName { get; }
public Transform StartSpot { get; }

public bool HasAuthority { get; }
public bool CanPossessWithoutAuthority { get; }
public bool IsPlayerController { get; }
public virtual bool IsLocalController { get; }

public bool IsMoveInputIgnored { get; }
public bool IsLookInputIgnored { get; }
```

`IsPlayerController` 不保存独立状态，直接根据 Controller 的实际类型判断：

```csharp
public bool IsPlayerController => this is PlayerController;
```

单机阶段：

```csharp
public virtual bool IsLocalController => true;
```

### 5.4 事件

```csharp
public event Action<Pawn, Pawn> PossessedPawnChanged;
public event Action<string, string> StateChanged;
public event Action<Quaternion> ControlRotationChanged;
```

事件只能在状态修改完成后发送。

### 5.5 方法

```csharp
public PossessionResult Possess(Pawn inPawn);
protected virtual void OnPossess(Pawn inPawn);

public bool UnPossess();
protected virtual void OnUnPossess();

protected virtual void SetPawn(Pawn inPawn);
public virtual void PawnPendingDestroy(Pawn inPawn);

public void SetPlayerState(PlayerState inPlayerState);
public virtual void InitPlayerState(PlayerState inPlayerState);
public virtual void CleanupPlayerState();

public Quaternion GetControlRotation();
public virtual bool SetControlRotation(Quaternion newRotation);

public virtual void ChangeState(string newStateName);

public void SetIgnoreMoveInput(bool ignore);
public void ResetIgnoreMoveInput();
public void SetIgnoreLookInput(bool ignore);
public void ResetIgnoreLookInput();

protected virtual void OnDestroy();
```

### 5.6 Possess 固定流程

```text
Controller.Possess(inPawn)
│
├── 如果 !HasAuthority && !CanPossessWithoutAuthority
│   └── 返回 RejectedNoAuthority
│
├── 保存 oldPawn
│
├── 调用 OnPossess(inPawn)
│   │
│   ├── 如果当前 Pawn 与 inPawn 不同，并且当前 Pawn 非空
│   │   └── UnPossess()
│   │
│   ├── 如果 inPawn 为空
│   │   └── 返回
│   │
│   ├── 如果 inPawn.Controller 非空
│   │   └── inPawn.Controller.UnPossess()
│   │
│   ├── inPawn.PossessedBy(this)
│   ├── SetPawn(inPawn)
│   ├── SetControlRotation(inPawn.transform.rotation)
│   └── inPawn.Restart()
│
├── 如果 Pawn 发生变化
│   └── PossessedPawnChanged(oldPawn, Pawn)
│
└── 返回 Succeeded 或 AlreadyPossessed
```

规则：

- `Possess(null)` 等价于解除当前 Pawn。
- 重复 Possess 当前 Pawn 不创建第二份关系。
- 如果目标 Pawn 被其他 Controller 控制，先让旧 Controller 执行 UnPossess。
- 外部事件不能观察到只更新一侧引用的中间状态。

### 5.7 UnPossess 固定流程

```text
Controller.UnPossess()
│
├── currentPawn 为空
│   └── 返回 false
│
├── 保存 oldPawn
├── 调用 OnUnPossess()
│   ├── oldPawn.UnPossessed()
│   └── SetPawn(null)
│
├── PossessedPawnChanged(oldPawn, null)
└── 返回 true
```

### 5.8 SetPawn

```csharp
pawn = inPawn;
character = inPawn as Character;
```

本阶段 `SetPawn()` 不进行：

- Transform Attach/Detach。
- Tick 顺序设置。
- 输入绑定。
- 相机绑定。
- 网络 ownership 设置。

### 5.9 PawnPendingDestroy

```text
PawnPendingDestroy(inPawn)
│
├── 如果 inPawn != Pawn
│   └── 返回
│
├── UnPossess()
└── ChangeState(Inactive)
```

Controller 是否销毁不由 `PawnPendingDestroy()` 自动决定。本阶段 Pawn 销毁后 Controller 保留，供后续重生再次 Possess。

### 5.10 Authority

- `hasAuthority` 默认值为 `true`。
- `Possess()` 必须检查 Authority。
- `hasAuthority` 不提供 public setter。
- 本阶段不创建 Authority 类型、角色枚举或网络接口。
- 后续接入网络时再确定谁负责更新 Authority。

### 5.11 ControlRotation

- 初始值为 `Quaternion.identity`。
- Possess 成功时对齐新 Pawn 的世界旋转。
- 修改前检查 Quaternion 是否包含 NaN/Infinity。
- 保存前归一化 Quaternion。
- ControlRotation 改变不直接修改 Pawn Transform。
- 只有值实际变化时才发送 `ControlRotationChanged`。

### 5.12 输入忽略计数

```text
SetIgnoreMoveInput(true)  → ignoreMoveInput + 1
SetIgnoreMoveInput(false) → ignoreMoveInput - 1，最小为 0
ResetIgnoreMoveInput()    → ignoreMoveInput = 0

SetIgnoreLookInput(true)  → ignoreLookInput + 1
SetIgnoreLookInput(false) → ignoreLookInput - 1，最小为 0
ResetIgnoreLookInput()    → ignoreLookInput = 0
```

## 6. Pawn.cs

### 6.1 职责

- 保存当前 Controller。
- 保存从 Controller 获得的 PlayerState 引用。
- 接收 PossessedBy/UnPossessed 生命周期。
- 销毁时主动通知当前 Controller。
- 提供 Restart 生命周期入口。

### 6.2 字段和属性

```csharp
private Controller controller;
private PlayerState playerState;
private bool isDestroying;

public Controller Controller { get; }
public PlayerState PlayerState { get; }
public bool IsDestroying { get; }
```

### 6.3 方法

```csharp
internal void PossessedBy(Controller newController);
internal void UnPossessed();
internal void SetPlayerState(PlayerState newPlayerState);

public virtual void Restart();

protected virtual void OnPossessed(Controller newController);
protected virtual void OnUnpossessed(Controller oldController);
protected virtual void OnControllerChanged(Controller oldController, Controller newController);
protected virtual void OnPlayerStateChanged(PlayerState oldState, PlayerState newState);

protected virtual void OnDestroy();
```

### 6.4 PossessedBy

```text
保存 oldController
controller = newController
SetPlayerState(newController.PlayerState)
OnPossessed(newController)
如果 Controller 变化，调用 OnControllerChanged
```

`PossessedBy()` 只能由同一程序集内的 Controller 调用。

### 6.5 UnPossessed

```text
保存 oldController
SetPlayerState(null)
controller = null
OnUnpossessed(oldController)
OnControllerChanged(oldController, null)
```

### 6.6 Pawn 销毁通知

```text
Pawn.OnDestroy()
│
├── isDestroying = true
├── 保存 currentController
└── currentController?.PawnPendingDestroy(this)
```

要求：

- 通知发生时 Pawn 仍能提供自己的 Controller 引用。
- `PawnPendingDestroy()` 必须是幂等的。
- Pawn 已经 UnPossess 后再销毁，不重复通知旧 Controller。
- Controller 销毁时先 UnPossess，避免 Pawn 继续持有已销毁 Controller。

## 7. Character.cs

本阶段 Character 只用于复刻 UE 中 Controller 的 Character 缓存，不实现移动。

```csharp
public class Character : Pawn
{
}
```

## 8. PlayerController.cs

### 8.1 职责

- 标识为玩家 Controller。
- 默认需要 PlayerState。
- Possess 成功后进入 Playing。
- Pawn 销毁后进入 Inactive。
- 为后续输入和相机实现保留 UE 对应生命周期方法。

### 8.2 行为

```csharp
public class PlayerController : Controller
{
    public override bool IsLocalController => true;

    protected override void OnPossess(Pawn inPawn);
    protected override void OnUnPossess();
}
```

`OnPossess()` 必须先或后调用基类，使基础双向关系建立流程不被跳过。完成后调用：

```csharp
ChangeState(ControllerStates.Playing);
```

本阶段不读取 Input System，不创建相机。

## 9. AIController.cs

### 9.1 职责

- 复用 Controller 的 Possession 行为。
- 默认不创建 PlayerState。
- 为后续 Brain、PathFollowing、Perception 保留派生类位置。

### 9.2 行为

```csharp
public class AIController : Controller
{
    public override bool IsLocalController => true;

    protected override void OnPossess(Pawn inPawn);
    protected override void OnUnPossess();
}
```

本阶段不创建 Brain、Blackboard、Perception、NavMesh 或移动意图接口。

## 10. PlayerState.cs

本阶段只实现 Controller/Pawn 关系所需的最小 PlayerState，不实现完整玩家数据模块。

字段：

```csharp
private Controller owningController;
private Pawn pawn;
```

属性：

```csharp
public Controller OwningController { get; }
public Pawn Pawn { get; }
```

方法：

```csharp
internal void SetOwningController(Controller controller);
internal void SetPawn(Pawn pawn);
```

关系要求：

- Controller 设置 PlayerState 时，更新 PlayerState.OwningController。
- Controller 已拥有 Pawn 时设置 PlayerState，同时更新 Pawn.PlayerState。
- Pawn 被 Possess 时，从 Controller 获取 PlayerState。
- Pawn UnPossess 时清空 Pawn.PlayerState 和 PlayerState.Pawn。
- PlayerState 不随 Pawn 销毁。

本阶段不添加玩家名、账号、比分、队伍、Ping 或观战字段。

## 11. ControllerStates.cs

对应 UE Controller 的状态名称：

```csharp
public static class ControllerStates
{
    public const string Inactive = "Inactive";
    public const string Playing = "Playing";
    public const string Spectating = "Spectating";
}
```

`ChangeState()`：

- newState 与当前状态相同时不重复通知。
- 保存旧状态。
- 更新 StateName。
- 状态稳定后发送 `StateChanged(oldState, newState)`。

## 12. PossessionResult.cs

```csharp
public enum PossessionResult
{
    Succeeded,
    AlreadyPossessed,
    RejectedNoAuthority,
    InvalidPawn
}
```

`Possess(null)` 在当前存在 Pawn 时执行 UnPossess；当前没有 Pawn 时返回 `InvalidPawn`。

## 13. 销毁顺序

### 13.1 Pawn 销毁

```text
Pawn.OnDestroy
→ Controller.PawnPendingDestroy(Pawn)
→ Controller.UnPossess
→ Pawn.UnPossessed
→ Controller.SetPawn(null)
→ Controller.ChangeState(Inactive)
→ Pawn 完成销毁
```

### 13.2 Controller 销毁

```text
Controller.OnDestroy
→ UnPossess
→ Pawn 清空 Controller 和 PlayerState
→ CleanupPlayerState 只解除关系
→ Controller 完成销毁
```

本阶段 Controller 销毁不自动销毁 PlayerState；PlayerState 的最终生命周期由后续 PlayerState 阶段定义。

## 14. 执行顺序

1. 创建目录。
2. 实现 `ControllerStates` 和 `PossessionResult`。
3. 实现最小 `PlayerState`。
4. 实现 `Pawn`。
5. 实现 `Character`。
6. 实现 `Controller` 字段、属性和事件。
7. 实现 `SetPawn`、PlayerState 关系和 ControlRotation。
8. 实现 `Possess / OnPossess`。
9. 实现 `UnPossess / OnUnPossess`。
10. 实现 `PawnPendingDestroy` 和双方 `OnDestroy` 清理。
11. 实现 Controller State 和输入忽略计数。
12. 实现 `PlayerController`。
13. 实现 `AIController`。
14. 在 Unity 中编译，确认运行时代码无新增编译错误。

## 15. 完成标准

- 项目无新增编译错误。
- Controller 与 Pawn 双向引用始终一致。
- Pawn 销毁不会在 Controller 中留下引用。
- Controller 销毁不会在 Pawn 中留下引用。
- Character 销毁后 PlayerController 和 PlayerState 可以保留。
- 原 Controller 可以重新 Possess 新 Character。
- PlayerController 与 AIController 使用同一套基础 Possession 流程。
- Controller 与 Pawn 没有 Transform 父子关系。
- 未实现任何 Character 移动、网络、相机、AI 或 Tick 依赖功能。
