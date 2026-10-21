# 修改记录

> 2026-10-19 诊断传输能力：按《3C连续帧功能实施计划.md》实现 `0x3C` 多帧请求（FF+CF）、逐片回显确认、发送/响应双截止、候选归属判定、跨事务门禁、取消与进度契约，并完成同源 TimeProvider 改造；详见本文 2026-10-19 条目。
>
> 2026-10-18 诊断第一阶段：按 `WpfApp1诊断架构讨论记录.md` 实现三个设备版本读取（22 F1 80 / F1 95 / F1 93，NAD 0x78），调度采用“暂停应用通信”模式；详见本文 2026-10-18 条目。
>
> 2026-10-15 文档整理：协议文档合并为 [PROTOCOL.md](../协议/PROTOCOL.md)（原 `LIN_APPLICATION_READ.md`、`LIN_APPLICATION_WRITE.md` 已并入）；早期 `AA 55 / CMD / SUB_CMD / LEN / XOR` 旧协议时期的详细记录已压缩为摘要，Git 历史可查完整内容。

## 2026-10-21

### 新增“固件升级”页面（侧边栏第二项）

**界面**

- 侧边栏新增“固件升级”入口，样式与“执行器调试”一致（`Segoe MDL2` 字形 `\uE895`），通过 `MenuItemViewModel` + `DataTemplate` 接入现有导航。
- 新页面 `FirmwareUpgradeView.xaml`：顶部标题栏为“固件升级 / Firmware Upgrade”，右侧串口状态芯片与“串口设置”“连接设备”按钮与调试页同构。
- 内容区域暂时留空，待升级业务（文件选择、传输、进度、校验）实现。

**架构**

- 新增 `ViewModels/FirmwareUpgradeViewModel.cs`（21 行）：构造注入 `SerialSettings`，与执行器调试页**共用同一实例**；串口连接由 `MainWindowViewModel` 级的 `connectionService` 持有，切换页面不重置、不断开。
- `MainWindowViewModel` 增加 `FirmwareUpgrade` 属性与第二个菜单项；`MainWindow.xaml` 的 `ContentControl.Resources` 增加 `FirmwareUpgradeViewModel → FirmwareUpgradeView` 映射。
- 样式沿用各视图自包含风格的既有组织方式，未抽取共享资源字典，不改动 `ActuatorDebugView`。

**验证**

- `dotnet build` 0 警告 0 错误；启动新编译版本确认主窗口与 XAML 加载正常，无启动异常。
- 页面间切换与串口状态保持需人工观察确认（自动化验证未覆盖 UI 交互）。

### 应用回复进行中切换诊断：收尾策略实现

- 记录用户反馈的问题：应用回复接收中触发诊断，首个 3C 打断中间件应用收发，出现无回显；原有暂停只等待当前应用写入结束。
- 按 0x15/0x33/0x34 管理待回复登记：`RunAppBeatAsync` 在发送前（同一临界区内）置位，`LinReadReplyParser` 校验通过并分发 `FrameReceived` 时清除该 ID 登记。
- 触发诊断立即暂停后续应用发送；从触发时刻起最多等 100 ms。无登记或提前全清就提前放行；到期仍有登记则清除全部并放行，不判为收尾超时失败。
- 保留“当前应用写入完成”前提；100 ms 包含写入剩余时间，不从写完后重计。写入超过截止时，写完后直接放行，不再追加 100 ms。
- 实现落点 `Services/LinBusScheduler.cs`：新增 `appReplyDrainWindow` 构造参数（默认 100 ms）、`pendingAppReplyIds` 登记集合、`appReplyDrainSignal` 等待信号；`CancelPendingEnterRequest`、连接状态变化、`Dispose`、`ExitDiagnosticModeAsync` 均清理登记并释放等待。
- 验证：临时控制台工程覆盖无登记立即切换、提前清空放行、到期强清、写入跨过截止不追加等待、发送失败撤回登记、撤销提前结束、FIFO 重复申请立即返回、放行后 3C 可发出，全部通过；100 ms 拍、10 ms 帧间隔、三帧顺序回归通过；`dotnet build` 0 警告 0 错误。
- 区分正常应用拍 100 ms、已有残帧字节间超时 50 ms 和新增收尾窗口；清登记不等于清接收缓存，也不解除诊断准入门禁。
- 同步[诊断架构讨论记录第 19 节](../架构/WpfApp1诊断架构讨论记录.md)、[现行协议第 13 节](../协议/PROTOCOL.md)、[应用帧说明第 4.6 节](../协议/APPLICATION_FRAMES.md)及[架构文档第 6.2 节](../架构/ARCHITECTURE.md)，更新实现落点与验证状态。
- 未执行实机复测：“应用回复接收中触发诊断”的回显恢复情况仍需实机确认。

## 2026-10-20

### 应用帧三帧序列、`0x33` 新位布局与 `0x34` 接入

**协议与解析**

- `0x33` 按新定义重排字节 6：bit0-2 运动状态（5 种：停止/运动/堵转/开路/短路），bit3-4 电压状态，bit5 温度状态，bit6-7 初始化状态；字节 7 改为堵转回退步数（8 bit）。
- `MotorStatusSnapshot` 增加 `InitializationState`、`StallRetreatSteps`，`MotorMotionState` 增加 `OpenCircuit`/`ShortCircuit`；`MotorStatusFrameParser` 按新位域提取。
- 新增 `MotorMeasurementFrameParser`：`0x34` 前 4 字节温度、后 4 字节电压，按小端 IEEE 754 binary32 读取；新增 `MotorMeasurementSnapshot`。

**调度**

- `LinBusScheduler` 由两位置游标改为“帧序列 + 索引”`appSequence`，当前序列 `0x15 → 0x33 → 0x34`；诊断暂停保存索引，恢复时按索引续发。
- 应用拍保持 100 ms；拍内相邻帧增加 10 ms 间隔（从上一帧串口写入完成起算），`0x15` 到下一拍 `0x15` 仍为 100 ms。
- 应用定时器改为单次重排（每次触发后从当前时刻排 100 ms），避免固定周期定时器回调延迟造成的相位压缩；忙则整拍跳过。

**界面**

- 帧表登记 `0x34`，`HandleStatusFrameReceived` 增加 `0x34` 分支；温度/电压按两位小数显示，非有限值保留上次有效值。
- “初始化状态”“开路故障”“短路故障”“马达故障”“堵转回退步数”由 `0x33` 驱动，替换原来的静态占位；“退步度数”标签改为“堵转回退步数”。

**验证与说明**

- `dotnet build` 0 警告 0 错误；临时控制台用例验证了 `0x33` 位域、`0x34` 小端 float、三帧顺序与 10 ms/100 ms 时序。
- Windows 定时器分辨率约 15.6 ms，10 ms 间隔实测可能为 10～16 ms；当前保证“不小于 10 ms”。
- 需求示例中的 `0x35` 未提供布局，本轮按 `0x34` 实现；若确需 `0x35`，补充帧定义后会自动进入读取序列。
- 浮点字节序、单位、换算与 `0x15` 重复快照的设备端幂等性仍需实机核对。

### 发布复核：诊断释放竞态仍待处理

- 后续复查确认一个未解决的 P1 竞态：请求已经交接为 `active`、但首个 `0x3C` 尚未提交时调用 `Dispose()`，调用方可能先收到 `Shutdown`，恢复执行的发送路径仍可能提交一个迟到的 `0x3C`；该问题已复现 3 次。
- 本次更新保留现有实现并记录此边界，竞态修复继续暂缓；发布内容不应被视为该释放时序已验收。

### 应用帧 `0x34` 温度、电压协议补充（仅文档，代码待接入）

- 根据中间件截图与用户确认，在 `PROTOCOL.md` 第 12 节补充 `0x34` 读取帧：PID=`B4`，8 字节 DATA，前四字节为温度 `float` 的原始内存字节，后四字节为电压 `float` 的原始内存字节；采用增强型校验。
- 确认接入后仍为 100 ms 一拍，发送顺序为 `0x15 → 0x33 → 0x34`，帧间不额外等待 100 ms、不等待回复；同步定义三个单帧暂停边界及诊断结束后的游标恢复顺序。
- 温度、电压数值由应用帧周期更新界面，与 `0x33` 故障状态位分开处理，不通过诊断按钮触发。
- 浮点格式、内存字节序、单位、换算、显示精度及异常值规则仍需核实；未据截图擅自指定设备数值格式或倍率。
- 同步诊断架构记录的后续扩展约定；本次未修改 C#/XAML、未构建、未执行实机验证，不作为已实现版本记录。

## 2026-10-19

### 诊断传输第二阶段：`0x3C` 多帧请求

实现依据：《3C连续帧功能实施计划.md》§4～§7。当前范围只做发送侧传输能力，不含升级业务、负响应解析和界面入口。

**分包与类型契约**

- `LinTransportProtocol` 增加 `EncodeRequestFrames`、`CalculatePduCount`、`GetUdsPayloadLength`：1～6 字节保持 SF；大于 6 字节编码为 1 个 FF + 若干 CF，序号从 1 起 4 位回绕，末帧 `FF` 填充，上限 4095。原有 `EncodeSingleFrame` 保留。
- 新增 `Models/DiagnosticMultiFrameOptions.cs`：`DiagnosticMultiFrameOptions`、`DiagnosticSendTiming`、`DiagnosticResponseTiming`。多帧参数不再塞入 `DiagnosticTiming`，SF 路径读取原有五参数不变。
- `Diagnostics.cs` 增加 `DiagnosticRequest.MultiFrameOptions`、`DiagnosticPhase`、`DiagnosticProgress`；`DiagnosticResult` 增加 `Progress`；新增失败原因 `RequestWriteTimeout`、`SendDeadlineExceeded`、`InterFrameDeadlineExceeded`、`ResponseDeadlineExceeded`、`ProtocolAttributionError`、`Cancelled`、`QueueAborted`、`DiagnosticGateClosed`、`InvalidRequest`，原有编号不变。

**事务与计时**

- 多帧请求拆分两段截止：`SendDeadline = t_send_start + SendBudget(N)`，`ResponseDeadline = t_last_complete + ResponseBudget`；`t_last_complete = max(最后一帧写入完成, 最后一帧有效回显)`。
- 逐片发送并逐片确认回显；回显早于写入任务返回时先记录、不误判超时。SF 路径同步修正“截止尚未初始化即参与判断”的竞态：发送前先登记临时截止，写入返回后按 t0 重算。
- 新增固定 `EchoDrain` 隔离：全部片确认后先清理解析器残留、`Reset`＋`BeginReceive`、原子发布 `WaitingResponse`，再按首次读取延迟启动 3D；发送与隔离期间不提交任何 3D。
- 候选按阶段判定：发送阶段只认已登记分片回显，其余候选丢弃；隔离期全部丢弃；响应阶段容忍窗口内匹配历史回显的候选丢弃，超窗则按 `ProtocolAttributionError` 终止，不投入 TP。
- `LinBusScheduler.StartResponsePolling` 增加首次延迟参数，`FirstResponsePollDelay` 与 `ResponsePollPeriod` 分别生效；SF 仍传同值以保持原首拍行为。

**队列、取消与门禁**

- `ExecuteAsync(request, cancellationToken = default)`：入队前取消、排队取消、运行期取消统一返回 `DiagnosticResult.Failed(Cancelled)`；排队取消立即完成、零写入，不打断前序请求。
- 失败策略按 §4.4.1：多帧发送失败、`ResponseDeadlineExceeded`、`DeadlineExceeded`、`ProtocolAttributionError`、已发送后的取消、已发请求后断线 → 中止整批，排队者返回 `QueueAborted`；`ResponseMismatch`（含 `7F`）只结束本笔并继续 FIFO。
- 新增跨事务归属门禁：`IsDiagnosticBlocked` 只读、`ResetDiagnosticGate()` 显式解除；门禁关闭后新提交返回 `DiagnosticGateClosed`，串口重连不解除。
- 门禁触发条件收窄（同日修订）：只在 `ProtocolAttributionError`、响应阶段取消、已发请求但响应未收齐时断线三种情况下关闭。`DeadlineExceeded` / `ResponseDeadlineExceeded` 仍清空同批排队，但**不再关闭门禁**——超时时设备本未应答，迟到响应概率极低，而门禁无界面入口解除，一次无应答会导致整轮诊断必须重启。残余风险（超时后立即重试可能认领上一笔的迟到响应）已在实施计划 §4.4.2 记录，进入升级业务前必须重新评估。
- `DiagnosticProgress` 在结果发布时冻结，记录总片数、写入尝试/完成、回显确认、有效 UDS 字节、当前索引、失败阶段与在途写入标志。

**计时设施**

- `DiagnosticTransactionService`、`LinBusScheduler`、`LinTransportProtocol`、`LinFrameChannel`、`LinReadReplyParser` 构造注入 `TimeProvider`（默认 `System`）；定时器改为 `CreateTimer` 返回的 `ITimer`，时间戳换算使用 `TimestampFrequency`。
- `MainWindowViewModel` 在现有组装位置统一传入 `TimeProvider.System`。
- `SerialPortConnectionService` 内部活动计时不跨模块传递，本次不改造。

**文档与验证**

- `PROTOCOL.md` 增加第 11 节多帧请求规则，补齐 `0x3C/0x3D` 帧表与标准型校验说明。
- 仓库外临时工程（普通 `net10.0` + 生产源码显式白名单，`Microsoft.Extensions.TimeProvider.Testing 9.10.0`、`System.IO.Ports 10.0.0`，SDK 10.0.401）执行 1615 项检查全部通过：长度 1/6/7/11/12/95/96/101/102/4095 的分包与还原、序号回绕、填充、固定样例；SF 版本读取回归；多帧 4 片成功与进度；`Timing` 全 1 ms 不影响多帧；回显缺失停止余片；`7F` 返回 `ResponseMismatch` 且不关门禁；响应超时关门禁、`DiagnosticGateClosed`、复位后恢复；排队取消零写入；显式失败文案映射；发送期杂散响应不提前完成；超窗历史回显判归属异常。随后对最终源码再次执行封版回归（分包、SF、多帧、门禁、运行中释放）通过。以上 1615 项执行于门禁触发面收窄之前。
- 门禁收窄后的针对性回归（同一仓库外临时工程方式，16 项检查全部通过）：SF 超时返回 `DeadlineExceeded` 且门禁不关闭、重试被接纳并成功；多帧响应超时返回 `ResponseDeadlineExceeded` 且门禁不关闭、后续请求非 `DiagnosticGateClosed`；归属异常关闭门禁、后续请求返回 `DiagnosticGateClosed` 且零发送、`ResetDiagnosticGate()` 后恢复；响应阶段取消关闭门禁。临时工程已删除，仓库内未新增测试项目或依赖。
- 构建：`dotnet build WpfApp1/WpfApp1.csproj` 0 警告 0 错误。

**尚未完成**

- 实机联调：逐片回显时延、隔离窗口、最大 CF 间隔、683 帧长请求的总时长均需抓包确认；相关默认值只是可运行基线，不是已验证协议参数。
- 正向升级业务（批次独占、安全访问、下载块、擦写、校验、重启）、`7F` 负响应结构化处理、UI 多帧发起入口尚未实现。
- 跨事务“同 DID 迟到回复”在无可靠隔离契约时按保守失败处理；门禁解除目前只有显式复位/重启。

### 多帧实现审查修复（F1～F4，同日追加）

依据《3C多帧实现审查与修复方案.md》§2～§5 修复四个 P1：

- **F1 重复分片回显**：发送阶段先精确比较当前待确认片的原始回显，再考虑历史匹配；历史回显只用于过滤，不推进、不刷新等待。107/108/4095 字节全 AA 载荷不再卡在第 18 帧，683 帧可全部确认。响应阶段的超窗历史回显判定保留。
- **F2 响应认领绑定真实 3D 提交**：`LinBusScheduler` 增加 `DiagnosticReadAttemptStarting`／`DiagnosticReadCommitted`／`DiagnosticReadAttemptFailed`，通知携带本轮询的事务身份。首读尚未开始时丢弃候选；首读在途时有界暂存（上限 32 条）；写入成功回执后按原接收时间重放，失败、完成或断线立即使上下文失效；重放前复核阶段与总截止。`TpReceiveEnabled` 不再单独构成认领依据。
- **F3 结果与排空分离**：`CompleteLocked` 冻结结果后立即完成 `PendingTransaction.Completion`，调用者不再等待发送出口排空；`RunTransactionAsync` 的 `finally` 继续负责排空，期间保持独占与整批中止策略。新增 `ISerialPortConnectionService.WaitForTransmitIdleAsync`，以发送工作线程的真实写入完成信号为准，外层 `SendAsync` 等待被取消不代表物理写入结束。
- **F4 获取独占期间取消**：`ProcessQueueAsync` 以 `enterTask.WaitAsync(pending.CancellationToken)` 等待共享申请，取消立即返回 `Cancelled`、零诊断写入、阶段为 `EnteringDiagnostic`；无有效等待者时 `CancelPendingEnterRequest` 提升代次并清除暂停意图，迟到完成不再切换模式；被丢弃申请任务的异常已观察。
- 文档：`3C连续帧功能实施计划.md` §10、`WpfApp1诊断架构讨论记录.md` §17、审查方案 §7.3／§7.4 已同步。
- 验证：仓库外临时工程（同一白名单方式）执行 55 项检查全部通过，覆盖 R1～R14：重复载荷全片确认、历史/重复回显不误推进、首读前注入不成功、首读在途暂存与回执后重放、首读失败不利用暂存、写入预算到期即返回且排空期隔离、外层取消后新请求 `QueueAborted`、等待独占取消及时、多等待者仅首个取消、断线/重连门禁、`Timing` 全 1 ms、SF 回归。临时工程已删除，仓库未新增测试项目或依赖；构建 0 警告 0 错误。
- 未验证边界：中间件若允许无标识的任意重复/迟到回显，字节层仍无法区分当前片与内容相同的旧片；暂存上限 32 条为可运行基线，须按中间件契约最终定稿；实机抓包仍未执行。

### 进入阶段生命周期补齐（F5，同日追加）

依据《3C多帧实现审查与修复方案.md》§7.5 修复“获取独占期间的断线/释放遗漏”：

- **登记进入态**：`DiagnosticTransactionService` 新增 `enteringPending`，在同一 `stateLock` 临界区内完成“出队 → `TryBeginRun` → 连接代次校验 → 登记”。断线与释放不再只能看到 queue 与 active，等待独占的请求离开循环局部变量。
- **单一终止入口**：新增 `CompleteEnteringLocked`（`TryFinishEntering` 只允许“已出队 → 结束”），冻结原因与零发送进度、`FailedPhase=EnteringDiagnostic` 后完成等待者；已交接给 active 的请求仍由 `CompleteLocked` 负责，二者不双写。
- **可中断等待**：`WaitForExclusiveEntryAsync` 以 `Task.WhenAny(enterTask, pending.Completion.Task)` 等待，`Cancelled`／`Disconnected`／`Shutdown` 均不等旧应用帧写入结束；结果竞争按“已释放 > 连接失效 > 调用者取消”判定。
- **原子交接**：等待返回后在 `stateLock` 内复核 `disposed`、连接代次、`pending` 未终结与 `enteringPending` 身份，随后一次性完成“清除 entering → 建立 active → 绑定 Owner”。`RunTransactionAsync` 改为接收已交接事务，不再自行重建 active。
- **断线/释放接入**：`HandleConnectionStateChanged` 在同一代次推进临界区结束 entering；`Dispose` 先置 `disposed`、结束 entering、失效未完成的独占申请，再结束 active、清空队列，最后在锁外解除订阅、释放定时器与取消注册。
- **迟到收尾**：无有效等待者时复用 `CancelPendingEnterRequest` 的代次保护撤销申请并观察迟到异常；循环 finally 增加遗留 entering 的兜底完成。
- 验证：仓库外临时工程（同一白名单方式，真实生产源码 + 串口/日志替身）42 项检查全部通过、连续三次一致：R15 断线即时 `Disconnected`；R16/R17 释放即时 `Shutdown`，迟到的旧写入不再发送 3C/3D；R18 重连不重放；R20 entering 取消后第二笔继续；R21 排队取消与 40 次“提交即取消”竞态均唯一完成；R22 重复释放。回归 R1（12 字节）、R2（4095 字节全 AA，683 帧全部确认）、R14（SF）通过。临时工程已删除；构建 0 警告 0 错误。
- 未验证边界：实机断线恢复与释放时序仍需抓包确认；负响应与升级业务不在本次范围。

## 2026-10-18

### 诊断模块第一阶段实现：三个设备版本读取

架构与执行规则以 `WpfApp1诊断架构讨论记录.md` 的定稿为准（模块职责、接收识别、TP 状态机、调度与时间规则、业务成功条件、应用恢复节奏）。

- 新增 `Models/Diagnostics.cs`：`DeviceVersionKind`、`DiagnosticRequest`、`DiagnosticResult`、`TpReceiveResult`、`FrameWriteReceipt`、`DiagnosticPacket`、`DiagnosticTiming`。
- 新增 `Services/LinTransportProtocol.cs`：SF 编码与 SF/FF/CF 重组；NAD、PCI、长度、序号（4 位回绕）、N_Cr 检查；放弃原因与“放弃后不重启、收齐只交付一次”。
- 新增 `Services/LinBusScheduler.cs`：由 `LinFramePollingService` 演进为统一发送调度器（原文件已删除）。100 ms 应用拍保持先 0x15 后 0x33；按单帧边界暂停并保存游标；3D 按 t0 + n×20 ms 节拍发送、忙则跳拍不补发；队列清空后从游标立即恢复并衔接整拍。
- 新增 `Services/DiagnosticTransactionService.cs`：全局 FIFO 串行执行、3C 回显逐字节核对、t0 与 t0+5 s 截止、TP 放弃原因保存、断线清空队列、统一收尾（停止新 3D → 清理 → 等待在途写入）。
- 新增 `Services/DeviceInformationService.cs`：三个版本 DID 映射、正响应前缀校验、ASCII 解释（长度由 FF 声明动态确定）。
- `LinFrameCodec` 增加 3C 请求构帧与经典校验（不以 PID 为种子）；`LinReadReplyParser` 增加诊断候选识别（增强解析失败后按经典校验回退，9 字节同为应用/诊断候选长度时不会互相误判）；`LinFrameChannel` 增加 3C/3D 发送（返回写入回执）与诊断候选事件；`CommunicationLogEntry` 增加“诊断请求/诊断数据”显示分支（诊断回包不显示反算 PID/ID）。
- 帧表登记 `0x3C`（主发、8 字节、经典）与 `0x3D`（从发、8 字节、经典）。
- 界面：三个版本读取按钮启用（电压/温度参数仍禁用），诊断进行中禁用电机操作并保留 0x33 显示；“初始化步数”行改为“Bootloader版本”。
- 删除未接线的 `ApplicationFrameService`，避免遗留可绕过诊断独占的应用发送路径。
- 验证：隔离输出构建通过，0 个警告、0 个错误；31 项隔离检查通过（三个请求字节 55 AA 55 3C 08 … DA/EF/DC、3D 读取 55 AA 55 7D 00、固件样例 7 段重组与 ASCII、回显逐字节核对、TP 异常输入、应用/诊断候选互不误判、报文窗口显示）；尚未实机联调。

## 2026-10-15

### 文档整理：协议文档合并

- `PROTOCOL.md` 重写为唯一现行协议文档，合并原读取/写入两份确认记录的全部现行内容；删除 2026-10-10 历史实现说明与旧协议残留。
- 删除 `LIN_APPLICATION_READ.md`、`LIN_APPLICATION_WRITE.md`；代码注释中的引用同步改为 `PROTOCOL.md` 章节号，并顺带修正 `MainWindowViewModel` 中一处过时的 Checksum 种子注释。
- `VERSION_HISTORY.md` 精简为版本标签清单，移除旧协议回退操作指引。
- 本 CHANGELOG 旧协议时期（2026-10-10 之前）条目压缩为摘要。

### 执行器调试界面标题红色状态文字移除（UI）

- 删除"执行器调试"标题下方的红色提示行；`ProtocolStatusText` 属性保留（"读取设备信息"提示通知复用）。

### 报文输出窗口：横向滚动条固定 + 新增"暂停显示"（UI）

- 现象：串口连接后拖动报文列表横向滚动条，新报文到达时总会被重置回最左侧。
- 原因：每批新行到达时自动 `ScrollToEnd()`，且虚拟化容器回收测量期间横向偏移可能被清零。
- `ProtocolLogWindow.xaml.cs`：记录 `userHorizontalOffset`，自动滚动到底后恢复，横向位置不再跳动。
- 新增"暂停显示"按钮（"清空"左侧）：暂停期间新报文只进缓冲、列表冻结，后台日志不受影响；恢复时补入缓冲条目。`ListCollectionView` 改绑镜像集合 `displayedEntries` 实现真正冻结。
- 验证：隔离输出构建通过，0 个警告、0 个错误。

### 电机控制按钮连接状态刷新修复（UI）

- 原因：MVVM Toolkit 对属性形式的 `CanExecute` 不自动接线 `NotifyCanExecuteChanged`，连接后按钮要等 WPF `RequerySuggested` 才解禁。
- 修复：连接状态变化时显式调用四个电机控制命令的 `NotifyCanExecuteChanged()`。
- 验证：隔离输出构建通过，0 个警告、0 个错误。

### 所有应用帧均有回复：分发模型确认与组帧超时收紧为 50 ms

- 协议确认：读取与写入请求均会有回复；回复解析出 ID 后广播给订阅方，有回调则执行、无人认领仅记报文日志；接收解析不阻塞 100 ms 周期发送。
- `LinReadReplyParser`：不再要求"从节点发布"，`0x15` 回复同样被接受并分发；P0/P1 奇偶自洽保留。
- `LinFrameChannel`：组帧超时 1000 ms → **50 ms 字节间超时**（仅开始接收后计时），检查周期 10 ms。
- 验证：隔离输出构建通过；7 项隔离检查通过（0x33/0x15 回复接受、未登记 ID 拒绝、超时丢弃、清理后不污染下一帧、一拍两帧连续解析）。

### 写入帧 Checksum 种子修正为 PID（依据中间件源码）

- 依据中间件 `Lin_CheckPID` / `Lin_CheckSum` 源码：写入帧校验以 **PID（`0x55`）为种子**，与标准增强型校验一致；修正前按口头描述以原始 ID `0x15` 为种子，与中间件不符。
- 校验值变化：全 0 帧 CS `EA`→`AA`；12000 step 帧 `DA`→`9A`；初始化帧 `A7`。
- 验证：隔离输出构建通过；9 项隔离检查通过，中间件 C 函数原样移植后 200 组随机数据逐一比对一致。

## 2026-10-14

### 新增 ID `0x15` 写入应用帧并接入电机控制按钮

- 线上格式 `55 AA 55 PID 08 + 8 字节 DATA + Checksum`；新增 `MotorCommandService` 指令快照；轮询改为每 100 ms 一拍先写后读。
- 电机控制按钮启用（确定/CLOSE/OPEN1-3/初始化校准/STOP，可用条件为串口已连接）；"读取设备信息"仍禁用。
- 验证：隔离输出构建通过；16 项隔离检查通过。

### `0x33` 状态帧界面业务绑定

- `MotorStatusFrameParser` 解析回复为 `MotorStatusSnapshot`，绑定当前位置、正/负极限（驱动目标位置钳制）、运动/电压/温度状态显示。
- 验证：隔离输出构建通过；13 项隔离解析检查通过。

### 按新读取约定适配源码并接入 100 ms 轮询

- `CreateReadRequest`（5 字节 `55 AA 55 PID 00`）与 `TryRecoverProtectedId`（增强型校验逆运算）；新增 `LinReadReplyParser`（替代旧 `LinFrameParser`）与 `LinFramePollingService`；登记 `0x33`（8 字节、从节点、Enhanced）。
- 验证：隔离输出构建通过；17 项隔离协议检查通过。版本标签 `lin-id33-read-20261014`。

### 报文输出窗口打开时卡顿修复（性能）

- 批量追加改为逐条单元素 Add/Remove 事件做增量更新，不再每批发 Reset 触发全量重筛与整表重建；四维筛选选项重建合并调度。
- 验证：隔离输出构建通过，0 个警告、0 个错误。

### 其他（UI/文档）

- 执行器调试界面单位统一为 step，删除全部百分比内容。
- 报文窗口回复帧 PID/ID 取消"（恢复）"标注。
- 目标位置行显示正/负极限；启用"记忆当前位置"（仅保存本机配置）。
- 帧类别标注为 LIN 无条件帧、明确读取标识 `00` 含义（文档）。

## 2026-10-13

- 新增 `LIN_APPLICATION_READ.md`，确认 `0x33` 读取串口约定：发送 `55 AA 55 73 00`，回复 `DATA + Checksum`，反算 PID/ID 后按帧表分发。仅文档，未改代码。

## 2026-10-10

- 切换到 LIN 应用帧收发底层：移除旧 `CMD/SubCmd/LEN/XOR` 协议及其全部源码；新增 `LinFrameDefinition/Catalog/Codec/Channel` 与 `ApplicationFrameService`；报文日志改为 LIN 字段。版本标签 `lin-application-framework-20261010`，回退基线 `pre-lin-application-20261010`。
- 隔离协议检查 37 项通过；构建 0 警告 0 错误。

## 2026-10-10 之前（摘要）

- **2026-09-22**：修复日志窗口延迟刷新导致的 CollectionView 异常（`DeferRefresh` 长期持有问题）。
- **2026-09-20**：串口日志 UI 批处理（50 ms / 200 条）；日志窗口隐藏时暂停视图刷新；TX/RX 有序队列（容量 256）与连接代次标识。
- **2026-09-18**：应用图标；电机控制发送与当前位置同步（旧协议 `0x20/0x30` 命令，已随 LIN 迁移废弃）。
- **2026-09-17**：快捷位置"记忆当前位置"下拉；初始化校准入口；报文窗口字段细分、自动跟随、多条件过滤与级联联动。
- **2026-09-16**：独立报文输出窗口与共享通信日志服务。
- **2026-09-15**：电机预设位置与 `config/motor-presets.json` 本地配置；初版旧协议 `PROTOCOL.md`（已废弃）。
- **2026-09-01～02**：初版界面与 WPF + MahApps.Metro + MVVM 基础框架；串口设置与连接管理；顶部品牌标识。
