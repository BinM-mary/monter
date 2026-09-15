# 通信协议帧记录

本文档是上位机与设备通信的协议帧记录入口。后续新增、修改或实现帧处理功能时，必须同步在本文档登记对应的命令、数据定义和处理状态。

## 1. 主机发送帧格式

```text
AA 55 | CMD | SUB_CMD | LEN_H | LEN_L | DATA[0] ... DATA[n-1] | CHECKSUM
```

| 字段 | 长度 | 定义 |
| --- | ---: | --- |
| Break | 2 bytes | 固定帧头：`0xAA 0x55` |
| Cmd | 1 byte | 主命令，取值范围：`0x10` ～ `0x7F` |
| Sub Cmd | 1 byte | 子命令，取值范围：`0x00` ～ `0x7F` |
| Data Length | 2 bytes | 数据区长度，按高字节在前：`LEN_H`、`LEN_L` |
| Data | n bytes | 数据区：`DATA[0]` ～ `DATA[n-1]` |
| Checksum | 1 byte | `CMD + SUB_CMD + LEN_H + LEN_L + DATA` 的 XOR 校验值 |

- 数据区长度：`n = (LEN_H << 8) | LEN_L`。
- 整帧长度：`7 + n` 字节。
- 帧头 `0xAA 0x55` 和末尾 `CHECKSUM` 自身不参与校验。

## 2. 从机回应帧格式

```text
55 AA | CMD | SUB_CMD_RESP | LEN_H | LEN_L | DATA[0] ... DATA[n-1] | CHECKSUM
```

| 字段 | 长度 | 定义 |
| --- | ---: | --- |
| Break | 2 bytes | 固定帧头：`0x55 0xAA` |
| Cmd | 1 byte | 主命令，取值范围：`0x10` ～ `0x7F` |
| Sub Cmd Resp | 1 byte | 支持请求时：`请求 SUB_CMD + 0x40`；不支持请求时：`0xDF` |
| Data Length | 2 bytes | 数据区长度，按高字节在前：`LEN_H`、`LEN_L` |
| Data | n bytes | 数据区：`DATA[0]` ～ `DATA[n-1]` |
| Checksum | 1 byte | `CMD + SUB_CMD_RESP + LEN_H + LEN_L + DATA` 的 XOR 校验值 |

- 数据区长度：`n = (LEN_H << 8) | LEN_L`。
- 整帧长度：`7 + n` 字节。
- 帧头 `0x55 0xAA` 和末尾 `CHECKSUM` 自身不参与校验。

## 3. 校验算法

初始校验值为 `0x00`，从 `CMD` 开始依次异或至最后一个数据字节。

```c
uint8_t chk = 0;
for (uint8_t i = 0; i < len; i++)
{
    chk ^= p_buf[i];
}
return chk;
```

发送帧中：

- `p_buf[0]` 是 `CMD`。
- `len = 4 + n`，即覆盖 `CMD`、`SUB_CMD`、`LEN_H`、`LEN_L` 和全部 `DATA`。
- 计算结果写入帧末尾的 `CHECKSUM`。

## 4. 后续帧登记规范

每新增或修改一项协议帧处理时，在下表增加或更新对应记录，并写明数据区的字节定义、方向和当前实现状态。

| 功能 | 方向 | CMD | SUB_CMD | 数据区定义 | 校验 | 上位机处理状态 |
| --- | --- | --- | --- | --- | --- | --- |
| 暂无 | - | - | - | - | XOR（见第 2 节） | 待补充 |

## 5. 帧处理规则

1. 接收时根据帧方向确认帧头：主机发送帧为 `0xAA 0x55`，从机回应帧为 `0x55 0xAA`。
2. 读取 `CMD`、子命令和两个长度字节后，计算数据区长度 `n`。
3. 收齐 `n` 字节数据和 1 字节校验值后，再进行 XOR 校验。
4. 从机正常回应时，确认 `SUB_CMD_RESP = 请求 SUB_CMD + 0x40`；若值为 `0xDF`，表示该请求不受支持。
5. 只有帧头、长度和校验都正确的帧，才交给对应命令处理逻辑。
