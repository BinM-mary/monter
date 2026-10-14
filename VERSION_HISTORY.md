# 版本标记与回退

仓库：[BinM-mary/monter](https://github.com/BinM-mary/monter)，发布分支为 `main`。

## 2026-10-14：ID 0x33 读取收发与轮询

版本标签：`lin-id33-read-20261014`；通过标签查询对应提交。升级前提交为 `2efa31994d423919e7a5411e3b46d5a74c2885a4`，此前版本标签继续保留。

- 已登记 ID `0x33`：从节点发布，8 字节 DATA，Enhanced checksum。
- 读取请求为 `55 AA 55 73 00`；接收只有 8 字节 DATA + 1 字节 Checksum，反算 PID/ID 后按帧表分发。
- 串口连接后每 100 ms 自动发送，不等待回复；断开或退出时停止，重连后恢复。上次串口写入未完成时跳过本拍。
- 日志区分读取请求和回复恢复字段；包含逐条增量追加/淘汰及合并筛选选项刷新，减少窗口全量刷新开销。
- 电机位置、极限和状态字段的界面绑定仍待实现，设备动作按钮仍禁用；诊断、TP 和升级尚未接入。
- CHANGELOG 记录本次源码适配的隔离构建为 0 警告、0 错误，17 项隔离协议检查通过；日志性能修改也记录构建通过。本次发布整理未重新运行构建或测试，实机收发和流畅度仍待验证。

查看该版本时，先保存未提交工作，再创建独立分支：

```powershell
git fetch origin --tags
git switch -c review/lin-id33-read-20261014 lin-id33-read-20261014
```

## 2026-10-10：LIN 应用帧框架

| 版本标签 | 含义 | 提交 |
| --- | --- | --- |
| `pre-lin-application-20261010` | 升级前的旧协议版本，已包含日志窗口隐藏恢复修复 | `15dbc2565e37c5ab89fe72b0bc19194b2b042b57` |
| `lin-application-framework-20261010` | LIN 共用单帧底层、通用应用帧发布/读取、LIN 日志；电机帧表和业务绑定待接入 | 通过标签查询；该提交的直接父版本为上面的回退基线 |

本次迁移之前，日志窗口修复仍在工作区中未提交。先将该修复独立提交，再提交 LIN 迁移，确保切回旧协议时仍能保留已完成的日志修复。更早的已发布提交 `d0d9df16ad24f9551ed0be0d7010c164b3b5e43d` 也仍在 Git 历史中。

本版本的实现、限制和验证结果见 [PROTOCOL.md](PROTOCOL.md) 与 [CHANGELOG.md](CHANGELOG.md)。构建通过，0 警告、0 错误；37 项模拟串口检查通过，实机联调仍为 `pending test`。

## 查看或继续使用旧版本

先保存当前未提交的工作，再基于旧标签创建一个独立分支：

```powershell
git fetch origin --tags
git switch -c rollback/legacy-protocol-20261010 pre-lin-application-20261010
```

该操作保留 `main` 和新版本提交，可在旧版本上查看、构建或继续修改。分支名已存在时，改用其他新名字或切换到已有分支。

返回最新主分支：

```powershell
git switch main
```

## 在主分支撤销本次迁移

如果希望 GitHub 的 `main` 恢复旧协议且保留发布历史，可在确认需要回退后执行：

```powershell
git switch main
git revert lin-application-framework-20261010
git push origin main
```

`revert` 创建新的撤销提交，不删除旧提交。若主分支之后又修改了同一批文件，撤销可能需要处理冲突；按标签创建旧版本分支不受后续源码修改影响。

本次上传只建立提交和标签，没有执行回退，也没有强制推送。

## 查询对应提交

```powershell
git show --no-patch --format=fuller pre-lin-application-20261010
git show --no-patch --format=fuller lin-application-framework-20261010
```

标签保存 Git 跟踪的源码和文档；`bin/obj` 和程序运行目录下的本地配置不在该版本标记中。
