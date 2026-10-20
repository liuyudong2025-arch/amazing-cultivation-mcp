# Amazing Cultivation MCP

让 AI 通过游戏内 Mod 读取并操作《了不起的修仙模拟器》。

本项目只借鉴 [rimUIMCP](https://github.com/Hcrab/rimUIMCP) 的“游戏内桥接 + 本地 Runtime + MCP”分层，不照搬环世界的游戏接口。桥接本身是标准《了不起的修仙模拟器》Mod：由 `GameMain:NewMod` 注册，使用 `OnBeforeInit` 加载程序集，并在 `OnStep` 的 Unity 主线程处理请求。读取和动作接口来自本游戏实际使用的 `XiaWorld`、`Wnd_GameMain`、`UI_WorldLayer`、`MapCamera` 与 FairyGUI。

## 架构

```text
AI / MCP Client
    │ stdio
    ▼
MCP Server (src/server.mjs)
    │ HTTP + 临时 Token，仅 127.0.0.1
    ▼
Local Runtime (src/runtime.mjs)
    │ WebSocket + 独立临时 Token，仅 127.0.0.1
    ▼
游戏内 C# Mod (AmazingCultivationMCP.Bridge.dll)
    │ Lua OnStep 主线程调度
    ├── XiaWorld.World / Map / Npc
    └── FairyGUI 控件树与原版事件
```

WebSocket 的网络回调只负责把请求加入队列。Lua Mod 每帧调用 `Bridge.Update()`，由 Unity 主线程读取游戏对象、执行 UI 动作并返回结果。

## 已实现能力

- 面向自主决策的一次性总览：世界、地图、门派、资源、人物、警报和当前选择。
- 世界时间、游戏模式、暂停及速度状态。
- 地图尺寸、NPC 数量和各类 Thing 数量。
- 活动人物分页读取：名称、位置、生命、年龄、性别、境界分类、阵营、真炁、当前工作和需求值。
- 地图格读取：地形、灵气、五行及比例、温度、肥沃度、美观度、光照和格内对象。
- 全地图对象分页搜索，可按 Thing 类型、名称/定义名和玩家所有权过滤。
- 门派状态、库存汇总、人物工作优先级与作息读取。
- 按人物或对象 ID 调用原版 `MapCamera.LookKey` 定位，并用 `Wnd_GameMain.ShowThingInfo` 打开原版信息面板。
- 通过原版管理器设置工作、作息、暂停/倍速，打开建造/生产/搜索界面，以及创建命名存档。
- 直接调用原版 `CommandMgr` 发现、添加、读取和移除砍伐、采矿、收获、拆除、灭火等 Thing/格子命令，并返回游戏内读回证据。
- 与 rimUIMCP 的地图注入思路一致，通过本游戏原生 `UILogicMgr.OnMapClick/OnMapDrag*` 对地图格点击和拖拽；无需 Windows 窗口句柄，可衔接原版建造、区域和命令模式。
- FairyGUI 控件树：层级、名称、文本、角色、位置、可见性、可操作状态，以及按钮/列表/下拉框/滑块的当前值。
- 通过 `targetId` 或语义选择器激活按钮、右键、填写文本、设置选中状态、选择列表项/下拉项和调整滑块。
- MCP 内置玩法指南，规定“观察 → 决策 → 操作 → 再观察”的可靠闭环与常用操作配方。
- 世界版本与 UI 快照 ID；重新载入世界后旧目标会失效。
- 与 rimUIMCP 相同思路的短事务租约、动作 `requestId` 去重/结果缓存，以及可游标轮询的动作事件日志。
- 本机 HTTP/WebSocket 双令牌鉴权。
- 截图和 Win32 输入备用接口。

## 环境

- Windows 10/11
- Node.js 20 或更高版本
- Steam 版《了不起的修仙模拟器》
- Windows .NET Framework 4 C# 编译器

运行时没有 npm 第三方依赖。

## 构建和安装 Mod

先关闭游戏，然后执行：

```powershell
cd C:\path\to\amazing-cultivation-mcp
$env:ACS_ROOT = 'D:\SteamLibrary\steamapps\common\AmazingCultivationSimulator'
.\scripts\install-mod.ps1
```

安装脚本会：

1. 针对当前游戏目录中的程序集编译 C# 桥接。
2. 将 `Info.json`、Lua 引导脚本和 DLL 复制到 `Mods\AmazingCultivationMCP`。

进入游戏的 Mod 管理器，启用 `Amazing Cultivation MCP` 后重启游戏。

只构建而不安装：

```powershell
.\scripts\build-bridge.ps1
```

## 启动 Runtime

```powershell
.\scripts\start-runtime.ps1
```

也可以前台运行以查看日志：

```powershell
npm run runtime
```

Runtime 会生成：

- `work/runtime.json`：MCP 到 Runtime 的地址和令牌。
- `%LOCALAPPDATA%\AmazingCultivationMCP\bridge.json`：游戏 Mod 到 Runtime 的 WebSocket 地址和令牌。

令牌每次启动都会更新，不要提交或分享这些文件。

## 配置 MCP

示例见 [`examples/codex.mcp.json`](examples/codex.mcp.json)：

```json
{
  "mcpServers": {
    "amazing-cultivation": {
      "command": "node",
      "args": ["C:/path/to/amazing-cultivation-mcp/src/server.mjs"],
      "env": {
        "ACS_ROOT": "D:/SteamLibrary/steamapps/common/AmazingCultivationSimulator"
      }
    }
  }
}
```

连接后先调用：

```json
{"method":"session.status","args":{}}
```

## 游戏内方法

| 方法 | 作用 | 常用参数 |
| --- | --- | --- |
| `session.status` | 桥接、世界和地图状态以及能力列表 | 无 |
| `session.sequence.begin/end` | 获取/释放短事务租约，避免多步操作被其他客户端穿插 | `leaseMs?` / `sequenceId` |
| `session.result` | 按 `requestId` 读取幂等动作的缓存结果 | `requestId` |
| `events.poll` | 从游标后读取动作事件日志 | `after?`, `limit?` |
| `state.overview` | 一次读取 AI 决策所需的有界总览 | `npcLimit?`, `resourceLimit?`, `alertLimit?` |
| `state.world` | 世界时间、模式、暂停和速度 | 无 |
| `state.map` | 地图尺寸与对象数量 | 无 |
| `state.npcs` | 活动人物分页列表 | `offset?`, `limit?` |
| `state.npc` | 单个人物、工作、需求和工作设置 | `id` |
| `state.grid` | 读取地图格的修仙环境与格内对象 | `key?` 或 `x`,`y`；均省略时读鼠标格 |
| `state.things` | 搜索 NPC、建筑、植物、物品等 | `type?`, `query?`, `playerOnly?`, `offset?`, `limit?` |
| `state.resources` | 门派库存和食物/药物汇总 | `query?`, `offset?`, `limit?` |
| `state.school` | 门派等级、声望、风水、弟子、解锁与作息 | 无 |
| `state.selection` | 当前选中对象 | 无 |
| `state.enums` | 可用对象类型、工作、作息和五行枚举 | 无 |
| `state.alerts` | 当前重要消息及其关联地点/对象 | `limit?` |
| `state.commandTypes` | 发现当前版本的原生命令定义 | `query?` |
| `state.commands` | 读取当前原生命令及目标/格子 | `type?`, `limit?` |
| `state.input` | 读取当前地图输入模式和弹窗阻塞状态 | 无 |
| `game.focusNpc` | 定位人物并打开原版人物信息 | `id` |
| `game.focusThing` | 定位任意对象并打开原版信息 | `id` |
| `game.lookGrid` | 镜头移动至地图格 | `key` 或 `x`,`y` |
| `game.openPanel` | 打开原版建造/生产/搜索/主菜单等界面 | `panel` |
| `game.setWorkPriority` | 修改人物工作优先级 | `id`, `workKind`, `priority` |
| `game.setWorkEnabled` | 启用或禁用人物工作 | `id`, `workKind`, `enabled` |
| `game.setVipWork` | 设置人物优先工作 | `id`, `workKind` |
| `game.interruptNpcJob` | 请求中断人物当前可中断工作 | `id` |
| `game.setSchedule` | 修改一个小时的门派作息 | `hour`, `plan` |
| `game.selectCommand` | 进入原版地图命令选择模式 | `name` |
| `game.addThingCommand` | 给对象添加原生命令并读回验证 | `command`, `id`, `params?` |
| `game.addGridCommand` | 给地图格添加原生命令并读回验证 | `command`, `key` 或 `x`,`y`, `params?` |
| `game.removeThingCommand` | 移除对象上的原生命令并验证 | `command`, `id` |
| `map.click` | 通过原版 `UILogicMgr` 点击地图格 | `key` 或 `x`,`y`, `button?` |
| `map.drag` | 通过原版 `UILogicMgr` 拖拽地图区域 | `from`, `to`, `button?` |
| `game.save` | 通过原版管理器创建命名存档 | `name` |
| `ui.snapshot` | FairyGUI 语义控件树 | `limit?` |
| `ui.input` | 操作原版 UI 控件 | `targetId/selector`, `action` |
| `runtime.pause` | 暂停游戏时间 | 无 |
| `runtime.speed` | 设置时间倍率 | `value` |
| `runtime.nextFrame` | 推进至少一个模拟帧并再次暂停，返回前后状态 | `speed?`, `timeoutMs?` |
| `runtime.advance` | 推进指定模拟帧并再次暂停，返回帧数证据 | `frames`, `speed?`, `timeoutMs?` |
| `runtime.runUntil` | 运行并轮询结构化状态，条件满足后暂停 | `observeMethod?`, `observeArgs?`, `path`, `operator`, `value`, `timeoutMs?` |

先调用 `state.enums` 获取当前游戏版本实际支持的 `workKind`、`plan` 和 Thing 类型，不要猜枚举名称。执行改变状态的动作后，重新调用对应的 `state.*` 或 `ui.snapshot` 核实结果。

UI 示例：

```json
{"method":"ui.snapshot","args":{"limit":3000}}
```

拿到按钮的 `targetId` 后：

```json
{
  "method":"ui.input",
  "args":{"targetId":"ui:1:12345678","action":"click"}
}
```

也可以使用语义选择器：

```json
{
  "method":"ui.input",
  "args":{"selector":{"role":"button","name":"功法"},"action":"click"}
}
```

文本框操作：

```json
{
  "method":"ui.input",
  "args":{"targetId":"ui:1:12345678","action":"fill","text":"太一门"}
}
```

列表、下拉框和滑块：

```json
{"method":"ui.input","args":{"targetId":"ui:1:123","action":"selectIndex","index":2}}
{"method":"ui.input","args":{"targetId":"ui:1:456","action":"selectValue","value":"SomeValue"}}
{"method":"ui.input","args":{"targetId":"ui:1:789","action":"setValue","value":75}}
```

选择器可组合使用 `name`、`text`、`contains`、`role`、完整 `type` 和 `nth`。界面变化后必须重新获取快照，因为旧 `targetId` 会失效。

MCP 客户端还会看到 `acsmcp_guide`。AI 应以 `state.overview` 开始每轮决策，优先处理警报和人物需求，执行动作后等待片刻并再次读取相关状态；不能仅凭“命令已派发”推断游戏结果。

## 备用视觉接口

以下接口在游戏内 UI 适配尚未覆盖某个界面时使用：

- `ui.observe`
- `ui.focus`
- `ui.move`
- `ui.click`
- `ui.drag`
- `ui.scroll`
- `ui.key`
- `ui.type`
- `runtime.wait`

坐标以游戏客户区左上角为 `(0,0)`。正常玩法应优先使用 `state.grid`、`state.npcs`、`game.focusNpc` 等游戏专用接口，再使用 `ui.snapshot` 和 `ui.input` 操作尚未建立专用适配的原版窗口。

## 验证

```powershell
npm test
.\scripts\build-bridge.ps1
```

自动测试覆盖 MCP 协议、结构化调用路由、本机令牌鉴权和 HTTP↔WebSocket 双向中继。实际游戏 API 和 UI 仍需在启用 Mod 后进行实机验证。

启用 Mod、重启游戏后运行只读实机检查：

```powershell
npm run smoke
```

在主菜单时会验证桥接和 UI 树；载入存档后再次运行，还会验证世界总览、枚举和警报读取。该脚本不会改变或保存游戏。

## 安全边界

- Runtime 与游戏桥只监听 `127.0.0.1`。
- 游戏 Mod 不提供任意 C#、Lua、反射调用或文件执行接口。
- `state.*` 是只读适配器；游戏动作集中在显式的 `game.*`、`ui.input` 和 `runtime.*` 方法。
- `ui.input` 的成功表示原版事件已派发；持续性结果需要再次读取状态或 UI 核实。
- 涉及覆盖存档、删除角色或解散门派等不可逆动作时，调用方应先向用户确认。

准备公开仓库或制作 Release 时，请按 [`PUBLISHING.md`](PUBLISHING.md) 的清单检查提交范围。
