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
- 0.6 高层 Helper：完整分页、殖民地巡检、开局建议、工作/作息确保、人物选择、命令确保/撤销和受监控时间推进。
- 0.7 修仙领域能力：人物五维与心情、功法境界、瓶颈与筑基、金丹与道行、秘籍树、术法、丹方/法宝配方、技能、伤病、装备和背包，以及可验证的修炼模式/行为设置。
- 0.8 战斗、建造和生产：读取 FightBody 并锁定目标、开战/停战、施放技能/法宝、设置自动技能；发现原生建造目录并放置蓝图；读取建筑和生产队列、添加/移除生产任务；改换功法与受确认保护的突破。
- 0.9 扩展系统：阵法状态与创建/取消、炼丹炼器配方和生产目录、拜师传功、农业仓储区域、门派交易外交状态及原版交易窗口、世界地图与历练派遣/召回。
- 1.0 风水与聚灵：成片读取原生灵气/五行场、读取建筑风水和房间/灵气区域，并提供聚灵阵与“大凶房”的只读规划器。
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

## 高层玩法 Helper

优先让 AI 调用 `helper.*`，只有 Helper 尚未覆盖某项玩法时再组合底层 `state.*`、`game.*` 和 `ui.*`。

| Helper | 作用 |
| --- | --- |
| `helper.observe` | 读取世界、门派、人物、资源、警报、输入状态和风险摘要 |
| `helper.startingPlan` | 生成带状态证据的开局优先事项，不修改游戏 |
| `helper.readAllNpcs` | 跨页读取全部活动人物并报告世界/帧一致性 |
| `helper.queryAllThings` | 跨页读取所有匹配的地图对象 |
| `helper.readAllResources` | 跨页读取完整门派库存 |
| `helper.ensureWork` | 幂等设置人物工作优先级、启用状态或优先工作 |
| `helper.ensureSchedule` | 幂等设置一个或多个小时的门派作息 |
| `helper.selectNpc` | 通过原版地图输入选择人物并读回验证 |
| `helper.ensureThingCommand` | 确保对象上存在砍伐、采矿、收获等原生命令 |
| `helper.ensureNoThingCommand` | 确保对象上的指定原生命令已经撤销 |
| `helper.buildRectangle` | 按矩形逐格建造墙、地板等，逐格验证；遇到失败返回已完成坐标 |
| `helper.buildLayout` | 按清单建造最多 128 个不同建筑单元，支持每格独立材料、方向并逐格验证 |
| `helper.buildArea` | 用原版拖拽建造屋顶等区域，并验证房间覆盖格 |
| `helper.saveLayoutTemplate` / `helper.listLayoutTemplates` | 将建筑清单保存为相对坐标模板并持久化列出，适合聚灵阵、卧房、工坊等复用布局 |
| `helper.buildLayoutTemplate` / `helper.deleteLayoutTemplate` | 在新原点按 0/90/180/270 度旋转建造模板，或经确认后删除模板 |
| `helper.emergencyDisengage` | 立即暂停，停止指定角色战斗、取消其阵法并验证脱战状态 |
| `helper.moveAnimalsToBreedingArea` | 使用原生搬运任务把动物移入繁殖区，并可推进时间验证区域登记 |
| `helper.commandFormationCombat` | 按集火或轮换策略命令阵法全体成员攻击，并逐人回读战斗目标 |
| `helper.retreatTo` | 停止战斗、解散相关阵法、向安全坐标编队撤退并可验证到达 |
| `helper.dismantleAndVerify` | 下达原生拆除命令，并可推进时间直到确认建筑已消失 |
| `helper.runAndWatch` | 推进游戏并监视多个结构化停止条件，结束时始终暂停 |
| `helper.planSpiritGathering` | 读取座位周边灵气、五行、障碍、风水和建造目录，给出聚灵阵候选坐标，不自动建造 |
| `helper.planOminousRoom` | 读取目标房间和附近建筑的原生风水结果，给出“大凶房”迭代方案，不自动建造 |
| `helper.prepareBreakthrough` | 汇总突破率、环境、灵气、风水和物品，满足阈值时可确认后直接突破 |
| `helper.refineAndTrack` | 下达原生炼宝任务并追踪到结束，报告输入变化和新获得物品 |

示例：

```json
{"method":"helper.startingPlan","args":{}}
{"method":"helper.ensureWork","args":{"id":8684,"workKind":"Plant","priority":3,"enabled":true}}
{"method":"helper.ensureThingCommand","args":{"command":"Cutoff","id":1050}}
{"method":"helper.runAndWatch","args":{"conditions":[{"method":"state.world","path":"dayHour","operator":"gte","value":12}],"timeoutMs":30000}}
{"method":"helper.planSpiritGathering","args":{"x":120,"y":85,"radius":6,"element":"Water"}}
{"method":"helper.planOminousRoom","args":{"x":132,"y":90,"radius":8}}
```

`ensure*` 方法会先读取现状，只修改不符合目标的部分，并在返回前读回验证。

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
| `state.npcProfile` | 人物种族、五维、心情、特性和经历 | `id` |
| `state.npcCultivation` | 功法、境界进度、瓶颈、筑基、金丹、道行、秘籍与配方 | `id` |
| `state.npcSkills` | 全部已初始化技能、等级、喜爱度、经验和禁用状态 | `id` |
| `state.npcHealth` | 身体健康、濒死状态、伤病与治疗进度 | `id` |
| `state.npcInventory` | 已装备和随身物品、法宝与秘籍元数据 | `id` |
| `state.combat` | 战斗目标、施法、技能冷却、自动技能与法宝 | `id?` |
| `state.buildCatalog` | 发现原生建造目录及精确建造名称 | `query?` |
| `state.building` | 建筑进度、风水、工人和生产队列 | `id` |
| `state.productionCatalog` / `state.recipes` | 建筑生产目录及人物已掌握的炼丹炼器配方 | 建筑或人物 ID、`kind?` |
| `state.formations` / `state.formationCatalog` | 活动阵法、阵型、节点和预设成员 | `id?` |
| `state.areas` / `state.areaCatalog` | 农业、仓储等区域及原生区域类型 | `id?`, `type?` |
| `state.trade` | 门派关系、好感点、倾向和交易货物 | `schoolId?` |
| `state.worldMap` | 地点解锁、历练成本、预测及在外人员 | `query?` |
| `state.tradeWindow` / `state.storyChoice` | 当前交易报价与历练/剧情选项 | 无 |
| `state.grid` | 读取地图格的修仙环境与格内对象 | `key?` 或 `x`,`y`；均省略时读鼠标格 |
| `state.qiField` | 成片读取灵气、五行、占用物和高灵气格 | `key?` 或 `x`,`y`，`radius?`, `top?` |
| `state.fengShui` | 读取建筑原生风水值以及 `AreaRoom`/`AreaAura` | 可选 `key` 或 `x`,`y`，`radius?` |
| `state.breakthrough` | 读取突破条件、成功率及季节、灵气、五行、风水等原生影响项 | `id`, `failureCount?` |
| `state.godCultivation` | 神修信仰、人口、神国建筑、护法、核心信徒、天劫与飞升状态 | `id` |
| `state.godBuildingCatalog` | 神国建筑解锁、占格、环位、等级、人口和信仰消耗 | `id` |
| `state.interactions` | 两名角色间可用的原生江湖互动、缘分、好感、警惕与访客状态 | `actorId`, `targetId` |
| `state.bodyCultivation` | 读取体修真气、架势、淬体进度与部位 | `id` |
| `state.esotericaCatalog` | 查询秘籍目录及某角色的习得资格 | `npcId?`, `query?` |
| `state.spellCatalog` | 查询术法与神通定义、消耗、原生命令及角色习得状态 | `npcId?`, `query?` |
| `state.skillTree` | 查询当前功法参悟树、前置/互斥、参悟值消耗和原生可学习判定 | `id` |
| `game.learnSkillTreeNode` | 经原生校验学习参悟节点，并回读新解锁术法/神通 | `id`, `name`, `confirm=true` |
| `state.outspread` / `state.outspreadCatalog` | 查询门派分舵、成员、建筑、政策、信仰、人口、仓储、灾害、监管及完整可用目录 | `name?` / `region?` |
| `game.openOutspreadRegion` | 派遣可行动角色开辟分舵 | `region`, `npcId`, `resourceLevel?`, `confirm=true` |
| `game.setOutspreadPolicy` / `game.setOutspreadBuild` | 按原生解锁与条件校验配置分舵政策和建筑槽位 | `region`, `name`, `type?`, `index?`, `confirm=true` |
| `game.setOutspreadChief` / `game.sendOutspreadMember` / `game.removeOutspreadMember` | 调度分舵负责人和成员 | `region`, `npcId/memberId`, `confirm=true` |
| `game.startOutspreadSupervision` / `game.collectOutspreadProducts` | 开始监管或领取分舵库存产物 | `region`, `confirm=true` |
| `state.dlc` | 查询官方熊猫、武当 DLC 安装状态、Lua 标记和内容路径 | 无 |
| `state.refiningForecast` | 按原版 `MakeFaBao` 公式计算炼器成功率、品质/灵气继承区间和十二品概率 | `npcId`, `itemId`, `buildingId?` |
| `state.population` | 分页读取来访者、动物与其他存活角色 | `offset?`, `limit?`, `query?` |
| `state.things` | 搜索 NPC、建筑、植物、物品等 | `type?`, `query?`, `playerOnly?`, `offset?`, `limit?` |
| `state.resources` | 门派库存和食物/药物汇总 | `query?`, `offset?`, `limit?` |
| `state.school` | 门派等级、声望、风水、弟子、解锁与作息 | 无 |
| `state.selection` | 当前选中对象 | 无 |
| `state.enums` | 可用对象类型、工作、作息和五行枚举 | 无 |
| `state.alerts` | 当前重要消息及其关联地点/对象 | `limit?` |
| `state.commandTypes` | 发现当前版本的原生命令定义 | `query?` |
| `state.commands` | 读取当前原生命令及目标/格子 | `type?`, `limit?` |
| `state.input` | 读取当前地图输入模式和弹窗阻塞状态 | 无 |
| `game.setPracticeMode` | 设置人物修炼模式并返回读回状态 | `id`, `mode` |
| `game.setPracticeBehaviour` | 设置人物当前修炼行为并返回读回状态 | `id`, `behaviour` |
| `game.changeGong` | 改换功法（高风险确认） | `id`, `gong`, `confirm=true` |
| `game.promoteInnerDisciple` | 走原版 `Up2Disciple` 流程晋升内门并登记门派弟子 | `id`, `gong`, `difficulty?`, `confirm=true` |
| `game.beginBreakthrough` | 条件检查后开始突破（高风险确认） | `id`, `confirm=true`, `heavenlyTribulation?` |
| `game.attackTarget` / `game.stopCombat` | 原生开战、锁定目标与停战 | 人物和目标 ID |
| `game.castSkill` / `game.setAutoSkills` / `game.castArtifacts` | 战斗技能、自动技能与法宝 | 人物、技能/模式和目标 |
| `game.beginBuild` / `game.placeBuilding` | 进入原生建造模式并放置蓝图 | 目录名称和地图格 |
| `game.moveBuilding` | 通过原生搬迁流程为已完成建筑放置目标蓝图 | 建筑 ID、目标格、`confirm=true` |
| `game.addProductionTask` / `game.removeProductionTask` | 管理建筑生产队列 | 建筑 ID、配方/任务名或任务 ID |
| `game.createFormation` / `game.cancelFormation` | 按原生阵型结阵或解阵（确认保护） | `shapeId`, `npcIds`, `confirm=true` |
| `game.createFormationShape` / `game.removeFormationShape` | 用已解锁节点创建并由游戏校验自定义阵型，或删除未使用阵型 | 节点清单或阵型 ID、`confirm=true` |
| `game.saveFormationScheme` / `game.removeFormationScheme` | 保存或删除阵型的有序成员方案 | 阵型/方案 ID、角色 ID、`confirm=true` |
| `game.apprentice` / `game.teachSecret` | 拜师并排入原生传功任务（确认保护） | 师徒 ID、秘籍 ID、`confirm=true` |
| `game.createArea` / `game.addAreaGrid` / `game.removeAreaGrid` | 创建并编辑农业、仓储等原生区域 | 区域类型/ID 和地图格 |
| `game.configurePlantArea` / `game.configureStorageArea` | 设置作物、门人限制、仓储优先级和出售/品质策略 | 区域 ID 与策略字段 |
| `game.configureGuardArea` | 设置守卫区敌我/锁定策略并增删守卫成员 | 区域 ID、角色 ID 清单 |
| `game.moveNpcTo` | 通过原生 `MoveNpc` 任务让门人搬运角色或动物到目标格 | 角色/动物 ID、目标格、`confirm=true` |
| `game.performTalkAction` | 执行当前原生可用的邀请、闲聊等江湖互动（确认保护） | `actorId`, `targetId`, `action`, `confirm=true` |
| `game.openGodCity` | 打开神修角色的原版神国管理窗口并返回结构化状态 | `id` |
| `game.createGodBuilding` / `game.upgradeGodBuilding` | 通过原生条件与资源检查建造或升级神国建筑 | 神修 ID、格子/建筑 ID、`confirm=true` |
| `game.removeGodBuilding` / `game.toggleGodBuilding` | 拆除、开启或关闭神国建筑及其效果 | 神修 ID、建筑 ID |
| `game.setGodBuildingGuard` | 为神国建筑配置或清除原生护法 | 神修 ID、建筑 ID、护法 ID、`confirm=true` |
| `game.openSchoolTrade` | 打开指定门派的原版交易窗口 | `schoolId` |
| `game.openWorldMap` / `game.startExplore` / `game.recallExplorer` | 世界地图、派遣历练和召回（确认保护） | 人物 ID、地点、`confirm=true` |
| `game.confirmTrade` / `game.chooseStoryOption` | 核验后成交或选择历练事件（确认保护） | `confirm=true`、选项索引 |
| `game.focusNpc` | 定位人物并打开原版人物信息 | `id` |
| `game.focusThing` | 定位任意对象并打开原版信息 | `id` |
| `game.lookGrid` | 镜头移动至地图格 | `key` 或 `x`,`y` |
| `game.openPanel` | 打开原版建造/生产/搜索/主菜单等界面 | `panel` |
| `game.setWorkPriority` | 修改人物工作优先级 | `id`, `workKind`, `priority` |
| `game.setWorkEnabled` | 启用或禁用人物工作 | `id`, `workKind`, `enabled` |
| `game.setVipWork` | 设置人物优先工作 | `id`, `workKind` |
| `game.interruptNpcJob` | 请求中断人物当前可中断工作 | `id` |
| `game.setSchedule` | 修改一个小时的门派作息 | `hour`, `plan` |
| `game.setSchoolMaster` | 通过原版 `SchoolMgr.SetMaster` 设置掌门并回读验证 | `id`, `confirm=true` |
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
