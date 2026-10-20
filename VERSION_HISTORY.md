# 版本标记

仓库：[BinM-mary/monter](https://github.com/BinM-mary/monter)，发布分支为 `main`。通过标签查询对应提交；更早的提交与标签仍保留在 Git 历史中。

## 版本标签清单

| 标签 | 日期 | 内容 |
| --- | --- | --- |
| （待打标签） | 2026-10-19 | `0x3C` 多帧请求、逐片回显、发送/响应双截止、归属门禁与 TimeProvider 改造；详见 [CHANGELOG.md](CHANGELOG.md) 2026-10-19 条目 |
| `lin-id33-read-20261014` | 2026-10-14 | ID `0x33` 读取收发、回复反算与 100 ms 轮询；升级前提交 `2efa319` |
| `lin-application-framework-20261010` | 2026-10-10 | LIN 共用单帧底层与通用应用帧接口；电机帧表和业务绑定待接入 |
| `pre-lin-application-20261010` | 2026-10-10 | 升级前的旧协议版本（`AA 55 / CMD / LEN / XOR`，已废弃），含日志窗口隐藏恢复修复；提交 `15dbc25` |

## 查看对应提交

```powershell
git fetch origin --tags
git switch -c review/<分支名> <标签名>
git show --no-patch --format=fuller <标签名>
```

标签保存 Git 跟踪的源码和文档；`bin/obj` 和程序运行目录下的本地配置不在版本标记中。当前协议以 [PROTOCOL.md](PROTOCOL.md) 为准。
