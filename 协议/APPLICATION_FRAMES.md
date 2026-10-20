# 应用帧清单与发送规则（现行实现）

更新日期：2026-10-20。本文只描述**应用帧**（LIN 无条件帧）的登记、组帧、调度与接收规则，依据当前代码实际行为编写，并与 `PROTOCOL.md` 对照。诊断帧 `0x3C/0x3D` 仅在与应用帧争用总线时提及。

---

## 0. 结论摘要

- 当前在运行的应用帧有 **3 条**：`0x15`（写入，电机控制）、`0x33`（读取，电机状态）、`0x34`（读取，温度/电压）。
- 正常节拍为 **100 ms 一拍**，拍内顺序固定 **`0x15 → 0x33 → 0x34`**，相邻帧间隔 **10 ms**：
  `t=0` 发 `0x15`，`t=10` 发 `0x33`，`t=20` 发 `0x34`，`t=100` 发下一拍 `0x15`。
- 10 ms 帧间隔从上一帧**串口写入完成**起算；不等待设备回复，写入完成就进入间隔。
- 调度器已从“两位置游标”改为 **帧序列 + 索引**，诊断暂停时保存下一帧在序列中的位置，恢复时从该位置续发。
- 诊断仍以**单帧写入完成**为抢占边界；诊断期间一条应用帧都不发，结束后立即续发并衔接 100 ms 整拍。
- 当前未登记 `0x35`。需求示例中出现的 "ID35" 没有提供信号布局，本轮按 `0x34` 实现；若确有 `0x35`，补充帧定义后会自动进入读取序列。

---

## 1. 判定标准与代码边界

### 1.1 什么是"应用帧"

应用帧指 LIN **无条件帧**中承载电机业务数据的帧，ID 范围为 `0x00～0x3B`。事件触发帧、偶发帧尚未引入。

```csharp
// Models/LinFrameDefinition.cs
public bool IsDiagnostic => Id >= 0x3C;
```

| ID 范围 | 类别 |
| --- | --- |
| `0x00～0x3B` | 应用帧 |
| `0x3C/0x3D` | 诊断帧（固定 8 字节、经典校验） |
| `0x3E/0x3F` | 保留，拒绝组帧 |

### 1.2 调度器如何选出应用帧

```csharp
// LinBusScheduler 构造函数
var statusReadIds = channel.Catalog.Definitions
    .Where(definition => !definition.IsDiagnostic
                      && definition.Publisher == LinPublisher.Slave)
    .Select(definition => definition.Id)
    .ToArray();

var steps = new List<AppFrameStep>();
if (statusReadIds.Length > 0)
{
    if (motorCommands is not null)
        steps.Add(new AppFrameStep(MotorCommandService.FrameId, IsWrite: true));
    steps.AddRange(statusReadIds.Select(id => new AppFrameStep(id, IsWrite: false)));
}
```

当前帧表 5 条，其中应用帧 3 条：

```csharp
// ViewModels/MainWindowViewModel.cs
new LinFrameDefinition(0x33, 8, LinPublisher.Slave,  LinChecksumType.Enhanced),
new LinFrameDefinition(0x34, 8, LinPublisher.Slave,  LinChecksumType.Enhanced),
new LinFrameDefinition(0x15, 8, LinPublisher.Master, LinChecksumType.Enhanced),
new LinFrameDefinition(0x3C, 8, LinPublisher.Master, LinChecksumType.Classic),
new LinFrameDefinition(0x3D, 8, LinPublisher.Slave,  LinChecksumType.Classic)
```

得到的应用序列为：

```text
[0] 0x15 写入
[1] 0x33 读取
[2] 0x34 读取
```

新增从节点读取帧时，只要在帧表登记，就会自动追加到序列尾部；写入帧固定为 `0x15` 且始终位于序列首位。

---

## 2. 帧清单

### 2.1 应用帧（已实现）

| ID | PID | 发布方 | 串口请求形态 | 数据长度 | 校验 | 业务用途 |
| --- | --- | --- | --- | ---: | --- | --- |
| `0x15` | `55` | 主节点（上位机写） | `55 AA 55 55 08 + 8B DATA + CS`，14 字节 | 8 | 增强 | 电机运动控制 |
| `0x33` | `73` | 从节点（电机答） | `55 AA 55 73 00`，5 字节 | 8 | 增强 | 位置、极限、运动/电压/温度/初始化状态、堵转回退步数 |
| `0x34` | `B4` | 从节点（电机答） | `55 AA 55 B4 00`，5 字节 | 8 | 增强 | 温度、电压两组 `float` 数值 |

### 2.2 诊断帧（不是应用帧）

| ID | PID | 发布方 | 串口请求形态 | 数据长度 | 校验 | 用途 |
| --- | --- | --- | --- | ---: | --- | --- |
| `0x3C` | `3C` | 主节点 | `55 AA 55 3C 08 + 8B PDU + CS`，14 字节 | 8 | 经典 | UDS 请求（含原样回显） |
| `0x3D` | `7D` | 从节点 | `55 AA 55 7D 00`，5 字节 | 8 | 经典 | UDS 响应 |

> 应用回复与诊断回复在字节层都可能是 9 字节且无帧头；归属靠当前业务上下文 + 校验类型判定，不靠字节本身。

---

## 3. 逐帧说明

### 3.1 `0x15` 写入帧：电机控制

**线上格式（14 字节）**

```text
55 AA | 55 | 55 | 08 | D0 D1 D2 D3 D4 D5 D6 D7 | CS
Break  Sync  PID  写标识 └────── 8 字节 DATA ──────┘ 增强校验
```

- `08` 是串口接口约定的"写入标识"，**不参与校验**；
- Checksum 以 PID `0x55` 为种子，只累加 8 字节 DATA，模 255 折叠后取反；
- 全 0 初始帧：`55 AA 55 55 08 00 00 00 00 00 00 00 00 AA`。

**数据布局**

| 字节 | 位宽 | 信号 | 取值 |
| --- | ---: | --- | --- |
| D0 | 8 | 运动控制指令 | `0x0` 立即停止；`0x1` 运动到指定位置；`0x2` 一直运动；`0x3` 执行初始化 |
| D1 | 8 | 运动方向 | `0x0` UNKNOWN；`0x1` CW；`0x2` CCW（上位机固定 `0x0`） |
| D2～D3 | 16 | 目标位置 | 无符号 step，低位在前 |
| D4～D7 | 32 | 保留 | 固定 0 |

**数据来源与语义**

`MotorCommandService` 持有"当前指令快照"，界面操作只修改字段：

| 界面操作 | D0 | D2～D3 |
| --- | --- | --- |
| 目标位置"确定" | `0x1` | 当前目标位置（已按 `[负极限, 正极限]` 钳制） |
| 快捷位置 CLOSE | `0x1` | 负极限位置 |
| 快捷位置 OPEN1/2/3 | `0x1` | 对应预设 step |
| 初始化校准 | `0x3` | 保持上一次值 |
| STOP | `0x0` | 保持上一次值 |

- 快照是**电平语义，不是事件队列**。每拍都按最新快照重新组帧发送，同一条指令会持续重发，直到被下一次界面操作改写；
- 上电初始快照为全 0；连接后第一拍就会发出全 0 帧；
- 断开串口**不清除**快照，重连后继续按断开前的值发送；
- `0x15` 的回复按协议存在，但业务含义未定义，当前只写入报文窗口。

### 3.2 `0x33` 读取帧：电机状态

**发送（5 字节，只有 Header）**

```text
55 AA | 55 | 73 | 00
Break  Sync  PID  读取标识（无 DATA、无校验）
```

**回复（9 字节，无帧头）**

```text
D0 D1 D2 D3 D4 D5 D6 D7 | CS
└────── 8 字节 DATA ─────┘  增强校验（PID 0x73 为种子）
```

**信号布局（2026-10-20 修订）**

| 起始字节 | 起始位 | 位宽 | 信号 | 说明 |
| --- | ---: | ---: | --- | --- |
| 0 | 0 | 16 | 当前位置 | 无符号 step，低位在前 |
| 2 | 16 | 16 | 正极限位置 | 无符号 step，低位在前 |
| 4 | 32 | 16 | 负极限位置 | 无符号 step，低位在前 |
| 6 | 48 | 3 | 运动状态 | `0` 停止；`1` 运动；`2` 发生过堵转；`3` 开路；`4` 短路；`5～7` 保留 |
| 6 | 51 | 2 | 电压状态 | `0` 正常；`1` 欠压；`2` 过压；`3` 保留 |
| 6 | 53 | 1 | 温度状态 | `0` 正常；`1` 过温 |
| 6 | 54 | 2 | 初始化状态 | `0` 完成；`1` 初始化中；`2` 错误；`3` 未定义 |
| 7 | 56 | 8 | 堵转回退步数 | 无符号 step，`0x00～0xFF` |

位 48～55 恰好占满字节 6：

```text
bit:   7 6 | 5 | 4 3 | 2 1 0
       init |温度|电压 | 运动
       2bit |1bit|2bit | 3bit
```

**代码解析**

```csharp
var flags = data[6];
var motion = (MotorMotionState)(flags & 0x07);
var voltage = (MotorVoltageState)((flags >> 3) & 0x03);
var overTemperature = ((flags >> 5) & 0x01) != 0;
var initialization = (MotorInitializationState)((flags >> 6) & 0x03);
var retreatSteps = data[7];
```

**界面映射**

| 信号 | 界面行 | 显示 |
| --- | --- | --- |
| 初始化状态 | 初始化状态 | 完成（绿）/ 初始化中（蓝）/ 错误（红）/ 未定义（灰） |
| 运动状态 | 运动状态 | 停止（绿）/ 运动中（蓝）/ 堵转、开路、短路（红）/ 保留（灰） |
| 运动状态 = 堵转 | 堵转故障 | 堵转（红）/ 其他为正常（绿） |
| 运动状态 = 开路 | 开路故障 | 开路（红）/ 其他为正常（绿） |
| 运动状态 = 短路 | 短路故障 | 短路（红）/ 其他为正常（绿） |
| 运动状态 ∈ {堵转, 开路, 短路} | 马达故障 | 故障（红）/ 其他为正常（绿） |
| 堵转回退步数 | 堵转回退步数 | `N step` |
| 电压状态 | 欠压故障、过压故障 | 欠压 / 过压 / 正常 |
| 温度状态 | 过温故障 | 过温 / 正常 |

显示遵循"最后一次有效读取"：没有新回复就保持原值，不超时清零；断开串口才清空。因此轮询停止时界面会显示旧值且没有"数据已过期"提示。

### 3.3 `0x34` 读取帧：温度与电压

**发送（5 字节）**

```text
55 AA | 55 | B4 | 00
Break  Sync  PID  读取标识
```

**回复（9 字节）**

| 字节 | 类型 | 内容 |
| --- | --- | --- |
| D0～D3 | `float`（小端 binary32） | 电机温度 |
| D4～D7 | `float`（小端 binary32） | 电机电压 |

中间件用联合体共享 `float` 与 `byte[4]` 的内存，`bytes[0..3]` 依次放入 D0～D3，因此按小端读取即为原始 `float`。当前实现：

```csharp
var temperature = BinaryPrimitives.ReadSingleLittleEndian(data[..4]);
var voltage = BinaryPrimitives.ReadSingleLittleEndian(data[4..8]);
```

- 不做倍率、偏移或单位换算；
- 界面按两位小数显示原始数值，例如 `36.50`、`24.75`；
- `NaN/Infinity` 视为无效读取，保留上一次有效显示；
- 单位、换算关系、有效范围仍需实机核对；若设备实际为大端，只需调整 `MotorMeasurementFrameParser`。

### 3.4 三帧的配对关系

- 同一拍内固定 `0x15 → 0x33 → 0x34`，但**读取结果不校验写入是否成功**；
- 写入只代表"字节写进串口"，不代表中间件已上总线，更不代表电机已执行；
- 电机是否响应，只能通过后续 `0x33` 的位置/运动状态间接观察；
- 诊断可以插在任意两帧之间（单帧边界暂停），因此恢复后的读取可能反映诊断之后的状态，而不是配对写入之前的状态。

---

## 4. 发送架构与硬性规则

### 4.1 调用链

```text
界面操作
  └─ 只改 MotorCommandService 快照，不直接发串口

LinBusScheduler（唯一节拍源 + 唯一发送出口）
  ├─ 应用拍：0x15 → 10 ms → 0x33 → 10 ms → 0x34
  └─ 诊断：3C 写入、3D 轮询（仅诊断独占模式）
        ↓
LinFrameChannel（构帧、连接检查、写后记日志）
        ↓
LinFrameCodec（PID、Checksum、08/00 标识）
        ↓
SerialPortConnectionService（TX/RX 有序队列）
        ↓
串口 → 中间件
```

### 4.2 规则表

| # | 规则 | 实现位置 | 说明 |
| ---: | --- | --- | --- |
| 1 | 单一发送出口 | `LinBusScheduler` | 应用帧与诊断帧都不绕过调度器直接写串口 |
| 2 | 单一节拍源 | `appTimer`，单次 100 ms | 不为每条帧建独立定时器 |
| 3 | 固定顺序 | `appSequence` | `0x15 → 0x33 → 0x34` |
| 4 | 帧间 10 ms | `RunAppBeatAsync` + `Task.Delay` | 从上一帧串口写入完成起算 |
| 5 | 不等待回复 | 无读取超时、无 ACK | 调度只关心写入完成 |
| 6 | 忙则跳拍 | `if (sendBusy) return;` | 不排队、不补发、不积压 |
| 7 | 快照语义 | `MotorCommandService.CreateDataSnapshot()` | 每拍取最新值，重复发送 |
| 8 | 断线即停 | `HandleConnectionStateChanged(false)` | 定时器停止，在途代次作废 |
| 9 | 重连复位 | `appStepIndex = 0` | 从 `0x15` 重新开始 |
| 10 | 诊断独占 | `EnterDiagnosticModeAsync` | 暂停应用发送，一次事务内不插应用帧 |

### 4.3 一拍内到底发生了什么

```text
t = 0 ms     单次定时器触发
             ├─ 检查：已连接？应用模式？未请求暂停？
             │    任一不满足 → return（不再排下一拍，诊断会接管）
             ├─ 先排下一拍：appTimer.Change(100 ms, 单次)
             ├─ 若上一拍仍在写 → return（本拍跳过，下一拍已在 100 ms 后）
             ├─ appStepIndex = 0，sendBusy = true，代次 +1
             ├─ 发 0x15：55 AA 55 55 08 + 快照 + CS（14 字节）
             ├─ 索引 +1；若已请求诊断暂停 → return
             ├─ 等待 10 ms（从 0x15 写入完成起算）
             ├─ 发 0x33：55 AA 55 73 00（5 字节）
             ├─ 索引 +1；等待 10 ms
             ├─ 发 0x34：55 AA 55 B4 00（5 字节）
             └─ 索引到达序列末尾，return
t = 100 ms   下一次单次定时器触发，重复上述过程
```

要点：

- 一拍物理写入总量为 **24 字节**（14 + 5 + 5），三帧间隔各 10 ms；
- 拍周期为 **100 ms**，从本拍 `0x15` 的发送时刻起算；10 ms 帧间隔不改变拍周期；
- 使用**单次定时器**而不是固定周期定时器：本拍启动后从当前时刻排下一拍，避免回调延迟造成相位压缩；
- 若一拍执行超过 100 ms，下一次触发看到 `sendBusy` 为真就跳过，实际周期被拉长；
- 跳过不积累欠账，不会出现"补发两拍"；
- `0x15` 写入完成后若收到诊断暂停请求，本拍剩余的 `0x33/0x34` 不再发送，索引停留在 `0x33`。

### 4.4 连接、断开、重连

| 事件 | 调度器行为 |
| --- | --- |
| 应用启动未连接 | `mode = Disconnected`，定时器不启动，不发任何帧 |
| 串口连接成功 | `mode = AppRunning`；`appStepIndex = 0`；单次定时器 100 ms 后触发。**第一帧在连接后约 100 ms 发出** |
| 串口断开 | `mode = Disconnected`；停止应用与诊断定时器；`sendGeneration++` 使在途任务失效；快照保留 |
| 重新连接 | 从 `0x15` 重新开始；不恢复断线前的诊断事务 |

### 4.5 诊断抢占与恢复

**进入诊断**

1. 协调层调用 `EnterDiagnosticModeAsync()`；
2. 置 `pauseRequested = true`，等待当前 `activeSendTask`；
3. 当前**单帧**串口写入完成即停：若正在发 `0x15`，本拍 `0x33/0x34` 不再发；若正在发 `0x33`，`0x34` 不再发；
4. 不等待应用回复，不把应用帧插进诊断帧之间；
5. 进入 `DiagnosticExclusive`，应用与诊断定时器都停止。

**退出诊断**

`ExitDiagnosticModeAsync()` 会立即执行一次 `RunAppBeatAsync`，从保存的索引继续：

| 暂停位置 | 恢复后的实际发送 |
| --- | --- |
| `0x15` 写完之后 | 立即 `0x33` → 10 ms → `0x34` → 100 ms 后下一拍 |
| `0x33` 写完之后 | 立即 `0x34` → 100 ms 后下一拍 |
| `0x34` 写完之后 | 立即新一拍 `0x15` → 10 ms → `0x33` → 10 ms → `0x34` |

- 恢复起点记为 `r0`，后续整拍在 `r0 + 100 ms`、`r0 + 200 ms`……开始；
- 暂停期间漏掉的周期不补发；
- 若诊断队列未清空，不恢复应用发送（多笔诊断之间不插应用帧）。

---

## 5. 接收与分发规则（应用帧）

### 5.1 回复格式

所有应用帧（读取与写入）按协议都会有回复，格式统一为：

```text
D0 D1 D2 D3 D4 D5 D6 D7 | CS
└────── 8 字节 DATA ─────┘  校验
```

回复**不含** Break、Sync、PID 或长度字段，无法从字节流直接看出这是哪条帧的回复。

### 5.2 识别流程

```text
串口字节 → 共享接收缓存
    ↓
按已登记帧的 DataLength + 1 作为候选长度收齐（当前均为 9 字节）
    ↓
反算候选 PID：candidatePid = ((~CS - ΣDATA) % 255 + 255) % 255
    ↓
P0/P1 奇偶自洽检查
    ↓
ID = PID & 0x3F → 查帧表
    ↓
FrameReceived 广播 → 订阅方按 ID 认领
```

- 增强校验失败后，若长度同时是诊断候选长度且经典校验通过，则作为诊断候选交给协调层；
- 两种校验可区分：经典校验帧经增强逆运算恒得 PID `0x00` 且奇偶不自洽；
- 无法匹配的字节逐个丢弃并记错误日志；
- 一次收到的字节块可连续解析多帧（当前一拍三条回复共 27 字节）。

### 5.3 组帧超时

- 开始接收后 **50 ms** 内未收齐完整回复，丢弃残缺缓存；
- 检查周期 **10 ms**；
- 未开始接收不启动超时（有回复就收，没有也不管）；
- 目的：回复无帧头帧尾，残缺缓存会把下一帧的前几个字节吃进来，导致后续拼接错位。

### 5.4 各帧的消费方

| 帧 | 消费方 | 行为 |
| --- | --- | --- |
| `0x33` 回复 | `MainWindowViewModel` → `MotorStatusFrameParser` → `ActuatorDebugViewModel` | 更新位置、极限、运动/电压/温度/初始化状态、堵转回退步数 |
| `0x34` 回复 | `MainWindowViewModel` → `MotorMeasurementFrameParser` → `ActuatorDebugViewModel` | 更新当前温度、当前电压 |
| `0x15` 回复 | 无业务订阅方 | 仅记录到报文窗口 |
| `0x3C/0x3D` | `DiagnosticTransactionService` | 回显核对、TP 重组、结果发布 |

---

## 6. 参数总表

### 6.1 应用帧

| 参数 | 当前值 | 位置 | 说明 |
| --- | ---: | --- | --- |
| 应用拍周期 | 100 ms | `LinBusScheduler.appPeriod` | 连接后启动，断开停止 |
| 帧间间隔 | 10 ms | `LinBusScheduler.appInterFrameGap` | 从上一帧串口写入完成起算 |
| 每拍帧数 | 3 | `appSequence` | `0x15 → 0x33 → 0x34` |
| 回复等待 | 无 | — | 调度不依赖回复 |
| 组帧超时 | 50 ms | `LinFrameChannel.frameAssemblyTimeout` | 空闲 50 ms 丢残缺缓存 |
| 组帧检查周期 | 10 ms | `LinFrameChannel.receiveTimer` | — |
| 发送失败处理 | 仅 `Debug.WriteLine` | `RunAppBeatAsync` catch | 不重试、不提示、不停调度 |
| 写超时兜底 | `SerialPort.WriteTimeout` | 串口设置 | 调度层无显式单帧超时 |
| 断线重连首帧 | 连接后约 100 ms | `HandleConnectionStateChanged` | 从 `0x15` 开始 |

> 10 ms 是设计目标与最小间隔；Windows 默认定时器分辨率约 15.6 ms，实测间隔可能为 10～16 ms。若设备要求严格 10 ms，需要提高系统定时器精度或改用硬件定时，不能依赖 `Task.Delay`。

### 6.2 诊断帧（仅用于理解抢占时序）

| 参数 | 当前值 | 位置 |
| --- | ---: | --- |
| `3C` 回显等待 | 1000 ms | `DiagnosticTiming.EchoWaitTimeout` |
| SF 首拍 `3D` | 20 ms | `DiagnosticTiming.ResponsePollPeriod` |
| SF `3D` 节拍 | 20 ms | 同上 |
| SF 总截止 | 5 s | `DiagnosticTiming.TotalDeadline` |
| 多帧单片写入预算 | 500 ms | `DiagnosticSendTiming.WriteBudget` |
| 多帧单片回显预算 | 1000 ms | `DiagnosticSendTiming.EchoBudget` |
| 相邻分片最大间隔 | 1000 ms | `DiagnosticSendTiming.InterFrameGapBudget` |
| 发送调度余量 | 1 s | `DiagnosticSendTiming.SchedulingMargin` |
| 回显隔离 | 50 ms | `DiagnosticSendTiming.EchoDrainDuration` |
| 历史回显容忍 | 200 ms | `DiagnosticSendTiming.EchoToleranceAfterLastComplete` |
| 多帧响应预算 | 5 s | `DiagnosticResponseTiming.ResponseBudget` |
| 多帧首个 `3D` | 20 ms | `DiagnosticResponseTiming.FirstResponsePollDelay` |
| 多帧 `3D` 节拍 | 20 ms | `DiagnosticResponseTiming.ResponsePollPeriod` |
| `N_Cr` | 1000 ms | `ConsecutiveFrameTimeout` |

> 多帧参数是"可运行基线"，尚未经中间件/Bootloader 契约与实机验证，不应视为最终值。

---

## 7. 现状差异与风险

### 7.1 控制指令是"持续重发"而不是"一次性触发"

`MotorCommandService` 保存的是当前状态，不是待发队列。`SetInitialize()`（`0x3`）或 `SetMoveToPosition()`（`0x1`）写入后，该值会一直保留，每 100 ms 重发一次，直到 STOP 或其他指令替换。

需要确认设备端对重复指令的处理：

- 若设备把每帧都当作新的"开始初始化/开始运动"，可能反复触发；
- 若设备只在指令变化时动作，则当前实现只是浪费带宽；
- 无论哪种，都应在协议层明确"重复帧是否幂等"，或在业务层改为"发送后回落 STOP / 等待状态确认"。

### 7.2 `0x15` 回复无业务语义，写入不可确认

- 串口写入成功 ≠ 中间件已上总线 ≠ 电机已执行；
- `0x15` 的回复当前只显示在报文窗口，不校验、不关联请求；
- 因此上位机无法区分"命令被接受""命令被丢弃""电机未响应"，只能靠 `0x33` 的位置/状态间接推断。

### 7.3 应用发送没有调度层超时、重试与失败提示

- `RunAppBeatAsync` 捕获所有异常后只写 `Debug.WriteLine`；
- 调度层没有单帧写超时；只有 `SerialPort.WriteTimeout` 在驱动层兜底；
- 若写线程阻塞在 `SerialPort.Write`，`sendBusy` 会一直占用，后续拍全部跳过，直到写入返回或超时；
- 没有重试；Release 下 `Debug.WriteLine` 不可见，界面除连接状态外没有"发送失败"提示。

### 7.4 忙则跳拍会拉长状态刷新周期，且无过期标记

- 写入变慢时，定时器触发被跳过，`0x33/0x34` 的实际刷新率低于 10 Hz；
- 界面按协议保持"最后一次有效读取"，没有时间戳或"数据已过期"提示；
- 操作员可能根据几秒前的旧位置/旧温度做判断。

### 7.5 10 ms 间隔受系统定时器分辨率影响

`Task.Delay(10 ms)` 在 Windows 上通常实际为 15～16 ms，不能保证精确 10 ms。当前实现保证**不小于 10 ms**，满足"帧间至少间隔 10 ms"的常见设备要求；若设备要求严格 10 ms 或对间隔上限敏感，需要改用高精度定时方案并实机抓包确认。

### 7.6 `0x35` 未定义

需求示例中出现了 `0x35`，但没有提供信号布局、长度或校验类型。当前未登记该帧，避免按未确认信息猜测。若确需 `0x35`，补充定义后在帧表新增一条从节点读取帧，调度器会自动追加到 `0x34` 之后，并按同样 10 ms 间隔发送。

### 7.7 `0x33` 新增状态位的显示约定

- 运动状态 `0x2` 现在直接显示"堵转"，不再显示"停止"；堵转同时点亮"堵转故障"与"马达故障"；
- `0x3/0x4` 开路/短路同时点亮对应行与"马达故障"；
- 初始化状态 `0x3` 按"未定义"灰色显示，不当作错误；
- 堵转回退步数按字节 7 原值显示，不做角度换算（界面标签已改为"堵转回退步数"）。

---

## 8. 维护检查清单

### 8.1 新增一条周期读取应用帧

1. 在 `MainWindowViewModel` 的 `LinFrameCatalog` 增加 `new LinFrameDefinition(id, 8, LinPublisher.Slave, LinChecksumType.Enhanced)`；
2. 确认它排在 `0x34` 之后（帧表枚举顺序决定序列尾部顺序）；
3. 新增解析器，并在 `HandleStatusFrameReceived` 增加对应分支；
4. 验证三条/四条回复连续到达时的拆包、粘包与 ID 识别；
5. 验证三个暂停边界的恢复顺序（新增帧会增加边界数量）；
6. 同步更新 `PROTOCOL.md`、`APPLICATION_FRAMES.md`、`CHANGELOG.md`。

### 8.2 修改应用拍周期或帧间隔

- 周期只改 `LinBusScheduler` 构造参数 `appPeriod`；
- 帧间隔只改 `appInterFrameGap`；
- 不要在 `RunAppBeatAsync` 里插入固定 `Task.Delay(100)` 或新建定时器；
- 任何"等待回复再发下一帧"的改动都会破坏"不等待"的前提，需同时定义超时与失败策略。

### 8.3 回归验证点

| 场景 | 期望 |
| --- | --- |
| 正常连接 | 约 100 ms 后首帧 `0x15`，10 ms 后 `0x33`，再 10 ms 后 `0x34`；下一拍 `0x15` 在 100 ms 后 |
| 串口忙 | 该拍整体跳过，不补发，下一拍正常 |
| 诊断暂停在 `0x15` 后 | 诊断期间无应用帧；结束后先 `0x33`，10 ms 后 `0x34` |
| 诊断暂停在 `0x33` 后 | 结束后先 `0x34`，再 100 ms 整拍 |
| 诊断暂停在 `0x34` 后 | 结束后立即新一拍 `0x15 → 0x33 → 0x34` |
| 断线 | 立即停止；在途写入不误伤重连后的新任务 |
| 重连 | 从 `0x15` 重新开始，索引归零 |
| 三条回复粘包 | 逐帧解析，不串帧、不吞字节 |
| 未登记 ID | 逐字节丢弃并记日志，不影响后续帧 |

---

## 9. 一句话总结

当前应用层是"**三条帧、100 ms 一拍、拍内 10 ms 间隔、先写后读、写完即走、诊断按单帧边界独占**"的周期快照模型；`0x34` 已接入，`0x35` 仍待协议定义。
