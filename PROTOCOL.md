# LIN 应用帧串口接口与收发架构

更新日期：2026-10-10。

本文件记录当前生效的协议及实现范围。新增或修改帧处理时，必须同步维护本文件和 `CHANGELOG.md`。

旧的 `AA 55 / CMD / SUB_CMD / LEN / DATA / XOR` 协议已从运行代码移除，不再兼容；历史开发记录仍保留在 `CHANGELOG.md`。

## 1. 链路和当前范围

```text
上位机 → 串口 → 中间件（LIN 主节点、完整协议栈）→ LIN → 电机
```

- 双方已约定：串口上的 `55 AA` 表示 LIN Break，其余 LIN 字段保留。
- 本阶段完成 LIN 单帧通道和应用帧发布/读取接口。
- 电机应用帧表尚未提供；启动帧表为空，未登记 ID 不猜测数据长度或业务含义。
- 具体电机按钮的 LIN ID、信号布局和状态解析尚未接入，相关设备操作禁用并显示原因。
- 本地 OPEN 预设及串口设置继续使用。
- 诊断事务、LIN TP 分包组包、诊断调度和升级流程留待后续接入，共用本阶段的单帧通道。

## 2. 串口发送格式

### 发布主节点应用数据

```text
55 AA | 55 | PID | DATA[0..n-1] | CHECKSUM
Break   Sync
```

### 读取从节点应用数据

```text
55 AA | 55 | PID
Break   Sync
```

发送 Header 不附加 Data、占位数据、长度或 checksum。

| 字段 | 长度 | 规则 |
| --- | ---: | --- |
| Break 标记 | 2 bytes | 固定 `55 AA`，中间件还原为总线 Break |
| Sync | 1 byte | 固定 `55` |
| PID | 1 byte | 6 位 ID 加 P0/P1；API 接收 ID，不接收 PID |
| Data | 1～8 bytes | 长度由帧表确定；线上没有独立 LEN 字节 |
| Checksum | 1 byte | 根据该帧定义选择 classic 或 enhanced |

Header 长度为 4 字节；完整帧长度为 `5 + n` 字节。

应用 ID 为 `0x00～0x3B`；`0x3C/0x3D` 属于诊断，`0x3E/0x3F` 保留。共用编解码器支持诊断单帧的基本格式，但应用帧服务拒绝诊断 ID，不实现诊断事务。

## 3. PID 和 checksum

```text
P0 = ID0 XOR ID1 XOR ID2 XOR ID4
P1 = NOT(ID1 XOR ID3 XOR ID4 XOR ID5)
PID = ID | (P0 << 6) | (P1 << 7)
```

- 示例：ID `0x00` → PID `0x80`；ID `0x13` → PID `0xD3`；ID `0x3D` → PID `0x7D`。
- classic：只累加 Data，进位回加，最终逐位取反。
- enhanced：先累加 PID，再累加 Data，进位回加，最终逐位取反。
- Break 和 Sync 不参与数据 checksum。
- `0x3C/0x3D` 固定 8 字节数据及 classic checksum。
- 应用帧的校验类型必须来自电机协议，不能由 ID 范围或收到的字节猜测。

已知向量（用于编解码检查，不是新电机帧定义）：

```text
Data = 4A 55 93 E5
classic checksum = E6
PID = D3 时 enhanced checksum = 13

完整帧示例：55 AA 55 D3 4A 55 93 E5 13
```

依据：[LIN 2.2A](https://community.nxp.com/pwmxy87654/attachments/pwmxy87654/16-bit/18786/3/LIN_Specification_Package_2.2A.pdf) 第 2.3 节；[Microchip LIN 帧说明](https://developerhelp.microchip.com/xwiki/bin/view/applications/lin/data-link-layer/)。

## 4. 帧定义与模块边界

`LinFrameDefinition` 明确包含：

| 属性 | 含义 |
| --- | --- |
| Id | 6 位帧 ID |
| DataLength | 1～8 字节，由帧定义确定 |
| Publisher | Master 或 Slave，决定数据由谁发布 |
| ChecksumType | Classic 或 Enhanced |

`LinFrameCatalog` 在启动时接收完整帧定义，同一 ID 不允许重复。信号字节序、缩放、有效值和位布局由后续电机业务适配定义，不在底层猜测。

```text
电机业务/界面（信号映射待提供）
           ↓
ApplicationFrameService（应用帧发布、一次读取、有效从节点帧事件）
           ↓
LinFrameChannel（共用构帧、接收校验和日志）
           ↓
SerialPortConnectionService（共用 TX/RX 有序队列）
           ↓
串口 → 中间件

后续诊断服务 → 诊断事务/TP → 同一个 LinFrameChannel
```

| 文件 | 职责 |
| --- | --- |
| `Models/LinFrameDefinition.cs`、`Models/LinFrame.cs` | 帧定义和完整有效帧 |
| `Services/LinFrameCatalog.cs` | ID、长度、发布节点和校验类型的固定配置 |
| `Services/LinFrameCodec.cs` | Header/完整帧构造、PID 和 checksum |
| `Services/LinFrameParser.cs` | 缓存拆帧、错误恢复、不完整帧超时 |
| `Services/LinFrameChannel.cs` | 共用串口帧通道、日志和有效帧分发 |
| `Services/ApplicationFrameService.cs` | 应用帧发布、单次读取等待及从节点状态帧分发 |

## 5. 应用帧接口和流程

### PublishAsync(id, data, cancellationToken)

1. 查找已登记的应用帧定义，要求 Publisher 为 Master。
2. 数据长度必须严格等于该帧 DataLength。
3. 构造完整帧并交给既有串口 TX 队列。
4. 串口实际写入成功后记录 TX 日志。

返回成功只表示串口写入完成，不代表中间件已经在总线上发送，也不代表电机动作完成。

### ReadAsync(id, responseTimeout, cancellationToken)

1. 查找已登记的应用帧定义，要求 Publisher 为 Slave。
2. 同时只允许一个单次应用读取等待，其他读取按序等待。
3. 先登记等待对象，再发送该 ID 的 Header，避免快速返回的帧遗漏。
4. Header 串口写入完成后开始响应等待预算。
5. 接收相同 ID 且通过长度、PID 和 checksum 校验的完整帧后完成读取。
6. 超时、取消、断开、连接代次改变或释放服务时结束等待；不自动重发。
7. 排队的旧读取不能在重连后继续发出 Header。

等待任务不阻塞 RX 线程；不把诊断等待或业务等待放入串口底层。

### FrameReceived

- 共用通道分发每条有效完整帧，供应用和后续诊断处理订阅。
- 应用帧服务只分发 Publisher 为 Slave 的应用帧；主节点完整帧回显和诊断帧不完成应用读取。
- 未匹配当前读取的从节点应用帧仍会分发，便于更新周期状态。
- 单个业务订阅异常不会中止同一接收块中后续完整帧的处理。
- UI 属性更新应由业务适配切回 WPF Dispatcher。

## 6. 当前接收格式与恢复策略

接收解析器按以下完整帧格式实现：

```text
55 AA | 55 | PID | DATA[0..n-1] | CHECKSUM
```

该格式需要用中间件实际串口返回确认。若返回只包含 Data/Checksum、存在独立 Header 回显或独立错误状态报文，应先明确约定，再修改适配器；不自动猜测多种格式。

1. 串口一次读取可能只收到部分字节，也可能包含多条完整帧，均先进入接收缓存。
2. 找到 Break/Sync 和有效 PID 后，从帧表取得长度，再收齐完整候选帧。
3. 正常候选帧的数据区允许出现 `55 AA`，不能按标记直接切割。
4. 未登记 ID、错误 PID、无效 checksum、噪声和不完整帧超时只进入错误日志，不交给业务，也不完成读取。
5. 无效候选帧通过重新寻找帧头恢复；不把错误候选长度后的全部字节直接丢弃。
6. 不完整帧默认在连续 1000 ms 没有新字节后清理，通道每 100 ms 检查一次。该预算用于串口组帧，与应用读取等待和未来诊断超时分开。
7. 连接状态改变时清理缓存和读取等待。

没有线上请求序号的情况下，同 ID 周期帧或超时后的迟到帧无法仅凭字节证明它响应哪次 Header；调用方不能把收到状态帧直接解释为某次动作完成确认。后续根据中间件调度/状态接口明确事务关联。

## 7. 日志和界面

- 日志显示时间、方向、类型、Break、Sync、ID、PID、Data 长度、Data、checksum 类型、Checksum、校验状态和原始字节。
- Data 长度是界面计算值，不是线上新增的 LEN 字段。
- Header 类型显示为“帧头”，明确无数据 checksum。
- 筛选为方向、类型、ID、PID；保留原有级联筛选和隐藏恢复修复。
- 50 ms 批处理、每批最多 200 条、5000/10000 条保留策略继续使用。
- 串口设置和本地 OPEN 配置保留；没有电机帧映射时，原读取/动作按钮禁用，顶部显示“LIN 应用帧定义待配置”。
- 原静态故障灯、初始化状态和装配类型尚未接入新电机实际信号，不能作为实测状态。

## 8. 待确认及验证范围

| 项目 | 当前状态 |
| --- | --- |
| 电机控制/状态 ID、长度、发布节点、校验类型 | 待提供帧表或 LDF |
| 信号字节/位布局、字节序、缩放和有效值 | 待提供，设备按钮未绑定 |
| 必须周期发送的帧、周期和调度位置 | 待确认；本阶段不新增自动周期调度 |
| 中间件接收返回、Header/完整帧回显、无响应和错误状态 | 待确认；接收代码按完整格式实现 |
| 中间件排队、总线发送完成确认和帧槽仲裁 | 待确认；串口写入不冒充总线完成 |
| 诊断/TP/升级 | 本阶段未实现 |
| 静态构建 | 隔离输出 WPF 构建通过，0 警告、0 错误 |
| 隔离协议检查 | 37 项通过，链接实际生产源码，使用模拟串口 |
| 中间件/电机实机收发和 UI 联调 | pending test |

隔离检查覆盖 PID/校验已知向量、Header 和完整帧、分段/连续接收、数据内标记、错误恢复、未知 ID、不完整帧超时、旧协议拒绝、发布/读取、错误和其他帧不完成读取、立即响应、读取串行、超时/取消、断开重连及释放。检查工程位于系统临时目录，未向主项目添加测试依赖。
