# WpfApp1（Holychip Motor Studio）架构文档

- 文档日期：2026-10-20，2026-10-21 增补
- 2026-10-21 文档补充：诊断前“应用待回复标志 + 从触发起最多等待 100 ms”的收尾策略已接入 `LinBusScheduler`，见第 6.2 节及诊断架构讨论记录第 19 节。
- 2026-10-21 文档补充：新增“固件升级”页面（侧边栏第二项），导航与页面结构见第 4.5.1、4.5.5 节；并已实现固件文件加载（.bin/.hex）、信息展示与十六进制预览（服务层解析，与界面解耦），设备升级传输尚未实现，进度区为占位。
- 适用代码：`main` 分支，提交 `18f67a1`（`feat: support LIN 0x3C multi-frame diagnostics`）及其后工作区状态
- 文档性质：对**现有代码**的静态梳理与评价，不含未实现功能的承诺
- 依据材料：源代码全文、`../协议/PROTOCOL.md`、`../版本/CHANGELOG.md`、`../版本/VERSION_HISTORY.md`、`WpfApp1诊断架构讨论记录.md`、`../修复/` 目录三份设计与修复文档、`../入门/WPF_MahApps_MVVM_基础框架搭建.md`
- 构建状态：`dotnet build WpfApp1/WpfApp1.csproj` → 0 警告、0 错误（直接在 `bin` 输出时会因应用程序正在运行而报 MSB3027 文件占用，属环境问题，非代码问题）

> 阅读建议：只想改界面看第 3、4.5 节；改通信或新增帧看第 4.2～4.3、5.3～5.7、8 节；做重构或接手维护先读第 2、6、7 节。

---

## 1. 项目概览

### 1.1 定位

这是一个 Windows 桌面**上位机（HMI）程序**，通过串口连接一个 LIN 主节点中间件，再由中间件在 LIN 总线上与电机从节点通信：

```text
WPF 上位机 ──串口(COM)──> LIN 主节点中间件 ──LIN 总线──> 电机从节点
   │                          │
   │ 负责：应用帧业务、LIN 帧构帧/校验、PID 计算、
   │       UDS 诊断请求构造、LIN-TP 分包/重组、发送调度、时序与超时
   │ 负责：LIN 电气信号、单帧总线收发（不做诊断分包/重组）
```

关键边界（来自 `WpfApp1诊断架构讨论记录.md` §1）：

- 上位机自己完成 UDS 请求构造、SF/FF/CF 分包、响应重组、3C/3D 排程；
- 中间件只做 LIN 电气层与单帧收发，**不保证**自动分包/重组/连续读取；
- 串口写入成功 ≠ 中间件已上总线 ≠ 电机已动作，所有时序都基于"软件观察点"。

### 1.2 功能范围（当前实现）

| 功能 | 状态 | 入口 |
| --- | --- | --- |
| 串口枚举、参数配置、连接/断开、热插拔监测 | 已实现 | 串口设置 Flyout |
| 应用帧 `0x15` 控制写入（100 ms 周期） | 已实现 | 电机控制卡片 |
| 应用帧 `0x33` 状态读取（100 ms 周期）与界面绑定 | 已实现 | 执行器调试页 |
| 诊断 `0x3C/0x3D` 单帧（SF）版本读取（Bootloader/固件/硬件） | 已实现 | 读取设备信息卡片 |
| 诊断 `0x3C` 多帧（FF+CF）请求发送能力 | 已实现（无界面入口） | 仅 API |
| 报文输出窗口（收发报文、四维筛选、暂停、保留条数） | 已实现 | 底部状态栏 |
| 报文数据导出 Excel（`.xlsx`，导出当前筛选结果） | 已实现 | 报文输出窗口“保存数据” |
| 电机预设位置本地配置（`config/motor-presets.json`） | 已实现 | 统一配置 Flyout |
| 应用帧 `0x34` 温度/电压读取（100 ms 周期）与界面绑定 | 已实现 | 执行器调试页 |
| 固件升级页面框架（标题栏 + 串口区） | 已实现 | 固件升级页 |
| 固件文件加载（.bin/.hex）、信息展示与十六进制预览 | 已实现 | 固件升级页 |
| OTA 升级流程第一至第十五步（在线确认 + 仅读 APP 版本 + 再次确认 + 扩展/编程会话 + 安全访问 + 擦除 + 下载申请 + 逐块传输 + 结束传输 + 固件校验 + 设备复位） | 已实现（擦除仅 HEX；Bootloader/硬件版本默认跳过） | `OtaUpgradeService` + `DiagnosticSessionService` + `SecurityAccessService` + `EraseFlashService` + `FirmwareDownloadService` + `FirmwareVerificationService` + `EcuResetService` + 固件升级页 |
| 复位后的启动验证（新固件是否运行） | 未实现（有意不自动化） | 需实机确认探测方式、等待时间与预期版本；`51 01` 只证明设备接受复位 |
| UDS 负响应（`7F`）结构化解析与 NRC 文案 | 已实现 | `DiagnosticTransactionService` + `Models/Diagnostics.cs` |
| NRC `78` 响应待定：续期等待窗口、TP 续收、3D 截止联动 | 已实现（离线 22 项） | 协调层 + `LinBusScheduler.ExtendResponsePollingDeadline`，设计见 `架构/OTA升级流程.md` §6.5 |
| 设备运行参数写入（NAD、休眠、方向等） | **界面占位，无绑定** | — |

### 1.3 技术栈

| 项 | 版本/选择 | 说明 |
| --- | --- | --- |
| 运行时 | .NET 10（`net10.0-windows`） | `Nullable`、`ImplicitUsings` 均开启 |
| UI 框架 | WPF | `WinExe`，单窗口 + 一个独立工具窗口 |
| UI 皮肤 | MahApps.Metro 2.4.11 | `MetroWindow`、`HamburgerMenu`、`Flyout` |
| MVVM | CommunityToolkit.Mvvm 8.4.2 | `[ObservableProperty]`、`[RelayCommand]` 源生成器 |
| 串口 | System.IO.Ports 10.0.0 | `SerialPort` + 自建双队列 |
| 计时 | `TimeProvider` / `ITimer` | 全链路同源注入，便于虚拟时间测试 |
| 序列化 | `System.Text.Json` | 仅用于本地预设配置 |
| 依赖注入 | **无** | 手工在 `MainWindowViewModel` 组装 |
| 日志/持久化 | **无**（仅 `Debug.WriteLine`） | 报文日志只在内存 |
| 测试工程 | **无**（仓库内） | 验证依赖仓库外临时工程，用完即删 |

解决方案 `WpfApp1.slnx` 只含一个工程 `WpfApp1`，**单程序集**，按文件夹分层（`Models/ViewModels/Views/Services/Controls`）。

---

## 2. 整体架构

### 2.1 分层视图

```text
┌──────────────────────────────────────────────────────────────────────────┐
│ 表现层 (Views)                                                           │
│   MainWindow.xaml（MetroWindow + HamburgerMenu + Flyout + 状态栏）        │
│   ActuatorDebugView.xaml（执行器调试页，UserControl）                     │
│   FirmwareUpgradeView.xaml（固件升级页，UserControl：文件信息/日志/操作）  │
│   FirmwareContentWindow.xaml(.cs)（文件内容独立窗口，与主页面共享 VM）     │
│   ProtocolLogWindow.xaml（独立报文窗口）+ 少量 code-behind（滚动/窗口）    │
└───────────────────────────────┬──────────────────────────────────────────┘
                                │ Binding / Command
┌───────────────────────────────▼──────────────────────────────────────────┐
│ 视图模型层 (ViewModels)                                                  │
│   MainWindowViewModel      —— 组合根 + 导航 + 0x33 分发 + 生命周期        │
│   ActuatorDebugViewModel   —— 电机操作、状态显示、版本读取、预设配置      │
│   FirmwareUpgradeViewModel —— 固件加载命令、信息/预览映射、页内日志        │
│   SerialSettingsViewModel  —— 串口设置、连接、端口轮询                    │
│   ProtocolLogWindowViewModel —— 报文筛选/暂停/保留条数                    │
└───────────────────────────────┬──────────────────────────────────────────┘
                                │ 方法调用（VM 只依赖服务，不碰串口字节）
┌───────────────────────────────▼──────────────────────────────────────────┐
│ 应用业务层 (Application)                                                 │
│   DeviceInformationService —— 版本 DID 映射、UDS 请求构造、ASCII 解释      │
│   DiagnosticSessionService —— 会话控制（10 03/10 02）、正响应判定         │
│   SecurityAccessService   —— 安全访问（27 09/27 0A）、Key 计算            │
│   EraseFlashService       —— 擦除（31 01 FF 00 + 起始地址 + 长度）        │
│   FirmwareDownloadService —— 下载（34/74、36/76、37/77 构造与解析）       │
│   FirmwareVerificationService —— 固件校验（31 01 02 02 + CRC32 + 结果）   │
│   EcuResetService          —— 设备复位（11 01 → 51 01，SF、不重发）       │
│   OtaUpgradeService        —— OTA 流程编排（第一至第十五步，独占保持）    │
│   FirmwareFileService      —— 固件文件加载（BIN/HEX）、CRC32、后台解析    │
│   MotorCommandService      —— 0x15 指令快照（无发送能力）                 │
│   MotorStatusFrameParser   —— 0x33 DATA → MotorStatusSnapshot            │
│   MotorPresetStorageService—— 本地 JSON 配置读写（原子替换）              │
└───────────────────────────────┬──────────────────────────────────────────┘
                                │
┌───────────────────────────────▼──────────────────────────────────────────┐
│ 协议/调度层 (Protocol & Scheduling)                                      │
│   DiagnosticTransactionService —— 诊断 FIFO、回显核对、截止、门禁、收尾    │
│   LinTransportProtocol         —— LIN-TP：SF/FF/CF 编码与重组、N_Cr        │
│   LinBusScheduler              —— 唯一发送出口、100 ms 应用拍、20 ms 3D 拍 │
└───────────────────────────────┬──────────────────────────────────────────┘
                                │
┌───────────────────────────────▼──────────────────────────────────────────┐
│ 帧适配层 (Frame Adapter)                                                 │
│   LinFrameChannel     —— 构帧/发送入口、接收分发、日志、Dispose 边界      │
│   LinReadReplyParser  —— 单一字节缓存、候选识别、反算 PID、重同步        │
│   LinFrameCodec       —— PID/Checksum（增强/经典）、读取/写入/诊断构帧    │
│   LinFrameCatalog     —— 帧表（ID/长度/发布节点/校验类型）                │
└───────────────────────────────┬──────────────────────────────────────────┘
                                │
┌───────────────────────────────▼──────────────────────────────────────────┐
│ 基础设施层 (Infrastructure)                                              │
│   SerialPortConnectionService —— 连接生命周期、TX/RX 有界队列、代次保护   │
│   SerialPortDiscoveryService  —— 端口枚举（隔离 System.IO.Ports）         │
│   CommunicationLogService     —— 报文汇集、50 ms/200 条批量、保留策略     │
└──────────────────────────────────────────────────────────────────────────┘
```

### 2.2 分层职责与"不做什么"

| 层 | 拥有的可变状态 | 明确不承担 |
| --- | --- | --- |
| 表现层 | 界面控件状态、Flyout 开关 | 不解析字节、不计时、不持有缓冲 |
| 视图模型 | 显示用属性、命令可用性 | 不组帧、不管发送节拍、不存 TP 缓冲 |
| 应用业务 | 指令快照、DID 映射、预设配置 | 不做分包、不判 CF 序号、不排程 |
| 协议/调度 | 队列、序列索引、截止时间、TP 状态、门禁 | 不解释版本字符串、不直接操作 `SerialPort` |
| 帧适配 | 共享接收缓存、帧表、日志入口 | 不决定诊断 3C/3D 归属（交由协调层上下文） |
| 基础设施 | 串口句柄、队列、连接代次 | 不含 DID/TP/诊断排队语义 |

这套划分在 `WpfApp1诊断架构讨论记录.md` §11 中被称为"三层状态机"（总线调度 / 诊断业务 / TP 接收），代码严格按此落地：**每份可变状态只由所属模块修改**，其余层通过事件或返回值驱动。

### 2.3 依赖方向与硬性约束

```text
View ──> ViewModel ──> 业务服务 ──> 调度/协调 ──> 帧通道 ──> 串口服务
                                        │
                                        └──> LinTransportProtocol（纯内存，无 IO）
接收方向：串口服务 ──事件──> 帧通道 ──事件──> 协调层 / ViewModel
```

必须遵守的约束（破坏其中任一条都会引入难以定位的时序缺陷）：

1. **单一发送出口**：任何帧（应用/诊断）都必须经 `LinBusScheduler` 发出，不允许业务直接调 `LinFrameChannel.Send*Async` 绕过诊断独占。历史上直接调用通道的 `ApplicationFrameService` 已被删除（CHANGELOG 2026-10-18）。
2. **锁顺序单向**：协调层锁 → 调度器锁 → 通道锁（叶子）。接收回调不得在通道锁内触发，`LinFrameChannel.HandleBytesReceived` 因此只在锁内读状态、锁外 `parser.Append`。
3. **诊断候选不虚构 PID**：8 字节 PDU + 经典校验的回包没有帧头，入口只输出"候选"，3C 回显/3D 响应的归属由当前事务上下文判定。
4. **结果只发布一次**：`CompleteLocked` 是唯一发布点，`PublishedResult` 冻结后不再变更。
5. **串口写入 ≠ 总线完成**：`FrameWriteReceipt` 只表示"串口写完成"，命名与语义都不允许被当成电机执行成功。

### 2.4 组合根与对象图

没有 DI 容器，`MainWindowViewModel` 构造函数就是唯一的组合根（`MainWindowViewModel.cs:13-61`），创建顺序即依赖顺序：

```text
CommunicationLogService(Application.Current.Dispatcher)
SerialPortConnectionService()
SerialSettingsViewModel(SerialPortDiscoveryService, connectionService)
LinFrameCatalog([0x33, 0x15, 0x3C, 0x3D])
LinFrameChannel(connectionService, CommunicationLog, Catalog, TimeProvider.System)
MotorCommandService()
LinBusScheduler(FrameChannel, MotorCommands, TimeProvider.System)
DiagnosticTransactionService(FrameChannel, BusScheduler, TimeProvider.System)
DeviceInformationService(Diagnostics)
DiagnosticSessionService(Diagnostics)
SecurityAccessService(Diagnostics)
EraseFlashService(Diagnostics)
FirmwareDownloadService(Diagnostics)
FirmwareVerificationService(Diagnostics)
EcuResetService(Diagnostics)
OtaUpgradeService(Diagnostics, DeviceInfo, SessionControl, SecurityAccess, EraseFlash, FirmwareDownload, FirmwareVerification, EcuReset, TimeProvider.System)
ActuatorDebugViewModel(SerialSettings, MotorCommands, DeviceInfo, Diagnostics)
FirmwareUpgradeViewModel(SerialSettings, OtaUpgrade)   // 与调试页共用同一 SerialSettings；内部 new FirmwareFileService（可注入替换）
```

对象图（→ 表示持有引用）：

```text
MainWindow
 └─ MainWindowViewModel
     ├─ CommunicationLogService ──> Dispatcher（WPF 依赖）
     ├─ SerialPortConnectionService ──> SerialPort, RX/TX 队列
     ├─ SerialSettingsViewModel ──> DiscoveryService, ConnectionService
     ├─ LinFrameChannel ──> ConnectionService, Log, Catalog, LinReadReplyParser
     ├─ MotorCommandService
     ├─ LinBusScheduler ──> Channel, MotorCommands
     ├─ DiagnosticTransactionService ──> Channel, Scheduler, LinTransportProtocol
     ├─ DeviceInformationService ──> Diagnostics
     ├─ DiagnosticSessionService ──> Diagnostics
     ├─ SecurityAccessService ──> Diagnostics
     ├─ EraseFlashService ──> Diagnostics
     ├─ FirmwareDownloadService ──> Diagnostics
     ├─ FirmwareVerificationService ──> Diagnostics
     ├─ EcuResetService ──> Diagnostics
     ├─ OtaUpgradeService ──> Diagnostics, DeviceInfo, SessionControl, SecurityAccess, EraseFlash,
     │                        FirmwareDownload, FirmwareVerification, EcuReset
     └─ ActuatorDebugViewModel ──> SerialSettings, MotorCommands, DeviceInfo, Diagnostics,
                                    MotorPresetStorageService（内部 new）
     └─ FirmwareUpgradeViewModel ──> SerialSettings（与调试页同一实例）、FirmwareFileService（内部 new）
                                    ──> IntelHexParser / FirmwareHexFormatter（静态纯函数）
```

反向依赖只有事件订阅：

- `Channel.FrameReceived` → `MainWindowViewModel.HandleStatusFrameReceived`（0x33）
- `Channel.ConnectionStateChanged` → Scheduler / Diagnostics
- `Channel.DiagnosticPacketReceived` → Diagnostics
- `Scheduler.DiagnosticReadAttemptStarting/Committed/AttemptFailed` → Diagnostics
- `Diagnostics.DiagnosticActivityChanged` → ActuatorDebugViewModel（按钮禁用）
- `SerialSettingsViewModel.PropertyChanged(IsConnected)` → ActuatorDebugViewModel

释放顺序（`MainWindowViewModel.Dispose`）：先解事件 → `ActuatorDebug` → `Diagnostics` → `BusScheduler` → `FrameChannel` → `SerialSettings` → `connectionService`，即**从上层往下层拆**，与创建顺序相反。

---

## 3. 目录与文件职责

```text
WpfApp1/
├─ App.xaml(.cs)              Application 定义（无启动逻辑、无全局异常处理）
├─ MainWindow.xaml(.cs)       MetroWindow 外壳；导航、两个 Flyout、状态栏；
│                             code-behind 仅处理菜单动画与报文窗口打开
├─ Models/                    纯数据：帧定义、诊断契约、状态快照、日志条目
├─ Services/                  全部业务与通信实现（本项目的主体，约 5.2k 行）
├─ ViewModels/                7 个 VM 文件（2 个页面 VM、菜单项/预设项、日志窗口 VM 等）
├─ Views/                     ActuatorDebugView、FirmwareUpgradeView(UserControl)、ProtocolLogWindow(Window)
├─ Controls/                  HamburgerMenuItemTemplateSelector（当前未被 XAML 引用）
└─ Resources/                 app.ico、logo-transparent.png
```

### 3.1 文件清单与规模（便于定位）

| 文件 | 行数 | 角色 |
| --- | ---: | --- |
| `Services/DiagnosticTransactionService.cs` | 2420 | 诊断事务协调层（最复杂，改动风险最高；含 OTA 独占保持、负响应分类与 NRC 78 续期） |
| `Services/LinBusScheduler.cs` | 948 | 统一发送调度器（含诊断前应用回复收尾、3D 轮询截止续期、应用帧测试开关） |
| `ViewModels/ProtocolLogWindowViewModel.cs` | 676 | 报文窗口筛选/暂停/镜像集合/保存数据 |
| `ViewModels/ActuatorDebugViewModel.cs` | 732 | 执行器调试页全部交互 |
| `Services/SerialPortConnectionService.cs` | 509 | 串口连接 + 双队列 |
| `Services/LinTransportProtocol.cs` | 362 | LIN-TP 编解码与重组状态机 |
| `ViewModels/SerialSettingsViewModel.cs` | 339 | 串口设置与端口监测（两页共用） |
| `MainWindow.xaml` | 329 | 主窗口布局与样式 |
| `Services/LinFrameChannel.cs` | 309 | 帧通道 |
| `Models/Diagnostics.cs` | 422 | 诊断请求/结果/进度/枚举、负响应与 78 续期通知、失败文案与 NRC 映射 |
| `Views/ActuatorDebugView.xaml` | 271 | 调试页布局（大量内联样式） |
| `Services/LinReadReplyParser.cs` | 253 | 共享接收解析 |
| `Services/LinFrameCodec.cs` | 241 | 构帧与校验算法 |
| `Models/CommunicationLogEntry.cs` | 229 | 报文记录的显示语义（4 种串口形态判别） |
| `Services/CommunicationLogService.cs` | 224 | 日志汇集与 UI 批处理 |
| `Services/CommunicationLogExcelExporter.cs` | 473 | 报文导出 `.xlsx`（手写最小 OOXML 包，无第三方依赖） |
| `Views/ProtocolLogWindow.xaml(.cs)` | 226 + 200 | 报文窗口 + 滚动跟随 code-behind |
| `ViewModels/MainWindowViewModel.cs` | 173 | 组合根、导航、0x33/0x34 分发、OTA 服务接线、应用帧测试开关 |
| `Views/FirmwareUpgradeView.xaml` | 231 | 固件升级页布局（文件信息/日志/升级操作，内联样式） |
| `Views/FirmwareContentWindow.xaml(.cs)` | 137 + 22 | 文件内容独立窗口（十六进制查看器） |
| `ViewModels/FirmwareUpgradeViewModel.cs` | 629 | 固件加载命令、NAD 输入、OTA 启动、日志与进度（1–15 步）、NRC 78 提示（成功不追加汇总日志） |
| `Services/OtaUpgradeService.cs` | 861 | OTA 升级流程编排（第一至第十五步，仅读 APP 版本、第三/四步默认跳过，复位后 50 ms 复核 APP 版本，含块切片/偏移/块号、块间 10 ms 间隔、复位编排与 NRC 78 提示转发；完成只报进度节点） |
| `Models/OtaSession.cs` | 309 | OTA 阶段/分步报告/结果模型（含擦除、下载、校验与复位结果快照，复位快照带复位后 APP 版本） |
| `Services/FirmwareDownloadService.cs` | 460 | 下载业务（`34`/`74`、`36`/`76`、`37`/`77` 构造与解析，74 块长校验，`36` 片间 20 ms 间隔） |
| `Services/FirmwareVerificationService.cs` | 196 | 固件校验业务（`31 01 02 02` + 地址/大小/CRC32，结果 00/01 判定） |
| `Services/EcuResetService.cs` | 135 | 设备复位业务（`11 01` 两字节 SF 请求，只认恰好 `51 01`） |
| `Models/FirmwareTransferPlan.cs` | 167 | 下载/校验参数快照（复用擦除范围，数据副本与 CRC32 复算，含临时 `Crc32TestXor` 测试开关） |
| `Models/FirmwareCrc32.cs` | 43 | CRC-32（IEEE 802.3）纯算法，供加载显示与第十四步校验共用 |
| `Services/EraseFlashService.cs` | 155 | 擦除业务（`31 01 FF 00` 12 字节多帧请求与正响应判定） |
| `Models/ErasePlan.cs` | 140 | 擦除参数快照（地址/长度来自固件映像，含范围校验） |
| `Services/SecurityAccessService.cs` | 222 | 安全访问业务（`27 09`/`27 0A`、Key 计算） |
| `Services/DiagnosticSessionService.cs` | 128 | 会话控制业务（`10 03`/`10 02` 与正响应格式判定） |
| `Services/IntelHexParser.cs` | 226 | Intel HEX 解析与映像归一化 |
| `Services/FirmwareFileService.cs` | 100 | 固件文件加载（BIN/HEX），CRC32 复用 `Models/FirmwareCrc32` |
| `Services/FirmwareHexFormatter.cs` | 123 | 十六进制预览行与文本构建 |
| `Models/FirmwareImage.cs` | 63 | 固件相关纯数据模型 |
| `Services/MotorPresetStorageService.cs` | 109 | 预设 JSON 读写 |
| `Services/DeviceInformationService.cs` | 89 | 版本读取业务（可覆盖 NAD） |
| `MainWindow.xaml.cs` | 94 | 主窗口 code-behind |
| `Models/DiagnosticMultiFrameOptions.cs` | 73 | 多帧时间参数 |
| 其余 Model / 接口 / 控件 | < 70 | 见各文件头部注释 |

合计约 **9.9k 行**（含 XAML），其中 `Services/` 约 5.7k 行，是本项目的重心。

---

## 4. 核心模块详解

### 4.1 基础设施层

#### 4.1.1 `SerialPortConnectionService`（509 行）

职责：单个串口的打开/关闭、字节级收发、连接代次保护。**不懂 LIN，也不懂业务**。

- **双有界队列**：`receiveQueue` / `transmitQueue`，各容量 256，用 `SemaphoreSlim` 做槽位计数。
- **两个长驻工作线程**（`Task.Run` + `await signal.WaitAsync`）：RX 工作线程把字节块抛给 `BytesReceived`；TX 工作线程串行执行 `SerialPort.Write`。
- **发送**：`SendAsync` 先取槽位 → 入队 → 释放信号 → `await request.Completion.Task.WaitAsync(cancellationToken)`。每个 `PendingWrite` 自带 `TaskCompletionSource` 与**提交时的连接代次**。
- **排空语义**：`WaitForTransmitIdleAsync()` 等待"最后一次已提交写入"的真实完成（`lastTransmitRequest`），刻意不等某个等待方的 CancellationToken——这是 F3 修复的关键（外层 `SendAsync` 被取消不代表物理写入结束）。
- **代次保护**：`connectionGeneration` 在打开/关闭时递增；写入与接收工作时都校验代次，避免旧连接的字节/任务污染新连接。
- **拔出处理**：`ConnectedPortName` 即使句柄失效也返回端口名，供上层检测"端口消失"。
- **接收活动时间戳**：`ReportReceiveActivity` / `HasRecentReceiveActivity(3s)`，供 `SerialSettingsViewModel` 在报文高频时跳过端口枚举。

注意点：
- `EnqueueReceivedBytes` 在队列满时**阻塞串口事件线程**（`receiveQueueSlots.Wait`）。这是"绝不丢字节"的取舍，256 深度正常够用，但极端卡顿时会反压 `DataReceived`。
- `Dispose` 里 `Task.WaitAll(receiveWorker, transmitWorker)` 是同步阻塞，若在 UI 线程调用会短暂冻结界面。

#### 4.1.2 `SerialPortDiscoveryService`（31 行）

`SerialPort.GetPortNames()` 的薄封装，排序后返回 `SerialPortOption`。存在意义是让 VM 不依赖 `System.IO.Ports`。

#### 4.1.3 `CommunicationLogService` + `CommunicationLogEntry`

`CommunicationLogService`：
- 生产侧（任意线程）`RecordSent/RecordReceived` → 入 `pendingEntries` 队列，**不碰 UI 集合**；
- `DispatcherTimer`（50 ms，Background 优先级）批量搬运，每批最多 200 条；
- 保留策略 5000/10000 条，溢出从头部逐条 `RemoveAt(0)`；
- **关键性能决策**：批量搬运时逐条 `Add`/`Remove` 发单元素事件，而不是一次 `Reset`。因为 `ListCollectionView` 遇 `Reset` 会全量重筛 + 整表重建，100 ms 轮询下会导致报文窗口明显卡顿（CHANGELOG 2026-10-14）。

`CommunicationLogEntry` 是"**显示语义**"的集中地：构造时按字节形态判定为「读取请求 / 写入帧 / 诊断请求 / 诊断数据 / 回复帧 / 应用帧 / 帧头 / 未配置 / 错误」之一，并分别填充 Break、Sync、PID、ID、长度、Data、Checksum 类型与结论。诊断回包**不填反算 PID**（因为它没有帧头）。

> 这一层把"协议解释"与"业务解释"分开了：日志只负责如实呈现字节与校验结论，不判断"版本读取成功"。

### 4.2 帧适配层

#### 4.2.1 `LinFrameCatalog` / `LinFrameDefinition`

帧表的唯一来源：ID、DATA 长度、发布节点（Master/Slave）、校验类型（Classic/Enhanced）。构造时做严格校验：

- ID ≤ 0x3D（0x3E/0x3F 保留）；
- 长度 1～8；
- `id >= 0x3C` 必须 8 字节 + 经典校验；
- 0x3C 必须 Master 发布、0x3D 必须 Slave 发布。

未登记 ID 一律拒绝（"不猜测长度和业务含义"）。

当前登记 5 帧：`0x33`（Slave/8/Enhanced）、`0x34`（Slave/8/Enhanced）、`0x15`（Master/8/Enhanced）、`0x3C`（Master/8/Classic）、`0x3D`（Slave/8/Classic）。

#### 4.2.2 `LinFrameCodec`（241 行）

纯静态算法，无状态、无 IO：

| 能力 | 说明 |
| --- | --- |
| `CalculateProtectedId` / `IsProtectedIdValid` | LIN PID 的 P0/P1 计算与自洽校验 |
| `CreateReadRequest` | `55 AA 55 PID 00`（5 字节，读取标识 `00` 是串口接口约定，非校验值） |
| `CreateWriteRequest` | `55 AA 55 PID 08 DATA(8) CS`（14 字节，`08` 不参与校验） |
| `CreateDiagnosticRequest` | `55 AA 55 3C 08 PDU(8) CS`（14 字节，经典校验） |
| `CalculateChecksum` | 增强型 = PID 种子 + DATA；经典型 = 仅 DATA；模 255 折叠后取反 |
| `CalculateWriteChecksum` | 写入帧专用，等价于增强型（以 PID 为种子、不含 `08`） |
| `TryRecoverProtectedId` | **反算** PID：`(~CS - ΣDATA) mod 255`，回复不带 PID 时的唯一恢复手段 |

两个易错点（代码与文档都已标注）：
1. 写入帧校验种子是 **PID**，不是原始 ID（曾按口头描述写错，`EA→AA` 的差异由此而来）；
2. 诊断帧必须经典校验，且**回包不含 Header**，不能用增强型反算恢复 PID。

#### 4.2.3 `LinReadReplyParser`（253 行）

单一共享字节缓存 + 双候选识别。这是整个接收链路的"心脏"。

```text
Append(bytes)
  ├─ 加锁：追加到 bufferedBytes，记录 lastAppendTimestamp
  ├─ while (缓存 >= 最短候选长度)
  │    ├─ 按候选长度升序尝试 TryParse（应用帧）
  │    │    ├─ 增强校验反算 PID
  │    │    ├─ P0/P1 奇偶自洽（拦截损坏数据）
  │    │    ├─ 查帧表（排除诊断帧）
  │    │    └─ 用候选 PID 复算 Checksum 复核
  │    └─ 失败后尝试诊断候选（8+1 且经典校验通过）
  ├─ 仍无法匹配且缓存 ≥ 最长候选 → 丢 1 字节重同步，记一条"无法匹配"记录
  └─ 否则等待更多字节（可能是更长回复的前缀）
  └─ 出锁：FlushResults（外部回调，绝不在锁内）
```

设计要点：
- **应用与诊断候选在同一缓存竞争**，不各复制一份，避免"半帧被两方各消费一次"；
- 两种校验天然可区分：经典帧经增强逆运算恒得 PID `0x00` 且奇偶自洽失败；
- `DiagnosticFrameReceived` 只输出 `(raw, pdu, checksumValid, timestamp)`，**不判 3C/3D**；
- 50 ms 字节间超时（`ExpirePartialFrame`，由通道的 10 ms 定时器驱动）清掉残缺缓存并保留拒绝记录，防止残缺字节"吃掉"下一帧帧头；
- `DiscardPartialCandidate`（诊断阶段切换时用）同样先记录再丢弃，不静默丢数据。

#### 4.2.4 `LinFrameChannel`（309 行）

统一收发门面，把"构帧 + 串口 + 日志 + 分发"合成一个对象：

- 发送 API：`SendReadRequestAsync`（Slave 应用帧读取）、`SendWriteRequestAsync`（Master 应用帧写入）、`SendFrameAsync`（通用）、`SendDiagnosticRequestAsync`（3C PDU）、`SendDiagnosticReadRequestAsync`（3D）。**每个发送后都记 TX 日志并返回 `FrameWriteReceipt`（含单调时间戳）**。
- 接收链路：`ConnectionService.BytesReceived` → 状态检查（**锁外**）→ `parser.Append` → 三类回调：
  - `FrameReceived`：先记 RX 日志，再逐个订阅方 try/catch 调用（一个订阅方异常不影响其他帧）；
  - `DiagnosticPacketReceived`：先记 RX 日志，再广播候选；
  - `FrameRejected`：只记日志（错误/未识别）。
- `Dispose` 与连接变化的回调都在锁外触发，避免"通道锁 → 协调层锁 → 调度锁"的锁环。

### 4.3 协议与调度层

#### 4.3.1 `LinBusScheduler`（948 行）— 唯一发送出口

三个状态：`AppRunning` / `DiagnosticExclusive` / `Disconnected`；应用拍内部使用**帧序列 + 索引** `appSequence`（`0x15` 写入、`0x33` 读取、`0x34` 读取）和 `appStepIndex`。

**应用帧测试开关** `ApplicationFramesEnabled`（默认 `true`）：为 `false` 时连接成功也不启动应用定时器，应用拍无法启动，`RunAppBeatAsync` 在帧边界退出；诊断进入/退出、3D 轮询与 OTA 全流程不受影响，退出诊断后也不会恢复应用帧。该开关仅为现场静默总线测试用，初始值由组合根 `MainWindowViewModel.EnableApplicationFrames` 常量给出。

**应用拍（100 ms，拍内 10 ms 帧间隔）**：单次定时器触发 → 先排下一拍（100 ms，单次）→ 若 `sendBusy` 或已请求暂停则跳过 → `RunAppBeatAsync`：
1. 从 `appSequence[appStepIndex]` 取当前帧，索引指向下一帧；
2. 写入帧发 `0x15`（快照），读取帧发对应 `0x33/0x34` 的 Header；
3. 每帧写完在锁内递增索引并检查 `mode/pauseRequested/disposed`，需要让出就停；
4. 序列未结束则等待 10 ms（从上一帧写入完成起算），继续下一帧；序列结束则本拍结束；
5. 定时器在 100 ms 后触发下一拍，10 ms 帧间隔不改变 100 ms 拍周期。

**诊断独占**：
- `EnterDiagnosticModeAsync()`：置 `pauseRequested` 停止启动后续应用帧，按 `appReplyDrainWindow`（默认 100 ms）等待已发出应用帧的待回复登记清除，再等**当前单帧**写完，然后切 `DiagnosticExclusive`、停两个定时器；申请带 `enterRequestGeneration` 代次，被撤销/被更新批次取代后迟到完成不再切模式。
- `SendDiagnosticRequestAsync(pdu)`：先占 `sendBusy` 再启任务（避免同步完成导致出口永久占用，F2 缺陷修复），返回 `FrameWriteReceipt`。
- `StartResponsePolling(anchor, firstDelay, period, deadline, ownerToken)`：按 `anchor + n×period` 的**绝对节拍**调度；错过就跳过（`ScheduleNextPollLocked` 用 while 前移 `pollBeatIndex`），不补发。
- `StopResponsePolling()`：只禁止新的 3D 提交。
- `WaitForSendIdleAsync()`：先等调度器登记的在途任务，再等串口真实排空。
- `ExitDiagnosticModeAsync()`：从**保存的序列索引**立即续发（`RunAppBeatAsync`），然后按 `appPeriod` 衔接整拍；若索引已在序列末尾，则新一轮从 `0x15` 开始。

**代次机制**（并发正确性的核心）：
- `sendGeneration`：出口占用归属，`ClearSendTask(generation)` 只在代次仍匹配时释放；
- `enterRequestGeneration`：独占申请归属；
- 断线时递增 `sendGeneration` 并清占用，旧任务完成后不会误清新连接。

#### 4.3.2 `LinTransportProtocol`（362 行）— LIN-TP

**发送编码**（静态、纯函数）：
- `EncodeSingleFrame`：NAD + `0L` + 消息 + `0xFF` 填充 = 8 字节；
- `EncodeRequestFrames`：L ≤ 6 → SF；L > 6 → 1 FF（`1H HL` + 5 字节）+ ⌈(L-5)/6⌉ CF（`2N` + 6 字节，末帧填 `FF`），序号 1..15,0,1…，上限 4095；
- `CalculatePduCount` / `GetUdsPayloadLength`。

**接收重组状态机**：`WaitingFirstFrame / Reassembling / Abandoned / Completed`

| 状态 | 事件 | 处理 |
| --- | --- | --- |
| 任意 | NAD 不匹配 | 忽略，不改状态、不刷 N_Cr |
| 任意 | 校验失败 | 入口已丢弃，根本不会进 TP |
| Waiting | SF(1~6) | 交付消息 → `Completed` |
| Waiting | FF(7~4095) | 存长度与首段，期望序号 1，启动 N_Cr → `Reassembling` |
| Waiting | 孤立 CF / 非法 PCI / 非法长度 | 忽略 |
| Reassembling | 正确 CF 未收齐 | 追加、推进序号、刷新 N_Cr |
| Reassembling | 正确 CF 恰好收齐 | 按声明长度截断 → `Completed`（只交付一次） |
| Reassembling | CF 错序/重复、N_Cr 到期、出现另一条合法 SF/FF | `Abandoned` + 原因，**不重启、不重发、不停 3D** |

设计取舍（已文档化为项目决策）：重组期间收到同 NAD 的另一条合法 SF/FF，直接放弃本次重组而不把新首帧当起点——因为响应连续性已被破坏，宁可失败也不拼接两条响应。`CheckTimeout` 由协调层的 10 ms 检查定时器驱动。

#### 4.3.3 `DiagnosticTransactionService`（2420 行）— 诊断事务协调层

这是全项目最复杂、风险最高的文件。核心结构：

| 结构 | 作用 |
| --- | --- |
| `ExecutionPlan` | 入队前生成的**不可变快照**（NAD、UDS 请求、预期前缀、分片数组、时间配置），运行期不再回读调用方对象 |
| `PendingTransaction` | 排队项：连接代次、取消注册、`TaskCompletionSource`；三态（排队 0 / 执行 1 / 结束 2）用 CAS 保证单次完成 |
| `ActiveTransaction` | 运行期状态：阶段、各截止时间、回显表、写入计数、TP 上下文、进度、结果 |

**提交 `ExecuteAsync(request, cancellationToken)`** —— 永不抛异常，一律返回 `DiagnosticResult`：
1. 已取消 → `Cancelled`；参数非法 → `InvalidRequest`；门禁关闭 → `DiagnosticGateClosed`；整批收尾期 → `QueueAborted`；
2. 绑定当前 `connectionGeneration`，注册取消回调（排队取消只 CAS 到"结束"并立即完成，不抢锁）；
3. 入队；若是首笔则立即 `scheduler.EnterDiagnosticModeAsync()`（暂停耗时不计入 5 s）；启动 `ProcessQueueAsync`。

**队列循环 `ProcessQueueAsync`**：
- FIFO 出队 → 跳过已取消 → CAS 抢占执行权 → 校验连接代次 → 在**同一临界区**登记 `enteringPending`；
- 等待独占时用 `Task.WhenAny(enterTask, pending.Completion)`，取消/断线/释放可立即唤醒，不必等旧应用帧写完（F5）；
- 独占到手后在锁内原子完成"清 entering → 建 active → 绑 Owner"；
- `RunTransactionAsync` → 完成后写回 `pending.Completion`；
- `finally` 用 `loopGeneration` 判定自己是否仍是"当前循环"，是则清共享状态、`ExitDiagnosticModeAsync()`、必要时发 `DiagnosticActivityChanged(false)`（若期间又有入队则不发，避免按钮提前解禁）。

**单帧（SF）路径**：
```text
登记临时截止（防回显先到被误判超时）
  → BeginReceive（发送前启用 TP，SF 保持旧行为）
  → 发送 3C → 拿回执 t0
  → 重算 EchoDeadline = t0+1000ms、ResponseDeadline = t0+5000ms
  → StartResponsePolling(t0, 20ms, 20ms, deadline)
  → 并行等待：回显（逐字节比对）+ 响应（TP 重组）
  → 两者都通过才算成功；响应先到则停 3D，但仍在回显窗口内等回显
```

**多帧路径**：`SendMultiFrameRequestAsync`
- 发送预算 `sendBudget = N×(WriteBudget+EchoBudget) + (N-1)×InterFrameGap + Margin`；
- 逐片：等最小间隔 → 检查发送截止 → 提交写入 → 等写入回执（`WriteBudget`）→ 若回显未到则等回显（`EchoBudget`）→ 下一片；
- 全部确认后：`t_last = max(最后写入, 最后回显)`，`ResponseDeadline = t_last + ResponseBudget`，进入固定 `EchoDrain`（50 ms，不被新回显延长）；
- 隔离结束：丢弃解析器残留 → `Reset` + `BeginReceive` → **原子**发布 `WaitingResponse`/`TpReceiveEnabled` → 启动 3D（首拍延迟 20 ms）。

**回显/响应归属（多帧判定表）**：

| 阶段 | 处理 |
| --- | --- |
| `SendingRequest` | 只与"当前待确认片"精确比对（F1：先比当前片再比历史，避免全 AA 载荷被旧片遮挡）；命中则确认并推进 |
| `EchoDrain` | 全部丢弃，不延长窗口 |
| `WaitingResponse` | 命中**任一已尝试片**的回显：容忍窗口内丢弃；超窗 → `ProtocolAttributionError`（归属异常，关门禁） |
| `WaitingResponse` 且首个 3D 未提交 | 首读在途 → 有界暂存（上限 32）；首读未开始 → 丢弃；回执后按原接收时间重放（F2） |

**NRC 78 响应待定**（`架构/OTA升级流程.md` §6.5，已实现）：
- `ApplyTpResult` 先分类负响应：完整 `7F + 原 SID + 78` 不结束本笔，按 `packet.ReceivedTimestamp + ResponseWindow` 续期 `ResponseDeadline`（SF 窗口取 `TotalDeadline`、多帧取 `ResponseBudget`，不另设绝对上限）；
- TP 在本条 SF 收齐后已进入 `Completed`，续期分支立即 `BeginReceive` 重新准备接收；不清空串口共享缓存，同一字节块内紧跟的最终响应可依次进入；
- 同步调用 `scheduler.ExtendResponsePollingDeadline(transaction, deadline)`：只替换 3D 轮询截止并按原节拍续排，不重锚、不补发；原截止已停止排拍时重新激活；
- 回显截止独立、不因 `78` 延长；最终正响应已核对、只等回显时不再接受续期；迟到的 `78` 在接收入口按原截止先判失败；
- `ResponsePendingReceived` 事件在协调层锁外触发（锁内只写 `PendingNotice`，接收入口出锁后派发），供升级页写“设备处理中”日志，不改变结果。

**失败策略 `ApplyBatchPolicyLocked`**：

| 失败原因 | 单笔 | 清空同批 | 关闭门禁 |
| --- | --- | --- | --- |
| `ResponseMismatch`（含未解析 `7F`） | ✔ 结束本笔 | ✘ | ✘ |
| `NegativeResponse`（`7F + 原 SID + NRC`，NRC 非 78） | ✔ 结束本笔 | ✘ | ✘ |
| 多帧/发送类失败、`EchoTimeout`（多帧） | ✔ | ✔ `QueueAborted` | ✘ |
| `ResponseDeadlineExceeded` / `DeadlineExceeded` | ✔ | ✔ | ✘（收窄后的决策：设备无应答概率下，锁死整轮代价过高） |
| `ProtocolAttributionError` | ✔ | ✔ | ✔ |
| 响应阶段取消 | ✔ | ✔ | ✔ |
| 断线（已有写入且响应未验证） | ✔ | ✔ `Disconnected` | ✔ |

**门禁**：`IsDiagnosticBlocked` 只读、`ResetDiagnosticGate()` 显式解除，**串口重连不解除**，当前**未接界面**。

**收尾顺序**（`RunTransactionAsync.finally`）：停新 3D → 停检查定时器 → `TP.Reset()` → `WaitForSendIdleAsync()` → 清 `active`。结果（`DiagnosticResult`）在 `CompleteLocked` 时已冻结并完成调用方，**排空不影响结果返回**（F3）。

### 4.4 应用业务层

#### 4.4.1 `DeviceInformationService`（102 行）

- 固定 NAD `0x78`（未显式指定时的回退值），服务 `0x22`（ReadDataByIdentifier）；
- DID 映射：Bootloader `F1 80`、Firmware `F1 95`、Hardware `F1 93`；
- 预期正响应前缀 `62 + DID`；
- 成功后把去掉前 3 字节的剩余数据按 ASCII 解释（**长度由 FF 声明动态决定**，不写死 33 字节），`TrimEnd()` 后返回；
- 失败原因映射为中文文案（`FailureText`，兜底会显示枚举名以便发现遗漏）；设备返回负响应时在原因后附带 `7F xx NRC` 与中文 NRC 说明。

#### 4.4.2 `MotorCommandService`（70 行）

`0x15` 的 8 字节指令快照，**只有状态没有发送能力**：

| 字节 | 含义 | 取值 |
| --- | --- | --- |
| 0 | 运动控制指令 | `0` 停止 / `1` 运动到指定位置 / `2` 一直运动 / `3` 执行初始化 |
| 1 | 运动方向 | `0` UNKNOWN（上位机固定 0，未实现） / `1` CW / `2` CCW |
| 2-3 | 目标位置 | 16 bit 无符号 step，小端 |
| 4-7 | 保留 | 0 |

界面操作只改快照，**由调度器每拍读取快照组帧**——这是"操作与发送解耦"的关键：点一次按钮不会立即发一帧，而是改变后续每一拍的写入内容。

#### 4.4.3 `MotorStatusFrameParser` + `MotorStatusSnapshot`

`0x33` 的 8 字节 DATA → 位置/极限/状态位。位域：byte6 bit0-2 运动状态（0 停止/1 运动/2 堵转/3 开路/4 短路）、bit3-4 电压（0 正常/1 低/2 高/3 保留）、bit5 温度高、bit6-7 初始化状态（0 完成/1 初始化中/2 错误/3 未定义）；byte7 为堵转回退步数。

`0x34` 由 `MotorMeasurementFrameParser` 处理：前 4 字节温度、后 4 字节电压，均为小端 binary32 `float`。

#### 4.4.4 `MotorPresetStorageService`（109 行）

`config/motor-presets.json` 的读写：版本号 + 预设数组；读取时严格校验（版本、条数、ID 集合、非负），不合法回退默认值并尝试重写；保存用 `.tmp` + `File.Move(overwrite)` 原子替换。

#### 4.4.5 `DiagnosticSessionService`（128 行）

OTA 第六、七步的会话业务层，只构造请求、判定正响应格式，**不处理 TP、调度、超时与 OTA 独占**：

- 会话切换共用 `EnterSessionAsync(sessionType, nad, token)`：请求 `10 + 子功能`，预期正响应前缀 `50 + 子功能`；
- `EnterExtendedSessionAsync`（`03`）、`EnterProgrammingSessionAsync`（`02`，只进入会话、不含解锁）；
- 成功要求完整 UDS 长度为 6 字节（`50 xx + P2(2) + P2*(2)`），后四字节**不解析、不建属性、不改变任何计时**；
- 长度不符返回 `ResponseMismatch`，补充说明带子功能与实际长度（如 `10 02 正响应长度为 4 字节，预期 6 字节`）；设备负响应原样带回 `DiagnosticNegativeResponse`。
- 关联契约：`Models/Diagnostics.cs` 的 `DiagnosticNegativeResponse`（原 SID/NRC/原始 UDS）、`DiagnosticNrcText`（NRC 中文映射）、`DiagnosticFailureReason.NegativeResponse`；分类点在 `DiagnosticTransactionService.ApplyTpResult`（完整 UDS 收齐后先判负响应，再核对正响应前缀）。
- 第八、九步的安全访问由同层 `SecurityAccessService` 承担（§4.4.6），本服务不扩展。

#### 4.4.6 `SecurityAccessService`（222 行）

OTA 第八、九步的安全访问业务层，与 `DiagnosticSessionService` 同层同位：只构造请求、判定正响应格式、计算 Key，**不处理 TP、调度、超时与 OTA 独占**，不缓存 Seed/Key。

- `RequestSeedAsync`：请求 `27 09`，预期前缀 `67 09`；成功要求完整 UDS 恰为 6 字节（`67 09 + 四字节 Seed`），Seed 按报文顺序原样返回，不颠倒字节；
- `SendKeyAsync`：请求 `27 0A + 四字节 Key`（6 字节 UDS，仍为 SF），预期前缀 `67 0A`；成功要求完整 UDS 恰为 2 字节；Key 以二进制发送，不转 ASCII；
- 两者长度不符均返回 `ResponseMismatch` 并给出实际/预期长度；设备负响应原样带回 `DiagnosticNegativeResponse`；
- `SeedToKey`（uint 返回值）与 `CreateKey`（线上顺序四字节）是纯函数：按用户提供的 `FLB_SeedToKey` 原式与 mask `0x3B61B270` 计算，Key 高字节先发送，不使用宿主端序拷贝；
- 全零 Seed 的路径未确认：`IsZeroSeed` 只做标记，由 `OtaUpgradeService` 决定（当前记日志后退出、不发 Key，`架构/OTA升级流程.md` §8.3）。

#### 4.4.7 `EraseFlashService`（155 行）+ `ErasePlan`（140 行）

OTA 第十步的擦除业务层，与前两个业务服务同层同位：只构造请求、判定正响应格式，**不处理 TP、调度、超时与 OTA 独占**，不重发、不追加 `31 03` 结果查询。

- `ErasePlan`（`Models/ErasePlan.cs`）：不可变参数快照，保存 `StartAddress`、`Length`、来源格式与文件名；`ToUdsRequest()` 生成 `31 01 FF 00` + 大端四字节起始地址 + 大端四字节字节长度共 12 字节；不插 `44` 格式标识，也不把结束地址当长度。
- `TryCreate(FirmwareImage)` 的硬校验只有四条：`BIN` 直接拒绝（`BaseAddress=0` 是加载默认值，不是设备 APP 地址）、`BaseAddress` 必须落在 `0..0xFFFFFFFF`、`ImageSize` 必须大于 0 且不超过四字节长度字段、`BaseAddress + Length` 不得越过 32 位地址空间。分区、页对齐等依赖设备契约的规则未擅自实现。
- `EraseAsync`：12 字节请求必须显式提供 `DiagnosticMultiFrameOptions`（默认值即 §10.5 基线），预期前缀 `71 01 FF 00`；正响应长度不足或前缀不符返回 `ResponseMismatch`。
- 状态记录（`71 01 FF 00` 之后的内容）原样放 `EraseResult.StateRecord`，**不解释、不补 `00`、不作为擦除完成判据**；`OtaEraseOutcome.CompletionConfirmed` 恒为 `false`，升级日志只写“请求已被设备接受”。

#### 4.4.8 `FirmwareDownloadService`（446 行）+ `FirmwareTransferPlan`（167 行）

OTA 第十一至第十三步的下载业务层，与擦除服务同层同位：只构造/解析 `34`/`36`/`37`，**不保存块偏移、不重传、不处理 TP 与超时**。

- `FirmwareTransferPlan`：地址与长度直接取自第十步已提交的 `ErasePlan`，数据为加载时映像的副本，CRC32 按该副本复算（`FirmwareCrc32`）；长度或起始地址与映像不一致时拒绝生成，确保 34/36/37/31 02 02 全程同源；`BIN` 在第十步已被拒绝，这里不会拿到范围。**临时测试开关** `Crc32TestXor`（默认 `0`，`x ^ 0 == x` 不改变行为）：非 `0` 时对复算结果异或，只扰动发给设备的校验值，界面「校验值」仍来自 `FirmwareImage.Crc32`，用于验证设备侧校验失败路径；测完必须改回 `0`。
- `RequestDownloadAsync`：`34 00 44` + 大端地址 + 大端字节长度（11 字节，必走多帧）；解析 `74` 时校验长度格式高四位 1～4、响应恰为 `2 + n` 字节、块长 > 2 且 ≤ 4095，容量 `= min(块长 − 2, 128)`。
- `TransferDataAsync`：`36 + 块号 + 数据`，长度 > 6 显式给多帧选项（`MinimumInterFrameGap = FrameGap = 20 ms`；末块很短时按 SF）；正响应必须恰为 `76 BSC` 且块号与当前块一致，否则返回 `ResponseMismatch` 并保留原始响应。
- `RequestTransferExitAsync`：单字节 `37`，要求完整响应恰为 1 字节 `77`。

#### 4.4.9 `FirmwareVerificationService`（196 行）

OTA 第十四步的校验业务层：

- 请求 16 字节 `31 01 02 02 + 地址 + 大小 + CRC32`，CRC 用 `WriteUInt32BigEndian` 写入（高字节先发，不依赖宿主端序）；
- 只接受完整 5 字节 `71 01 02 02 Result`；`00` 成功、`01` 业务失败（`RoutineFailed=true`，不伪装成 NRC=01）、其他结果值按未知失败并给出十六进制；
- RID 与擦除的 `FF00` 明确区分，不复用擦除服务的判定。

#### 4.4.10 `EcuResetService`（135 行）

OTA 第十五步的复位业务层，与校验服务同层同位：

- 请求固定两字节 `11 01`（SID + 子功能 01），走既有 SF 时序（`DiagnosticTiming.Default`：t0 + 20 ms 首拍、20 ms 节拍、回显 1000 ms、总截止 5 s）；
- 只接受完整 2 字节 `51 01`：`51 02` 等其他子功能、错误长度都不判成功；正响应前缀交给协调层匹配，业务层再做长度与子功能复核；
- 有效 `7F 11 78` 由协调层续期 5 s 并继续 3D，本类不重发复位；非 78 负响应原样返回给编排层记录；
- 不自动重连、不补发 `10 01`、不新建计时器体系；`51 01` 只代表设备接受复位请求，不代表新固件已启动。

### 4.5 表现层

#### 4.5.1 `MainWindow` + `MainWindowViewModel`

- 外壳：`MetroWindow`（最大化），左侧 `HamburgerMenu`（展开/收起带 280 ms 动画，`IsNavigationExpanded` 驱动文字可见性），两个 `Flyout`（串口设置 / 统一配置），底部状态栏（连接指示、端口、报文窗口入口、静态占位文字）。
- 导航：`MenuItemViewModel{Label, Glyph, ViewModel}` → `SelectedMenuItem` → `CurrentViewModel` → `ContentControl` 用 `DataTemplate` 映射到页面 View。当前两个菜单项：`执行器调试`（`\uE713`）→ `ActuatorDebugView`、`固件升级`（`\uE895`）→ `FirmwareUpgradeView`。
- 页面切换只换 `ContentControl.Content`，VM 与通信服务都是常驻实例：**串口连接、应用帧调度、诊断独占不因切页而重置或停止**。新增页面只需再加一个 `MenuItemViewModel` 与一条 `DataTemplate`。
- `HandleStatusFrameReceived`：按 ID 分发 `0x33` / `0x34` → 解析 → `Dispatcher.BeginInvoke` 回 UI → `ApplyMotorStatus` / `ApplyMotorMeasurements`。**唯一把通信线程数据搬到 UI 的位置。**

#### 4.5.2 `ActuatorDebugViewModel`（732 行）+ `ActuatorDebugView.xaml`

承载了执行器调试页的全部交互（页面偏"重"，见第 7 节）：

| 关注点 | 实现 |
| --- | --- |
| 版本读取 | `ReadDeviceInfoCommand(string operationName)`，按**中文按钮文字**映射 `DeviceVersionKind` |
| 电机命令 | 确定位置/CLOSE/OPEN1-3/初始化/STOP → 改 `MotorCommandService` 快照 + 提示条 |
| 位置钳制 | `NegativeLimitSteps`、`TotalTravelSteps` 变化时 `Math.Clamp(TargetPositionSteps, ...)`；负极限 > 正极限时以较大值为上限 |
| 状态显示 | `ApplyMotorStatus` 把状态位映射为文本+颜色（正常绿 `#107C10`、故障红 `#C42B1C`、运动中蓝 `#0078D4`、空闲灰 `#5C5C5C`） |
| 预设配置 | `Presets`（展示）/`EditablePresets`（编辑副本）/保存/记忆当前位置 |
| 按钮可用性 | `SerialSettings.IsConnected && !isDiagnosticActive`；连接状态变化与诊断活动变化都显式 `NotifyCanExecuteChanged()`（Toolkit 对属性型 CanExecute 不自动接线） |
| 提示条 | `CancellationTokenSource` + `Task.Delay` 自动隐藏（1 s / 3 s） |

#### 4.5.3 `SerialSettingsViewModel`（339 行）

- 端口枚举与 1 s 轮询（`PeriodicTimer`），面板打开/未连接/无接收活动时才检查（`HasRecentReceiveActivity(3s)` 避免干扰高速通信）；
- 连接前校验端口是否仍存在；已连接端口消失 → 自动断开并提示"串口已移除"；
- **连接/断开由 VM 调服务**，但 `LinBusScheduler` 是通过 `ConnectionStateChanged` 事件被动感知的，两条路径最终汇聚到同一事件。

#### 4.5.4 `ProtocolLogWindow` + `ProtocolLogWindowViewModel`（226 + 200 + 676 行）

- VM 用**镜像集合** `displayedEntries` + `ListCollectionView` 实现"暂停显示"（直接绑源集合无法真正冻结）；
- 四维级联筛选（方向/类型/ID/PID）：用 `filterValueCounts` 计数 + "排除自身维度后是否匹配其他筛选"来生成候选值，最多 4 轮收敛；选项用增删/移动增量更新，避免清空导致 WPF 短暂 `null`；
- 筛选选项重建用 `Dispatcher.BeginInvoke(Background)` 合并，避免逐条通知引发重建风暴；
- 窗口关闭时 `SuspendUpdates()`，重新显示时与源集合重新同步（隐藏期间的 Add 被丢弃）；
- code-behind 负责"自动跟随最新"+ `userHorizontalOffset` 保持横向滚动位置（`ScrollToEnd` 会重置 `HorizontalOffset`）。
- **保存数据**（2026-10-23）：蓝色主按钮（`SaveDataCommand`）把当前**可见集合**快照导出为 Excel；`SaveFileDialog` 与提示留在 VM，构包与写文件全在 `CommunicationLogExcelExporter`（纯静态、无 WPF/无第三方库）。成功只写窗口底部状态行（`StatusMessage`，含条数与完整路径），失败弹 `MessageBox`；无数据时提示且不生成空文件。导出不影响采集、不清空记录，暂停时导出即冻结画面内容。
  - **布局**：与筛选控件**同处一条工具条**——`DockPanel` 先声明 `DockPanel.Dock="Right"` 的按钮，筛选 `WrapPanel` 填充剩余宽度并在窄窗时自行换行。按钮不再单独占一行，工具条第 1 行（`Grid.Row=1`）高度从约 105 逻辑 px 回到约 28，列表可视高度恢复约 41 px。
  - **样式**：`SaveDataButtonStyle` 由隐式 `Button` 样式派生，`MinHeight=28`（同行下拉框 26，仅高 2 px）、`FontSize=13`、`Padding=16,3`，并显式设 `BorderThickness=0` 与 `FocusVisualStyle={x:Null}`、自行覆盖 hover/pressed/disabled 三态背景。此前按钮四周出现 2 逻辑 px 纯黑描边，来源是默认按钮模板的边框与焦点视觉（`BorderBrush` 与背景同色本应不可见，但模板触发器/焦点装饰会改色），必须去掉这两项才彻底消失，只改颜色无效。

#### 4.5.5 `FirmwareUpgradeViewModel`（629 行）+ `FirmwareUpgradeView.xaml`（231 行）+ `FirmwareContentWindow`

2026-10-21 新增的页面，当前实现**固件文件加载、信息展示、十六进制预览与 OTA 第一至第十五步的启动**；复位已接入，复位后的启动验证仍由实机确认。
2026-10-22 起十六进制预览**移入独立窗口** `FirmwareContentWindow`，主页面不再内联查看器卡片。
2026-10-23 起 VM 保存加载时的 `FirmwareImage` 快照（`currentImage`）作为第十至第十四步范围、数据与 CRC32 的唯一来源；重新打开文件或重置查看器都会同步更新，避免用过期映像算范围。
进度映射随步骤增补重排为：探测 0–18%、三版本 24/30/36%（当前第三、四步跳过，30/36 不命中，24 直接进 36–42）、再次确认 39–42%、会话 45/47%、Seed 50%、Key 53%、擦除 56%、下载申请 58%、逐块传输 58–96%（按已确认字节）、结束传输 97%、校验 98%、复位 99%、完成 100%；仅影响显示，不改变任何成功判据与计时。

| 关注点 | 实现 |
| --- | --- |
| 串口连接保持 | 构造时注入主窗口共享的 `SerialSettings`，与执行器调试页**同一实例**；`connectionService` 在 `MainWindowViewModel` 级持有，切页不触碰 |
| 页面结构 | 标题栏卡（同调试页 `CardStyle`）+ 工具行（打开固件、文件内容按钮）+ 文件信息条（图标/文件名/格式/映像大小/CRC32/修改日期）+ 日志区（深色面板 + 进度占位 + 保存/清空日志）+ 底部操作行（NAD 输入框 + 配置 NAD / 开始升级） |
| NAD 输入 | `NadText` 默认 `0x78`（与 `DeviceInformationService.DiagnosticNad` 同源）；`TryGetNad` 接受 `0x78` / `78` 十六进制写法、限单字节 0x00–0xFF，非法或为空回退默认并记 Warning；“配置 NAD”校验并规范化为 `0xXX`，“开始升级”发送前同样校验；OTA 传输接入后 NAD 组装统一经 `TryGetNad` 取值（`ReadVersionAsync` 已支持可选 `nad` 参数） |
| 文件内容窗口 | 工具行“文件内容”按钮（`ShowContentCommand`，仅已加载固件时可用）弹出 `FirmwareContentWindow`：与主页面共享同一 `FirmwareUpgradeViewModel`，数据实时刷新；非模态 `Show()`，点关闭按钮即 `Close()` 销毁、不驻留后台；已打开时重复点击只 `Activate()` 唤到前台 |
| 打开固件 | `OpenFirmwareCommand`：`OpenFileDialog`（筛选串取自 `FirmwareFileService.OpenFileDialogFilter`）→ `LoadAsync`；错误以 `FirmwareFileFormatException` 消息入日志（Error 级别），不弹异常 |
| 数据流 | VM 只调用 `FirmwareFileService.LoadAsync` 并把 `FirmwareImage` 映射为显示属性；HEX 解析、CRC32、预览行构建全部在服务层（`IntelHexParser` / `FirmwareCrc32` / `FirmwareHexFormatter`），VM 不接触文件字节解析 |
| 预览 | `FirmwareHexFormatter.BuildLines`：每行 16 字节，偏移（8 位十六进制）+ 十六进制 + ASCII；上限 1 MB（`MaxPreviewBytes`），超出记 Warning 日志；`ListBox` 开虚拟化 + 回收模式，行悬停高亮、无选中态 |
| 复制/日志 | `CopyContentCommand` 把预览行拼为纯文本入剪贴板；页内日志 `ObservableCollection` 上限 500 条，`Run.Style + DataTrigger` 按级别着色 |
| 样式 | 沿用各视图自包含样式的既有组织方式（全部在 `UserControl.Resources` 内定义），未抽取共享资源字典，不改动调试页 |
| 开始升级 | `StartUpgradeCommand`（需固件已加载、串口已连接且无 OTA 在跑）→ `OtaUpgradeService.RunAsync(NAD, FirmwareImage)`：NAD 取页面输入框（默认 0x78），固件取 `currentImage` 快照；分步进展经 `StepReported` 切回 UI 线程写入日志（`OTA-1`…`OTA-15` 标签）并驱动进度条（第十二步按 `CompletedBytes/TotalBytes` 计算）；`NoticeReported` 只写非步骤提示（“设备处理中（NRC 78）”、第三/四步跳过说明），不改进度；**成功后不再追加任何汇总/待确认日志**（各步骤结果已由 `OTA-1`…`OTA-15` 逐条给出），只把进度置 100%、状态行写“十五步升级报文流程完成，设备已返回复位肯定响应”；失败只写一条“本次 OTA 结束于 <阶段>：<原因>” |
| OTA 流程 | `OtaUpgradeService`（应用业务层，`架构/OTA升级流程.md` 第一至第十五步）：第一步 TesterPresent 连续三次在线确认（累计两败退出/间隔 1 s），第二步只读 APP（软件）版本（失败立即退出；第三、第四步的 Bootloader/硬件版本读取由 `ReadBootloaderAndHardwareVersions` 控制，默认 `false` 整步跳过、不发 `22 F1 80`/`22 F1 93`），第五步再次连续两次在线确认（计数独立初始化），第六步经 `DiagnosticSessionService` 发送 `10 03` 进入扩展诊断会话，第七步发送 `10 02` 进入编程会话，第八步经 `SecurityAccessService` 请求 `27 09` 取得四字节 Seed，第九步按中间件原式计算 Key 并发送 `27 0A`，第十步经 `EraseFlashService` 按固件映像的起始地址与长度发送 `31 01 FF 00`，第十一步经 `FirmwareDownloadService` 发送 `34 00 44` 并按 74 容量折算每包字节，第十二步按 `min(容量, 剩余)` 切片逐块发送 `36 + 块号 + 数据`（只认匹配块号的 76，块号 FE FF 00 01 回绕，片间最小间隔 `FrameGap = 20 ms`、块间等待 `BlockInterval = 10 ms` 且首块除外），第十三步全部确认后发送 `37` 并要求恰好 `77`，第十四步经 `FirmwareVerificationService` 发送 `31 01 02 02 + 地址 + 大小 + CRC32` 并按结果 00/01 判定，第十五步经 `EcuResetService` 发送两字节 `11 01` 并要求恰好 `51 01`（失败保留前序结果，不重发复位；`51 01` 不等于新固件已启动）；复位确认后等待 `ResetVersionDelay = 50 ms` 再读一次 APP 版本（`22 F1 95`），成功写入 `OtaResetOutcome.AppVersionAfterReset` 并显示在完成状态行，读不到只记 Warning、不判本次升级失败、不重试。报文与超时全部复用诊断协调层与版本读取服务，"完整 UDS 恰为 `7E 00`"在业务层判定；非 78 负响应把原始 UDS、NRC 与中文原因写入升级日志后退出，不重试、不弹窗；有效 NRC 78 由协调层续期并继续等待最终响应，业务层只转一条提示日志。独占经 `DiagnosticTransactionService.BeginOtaExclusiveHold/EndOtaExclusiveHoldAsync` 保持：多笔事务间队列清空不恢复应用帧。复位后的启动验证不自动化，由实机确认（§15.5） |
| 路由 | `MainWindowViewModel.FirmwareUpgrade` + 菜单项 + `ContentControl.Resources` 中的 `DataTemplate` |

数据模型（`Models/FirmwareImage.cs`）：`FirmwareImage`（格式/文件名/路径/大小/修改时间/映像字节/基地址/CRC32/HEX 摘要）、`IntelHexSummary`（记录数/数据字节数/最低最高地址/入口地址）、`HexPreviewLine`、`FirmwareLogLevel` / `FirmwareLogEntry`。

HEX 解析要点（`Services/IntelHexParser.cs`，internal static 纯函数）：
- 支持记录类型 00/01/02/04/05（02 段地址×16、04 线性地址×65536、05 入口地址），未知类型忽略（前向兼容）；
- 逐行校验（冒号、偶数字符、合法十六进制、整行字节求和为 0、长度字段一致），失败抛带行号的 `FirmwareFileFormatException`；
- 归一化映像：以最低数据地址为基地址，段间空洞填 `0xFF` 并统计填充量；同地址重复写入以最后一条为准；
- EOF 之后的冗余行忽略；空行跳过；CRLF/LF 均可。

BIN 路径：整文件即映像（基地址 0），CRC32 按文件内容；HEX 的 CRC32 按归一化映像计算（`FirmwareCrc32`，IEEE 802.3 查表实现）。文件读取与解析在 `Task.Run` 后台线程执行，不阻塞 UI。

---

## 5. 业务流程与数据流

### 5.1 启动与组装

```text
App(MetroWindow 启动) → MainWindow ctor → new MainWindowViewModel()
  → 创建日志/串口/帧表/通道/调度/诊断/业务/VM（见 2.4）
  → DataContext = VM → 默认选中"执行器调试"（菜单首位）
  → 切到"固件升级"：仅 CurrentViewModel 换引用，串口与调度不受影响
用户点"连接设备" → SerialSettingsViewModel.ToggleConnectionAsync
  → 校验端口 → connectionService.ConnectAsync
  → ConnectionStateChanged(true)
      ├→ LinFrameChannel.isConnected = true（parser.Reset）
      ├→ LinBusScheduler: mode = AppRunning, appStepIndex = 0, 启动 100 ms 单次定时器
      └→ DiagnosticTransactionService: isConnected = true
```

### 5.2 连接/断开与热插拔

| 场景 | 行为 |
| --- | --- |
| 打开失败 | 返回 `SerialConnectionResult.Failure(message)`，VM 显示"连接失败"+原因 |
| 用户断开 | `ClosePort` → 失败所有排队写入、清空接收队列 → `ConnectionStateChanged(false)` → 调度器停表、协调层清队列 |
| 端口拔出 | 端口轮询发现端口消失 → 自动 `DisconnectAsync("串口已断开")` |
| 重连 | 调度器从 `0x15` 重新开始，**不恢复断线前的诊断事务**；协调层 `connectionGeneration++` 使旧请求判 `Disconnected` |
| 切换端口 | `OpenPort` 先关旧口再开新口，会发一次 `false` 再发 `true` |

### 5.3 100 ms 应用轮询与状态回显（主链路）

```text
[100 ms 单次定时器] LinBusScheduler.AppTimerCallback
        │ 锁内：mode==AppRunning → 先排下一拍 → !pauseRequested && !sendBusy → 占出口
        ▼
RunAppBeatAsync（锁外写入，按 appSequence 顺序）
        │
        ├─ 0x15 → channel.SendWriteRequestAsync(0x15, snapshot)
        │           → connectionService.SendAsync → TX 队列 → SerialPort.Write
        │           → 记 TX 日志（"写入帧"）→ 索引 +1 → 等待 10 ms
        ├─ 0x33 → channel.SendReadRequestAsync(0x33) → 5 字节 55 AA 55 73 00
        │           → 索引 +1 → 等待 10 ms
        └─ 0x34 → channel.SendReadRequestAsync(0x34) → 5 字节 55 AA 55 B4 00
                    → 索引到末尾 → 本拍结束（等下一拍）

[设备回复] SerialPort.DataReceived
        → 读字节块 → RX 队列(256) → RX 工作线程 → BytesReceived
        → LinFrameChannel.HandleBytesReceived（锁外 parser.Append）
        → LinReadReplyParser：9 字节 → 增强反算 PID → 帧表 0x33/0x34 → 复算校验
        → FrameReceived(0x33/0x34)
             ├─ 记 RX 日志（"回复帧"）
             └─ MainWindowViewModel.HandleStatusFrameReceived
                   ├─ 0x33 → MotorStatusFrameParser.Parse → ApplyMotorStatus
                   └─ 0x34 → MotorMeasurementFrameParser.Parse → ApplyMotorMeasurements
                   → Dispatcher.BeginInvoke 回 UI
```

语义约定：**状态显示"最后一次有效读取"**，无新帧不清零、不超时复位；断开串口才恢复 `—`。

### 5.4 电机控制下发

```text
按钮(确定/CLOSE/OPEN1-3/初始化/STOP)
  → 可用性 = 已连接 && 无诊断进行
  → MotorCommandService.SetMoveToPosition/SetInitialize/SetStop
  → 提示条"已下发目标位置：N step"
  → （下一个 100 ms 拍）调度器按新快照组帧发出 0x15
```

注意："已下发"只表示**快照已更新**，真正的发送在下一拍；串口断开不清空快照，重连后继续按快照发送。

### 5.5 接收解析与分发的统一入口

```text
串口字节块（可能半帧，也可能多帧粘连）
        ▼
唯一共享缓存 bufferedBytes
        ▼
按候选长度（帧表 DataLength+1 去重升序）逐个尝试
   ├─ 增强校验逆运算 → PID 奇偶自洽 → 帧表命中 → 复算校验  ⇒ 应用帧
   ├─ 8+1 且经典校验通过                                ⇒ 诊断候选（不判 3C/3D）
   └─ 都失败且缓存≥最长候选                             ⇒ 丢 1 字节重同步 + 错误记录
        ▼
先记日志，再按类型分发（回调都在锁外）
   ├─ FrameReceived           → 0x33 / 0x34 业务
   ├─ DiagnosticPacketReceived → 协调层：当前事务认领（回显或 TP）
   └─ FrameRejected           → 仅日志
```

### 5.6 诊断单帧事务（版本读取）完整时序

```text
用户点"读取固件版本"
 → ActuatorDebugViewModel.ReadDeviceInfoAsync("读取固件版本")
 → DeviceInformationService.ReadVersionAsync(Firmware)
       request = { NAD 0x78, [22 F1 95], 预期 [62 F1 95] }
 → Diagnostics.ExecuteAsync(request)                     【入队】
       ├ 首笔：scheduler.EnterDiagnosticModeAsync()（只等当前单帧写完）
       └ ProcessQueueAsync 开始循环
 → 独占到手 → ActiveTransaction
 → LinTransportProtocol.EncodeSingleFrame → PDU 78 03 22 F1 95 FF FF FF
 → scheduler.SendDiagnosticRequestAsync(PDU)  → 14 字节 55 AA 55 3C 08 ... DA
       ↑ 发送前先登记临时截止（防回显先到被误判超时）
       ↑ t0 = 回执时间戳；EchoDeadline = t0+1000；ResponseDeadline = t0+5000
 → StartResponsePolling(t0, 20ms, 20ms, deadline)  → t0+20, +40, +60 … 发 3D
       同时 InspectTimer 每 10 ms 检查 N_Cr / 回显截止 / 总截止

 [接收] 78 03 22 F1 95 FF FF FF DA  → 与请求 PDU+CS 逐字节相同 ⇒ 回显有效
        （若响应已先到 → 此时直接成功）
 [接收] 78 10 24 62 F1 95 ...       → 非回显、校验通过、处于 WaitingResponse
        ⇒ TP.ProcessPdu：FF，总长 0x24=36，要求 CF 序号 1，启动 N_Cr(1000ms)
 [接收] 78 21 ... 78 26 37 FF…      → CF 1..6 依次追加，N_Cr 每次刷新
        ⇒ 收齐 36 字节 → MessageComplete
 → 业务校验：前 3 字节是否为 62 F1 95
        ├ 不匹配 → ResponseMismatch（只结束本笔，不关门禁）
        └ 匹配   → 回显已验证？→ 成功并 CompleteLocked
                   否则 → 停新 3D，在回显窗口内继续等（不重启计时）

 → 收尾：停 3D → 停检查定时器 → TP.Reset → 等发送出口排空
 → 队列空？是 → ExitDiagnosticModeAsync（从序列索引恢复应用拍）+ DiagnosticActivityChanged(false)
              否 → 继续下一笔，全程保持诊断独占
 → ActuatorDebugViewModel 更新 FirmwareVersionText，显示"读取成功"提示
```

### 5.7 诊断多帧请求（FF + CF）

```text
ExecuteAsync（UdsRequest 长度 > 6，必须显式提供 MultiFrameOptions）
 → EncodeRequestFrames → [FF, CF1…CFn]
 → 独占后逐片：
      等最小间隔 → 检查 SendDeadline → 写 3C → 等写入回执(WriteBudget)
      → 等该片回显(EchoBudget，只认"当前待确认片") → 下一片
 → 全部确认 → t_last = max(最后写入, 最后回显)
             ResponseDeadline = t_last + ResponseBudget
             EchoDrain 固定 50 ms（期间 3C、3D 全不发）
 → 隔离结束 → 丢弃解析器残留 → Reset+BeginReceive → WaitingResponse → 启动 3D
 → 响应阶段：超窗历史回显 ⇒ ProtocolAttributionError（关门禁）；
             其余候选 ⇒ 首读回执后才认领（在此之前有界暂存 ≤32 条）
```

已知限制（文档已明确记录，不是 bug）：
- 发送期间不发 3D ⇒ **无法提前读取负响应 NRC**；
- 若中间件允许无标识的重复/迟到回显，字节层无法区分"当前片"与"内容完全相同的旧片"；
- 时间参数（500/1000/50/200 ms）是**可运行基线**，未经实机抓包确认。

### 5.8 报文窗口数据流

```text
任意线程 RecordSent/RecordReceived
  → pendingEntries（有界，超出丢最旧）
  → DispatcherTimer 50 ms / 最多 200 条
  → entries（ObservableCollection，5000/10000 上限）
  → ProtocolLogWindowViewModel 镜像 displayedEntries（可暂停）
  → ListCollectionView（四维筛选）
  → ListView（虚拟化 + 自动跟随最新 + 保持横向偏移）
```

---

## 6. 关键机制（维护必读）

### 6.1 统一发送出口

| 想做什么 | 正确做法 | 错误做法 |
| --- | --- | --- |
| 发应用写入帧 | 改 `MotorCommandService` 快照，等调度器发 | 直接 `channel.SendWriteRequestAsync` |
| 发诊断请求 | `Diagnostics.ExecuteAsync(request)` | 直接 `channel.SendDiagnosticRequestAsync` |
| 加一条周期应用帧 | 在帧表登记（Slave 发布），调度器自动纳入 `statusReadIds` | 新建一个 `Timer` 单独发 |

调度器把"帧表里所有非诊断、Slave 发布的帧"收集为读取序列，并在 `0x15` 之后按帧表顺序追加到 `appSequence`。当前序列为 `0x15 → 0x33 → 0x34`，每拍完整走一遍；新增从节点读取帧会自动追加到序列尾部。**不要**为周期帧新建 `Timer`，也不要绕过调度器直接写通道。

### 6.2 单帧边界暂停与序列恢复

```text
暂停在 0x15 之后：恢复 → 立即 0x33 →（10 ms）→ 0x34 → 100 ms 后下一拍
暂停在 0x33 之后：恢复 → 立即 0x34 → 100 ms 后下一拍
暂停在 0x34 之后：恢复 → 立即新一拍 0x15 →（10 ms）→ 0x33 →（10 ms）→ 0x34
```
- 只等当前单帧写完，不等整拍；已发出应用帧的回复收尾由下一段的 100 ms 窗口处理；
- 不补发暂停期间漏掉的周期；
- 队列未清空就**不恢复**应用发送（多笔诊断之间不插应用帧）。

**2026-10-21 已实现：应用回复收尾。** 用户反馈应用回复接收中进入诊断会导致首个 3C 无回显；仅等发送任务结束不覆盖对应应用回复的接收。现按 0x15/0x33/0x34 维护待回复标志：`RunAppBeatAsync` 在发送前（同一临界区内）登记，`LinReadReplyParser` 校验通过并分发 `FrameReceived` 时清除；触发诊断立即暂停后续应用发送，并从触发时刻起最多等 100 ms。无标志或提前全清即放行；到期仍有标志则全部清除后放行。首个 3C 仍须等已开始的应用写入完成；若写入跨过截止，写完后不追加等待。

放行时刻为 `max(写入完成, min(标志清空, t_req + 100 ms))`，因此 3C 可能晚于 100 ms（等那笔写入收尾），但不会从写入完成时重新计时。窗口长度由构造参数 `appReplyDrainWindow` 控制（默认 100 ms）。该方案未改变正常 100 ms 应用拍、10 ms 帧间隔、50 ms 残帧字节间超时或现有诊断预算；等待期间接收继续运行。到期清标志不等同于清缓存，异常残包/晚到数据和同 ID 重复在途的处理仍是已知边界。完整决策与验证边界见[诊断架构讨论记录第 19 节](WpfApp1诊断架构讨论记录.md)。

### 6.3 回显与响应同构下的归属判定

3C 回显、3D 响应、应用回复在字节层都可能是 9 字节，且都没有帧头。判定依据是**上下文**而非字节本身：

| 依据 | 用途 |
| --- | --- |
| 回显 == 请求 PDU + 经典校验（逐字节） | 唯一的强匹配，可确定"这是回显" |
| 经典校验通过 + 非回显 | 只能是响应候选，交 TP |
| 当前阶段（SendingRequest / EchoDrain / WaitingResponse） | 决定候选是否允许进入 TP |
| 首个 3D 是否已取得写入回执 | 决定候选是否被认领（F2） |
| 接收时间 vs 各截止时间 | 决定"迟到"与"归属异常" |

原则：**无法确认归属时保留为未知记录，不强行认领**。这条原则牺牲了部分"快速失败"，换来不会误判成功。

### 6.4 代次（generation）与取消

| 代次 | 位置 | 解决的问题 |
| --- | --- | --- |
| `connectionGeneration` | `SerialPortConnectionService` | 旧连接的读写任务不污染新连接 |
| `connectionGeneration` | `DiagnosticTransactionService` | 跨断线重连的旧诊断请求不重放 |
| `sendGeneration` | `LinBusScheduler` | 出口占用归属，避免同步完成/旧任务误清 |
| `enterRequestGeneration` | `LinBusScheduler` | 被撤销的独占申请迟到完成不切模式 |
| `loopGeneration` | `DiagnosticTransactionService` | 旧队列循环收尾不破坏新事务 |

取消语义：`ExecuteAsync` 的 `CancellationToken` **不抛 `TaskCanceledException`**，统一返回 `DiagnosticResult.Failed(Cancelled)`；排队取消立即返回且零写入；已发送后的取消会中止整批，响应阶段取消还会关门禁。

### 6.5 时间语义与参数表

| 参数 | 值 | 起点 | 位置 |
| --- | ---: | --- | --- |
| 应用拍周期 | 100 ms | 连接成功 | `LinBusScheduler.appPeriod` |
| 3D 首拍 / 周期 | 20 ms / 20 ms | 3C 写入完成 t0 | `DiagnosticTiming`、`DiagnosticResponseTiming` |
| SF 业务总截止 | 5000 ms | t0 | `DiagnosticTiming.TotalDeadline` |
| 3C 回显等待（N_As 代理） | 1000 ms | t0 | `DiagnosticTiming.EchoWaitTimeout` |
| N_Cr | 1000 ms | 有效 FF / 每个有效 CF | `ConsecutiveFrameTimeout` |
| 检查定时器 | 10 ms | 事务开始 | `DiagnosticTransactionService.inspectTimer` |
| 残缺帧清理 | 50 ms（字节间） | 最后一次收到字节 | `LinFrameChannel.frameAssemblyTimeout` |
| 诊断前应用回复收尾窗口 | 100 ms（上限） | 触发诊断 t_req | `LinBusScheduler.appReplyDrainWindow` |
| 多帧写入预算 / 回显预算 | 500 / 1000 ms | 每片提交 | `DiagnosticSendTiming` |
| 分片最小/最大间隔 | 0 / 1000 ms | 上一片写完 | `DiagnosticSendTiming` |
| 回显隔离 / 历史回显容忍 | 50 / 200 ms | t_last | `DiagnosticSendTiming` |
| 多帧响应预算 | 5000 ms | t_last | `DiagnosticResponseTiming` |
| 首读前候选暂存上限 | 32 条 | — | `MaxPendingReadCandidates` |
| 日志批处理 / 单批上限 | 50 ms / 200 条 | — | `CommunicationLogService` |
| 端口枚举周期 | 1000 ms | — | `SerialSettingsViewModel` |

所有内部计时使用 `TimeProvider.GetTimestamp()`（单调、与 `TimestampFrequency` 换算），界面日志时间用 `DateTimeOffset.Now`（墙上时间）。这一区分是刻意保留的。

### 6.6 线程模型与锁顺序

| 执行体 | 说明 |
| --- | --- |
| UI 线程 | 所有 VM 属性、命令、Dispatcher 回调 |
| 串口 `DataReceived` | 短临界区读字节，然后入队（可能阻塞等槽位） |
| RX 工作线程 | 从队列取块 → `BytesReceived` → 解析 → 业务回调 |
| TX 工作线程 | 串行 `SerialPort.Write`，完成 `TaskCompletionSource` |
| 定时器线程 | 应用拍、3D 拍、检查定时器、日志搬运、端口轮询 |
| 线程池任务 | `ProcessQueueAsync`（fire-and-forget）、各 `Send*Async` 续体 |

锁：每个组件一个 `stateLock`（`SerialPortConnectionService` 用 `syncRoot`，解析器用 `bufferedBytes`）。

**唯一合法顺序**：`DiagnosticTransactionService.stateLock` → `LinBusScheduler.stateLock` → `LinFrameChannel.stateLock` → `SerialPortConnectionService.syncRoot`。

因此：
- 不在通道锁内调用 `parser.Append`（会触发业务回调）；
- 不在调度锁内发起写入（会申请通道锁）；
- `SendDiagnosticReadCoreAsync` 先 `Task.Yield()` 再发通知，确保通知在锁外触发；
- 事件回调一律 try/catch 包裹，单个订阅方异常不影响其他订阅方。

### 6.7 校验与 PID 速查

| 用途 | 帧 | 校验 | 种子 |
| --- | --- | --- | --- |
| 读取请求 | `55 AA 55 PID 00` | 无 | — |
| 写入应用帧 | `55 AA 55 PID 08 DATA(8) CS` | 增强 | PID |
| 应用回复 | `DATA(8) CS` | 增强 | 恢复出的 PID |
| 诊断请求 3C | `55 AA 55 3C 08 PDU(8) CS` | 经典 | 0 |
| 诊断读取 3D | `55 AA 55 7D 00` | 无 | — |
| 诊断回包 | `PDU(8) CS` | 经典 | 0 |

PID 计算：`P0 = ID0^ID1^ID2^ID4`，`P1 = ~(ID1^ID3^ID4^ID5)`，`PID = ID | P0<<6 | P1<<7`。校验折叠：`sum > 0xFF` 时反复 `sum -= 0xFF`，最后 `~sum & 0xFF`。

---

## 7. 当前存在的问题与可改进点

分级：**P1** = 已知会导致错误行为或数据风险；**P2** = 影响可维护性/可测试性/可扩展性；**P3** = 工程化与代码整洁。

### 7.1 P1：已知缺陷与验证缺口

**（1）诊断释放竞态（已记录、未修复）**

`../版本/CHANGELOG.md` 2026-10-20 明确记录：请求已交接为 `active` 但**首个 `0x3C` 尚未提交**时调用 `Dispose()`，调用方可能先收到 `Shutdown`，而恢复执行的发送路径仍可能提交一个**迟到的 3C**。该问题已复现 3 次，修复被暂缓。位置：`DiagnosticTransactionService.RunTransactionAsync` 与 `Dispose` 的交界。

建议：在 `SendSingleFrameRequestAsync` / `SendMultiFrameRequestAsync` 提交写入前，于锁内复核 `disposed`（或引入"已提交不可撤回"的显式状态），并在 `Dispose` 中等待该提交窗口收敛后再返回。

**（2）仓库内没有任何自动化测试**

诊断层的正确性依赖大量并发/时序不变量（F1～F5、R1～R22 共数百项检查），但验证全部在**仓库外临时工程**完成、随后删除。这意味着：任何后续改动都无法回归验证，只能靠人工推理。这是当前最大的可持续性风险。

建议：把这些检查固化为仓库内 `tests/` 项目（`Microsoft.Extensions.TimeProvider.Testing` 的虚拟时钟 + 串口替身），至少覆盖：分包/重组、回显归属、门禁策略、断线与释放时序、应用拍恢复。

**（3）诊断门禁无解除入口**

`ResetDiagnosticGate()` 存在但未接界面。一旦因归属异常关门禁，用户只能重启应用，且重连不解除。虽然有"仅在确有陈旧字节证据时关门"的收窄，但缺少恢复路径仍是运维风险。

### 7.2 P2：架构与可维护性

**（4）10 ms 帧间隔依赖系统定时器分辨率**（`../协议/PROTOCOL.md` §7、§12.4）

应用序列已改为三帧 + 10 ms 间隔，但 `Task.Delay(10 ms)` 在 Windows 上通常实际为 15～16 ms，无法保证精确 10 ms。当前实现保证不小于 10 ms；若设备对间隔上限敏感，需要改用高精度定时方案并实机抓包确认。

**（5）业务类型由界面中文文案决定**

`ActuatorDebugViewModel.TryMapVersionKind` 用 `"读取固件版本"` 这样的按钮文字 `switch` 出 `DeviceVersionKind`。这违反了自己定下的规则（"不把界面中文按钮文字作为协议判断依据"，见讨论记录 §12.3）。文案一改（或做多语言）功能即失效。

建议：`CommandParameter` 直接传 `DeviceVersionKind`，或拆成三个独立命令。

**（6）VM 过重、职责混杂**

`ActuatorDebugViewModel` 732 行同时承担：电机命令、位置钳制、状态位→颜色映射、版本读取、预设编辑、提示条计时。`SerialSettingsViewModel` 同时是"设置面板状态"和"连接策略"。

建议：拆出 `MotorStatusPresenter`（状态映射）、`PresetEditorViewModel`、`NotificationService`（提示条）；`SerialSettingsViewModel` 把端口监测移到 `SerialConnectionMonitor`。

**（7）无 DI，组合根在 VM 内，隐藏依赖**

`new MotorPresetStorageService()` 直接写在 VM 里（路径来自 `AppContext.BaseDirectory`），`ProtocolLogWindow` 自己 `new` VM。当前规模可接受，但不利于替换实现与单元测试。

建议：引入 `Microsoft.Extensions.DependencyInjection`（.NET 10 桌面项目成本很低），把组合根上移到 `App.OnStartup`，VM 只声明构造函数依赖。

**（8）配置参数散落且不可调**

100 ms / 20 ms / 5 s / 50 ms / 32 条等分散在 6 个文件里，部分写死在默认参数中。现场调试（不同中间件、不同波特率）需要改代码重编译。

建议：抽 `DeviceProfile`（或 `appsettings.json` / `config/device-profile.json`）统一承载时序与限额，`DiagnosticTiming`/`DiagnosticSendTiming` 由配置构造。

**（9）无持久化日志、无全局异常处理**

`App.xaml.cs` 是空的；异常多数被 `catch { Debug.WriteLine }` 吞掉（Release 下不可见）。现场问题只能靠报文窗口（内存、重启即失）。而"发送失败""调度异常"这类信息恰恰最需要留痕。

建议：引入 Serilog（或轻量文件日志），在 `App` 挂 `DispatcherUnhandledException` / `AppDomain.UnhandledException` / `TaskScheduler.UnobservedTaskException`；把 `Debug.WriteLine` 换成结构化日志并保留级别。

**（10）接口不足导致可测性差**

`Services` 里只有 3 个接口（`ISerialPortConnectionService`、`ISerialPortDiscoveryService`、`ICommunicationLogService`），而调度器、协调层、通道都是 `sealed` 具体类。`TimeProvider` 注入做得很好，但其余依赖无法替换。

**（11）服务层耦合 WPF**

`CommunicationLogService` 构造需要 `Dispatcher`（由 VM 传 `Application.Current.Dispatcher`），`ProtocolLogWindowViewModel` 直接用 `Application.Current.Dispatcher`。这让通信日志无法在非 UI 环境复用/测试。

建议：日志服务只暴露事件/集合并内部抽象"UI 调度器"，或把 Dispatcher 编组上移到 VM。

### 7.3 P3：工程化与整洁度

| # | 问题 | 位置/证据 |
| --- | --- | --- |
| 12 | 界面存在大量**静态占位**：NAD 读写、休眠、方向、缓启停、堵转、保持电流等控件无命令绑定；状态栏 `HE11`、`--` 为硬编码 | `ActuatorDebugView.xaml:67-82`、`MainWindow.xaml:320-322` |
| 13 | 模板残留：`Title`/`UserName`/`StatusText`/`TestCommand`、"系统就绪" 示例代码仍在 | `ActuatorDebugViewModel.cs:169-181` |
| 14 | 未使用/未绑定成员：`InitializationStepsText`、`ProtocolStatusText`（无 XAML 引用）、`Controls/HamburgerMenuItemTemplateSelector`（无引用）、csproj 里的 `Converters/` 文件夹不存在 | 见 3.1、csproj |
| 15 | XAML 样式内联且大量单行压缩，颜色硬编码（`#F3F3F3`、`#0078D4`、`#C42B1C`…），无资源字典/主题，深色模式不可用 | `MainWindow.xaml`、`ActuatorDebugView.xaml` |
| 16 | `MainWindow.xaml.cs` 用 code-behind 做菜单动画与窗口管理（可接受），但 `IsNavigationExpanded` 是窗口级 DP，模板通过 `RelativeSource AncestorType=MainWindow` 绑定，复用性差 | `MainWindow.xaml.cs:28-92` |
| 17 | `appSequence` 顺序由帧表枚举顺序隐式决定（`0x15` 固定在前，`0x33/0x34` 靠手写顺序保证），新增帧时容易插错位置且无测试覆盖 | `LinBusScheduler` 构造函数、`MainWindowViewModel` 帧表 |
| 18 | `ProcessQueueAsync` 为 fire-and-forget，`finally` 内若抛异常将成为未观察异常 | `DiagnosticTransactionService.cs:407` |
| 19 | `SerialPortConnectionService.Dispose` 同步 `Task.WaitAll`，UI 线程退出时可能卡顿；RX 队列满会阻塞串口事件线程 | 见 4.1.1 |
| 20 | 异常吞掉后无用户可见反馈：应用帧发送失败仅 `Debug.WriteLine`，界面仍显示"已连接" | `LinBusScheduler.RunAppBeatAsync` catch |
| 21 | 文档漂移：历史文档曾把 `0x34` 标为"代码待登记"、把调度描述为两帧一拍；`../入门/WPF_MahApps_MVVM_基础框架搭建.md` 的检查项仍把 `CommunityToolkit.Mvvm` 标为未完成（实际已接入并使用） | `../协议/PROTOCOL.md` §3、`APPLICATION_FRAMES.md`、框架搭建文档 §当前阶段目标 |
| 22 | `../版本/VERSION_HISTORY.md` 中 `0x3C` 多帧仍未打标签（"待打标签"），与"提交 `18f67a1` 已实现"不一致 | `../版本/VERSION_HISTORY.md` |
| 23 | 无 CI、无发布配置（self-contained 发布仍在待办清单里）；`bin/obj` 与运行目录 `config/` 未纳入版本管理（合理，但缺发布说明） | 仓库根 |

### 7.4 改进路线建议

**短期（低风险、收益直接）**
1. 修复 7.1(1) 释放竞态，并补一条针对该场景的回归检查；
2. 门禁加一个开发者入口（菜单/快捷键 + 二次确认），或至少在报文窗口显示"诊断已锁止"状态；
3. 把 `TryMapVersionKind` 改为枚举参数（去掉中文文案耦合）；
4. 清理模板残留与未绑定成员，删除未使用的控件类，补齐/去掉 csproj 的 `Converters` 文件夹；
5. 引入文件日志 + 全局异常处理（半天工作量，现场价值最高）。

**中期（结构性）**
6. 建立仓库内测试工程，先覆盖诊断层不变量（虚拟时间 + 假串口）；
7. 抽 `DeviceProfile` 配置，把时序参数移出代码；
8. 拆 `ActuatorDebugViewModel`，引入 `Microsoft.Extensions.DependencyInjection`；
9. 应用拍已改为"帧序列 + 索引"并接入 `0x34`；后续新增读取帧只需登记帧表并补解析/界面；
10. 为 `Services` 关键类补接口（`IFrameChannel`、`IDiagnosticTransactionService`），提高可替换性。

**长期（能力扩展）**
11. 负响应 `7F` 后续扩展：`78` 已实现续期与续收；仍待 P2/P2* 实际应用、`78` 次数/整笔绝对上限策略、S3 保活；
12. OTA/升级业务：复位后的启动验证（等待时长、探测方式、预期版本）未自动化；擦除完成判据、CRC32 两端一致性、下载重传/断点续传与零 Seed 的最终处理路径待设备确认（零 Seed 当前记日志退出）；会话保活（S3）与退出是否发 `10 01` 待协议确认；
13. 主题化（资源字典 + 深色模式）、多语言（同时解决第 5 条根因）；
14. CI：`dotnet build` + 测试 + 打包（含 self-contained 发布配置）。

---

## 8. 维护指南：常见改动怎么做

### 8.1 新增一条周期读取应用帧（以 `0x35` 为例）

1. `MainWindowViewModel` 的 `LinFrameCatalog` 增加 `new LinFrameDefinition(0x35, 8, LinPublisher.Slave, LinChecksumType.Enhanced)`；
   → 调度器会自动把它追加到 `appSequence` 尾部，无需改定时器或插 `Task.Delay`。
2. 新增解析器，按协议布局提取信号。
3. 在 `MainWindowViewModel.HandleStatusFrameReceived` 增加 `frame.Id == 0x35` 分支，`Dispatcher` 回 UI 更新 VM 属性。
4. 在 `ActuatorDebugViewModel` 增加对应显示属性/赋值。
5. 验证新增后的序列顺序、10 ms 帧间隔和新增暂停边界的恢复位置；帧表枚举顺序决定序列顺序。
6. 验证：三帧连续回包（27 字节）在同一字节块内的拆包；诊断暂停/恢复的三个边界。

### 8.2 新增一个诊断服务（如某 DID 读取）

1. `DeviceInformationService` 增加 `DeviceVersionKind`（或新建业务服务）与 DID 映射；
2. 请求 `[0x22, hi, lo]`，预期前缀 `[0x62, hi, lo]`；
3. `ActuatorDebugViewModel` 加命令 → 调业务服务 → 更新显示；
4. **不要**自己发 3C、不要自己起定时器：`ExecuteAsync` 已处理排队、独占、回显、TP、超时、收尾；
5. 若请求长度 > 6：提供 `MultiFrameOptions`，否则会被 `InvalidRequest` 拒绝（这是刻意的，避免误用 SF 时间规则）。

### 8.3 调整时序参数

- 应用拍：`LinBusScheduler` 构造参数 `appPeriod`（当前由默认值 100 ms 提供）；
- SF 诊断：`DiagnosticTiming`（`DiagnosticRequest.Timing`）；
- 多帧：`DiagnosticSendTiming` / `DiagnosticResponseTiming`；
- **改之前先确认 `../协议/PROTOCOL.md` 与讨论记录中对应参数的来源与约束**（多数值待实机抓包确认，不要凭感觉调）。

### 8.4 加一个页面

1. 新建 `Views/XxxView.xaml` + `ViewModels/XxxViewModel`；
2. 在 `MainWindowViewModel` 构造 `MenuItems` 里加 `new("标题", "\uE713", vm)`；
3. 在 `MainWindow.xaml` 的 `ContentControl.Resources` 里加 `DataTemplate DataType="{x:Type vm:XxxViewModel}"` → `views:XxxView`。

### 8.5 修改通信层的"红线"

- 不在 UI 线程等待发送完成；
- 不在锁内发起跨层调用或触发事件；
- 不给应用帧开第二条发送路径；
- 不把"串口写完"当成"总线发出"或"电机到位"；
- 不在无上下文的情况下把 9 字节候选认定为回显或响应。

---

## 9. 附录

### 9.1 术语表

| 术语 | 含义 |
| --- | --- |
| 应用帧 | LIN 无条件帧，业务数据（`0x15` 写入、`0x33`/`0x34` 读取） |
| 诊断帧 | `0x3C` 请求 / `0x3D` 响应，承载 UDS |
| SF / FF / CF | 单帧 / 首帧 / 连续帧（LIN-TP 分段类型） |
| 回显（Echo） | 中间件把收到的 3C PDU 原样回送，用于确认"这一片确实发出去了" |
| 回显隔离（EchoDrain） | 多帧全部确认后的固定静默窗口，防止迟到回显污染响应阶段 |
| 归属（Attribution） | 判断一个无帧头候选属于回显、响应还是噪声 |
| 门禁（Gate） | 跨事务准入锁，关闭后拒绝新诊断，需显式复位 |
| 独占（Exclusive） | 诊断期间暂停应用发送的状态 |
| 序列索引（`appStepIndex`） | 应用拍下一次该发 `appSequence` 中的哪一帧 |
| 快照（Snapshot） | `0x15` 的 8 字节当前值，界面写、调度读 |
| t0 / t_last | 3C 串口写完时刻 / 多帧最后一片完成时刻 |

### 9.2 文档地图（谁该看哪份）

| 文档 | 用途 | 时效性提示 |
| --- | --- | --- |
| `ARCHITECTURE.md`（本文） | 代码结构与维护入口 | 当前 |
| `../协议/PROTOCOL.md` | 唯一现行协议（帧格式、校验、时序、`0x33` 位布局、`0x34` 浮点） | 现行；应用帧三帧 + 10 ms 帧间隔 |
| `../版本/CHANGELOG.md` | 变更历史与每次的验证范围 | 现行；含 P1 已知缺陷记录 |
| `WpfApp1诊断架构讨论记录.md` | 诊断架构的**决策依据**（为什么这么设计） | 定稿于 2026-10-18/19/20，§3 的"现有实现基线"是历史快照 |
| `../修复/3C连续帧功能实施计划.md` | 多帧能力的设计与决策 | 已实施，§10 为实施记录 |
| `../修复/3C多帧实现审查与修复方案.md` | F1～F5 缺陷根因与验收项 | 已修复，可作回归用例来源 |
| `../修复/诊断模块并发缺陷修复说明.md` | 5 个 P1 并发缺陷的修复细节 | 已修复 |
| `../版本/VERSION_HISTORY.md` | 版本标签 | **待补 `0x3C` 标签** |
| `../入门/WPF_MahApps_MVVM_基础框架搭建.md` | 脚手架教学（历史） | 部分检查项已过时，仅作背景 |

### 9.3 一句话总结

> 这是一个**分层清晰、时序严谨、但工程化不足**的单体 WPF 上位机：通信与诊断部分（`Services/` 约 5.2k 行）以"唯一发送出口 + 单帧边界暂停 + 代次保护 + 单一接收缓存 + 结果单次发布"构建了可靠的 LIN/UDS 栈，并用大量文档固化了设计决策；代价是**没有留在仓库里的自动化测试**、**时序参数不可配置**、**UI 层仍带脚手架痕迹**，以及一个**已复现但未修复的释放竞态**。后续维护应优先补测试与日志，再谈功能扩展。
