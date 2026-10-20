# GitHub 发布清单

## 可以提交

- `bridge/`：游戏内 C# 桥接源码。
- `src/`：MCP Server、Runtime 和本地传输层。
- `mod/`：可直接使用的 Lua、`Info.json` 和桥接 DLL。
- `scripts/`：构建、安装、启动和实机回归脚本。
- `tests/`：不启动游戏也能运行的协议测试。
- `assets/`：小于 1 MB 的创意工坊预览图。
- `examples/`、`research/`、`README.md`、`LICENSE`、`package.json`。

桥接 DLL 只有约 128 KB，可随源码提交，方便没有本地编译环境的用户直接安装。源码仍是权威版本。

## 不要提交

- `work/`：包含临时令牌、Runtime 端口、PID 和日志。
- `%LOCALAPPDATA%\AmazingCultivationMCP\bridge.json`：包含当前会话的 WebSocket 令牌。
- 游戏本体、游戏程序集、存档、Steam 登录数据或整个 `Mods` 目录。
- 本机专用的 MCP 配置；只提交已脱敏的 `examples/codex.mcp.json`。

这些项目已由 `.gitignore` 覆盖。提交前仍建议运行一次敏感信息检查。

## 提交前验证

```powershell
$env:ACS_ROOT = 'D:\SteamLibrary\steamapps\common\AmazingCultivationSimulator'
.\scripts\build-bridge.ps1
npm test
npm run smoke
```

`npm run smoke` 需要游戏已经启动、模组已启用且 Runtime 正在运行。完整实机回归可运行：

```powershell
node .\scripts\live-core-test.mjs
```

回归脚本会添加并撤销一条测试命令，结束时释放事务并暂停游戏。

## 建议的首个版本

- Git 标签：`v0.5.0`
- GitHub Release：附上仓库中的 `mod/AmazingCultivationMCP` 目录或其 ZIP。
- Steam 创意工坊更新：继续使用已有项目 ID `3823265890`，不要新建作品。
