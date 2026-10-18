# 修改记录

> 2026-10-18 诊断第一阶段：按 `WpfApp1诊断架构讨论记录.md` 实现三个设备版本读取（22 F1 80 / F1 95 / F1 93，NAD 0x78），调度采用“暂停应用通信”模式；详见本文 2026-10-18 条目。
>
> 2026-10-15 文档整理：协议文档合并为 [PROTOCOL.md](PROTOCOL.md)（原 `LIN_APPLICATION_READ.md`、`LIN_APPLICATION_WRITE.md` 已并入）；早期 `AA 55 / CMD / SUB_CMD / LEN / XOR` 旧协议时期的详细记录已压缩为摘要，Git 历史可查完整内容。

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
