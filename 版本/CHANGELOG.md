# 修改记录

> 2026-10-26 超过 65535 的输入不再静默截断：目标位置输入框改绑原始文本 `ActuatorDebugViewModel.TargetPositionText`（原先直接绑 `int` 的 `TargetPositionSteps`，`UpdateSourceTrigger=PropertyChanged` 会把用户刚输入的 70000 立刻改写为 65535）。现在输入过程中不改写文本，只有落在 0～65535 的合法值才实时同步到目标位置并带动滑块；按“确定”时若超出 16 bit 上限、为负数或非数字，提示具体原因并**拒绝下发**（0x15 位置字段只有 2 字节，硬发会让 70000 回绕成 4464 这类完全不同的位置）。`MotorPresetViewModel` 同样改绑 `PositionText`，`SavePresetConfiguration` 先逐项校验，非法输入在原有红色提示区报“OPEN1：位置 70000 超过 16 bit 上限 65535 step。”并阻止写入。仓库外 23 项离线检查（VM + 真实命令执行）全部通过，构建 0 警告 0 错误；`架构/ARCHITECTURE.md` §3.1、§4.5.2 已同步。
>
> 2026-10-26 目标位置范围容错 + 全局异常兜底（修 `'65312' cannot be greater than 1000` 闪退）：`ActuatorDebugViewModel` 新增 `MaxTargetPositionSteps = 65535`，输入框只按 16 bit 协议上限收敛，不再被设备回报的行程截断，超出行程只在下发提示里注明；滑块改用 `SliderMinimumSteps` / `SliderMaximumSteps` / `TargetPositionSliderValue` 三个规范化属性（恒满足 min ≤ max、长度 ≥ 1，滑块值只做显示收敛、**不回写**目标位置）；`ApplyMotorStatus` 先判区间是否可用——负极限 > 正极限（如 -224 按补码回报成 `0xFF20` = 65312）时负极限退回 0、行程取正极限，正极限为 0 时回落默认行程 12000。`MotorPresetViewModel` 按 65535 钳制，避免行程读数异常时把已保存预设改小。新增 `Services/CrashLog.cs` 与 `App.OnStartup` 的 `DispatcherUnhandledException` / `AppDomain.UnhandledException` / `TaskScheduler.UnobservedTaskException` 钩子：异常写入 `%LOCALAPPDATA%\HolyChipMotorStudio\logs\crash-yyyyMMdd.log`，界面线程异常记录后继续运行且只提示一次。仓库外 17 项离线检查（含 500 次随机状态回放）全部通过，构建 0 警告 0 错误；`架构/ARCHITECTURE.md` §3.1、§4.5.2、§7(9) 已同步。
>
> 2026-10-23 标题品牌名统一：`MainWindow.xaml` 的 `Title` 改为 `HolyChip Motor Studio`（原 `Holychip Motor Studio`），并新增 `TitleCharacterCasing="Normal"`——MahApps `MetroWindow` 默认 `Upper`，会把标题栏文字渲染成全大写（此前显示为 `HOLYCHIP MOTOR STUDIO`）。标题模板仍绑 `{Binding}` 到 `Title`，标题栏与任务栏、Alt+Tab 共用同一字符串；`架构/ARCHITECTURE.md` 标题同步为 `HolyChip`。构建 0 警告 0 错误，已启动程序截图核对标题栏显示为 “HolyChip Motor Studio”。
>
> 2026-10-23 复位后确认 APP 版本：`OtaUpgradeService` 新增 `ResetVersionDelay = 50 ms`，第十五步收到 `51 01` 后先 `Task.Delay(50 ms)` 再调用 `DeviceInformationService.ReadVersionAsync(Firmware)` 读一次 `22 F1 95`；成功时写 `[OTA-15] 复位后等待 50 ms 读取 APP 版本成功：<版本>`，并存入 `OtaResetOutcome.AppVersionAfterReset`（新增可选参数与 `AppVersionAfterResetText`），完成状态行改为“升级完成，复位后 APP 版本 <版本>”；失败只记 Warning 与原始原因，**不判本次升级失败**、不重试、不重连，状态行回落到原文案。升级页状态文案：`EcuReset` 且 `AttemptNumber >= 2` 时显示“复位后确认 APP 版本”。不新增第十六步，`51 01` 仍不等于启动成功。构建 0 警告 0 错误，未实机验证。
>
> 2026-10-23 升级只读 APP 版本：`OtaUpgradeService` 新增实例属性 `ReadBootloaderAndHardwareVersions`（默认 `false`），第三、第四步（Bootloader 版本 `22 F1 80`、硬件版本 `22 F1 93`）整步跳过——不发送请求、不计失败、不写结果，只在升级日志用 `NoticeReported` 留一条 Info 提示；第二步（APP/软件版本）保持原判定，失败仍立即退出。`OtaStage` 编号与协议文档不变，属性设为 `true` 即可恢复三步读取（调试时可在即时窗口直接改）。升级页进度中 30%/36% 两档在默认配置下不命中，由 24% 直接进入再次确认的 36–42%。构建 0 警告 0 错误，未实机验证。
>
> 2026-10-23 错误 CRC 测试开关（临时）：`Models/FirmwareTransferPlan.cs` 新增 `public const uint Crc32TestXor = 0x00000000u`，`TryCreate` 的 CRC 复算改为 `FirmwareCrc32.Compute(data) ^ Crc32TestXor`。默认 `0` 与原有行为逐位一致（`x ^ 0 == x`），不改变任何协议字段；非 `0` 时只扰动**发给设备**的校验值（如 `0xFFFFFFFF` 取反、`0x00000001` 翻转最低位），界面「校验值」读的仍是加载时 `FirmwareImage.Crc32`，可与 `OTA-11`/`OTA-14` 日志的 CRC32 直接对照。用途是验证设备侧校验失败路径（`71 01 02 02 01` 或 `7F 31 xx`）。**测试完成后必须把该常量改回 `0`。**
>
> 2026-10-23 OTA 第十五步实现：新增 `Services/EcuResetService.cs`（两字节 `11 01`，只认恰好 `51 01`），`OtaStage` 增加 `EcuReset = 15`、`Completed` 顺延为 16，`OtaSessionResult` 增加 `ResetOutcome`；`OtaUpgradeService` 只在校验结果 00 之后发送复位，失败保留擦除/下载/校验结果且不重发；升级页补 `OTA-15` 标签、99% 进度与“十五步升级报文流程完成，设备已返回复位肯定响应”状态行。仓库外 33 项离线检查通过（连跑 5 次一致），实机未验证。
>
> 2026-10-23 升级日志精简：成功后不再追加 6 条汇总日志（三版本/会话摘要、擦除请求摘要、下载与结束传输摘要、校验结果、待确认项各 1 条，加上完成阶段的整段总结）。`FirmwareUpgradeViewModel.ApplyOtaResult` 成功分支只置进度 100% 与状态文案，`ApplyOtaStep` 对 `OtaStage.Completed` 只推进度不写日志；`OtaUpgradeService` 的完成上报精简为“第一至第十四步完成。”仅作进度节点。各步骤结果仍由 `OTA-1`…`OTA-14` 逐条记录，失败日志不变。构建 0 警告 0 错误。
>
> 2026-10-23 现场测试临时关闭应用帧：`LinBusScheduler` 新增公开开关 `ApplicationFramesEnabled`（默认 `true`；为 `false` 时连接成功也不启动应用拍，`RunAppBeatAsync` 在帧边界退出，退出诊断后也不恢复），组合根用 `MainWindowViewModel.EnableApplicationFrames` 常量给出初值，本轮设为 `false`。诊断独占、3D 轮询、OTA 第一至第十四步均不受影响。仓库外 7 项离线检查通过（关闭时不发帧、打开后按 0x15→0x33→0x34 恢复、再次关闭不增长、关闭状态可正常进出诊断）。**测试完成后把该常量改回 `true` 并重新编译即可恢复，无需其他改动。**
>
> 2026-10-23 OTA 第十二步帧间隔：实机两次失败均为块内随机丢回显（块 3 首帧、块 13 第 6 帧），`FirmwareDownloadService` 把 `36` 的片间最小间隔固定为 `FrameGap = 20 ms`（`MultiFrameOptions` 显式传入），`OtaUpgradeService` 保留块间 `BlockInterval = 10 ms`；块周期约 325 → 500 ms、183 块总时长约 60 → 90 s，块号推进与失败语义不变；构建 0 警告 0 错误，实机未复测。
>
> 2026-10-23 OTA 第十一至第十四步实现：新增 `Models/FirmwareTransferPlan.cs`（复用擦除范围的参数快照，数据副本 + CRC32 复算）、`Services/FirmwareDownloadService.cs`（`34 00 44`→`74` 块长解析、`36`→`76` 块号核对、`37`→`77`）、`Services/FirmwareVerificationService.cs`（`31 01 02 02` + 地址/大小/CRC32 → `71 01 02 02 Result`），`FirmwareCrc32` 移入 `Models/`；`OtaStage` 增加 `DownloadRequest=11`、`DataTransfer=12`、`TransferExit=13`、`Verification=14`、`Completed=15`；仓库外 82 项离线检查通过。
>
> 2026-10-23 OTA 第十步擦除实现：新增 `Models/ErasePlan.cs`（起始地址/长度取自固化映像，BIN 拒绝）与 `Services/EraseFlashService.cs`（`31 01 FF 00` + 地址 + 长度，12 字节多帧），`OtaStage` 增加 `Erase = 10`、`Completed` 顺延为 11，`RunAsync` 改为接收 `FirmwareImage`；`71 01 FF 00` 只记“请求被接受”，擦除完成判据待设备契约确认；仓库外 50 项离线检查通过。
>
> 2026-10-23 报文输出窗口“保存数据”：新增 `CommunicationLogExcelExporter`（手写最小 OOXML 包，无第三方依赖）把当前筛选结果导出为 `.xlsx`，按钮并入筛选工具条右端（不再单独占行，并修掉按钮四周的黑色描边），成功只写底部状态行；openpyxl 结构校验通过。
>
> 2026-10-23 NRC 78 响应待定实现：协调层不再把有效 `7F 原SID 78` 当失败结束本笔，改为按接收时刻续期响应窗口、重新 `BeginReceive` 继续接收最终响应，并联动 `LinBusScheduler.ExtendResponsePollingDeadline` 延长 3D 轮询；升级页经 `ResponsePendingReceived` 写“设备处理中”提示。仓库外 22 项离线检查通过；`架构/OTA升级流程.md` §6.5/§6.9 已同步。
>
> 2026-10-23 OTA 第八、九步实现：新增 `SecurityAccessService`（`27 09` 请求四字节 Seed、`27 0A` 发送按 `FLB_SeedToKey` 原式算出的 Key，高字节先发送），`OtaStage` 增加 `SeedRequest = 8`、`KeySend = 9`、`Completed` 顺延为 10，固件页补 `OTA-8`/`OTA-9`；全零 Seed 当前按“记日志退出、不发 Key”处理（待确认）；仓库外 46 项离线检查通过。
>
> 2026-10-23 OTA 第七步实现：`DiagnosticSessionService` 会话方法参数化并新增 `10 02` 编程会话（`EnterProgrammingSessionAsync`），`OtaStage.ProgrammingSession = 7`、`Completed` 顺延为 8，固件页补 `OTA-7` 与 96%/100% 进度；仓库外 31 项离线检查通过。同日文档细化第七步并补充第八、九步（`27 09`/`27 0A`）设计，安全访问尚未编码。
>
> 2026-10-22 OTA 第五、六步实现：第五步复用第一步探测机制（目标连续两次成功、计数独立初始化），第六步新增 `10 03` 扩展诊断会话业务；协调层新增 `7F` 负响应结构化分类（原 SID/NRC/原始 UDS + 中文映射），非 78 负响应写入升级日志后退出；详见本文 2026-10-22 条目。
>
> 2026-10-22 OTA 前四步实现：按 `架构/OTA升级流程.md` 实现 TesterPresent 连续在线确认（连续三次成功/累计两次失败退出/间隔 1 s）与软件、Bootloader、硬件版本读取（任一失败立即退出），新增 OTA 独占保持，固件页“开始升级”接入真实流程；详见本文 2026-10-22 条目。
>
> 2026-10-19 诊断传输能力：按《3C连续帧功能实施计划.md》实现 `0x3C` 多帧请求（FF+CF）、逐片回显确认、发送/响应双截止、候选归属判定、跨事务门禁、取消与进度契约，并完成同源 TimeProvider 改造；详见本文 2026-10-19 条目。
>
> 2026-10-18 诊断第一阶段：按 `WpfApp1诊断架构讨论记录.md` 实现三个设备版本读取（22 F1 80 / F1 95 / F1 93，NAD 0x78），调度采用“暂停应用通信”模式；详见本文 2026-10-18 条目。
>
> 2026-10-15 文档整理：协议文档合并为 [PROTOCOL.md](../协议/PROTOCOL.md)（原 `LIN_APPLICATION_READ.md`、`LIN_APPLICATION_WRITE.md` 已并入）；早期 `AA 55 / CMD / SUB_CMD / LEN / XOR` 旧协议时期的详细记录已压缩为摘要，Git 历史可查完整内容。

## 2026-10-23

### OTA 第十五步：设备复位（`11 01` / `51 01`）

**改动**

- 新增 `Services/EcuResetService.cs`（135 行）：`ResetAsync` 构造两字节 `11 01`（SID + 子功能 01），`Timing = DiagnosticTiming.Default`（t0 + 20 ms 首拍、20 ms 节拍、回显 1000 ms、总截止 5 s）；`EcuResetResult` 保留请求字节、原始负响应与补充说明。只接受完整 2 字节 `51 01`，`51 02` 等其他子功能或错误长度均判失败；`7F 11 78` 的续期与继续 3D 由协调层负责，本类不重发复位。
- `Models/OtaSession.cs`（270 → 300 行）：`OtaStage` 增加 `EcuReset = 15`，`Completed` 顺延为 16；新增 `OtaResetOutcome`（记录 `51 01` 原始响应）；`OtaSessionResult` 增加 `ResetOutcome` 参数与属性，`Succeeded` / `Failed` 同步扩展。
- `Services/OtaUpgradeService.cs`（774 → 807 行）：构造函数增加 `EcuResetService`；第十四步成功后进入第十五步，失败上报保留 `eraseOutcome`、`downloadOutcome`、`verificationOutcome`；`Report(OtaStage.Completed, …)` 文案改为“十五步升级报文流程完成（设备已返回复位肯定响应）”。
- `ViewModels/MainWindowViewModel.cs`（173 行）：新增并注入 `EcuReset`。
- `ViewModels/FirmwareUpgradeViewModel.cs`（621 行）：新增 `OTA-15` 标签、第十五步状态文案、`EcuReset => 99` 进度；成功状态行改为“十五步升级报文流程完成，设备已返回复位肯定响应”。
- `Views/FirmwareUpgradeView.xaml`：进度说明改为“覆盖第一至第十五步；第十二步按设备已确认的字节数推进，新固件启动需实机验证”，操作行注释同步为“执行第一至第十五步”。

**行为变化**

- 升级流程由十四步延长为十五步：校验通过后自动发送一次 `11 01`，收到 `51 01` 才判定整轮成功；此前校验成功即为成功。
- 复位失败（非 78 负响应、`51 02`、超时、断线、取消）不重发、不自动重连、不补发 `10 01`，失败阶段记为第十五步，前序传输与校验结果保留。
- `51 01` 只表示设备接受复位请求：日志与界面文案都明确“不等于新固件已启动运行”，本轮不新增启动等待、探测或版本复核。
- 退出 OTA 独占后仍由既有机制恢复应用发送；设备复位期间恢复的 `0x15/0x33/0x34` 可能暂无应答，属预期行为。

**验证**

- `dotnet build` 0 警告 0 错误（输出到临时目录，未触碰运行中的 exe）。
- 仓库外临时工程 + 串口替身 **33 项全部通过、连跑 5 次一致**：阶段编号 15/16；完整流程中 `11 01` 位于最后的 `31 01 02 02` 之后且只发一次，整帧 `55 AA 55 3C 08 78 02 11 01 FF FF FF FF 73`，复位后仍有 3D 读取；校验结果 01 时不发复位；`7F 11 22`、`51 02`、无响应均判失败且不重发；`7F 11 78 → 51 01` 续收后成功；失败日志含步骤、NAD、原始 UDS 与 NRC。临时工程已删除。

**未做**

- 实机复位与真实总线时序、复位后应用帧的恢复表现、不同 Bootloader 的复位子功能约定、新固件启动验证（等待时长、探测方式、预期版本）均未做；不新增第十六步。

### OTA 第十一至第十四步：申请下载、逐块传输、结束传输与固件校验

**改动**

- 新增 `Models/FirmwareTransferPlan.cs`（151 行）：地址与长度直接取自第十步已提交的 `ErasePlan`，数据为加载时映像的副本，CRC32 按该副本用 `FirmwareCrc32` 复算；长度/起始地址与映像不一致时拒绝生成，保证 34/36/37/31 02 02 使用同一份固件快照。
- 新增 `Services/FirmwareDownloadService.cs`（446 行）：`RequestDownloadAsync` 构造 `34 00 44` + 大端地址 + 大端字节长度（11 字节多帧），解析 74 的长度格式高四位（1～4）、响应长度是否为 `2 + n`、块长是否 > 2 且 ≤ 4095，容量 `= min(块长 − 2, 128)`；`TransferDataAsync` 构造 `36 + 块号 + 数据`（长度 > 6 走多帧，末块很短按 SF），要求完整 `76 BSC` 且块号一致；`RequestTransferExitAsync` 发送单字节 `37`，要求完整 `77`。
- 新增 `Services/FirmwareVerificationService.cs`（196 行）：构造 16 字节 `31 01 02 02 + 地址 + 大小 + CRC32`（`WriteUInt32BigEndian`，高字节先发），只接受 5 字节 `71 01 02 02 Result`；00 成功、01 业务失败（`RoutineFailed`，不伪装为 NRC）、其他结果值按未知失败。
- `Models/FirmwareCrc32.cs`（43 行，新增）：由 `Services/FirmwareFileService.cs` 移出（仍为 `internal`），使参数模型可复算 CRC32 而不反向依赖服务层；`FirmwareFileService` 缩减为 100 行，算法与调用结果不变。
- `Models/OtaSession.cs`（175 → 270 行）：`OtaStage` 增加 `DownloadRequest = 11`、`DataTransfer = 12`、`TransferExit = 13`、`Verification = 14`，`Completed` 顺延为 15；新增 `OtaDownloadOutcome`、`OtaVerificationOutcome`，`OtaSessionResult` 增加 `DownloadOutcome` / `VerificationOutcome`；`OtaStepReport` 增加 `CompletedBytes` / `TotalBytes`（仅第十二步使用）。
- `Services/OtaUpgradeService.cs`（550 → 761 行）：构造函数增加 `FirmwareDownloadService`、`FirmwareVerificationService`；第十步后依次执行第十一至第十四步；第十二步按 `min(容量, 剩余)` 切片、偏移只在 76 确认后推进、块号 `unchecked + 1` 实现 FE FF 00 01 回绕，日志按 `max(1, 总块数/20)` 抽稀并携带已确认字节；`Report` 新增两个可选参数。
- `ViewModels/FirmwareUpgradeViewModel.cs`（591 → 654 行）：日志标签补 `OTA-11`…`OTA-14`，状态行补四步文案，第十二步显示块序号与字节进度；结果日志补下载块数/已确认字节/结束传输与校验结果，并把待确认项集中成一条。
- `ViewModels/MainWindowViewModel.cs`（151 → 160 行）：新增 `FirmwareDownload`、`FirmwareVerification` 并注入 `OtaUpgradeService`。

**行为变化**

- 第十步成功后，HEX 固件会先申请下载并按设备 74 返回的容量切片，每块等待匹配块号的 76 后才推进；全部块确认后发送 37 并要求恰好 77，最后发送 31 01 02 02 校验，结果 00 才算本次 OTA 成功。
- 块传输失败（76 块号不符、`7F 36 NRC`、超时）记录块号/偏移/长度/NRC 后退出，不发 37、不发校验、不推进已确认字节；不自动重传。
- 校验结果 01 与 `7F 31 NRC` 分开呈现：前者是肯定响应里的业务失败，日志明确“不是 NRC”，不自动重擦除/重传/复位。
- 擦除完成判据仍未确认：`CompletionConfirmed=false` 时按“设备已接受擦除请求”继续，并在第十一步日志中显式标注该前提。
- 进度条重新分配（探测 0–18、三版本 24/30/36、再次确认 39–42、会话 45/47、Seed 50、Key 53、擦除 56、下载申请 58、传输 58–96、结束 97、校验 98、完成 100）；仅显示变化，成功判据与计时未变。

**验证**

- `dotnet build` 0 警告 0 错误（输出到临时目录，未触碰运行中的 exe）。
- 仓库外临时工程（串口替身，34/74、36/76、37/77、31 02 02 全链路模拟）**82 项检查全部通过，连跑三次一致**：11 字节请求与 74 解析边界、130 字节 36 产生 22 片、末片 TP 填充、两层序号回绕（257 块末块 01）、末块缩短、设备侧重组字节一致、CRC32 高字节先发、76 错号与 NRC 73 失败路径、`7F 37 78 → 77` 与 `7F 31 78 → 00` 续期、BIN 零发送、前九步回归。
- 未做：实机抓包与真实 LIN 时序、设备 74 实际容量与 76 附加字段、Bootloader 侧 CRC32 参数一致性、重传/断点续传。

### OTA 第十二步：帧间隔（片间 20 ms、块间 10 ms）

**改动**

- `Services/FirmwareDownloadService.cs`（446 → 460 行）：新增 `FrameGap = 20 ms` 与静态 `TransferMultiFrameOptions`（`Send = DiagnosticSendTiming.Default with { MinimumInterFrameGap = FrameGap }`），`TransferDataAsync` 的 `36` 请求改用该选项；末块 UDS ≤ 6 字节仍走 SF，不受影响。
- `Services/OtaUpgradeService.cs`：新增公开常量 `BlockInterval`（10 ms，与 `ProbeInterval` 同风格）；第十二步 `while` 循环在计算本块长度前插入 `if (blockIndex > 0) await Task.Delay(BlockInterval, timeProvider, cancellationToken)`，即首块不等待、其余块在上一块 `76` 确认返回后先停 10 ms 再提交 `36`。
- `架构/OTA升级流程.md`：§12.4 增加片间隔与块间间隔两条，§12.6 记录报文依据、计算后的周期与验证状态。

**行为变化**

- 块内相邻 3C 由“回显一到就发”（实测 11–12 ms）改为**至少间隔 20 ms**（自上一片串口写入完成起算），单块周期由约 325 ms 升至约 500 ms，23,404 字节 / 183 块的理论总时长由约 60 s 升至约 90 s。
- 块与块之间保留 10 ms 固定间隔；块号推进、`76` 核对、78 续期、失败即退出不重传等语义均未改动；两个间隔都不计入 5 s 响应预算，也不恢复应用帧。
- 该 20 ms 只作用于 `36` 多帧请求，`34`（仅 2 片）、`37`、`31` 仍用默认 0 ms。

**验证**

- `dotnet build` 0 警告 0 错误（输出到临时目录，未触碰运行中的 exe）。
- 未做实机复测；原先 82 项离线检查不覆盖这两个等待，块传输其余断言不受影响。

### OTA 第十步：擦除（`31 01 FF 00` + 起始地址 + 字节长度）

**改动**

- 新增 `Models/ErasePlan.cs`（140 行）：擦除参数的不可变快照，保存 `StartAddress`、`Length`、来源格式与文件名；`ToUdsRequest()` 生成 `31 01 FF 00` + 大端四字节地址 + 大端四字节长度共 12 字节；`TryCreate(FirmwareImage)` 只做四条硬校验（BIN 拒绝、地址在 32 位内、长度大于 0 且可写入四字节、start+length 不越过 32 位空间），分区/页对齐等依赖契约的规则未实现。
- 新增 `Services/EraseFlashService.cs`（155 行）：`EraseAsync` 构造 12 字节请求并显式提供 `DiagnosticMultiFrameOptions`，预期前缀 `71 01 FF 00`；正响应长度不足或前缀不符返回 `ResponseMismatch`；`71 01 FF 00` 之后的状态记录原样保存在 `EraseResult.StateRecord`，不解释、不补 `00`。
- `Models/OtaSession.cs`（136 → 175 行）：`OtaStage` 增加 `Erase = 10`，`Completed` 顺延为 11；新增 `OtaEraseOutcome`（范围、状态记录、`RequestAccepted`、`CompletionConfirmed`），`OtaSessionResult` 增加 `EraseOutcome` 属性与对应工厂参数。
- `Services/OtaUpgradeService.cs`（479 → 550 行）：构造函数增加 `EraseFlashService`；`RunAsync` 签名改为 `(byte nad, FirmwareImage firmware, CancellationToken)`；第九步成功后执行第十步，参数不可用或请求失败都记 `OtaStage.Erase` 并退出；成功返回 `OtaEraseOutcome`，`CompletionConfirmed` 恒为 `false`。
- `ViewModels/FirmwareUpgradeViewModel.cs`（561 → 591 行）：新增 `currentImage` 保存加载时的 `FirmwareImage` 快照（重新打开/重置查看器时同步更新），`StartUpgradeAsync` 先确认快照存在再调用 `RunAsync`；日志标签增加 `OTA-10`，状态行“第十步 擦除 Flash”；结果日志写范围与“完成状态待确认”。进度沿用第九步的 99%，不改动前九步数值。
- `ViewModels/MainWindowViewModel.cs`（147 → 151 行）：新增 `EraseFlashService` 并注入 `OtaUpgradeService`。

**行为变化**

- 第九步成功后，HEX 固件会按归一化映像的起始地址（`BaseAddress`，即最低数据记录地址）与长度（`ImageSize`，含段间 0xFF 空洞）发送一条 12 字节擦除请求，由既有 TP 编码为 FF + 两条 CF，逐片回显、50 ms 隔离、3D 轮询与 `7F 31 78` 续期全部复用协调层。
- BIN 文件因缺少确定的烧录起始地址，在第十步拒绝生成请求并退出，**不发送任何 31 报文**；前九步不受影响。
- `71 01 FF 00` 只表示设备接受例程：结果与日志写明“请求已被设备接受”，`CompletionConfirmed` 为 false，不写“擦除完成”，也不追加 `31 03` 查询。

**验证**

- 主工程 `dotnet build` 0 警告 0 错误（输出到临时目录）。
- 仓库外临时工程 + 串口替身（按项目 LIN-TP 逐片回显、3D 时交付响应）共 50 项全部通过，连跑两次结果一致：12 字节 UDS 与图片一致、三片 PDU 与串口帧逐字节一致（CS `39/B2/C8`）、换 NAD 重算校验、四类参数拒绝且零发送、`71 01 FF 00` 与带状态记录、`7F 31 22` 负响应归类、`7F 31 78` 后续收且不重发、HEX 全流程（前九步无失败报告、第一步 3 次、第五步 2 次）、最后一笔请求为擦除三片、BIN 全流程第十步拒绝且无 31 请求。临时工程已删除。
- 未做实机抓包、设备真实返回与页对齐验证。

### 报文输出窗口：保存数据（导出 Excel）

**改动**

- 新增 `Services/CommunicationLogExcelExporter.cs`（473 行）：把报文列表写成 `.xlsx`。不引入第三方 Office 组件，按 SpreadsheetML 约定手写最小 OOXML 包（`[Content_Types].xml`、`_rels/.rels`、`xl/workbook.xml`、`xl/_rels/workbook.xml.rels`、`xl/styles.xml`、`xl/worksheets/sheet1.xml`），用 `ZipArchive` + `XmlWriter` 打包；13 列与界面列一一对应，时间带日期（`yyyy-MM-dd HH:mm:ss.fff`），全部单元格写 `inlineStr`，避免 Excel 把 `0x3C`、`E1 00` 之类文本推断成数字或日期；表头加粗 + 浅蓝底、列宽预设、冻结首行、`autoFilter`；目标目录不存在时自动创建。
- `ViewModels/ProtocolLogWindowViewModel.cs`（620 → 676 行）：新增 `SaveDataCommand`（导出当前**筛选后可见**集合的快照，`SaveFileDialog` 默认文件名 `报文_yyyyMMdd_HHmmss.xlsx`）、`StatusMessage`（底部状态行）。
- `Views/ProtocolLogWindow.xaml`（179 → 196 → 226 行）：蓝色主按钮“保存数据”（`#0078D4`、`FontSize 13`、`MinHeight 28`、`Padding 16,3`）**并入筛选工具条右端**——`DockPanel` 先声明 `DockPanel.Dock="Right"` 的按钮，筛选 `WrapPanel` 填充剩余宽度并在窄窗时自行换行；按钮比同行的下拉框（26 高）只高 2 px。底部状态行绑定 `StatusMessage`。
- 按钮样式收进窗口资源 `SaveDataButtonStyle`（派生自隐式 `Button` 样式）：显式 `BorderThickness="0"`、`FocusVisualStyle="{x:Null}"`，并覆盖 hover（`#106EBE`）、pressed（`#005A9E`）、disabled（`#A9C9E8`）三态。此前按钮独立成行时四周有 2 逻辑 px 纯黑描边，来源是默认按钮模板的边框与焦点视觉装饰，只改 `BorderBrush` 不生效，必须去掉边框厚度与焦点样式。
- 窗口 `Grid` 行定义由 6 行减为 5 行（标题 / 工具条 / 8 px 间隔 / 列表 / 状态行），表头总高从约 105 逻辑 px 回到约 28，列表可视高度恢复约 41 px。

**行为变化**

- 点击“保存数据”→ 保存对话框 → 写出 `.xlsx`；成功只在窗口底部状态行提示“已保存 N 条报文到 <路径>”，不弹模态框；失败弹错误框并保留原记录。
- 导出只读快照：不清空、不改动通信日志，不影响后台采集与后续记录；暂停显示时导出的即冻结画面中的条目；无数据时提示且不生成空文件。

**验证**

- 主工程 `dotnet build` 0 警告 0 错误（输出到临时目录，避免运行中的 exe 占用）。
- 仓库外临时工程引用主工程导出 7 条覆盖 TX 写入帧/读取请求/RX 诊断数据/回复帧/错误条目的样本（含 `&`、`<>`、`"` 转义与中文），用 openpyxl 读取校验：zip 完整、6 个 OOXML 部件齐全、`A1:M8`、表头与界面列一致、冻结 `A2`、`autoFilter` 正确、列宽与表头样式生效、全部单元格为文本类型、中文与特殊字符无乱码；空集合导出为仅表头的 `A1:M1`。临时工程与虚拟环境已删除。
- 未做实机/界面点击验证（需启动 WPF 应用）。
- 布局与样式用仓库外临时 WPF 工程加载真实 `ProtocolLogWindow` 验证（1120 与 768 两种窗口宽度，共 17 项）：按钮与首个筛选下拉框处于同一垂直带、右边缘与内容区右边界差 0.0 px、比下拉框高 2.0 px、窄窗时筛选换行但不压住按钮且按钮仍右对齐；`BorderThickness`、`FocusVisualStyle`、前景/背景色、模板边框容器厚度均为预期值。临时工程已删除。

### NRC 78 响应待定实现（`7F 原SID 78` 续期与续收）

**改动**

- `Services/DiagnosticTransactionService.cs`（2311 → 2420 行）：
  - 新增 `ResponsePendingNrc = 0x78` 与 `ActiveTransaction.ResponseWindow`（SF 取 `TotalDeadline`、多帧取 `ResponseBudget`）、`ResponsePendingCount`、`PendingNotice`。
  - `ApplyTpResult` 增加接收时间参数；负响应分类后先判 NRC：`78` 走 `AcceptResponsePendingLocked`，不 `CompleteLocked`、不写 `NegativeResponse`、不推进计数；非 `78` 仍立即按 `NegativeResponse` 失败。
  - `AcceptResponsePendingLocked`：仍活动且未核对最终正响应时，`ResponseDeadline = 接收时刻 + ResponseWindow`，立即 `transportProtocol.BeginReceive` 重新准备接收（TP 此前为 `Completed`），并调用调度器续期 3D；回显截止不刷新；不重发 3C、不新建事务。
  - 新增锁外触发的 `ResponsePendingReceived` 事件与 `RaiseResponsePending`：锁内只写 `PendingNotice`，`HandleDiagnosticPacket` 出锁后派发，订阅方异常不影响接收。
- `Services/LinBusScheduler.cs`（837 → 865 行）：新增 `ExtendResponsePollingDeadline(ownerToken, deadline)`，校验轮询属主后替换 `pollDeadline`、按原节拍续排；不重锚、不补发，原截止已停排时重新激活。
- `Models/Diagnostics.cs`（394 → 422 行）：新增 `DiagnosticResponsePendingNotice`（NAD、原 SID、NRC、次数、窗口长度、原始 UDS 文本）；更新 `DiagnosticNrcText` 中 `0x78` 的注释。
- `Services/OtaUpgradeService.cs`（450 → 479 行）：构造时订阅 `ResponsePendingReceived`，转为 `NoticeReported` 文本（含 `7F xx 78`、NRC 中文原因、次数、NAD、刷新窗口）；不改变进度与结果。
- `ViewModels/FirmwareUpgradeViewModel.cs`（547 → 561 行）：OTA 期间订阅/退订 `NoticeReported`，切回 UI 线程以 `Info` 级别写 `OTA` 日志。

**行为变化**

- 有效 `78` 不再导致 OTA 步骤失败：事务保持活动，3D 继续发送，等待窗口按每个有效 `78` 的接收时刻 + 5 s（默认配置）刷新，单笔总时长可超过原 5 s，不设绝对上限。
- 迟到 `78`（到达时已过当前截止）、错误 NAD/校验/长度/原 SID、回显缺失均不续期；最终正响应、非 `78` 负响应、超时、断线、取消仍只发布一次结果。

**验证**

- `dotnet build`（输出到临时目录，避免运行中的 exe 占用）0 警告 0 错误。
- 仓库外临时工程连跑两次 22 项全部通过：续期跨过原截止、无 78 超时、迟到/错误 SID/校验错误不续期、78 后非 78 负响应、回显窗口独立、同块 78 + 最终响应、连续两次 78 逐次续期、3D 续发与超时后停止、OTA 九步全流程及版本读取回归。
- 未验证：实机抓包与 LIN 时序、设备 78 实际频率与 `P2*` 取值、默认 5 s 下跨多窗口的长等待、断线/取消与 78 的组合。


### OTA 第八、九步实现（`27 09` 请求 Seed、`27 0A` 发送 Key）

**改动**

- 新增 `Services/SecurityAccessService.cs`（222 行）：`RequestSeedAsync` 请求 `27 09`，成功要求完整 UDS 恰为 6 字节（`67 09 + 四字节 Seed`），Seed 按报文顺序返回、不颠倒；`SendKeyAsync` 请求 `27 0A + 四字节 Key`（6 字节 UDS，仍为 SF），成功要求完整 UDS 恰为 2 字节（`67 0A`）；两者长度不符均返回 `ResponseMismatch` 并带实际/预期长度，设备负响应原样带回；`SeedToKey`（uint 返回值）与 `CreateKey`（线上四字节）为纯函数，严格按用户提供的 `FLB_SeedToKey` 原式与 mask `0x3B61B270` 计算，Key 高字节先发送、不使用宿主端序拷贝；`IsZeroSeed` 只做标记，不决定处理路径。
- `Services/OtaUpgradeService.cs`（371 → 450 行）：第七步成功后依次执行第八、九步，每次重新请求 Seed、不缓存；全零 Seed 当前按“记日志退出、不发送 Key”处理（§8.3 待确认）；第九步失败日志同时给出 Key 与负响应；负响应格式化由 `DescribeSessionFailure`/`SessionFailureSummary` 泛化为 `DescribeFailure`/`FailureSummary`，第六至第九步共用；新增 `FormatBytes` 统一 Seed/Key 十六进制显示；类注释、完成文案与 TODO 更新到第十步。
- `Models/OtaSession.cs`（130 → 136 行）：`OtaStage` 增加 `SeedRequest = 8`、`KeySend = 9`，`Completed` 由 8 顺延为 10。
- `ViewModels/MainWindowViewModel.cs`（142 → 147 行）：组合根新增 `SecurityAccessService` 并注入 `OtaUpgradeService`。
- `ViewModels/FirmwareUpgradeViewModel.cs`（538 → 547 行）：日志标签补 `OTA-8`/`OTA-9`；进度第六步 95%、第七步 96%、第八步 98%、第九步 99%、完成 100%；状态文案补“第八步 请求 Seed”“第九步 计算并发送 Key”，完成文案补安全访问通过。

**同步文档**

- `架构/OTA升级流程.md`：第八、九步改为“已实现”；§8.3 记录全零 Seed 的当前实现（不跳过、不发 Key、记日志退出）；§8.4 新增实现与验证状态；§9.4 改为实现结果；§9.5 待确认项更新；§9.6 新增实现与验证状态；文件头、总览表、第十步说明同步到第一至第九步。
- `架构/ARCHITECTURE.md`：功能矩阵、分层图、组合根与依赖树、行数表、§4.4.5 关联说明、新增 §4.4.6 `SecurityAccessService`、§4.5.5 升级页与 OTA 流程、长期清单同步。

**验证**

- `dotnet build`：0 警告 0 错误。
- 仓库外临时工程（`ProjectReference` 引主工程 + 串口/日志替身，真实 `LinBusScheduler`/`DiagnosticTransactionService`/`DiagnosticSessionService`/`SecurityAccessService`/`OtaUpgradeService`，连跑两次，用后已删）**46 项全部通过**：
  - 演算向量：`12 34 56 78` → `SeedToKey = 0x95450E0A`、`CreateKey = 95 45 0E 0A`；`A1 B2 C3 D4` → `0xA31DA70D`；`FF FF FF FF` → `4E D9 84 0F`；三字节 Seed 抛 `ArgumentException`。
  - 请求顺序 `3E 00×3 → 22 F1 95 → 22 F1 80 → 22 F1 93 → 3E 00×2 → 10 03 → 10 02 → 27 09 → 27 0A`；第九步请求 `78 06 27 0A 95 45 0E 0A`、CS `0x5D`；NAD `0x7A` 时 Key 报文 `27 0A A3 1D A7 0D`、CS `0xD8`。
  - Seed/Key 正响应长度错误均判失败且文案带实际/预期长度；`7F 27 22`、`7F 27 35`（密钥无效）、原 SID 不符的 `7F 10 22` 均按预期退出且不发送 `27 0A`；负响应只发布一次、日志含原始 UDS/NRC/NAD。
  - 全零 Seed 不发送 Key 并在文案中说明待确认；Key 无响应按 5 s 总截止失败；第七步失败不进入第八步；OTA 全程零应用帧。
- 未覆盖：实机抓包与 LIN 时序、设备是否接受该 Key、零 Seed 的实际含义、`NRC 0x78` 等待与续收、断线/取消专项（与既有步骤同一路径）。
- 未提交 git。

### OTA 第七步实现（`10 02` 进入编程会话）

**改动**

- `Services/DiagnosticSessionService.cs`（105 → 128 行）：抽出私有 `EnterSessionAsync(sessionType, nad, token)`，共用 `10 + 子功能` 请求、`50 + 子功能` 前缀与 6 字节长度判定；`EnterExtendedSessionAsync` 保留签名改为调用 `EnterSessionAsync(0x03, ...)`，新增 `EnterProgrammingSessionAsync`（`0x02`）；长度不符的补充说明带子功能（如 `10 02 正响应长度为 4 字节，预期 6 字节`）；后四字节 P2/P2* 仍不解析、不建属性、不改计时。
- `Models/OtaSession.cs`（121 → 130 行）：`OtaStage` 增加 `ProgrammingSession = 7`，`Completed` 由 7 顺延为 8；`OtaSessionResult.Failed` 在失败阶段为第七步时仍返回 `ExtendedSessionEntered = true`，失败只记 `FailedStage`，不回填前序步骤成果。
- `Services/OtaUpgradeService.cs`（343 → 371 行）：第六步成功后提交第七步；成功先上报 `ProgrammingSession` 再上报 `Completed`；失败日志为 `第七步：进入编程会话失败。设备负响应：7F 10 22；NRC=0x22，执行条件不满足；NAD=0x78。`；负响应格式化抽为 `DescribeSessionFailure` / `SessionFailureSummary`，第六、七步共用；类注释与 TODO 更新为第八步及后续。
- `ViewModels/FirmwareUpgradeViewModel.cs`（535 → 538 行）：日志标签补 `OTA-7`；进度第五步 90–94%、第六步由 100% 回退为 96%、第七步与完成 100%；状态文案补“第七步 进入编程会话”；成功日志补“已进入扩展诊断会话与编程会话”；命令注释更新到第一至第七步。

**同步文档**

- `架构/OTA升级流程.md`：第七步改为“已实现”，§7.7 落点、§7.8 验证清单增“本轮结果”列、§7.9 状态同步；同日文档另补充第八步 `27 09` 请求 Seed、第九步 `27 0A` 发送 Key 的设计与待确认项（未编码）。
- `架构/ARCHITECTURE.md`：功能矩阵、分层图、行数表、§4.4.5 会话业务、§4.5.5 升级页与 OTA 流程、长期清单同步到第一至第七步。

**验证**

- `dotnet build`：0 警告 0 错误。
- 仓库外临时工程（`ProjectReference` 引主工程 + 串口/日志替身，真实 `LinBusScheduler`/`DiagnosticTransactionService`/`DiagnosticSessionService`/`OtaUpgradeService`，连跑两次，用后已删）31 项全部通过：请求顺序 `3E 00×3 → 22 F1 95 → 22 F1 80 → 22 F1 93 → 3E 00×2 → 10 03 → 10 02`；第七步请求 PDU `78 02 10 02 FF FF FF FF`、CS `0x73`；`50 02` 成功、后四字节换成 `AA BB CC DD` 仍成功、4 字节响应失败、迟到 `50 03` 判失败；`7F 10 22` 只发布一次且日志含原始 UDS/NRC/NAD，`ExtendedSessionEntered` 仍为 true；第六步失败不提交 `10 02`；NAD `0x7A` 时 CS `0x71`；OTA 期间零应用帧。
- 未覆盖：实机抓包与 LIN 时序、设备接受 `10 02` 后的行为、`NRC 0x78` 等待、未知 NRC/原 SID 不符/超时/断线在第七步的专项用例（与第六步共用同一路径，未重跑既有 62 项）。
- 未提交 git。

## 2026-10-22

### OTA 升级流程第五、六步实现（按 `架构/OTA升级流程.md` §5、§6）

**新增**

- `Services/DiagnosticSessionService.cs`（105 行）：会话控制业务层。构造 `10 03`、预期前缀 `50 03`；成功要求完整 UDS 长度为 6 字节（`50 03 + P2(2) + P2*(2)`），后四字节**不解析、不建属性、不改计时**；长度不符返回 `ResponseMismatch` 并给出实际长度。含 `DiagnosticSessionResult`（成败、失败原因、负响应原始数据）。
- `Models/Diagnostics.cs`：新增 `DiagnosticNegativeResponse`（原请求 SID、NRC、原始 UDS，附 `RawUdsText` / `NrcText` / `Describe()`）、`DiagnosticNrcText`（11/12/13/21/22/24/31/33/35/36/37/70/73/78/7E 中文映射，未知值显示 `NRC=0xXX`）、`DiagnosticFailureReason.NegativeResponse`（追加在末尾，既有编号不变）；`DiagnosticResult` 增加 `NegativeResponse` 属性与可选构造参数（既有调用不变）；`DiagnosticFailureText` 补 `NegativeResponse => "设备返回负响应"`。

**改动**

- `DiagnosticTransactionService.ApplyTpResult`：完整 UDS 收齐后**先分类负响应**（长度恰为 3、首字节 `7F`、第二字节与本笔请求 SID 一致）→ 携带结构化数据返回 `NegativeResponse`；原 SID 不符或残缺的 `7F` 仍按 `ResponseMismatch`。批量策略把 `NegativeResponse` 与 `ResponseMismatch` 同列为"设备拒绝"，继续 FIFO、不关闭跨事务门禁。`CompleteLocked` 为唯一发布点，负响应数据随失败结果一起冻结。
- `DeviceInformationService`：`DeviceVersionResult` 携带负响应，`FailureText` 在原因后附带 `7F xx NRC` 与中文说明（既有非负响应路径文案不变）。
- `OtaUpgradeService`：`RunOnlineProbeAsync` 参数化（目标成功次数、阶段、显示名）；第四步成功后进入第五步（`RequiredSecondProbeSuccesses = 2`，累计失败上限与 1 s 间隔沿用第一步，计数独立初始化，与第四步之间不额外等待）；第五步通过后进入第六步，失败时日志输出 `第六步：进入扩展诊断会话失败。设备负响应：7F 10 22；NRC=0x22，执行条件不满足；NAD=0x78。`；取消时按当前阶段上报，不再固定报第一步。
- `Models/OtaSession.cs`：`OtaStage` 增加 `OnlineProbeAgain = 5`、`ExtendedSession = 6`，`Completed` 顺延为 7；`OtaSessionResult` 增加 `ExtendedSessionEntered`。
- `FirmwareUpgradeViewModel`：日志标签增加 `OTA-5`、`OTA-6`；进度改为第五步 90–94%、第六步及完成 100%；状态文案与成功日志同步到"第一至第六步"。
- `MainWindowViewModel`：组合根新增 `SessionControl`，`OtaUpgradeService` 构造注入。

**验证**

- `dotnet build` 0 警告 0 错误。
- 仓库外临时工程（ProjectReference + 串口替身，真实调度器/协调层/会话服务，用后已删）62 项检查全部通过：全流程请求顺序 `3E 00×3 → 22 F1 95 → 22 F1 80 → 22 F1 93 → 3E 00×2 → 10 03`、阶段上报顺序、第一/五步间隔 ≥ 990 ms、第四步到第五步 < 500 ms、第五步首败后重凑两连、第五步两败退出且不发 `10 03`、`7F 10 22` 只发布一次且日志含原始 UDS/NRC/NAD、`NRC 0x78` 仅文案映射不等待、正响应长度 4 字节与 `50 02` 均判失败、版本读取 `7F 22 31` 与第二步超时不再继续、OTA 期间无应用帧插入、门禁未被负响应关闭。
- 未覆盖：`78` 待定后的续收与等待、第五/六步断线取消专项、实机抓包与界面交互。未提交 git。

### OTA 升级流程第一至第四步实现（按 `架构/OTA升级流程.md`）

**新增**

- `Services/OtaUpgradeService.cs`（265 行）：OTA 编排服务。第一步 TesterPresent 连续在线确认（`3E 00` / 完整响应恰为 `7E 00`；连续三次成功通过、累计第二次失败退出、两笔间隔 1 s 从上一笔结果时刻起算、第三次成功后不额外等待）；第二至第四步依次读取软件（F1 95）、Bootloader（F1 80）、硬件（F1 93）版本，任一步失败立即退出、不重试不跳过。报文组装、回显核对、3D 轮询与超时全部复用 `DiagnosticTransactionService` / `DeviceInformationService`，不新增发送出口。
- `Models/OtaSession.cs`（109 行）：`OtaStage`（与文档步骤编号对应）、`OtaStepReport`（分步进展上报）、`OtaSessionResult`（结果与三个版本文本）。
- `Models/Diagnostics.cs` 新增 `DiagnosticFailureText`：失败原因界面文案的共享映射，`DeviceVersionResult.FailureText` 改为委托它，避免多处各写一套文本。
- `DiagnosticTransactionService` 新增 `BeginOtaExclusiveHold` / `EndOtaExclusiveHoldAsync`：OTA 独占保持——多笔事务之间队列暂时清空也不恢复应用帧，由 OTA 服务成对调用；释放时按既有策略退出诊断模式并恢复应用通信（断线不恢复）。既有单笔诊断行为不变。

**接入**

- 固件升级页“开始升级”命令执行 `OtaUpgradeService.RunAsync(nad)`：NAD 取页面输入框（默认 0x78）；分步进展经 `StepReported` 事件切回 UI 线程写入页内日志并驱动进度条（探测 0–45%、三个版本步骤 60/75/90%、完成 100%）；OTA 进行中禁止重复启动。
- `MainWindowViewModel` 组合根新增 `OtaUpgrade` 服务并注入固件页 VM。

**与文档的差异处理**

- 文档"完整 UDS 恰好为 `7E 00`"在 OTA 业务层判定（协调层仍按 `ExpectedResponsePrefix` 前缀匹配，共享逻辑未改动）。
- NAD 固定 `0x78` 改为取固件页输入框值（默认仍 0x78，与 `DeviceInformationService.DiagnosticNad` 同源）。
- 第五步及后续（固件下载、烧录、校验、复位）未定义，以 TODO 占位；进度条到 100% 只代表前四步完成。

**验证**

- `dotnet build` 0 警告 0 错误；启动 6 s 无异常。
- 临时控制台工程（ProjectReference 引主工程 + 串口替身模拟中间件回显与设备响应，真实系统时钟，用后已删）31 项检查全部通过：三连成功走完四步并读取三个版本、两笔间隔 ≥ 1 s 且第三次成功后不等待、累计两次失败退出（仅两笔、原因含回显超时）、失败一次后重新三连成功（共四笔）、第二步负响应立即退出（不发第三步）、OTA 全程独占期间应用帧计数为 0 且释放后恢复。
- 实机（真实串口与 LIN 时序、中间件真实行为）未验证；`StepReported` 的 UI 交互需人工观察确认。

### 固件升级页布局调整与升级操作入口

- 整页改为与执行器调试页同构的 `ScrollViewer` 布局，随滚轮上下滚动，不再局限于一屏。
- “文件内容”十六进制查看器加高至 520 固定高度，日志区加高至 300；两者内部滚动保留。
- 底部新增升级操作行：“配置 NAD…”（占位，记日志说明协议待接入）与“开始升级”（需已加载固件且串口已连接才可用；点击记录“传输尚未实现”警告日志）。
- `FirmwareUpgradeViewModel` 新增两个命令及可用性联动（固件加载状态与串口连接状态变化时刷新 `StartUpgradeCommand`）。
- 验证：`dotnet build` 0 警告 0 错误；启动确认 XAML 加载正常。NAD 配置与升级传输协议仍未确定，两个按钮当前均为占位。

### 文件内容移入独立窗口

- 主页面删除内联的“文件内容”查看器卡片（连带此前临时加的折叠按钮与 `IsViewerCollapsed` / `ToggleViewerCommand` 一并移除）。
- 工具行新增“文件内容”按钮，位于“打开固件文件…”之后；“打开固件文件…”左移（不再贴右边距，最小宽度 130）。
- 新增 `Views/FirmwareContentWindow.xaml(.cs)`：独立窗口，标题为“文件内容 - <文件名>”，`CenterOwner`、`ShowInTaskbar=False`；内容为列头 + 虚拟化列表（偏移/十六进制/ASCII）+ 复制按钮 + 空态；与主页面共享同一 `FirmwareUpgradeViewModel`，加载新文件时实时刷新。
- 生命周期：`ShowContentCommand` 以 `contentWindow` 字段跟踪实例，`Closed` 事件置空；窗口关闭即销毁，不隐藏、无后台驻留进程；已打开时重复点击只 `Activate()` 唤前台；按钮 `CanExecute = IsFirmwareLoaded`（未加载固件时禁用）。
- 验证：`dotnet build` 0 警告 0 错误；启动 6 s 无异常，无残留进程。窗口手动开合/重复点击的交互需人工观察确认。
- 独立窗口标题栏去掉最小化按钮：WPF 无对应属性，改为在 `SourceInitialized` 中调用 user32 `GetWindowLong/SetWindowLong` 清除 `WS_MINIMIZEBOX`（最大化与关闭保留）。
- 日志区标题栏新增“保存日志”按钮（“清空日志”左侧）：`SaveFileDialog` 默认名 `log_yyyyMMdd_HHmmss.log`；导出内容与界面展示一致（`HH:mm:ss [标签] 消息` 全量副本）；日志为空时弹窗提示且不生成文件；保存不清空、不改动现有日志。
- 升级操作行新增 NAD 输入框（位于“配置 NAD”“开始升级”按钮之前）：默认 `0x78`，支持 `0x78` / `78` 十六进制写法，限单字节 0x00–0xFF；非法或为空时回退默认值 0x78 并记 Warning 日志；“配置 NAD”按钮校验并把输入规范化为 `0xXX`，“开始升级”发送前同样校验。
- NAD 组装来源收口：`DeviceInformationService.ReadVersionAsync` 增加可选 `nad` 参数（空则回退 `DiagnosticNad` 0x78），服务层不再把硬编码作为唯一来源；执行器调试页版本读取调用不变（仍用默认 0x78），其他协议字段与发送流程未改动。OTA 升级传输接入后统一经 `FirmwareUpgradeViewModel.TryGetNad` 取值。
- 未提交 git。

## 2026-10-21

### 固件升级页：文件加载、信息展示与十六进制预览

**功能**

- 固件升级页新增"打开固件文件…"入口，支持 `.bin` 与 `.hex`（含大写扩展名）。
- 加载成功后在文件信息条展示：文件名（悬停显示完整路径）、格式、映像大小、CRC32 校验值、修改日期；HEX 额外展示地址范围与入口地址（查看器头部副标题与日志）。
- 新增十六进制内容预览：每行 16 字节，偏移地址（8 位十六进制）+ 十六进制 + ASCII（不可打印字符显示 `.`）；支持一键复制为纯文本。
- 页内日志区：打开/解析/截断警告/错误分级着色（Info/Success/Warning/Error），上限 500 条，可清空。
- 超过 1 MB 的映像只预览前 1 MB 并记 Warning，不生成全部 UI 行。

**架构（与界面解耦）**

- `Models/FirmwareImage.cs`：`FirmwareImage` / `IntelHexSummary` / `HexPreviewLine` / `FirmwareLogLevel` / `FirmwareLogEntry` 纯数据模型。
- `Services/FirmwareFileService.cs`：文件加载入口（扩展名校验、存在性检查、后台线程读取与解析、CRC32 计算），`FirmwareFileFormatException` 面向用户；BIN 整文件即映像（基地址 0）。
- `Services/IntelHexParser.cs`：Intel HEX 解析（记录类型 00/01/02/04/05，02 段地址×16、04 线性地址×65536、05 入口地址；未知类型忽略）；逐行校验（冒号/奇偶/十六进制/整行求和为 0/长度字段），失败抛带行号异常；归一化映像空洞填 0xFF 并统计；同地址重复写入以最后一条为准。
- `Services/FirmwareHexFormatter.cs`：预览行构建、大小/地址/十六进制流格式化、复制文本拼接。
- `FirmwareUpgradeViewModel` 只负责调用服务并映射为界面状态（`OpenFileDialog` 留在 VM，沿用项目 VM 依赖 System.Windows 的现状）；解析逻辑不在界面代码中。
- 视图沿用自包含样式组织方式；预览 `ListBox` 开启虚拟化 + 回收模式。

**尚未实现**

- 设备升级传输（烧录流程、进度条、结果反馈）未接入；进度区为占位。CRC32 当前仅用于展示校验值，未参与传输校验协议。

**验证**

- `dotnet build` 0 警告 0 错误；启动编译产物 6 秒无异常（XAML 运行时加载正常）。
- 临时控制台工程（链接主工程源文件，用后已删）57 项检查全部通过：CRC32 已知向量（"123456789" → 0xCBF43926）、HEX 04/02 地址扩展与空洞填充、重复写入覆盖、EOF 后冗余行忽略、CRLF 行尾、六类非法输入报错（缺冒号/校验和/非法字符/奇数字符/长度不一致/无数据记录）、BIN/HEX 文件加载路径、服务错误路径（文件不存在/扩展名不支持/空路径）、预览行数与文本格式、1 MB 截断上限。
- UI 交互（文件选择、预览滚动、复制）需人工观察确认。

### 新增"固件升级"页面（侧边栏第二项）

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
