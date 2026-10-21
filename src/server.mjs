import { createInterface } from 'node:readline';
import { spawn } from 'node:child_process';
import { access } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { captureWindow, runWindows } from './windows.mjs';
import { runtimeCall } from './runtime-client.mjs';
import { createLayoutTemplateStore } from './layout-template-store.mjs';
import { HELPER_HELP, runHelper } from '../agent/helpers/index.mjs';

const layoutTemplates = createLayoutTemplateStore();
const STATUS_METHOD_ADDITIONS = [
  'state.godBuildingCatalog', 'state.skillTree', 'state.outspread', 'state.outspreadCatalog', 'state.dlc', 'state.refiningForecast',
  'game.createGodBuilding', 'game.upgradeGodBuilding', 'game.removeGodBuilding', 'game.toggleGodBuilding', 'game.setGodBuildingGuard',
  'game.moveNpcTo', 'game.learnSkillTreeNode',
  'game.openOutspreadRegion', 'game.setOutspreadPolicy', 'game.setOutspreadBuild', 'game.cancelOutspreadBuild',
  'game.setOutspreadChief', 'game.sendOutspreadMember', 'game.removeOutspreadMember', 'game.startOutspreadSupervision', 'game.collectOutspreadProducts',
];

export const SERVER_INFO = { name: 'amazing-cultivation-mcp', version: '1.0.2' };

const METHOD_HELP = {
  'session.status': 'Report the in-game Mod session; falls back to Windows process status if the bridge is offline.',
  'session.sequence.begin': 'Acquire a short action/UI lease so another client cannot interleave mutations.',
  'session.sequence.end': 'Release a previously acquired action/UI sequence lease.',
  'session.result': 'Retrieve the cached outcome for an idempotent mutation requestId.',
  'events.poll': 'Poll the bounded in-game action event journal after a cursor.',
  'session.launch': 'Launch the configured game executable. Does not load or alter a save.',
  'state.overview': 'Read one bounded decision snapshot: world, map, sect, resources, NPCs, alerts, and selection.',
  'state.world': 'Read structured world time, mode, pause, and speed state from the in-game Mod.',
  'state.map': 'Read structured map size and object counts from the in-game Mod.',
  'state.npcs': 'Read a paged list of active player NPCs from the in-game Mod.',
  'state.npc': 'Read one NPC by ID, including current job, needs, and work settings.',
  'state.npcProfile': 'Read one NPC race, five base attributes, mood, features, and experiences.',
  'state.npcCultivation': 'Read one NPC cultivation art, realm/stage, bottleneck, foundation, core, Dao, manuals, spells, and formulas.',
  'state.breakthrough': 'Read native breakthrough readiness, probability, and factor contributions at the current NPC grid.',
  'state.bodyCultivation': 'Read body cultivation qi, attitude, unlocked methods, quenching materials and progress, and body parts.',
  'state.esotericaCatalog': 'Discover native esoterica definitions and whether an NPC has learned or can learn them.',
  'state.spellCatalog': 'Discover spell and magic definitions, costs, commands, unlock state, and one NPC learned state.',
  'state.skillTree': 'Read the current cultivation-art skill tree, prerequisites, costs, learned state, and native can-learn result.',
  'state.godCultivation': 'Read god-cultivation faith, population, tribulations, ascension, divine-city cells/buildings, guards, and believers.',
  'state.godBuildingCatalog': 'Read divine-city building definitions, unlock state, placement size/rings, levels, population, and faith cost.',
  'state.interactions': 'Read native Jianghu interaction actions, fate, favour, vigilance, visitor, and expulsion eligibility for two NPCs.',
  'state.npcSkills': 'Read all initialized skills for one NPC, including level, aptitude, experience, and bans.',
  'state.npcHealth': 'Read one NPC body health and active injuries with treatment progress.',
  'state.npcInventory': 'Read one NPC equipped and carried items, including artifact/manual metadata.',
  'state.item': 'Read one item including native feng-shui properties, artifact properties, and embryo activation state.',
  'state.refiningForecast': 'Calculate the native artifact-refining success rate, quality range, qi-inheritance range, and twelve-rate chance before dispatch.',
  'state.combat': 'Read native FightBody state, target, casting, skills, cooldowns, auto skills, and artifacts for one or all player NPCs.',
  'state.buildCatalog': 'Discover native build menu entries and exact names accepted by game.beginBuild/game.placeBuilding.',
  'state.building': 'Read one building, construction progress, feng shui, workers, and production queue.',
  'state.productionCatalog': 'Read the native recipes/tasks offered by one production building.',
  'state.recipes': 'Discover alchemy and artifact formulas, optionally annotated with one NPC learned state.',
  'state.formations': 'Read active formations, members, stability, pressure, shield, qi cost, and formation points.',
  'state.formationCatalog': 'Read native formation shapes, nodes, and saved member schemes.',
  'state.areas': 'Read farming, storage, trade, and other map areas with grids and specialized settings.',
  'state.areaCatalog': 'Discover native area types accepted by game.createArea.',
  'state.trade': 'Read sect relations, friend points, tendencies, and available school trade goods.',
  'state.tradeWindow': 'Read the currently selected native trade offer values and friend-point cost.',
  'state.worldMap': 'Read places, unlock/arrival state, travel costs, forecasts, and active explorations.',
  'state.outspread': 'Read opened sect outspread regions: staff, buildings, policy, faith, population, storage, disaster, supervision, and travel.',
  'state.outspreadCatalog': 'Discover outspread regions, open costs, buildings, policies, build skills, unlock state, and native conditions.',
  'state.dlc': 'Read the official Panda/WuDang DLC installation state, display names, Lua flags, and installed content paths.',
  'state.storyChoice': 'Read the current native story/adventure choice, option text, tips, NPC, and target grid.',
  'state.grid': 'Inspect an ACS map grid by key, x/y, or the current pointer: terrain, qi, five elements, temperature, fertility, beauty, light, and occupants.',
  'state.qiField': 'Sample a bounded square around a grid and return native qi/five-element values, occupants, aggregate statistics, and the strongest cells.',
  'state.fengShui': 'Read native building feng-shui ranks/values plus AreaRoom and AreaAura state, optionally around a selected grid.',
  'state.population': 'Page all living NPCs, including visitors and animals, with native interaction eligibility.',
  'state.things': 'Search and page map objects by ACS thing type, name/definition query, and player ownership.',
  'state.resources': 'Read paged sect inventory counts plus usable food, medicine, and cultivation-food totals.',
  'state.school': 'Read sect name, level, prestige, feng shui, scores, disciples, unlocks, manuals, and daily schedule.',
  'state.selection': 'Read objects currently selected by the game.',
  'state.enums': 'List valid ACS enum names for thing types, work kinds, schedules, ranks, and elements.',
  'state.alerts': 'Read current important in-game messages and their target grids/objects.',
  'state.commandTypes': 'Discover native ACS command definitions for work designations and map actions.',
  'state.commands': 'Read active native ACS commands, optionally filtered by command type.',
  'state.input': 'Read the native ACS map-input mode and modal/popup blocking state.',
  'game.focusNpc': 'Move the ACS camera to an NPC by ID and open the game\'s native thing-information panel.',
  'game.focusThing': 'Move the camera to any map object by ID and open its native information panel.',
  'game.lookGrid': 'Move the native map camera to a grid by key or x/y.',
  'game.openPanel': 'Open an original game panel: build, produce, search, main, close, or cancel.',
  'game.save': 'Start an original-game named save through MainManager.DoSave.',
  'game.setWorkPriority': 'Set one NPC work-kind priority (0-9) using the game job-engine setting.',
  'game.setWorkEnabled': 'Enable or disable one NPC work kind through JobNpcEngine.',
  'game.setVipWork': 'Assign one NPC preferred work kind through JobNpcEngine.',
  'game.interruptNpcJob': 'Ask JobNpcEngine to interrupt the NPC current job when interruptible.',
  'game.setSchedule': 'Set one hour of the sect daily schedule through SchoolMgr.',
  'game.setSchoolMaster': 'Assign an inner disciple as sect master through native SchoolMgr.SetMaster; requires confirm=true.',
  'game.setPracticeMode': 'Set an NPC cultivation mode through NpcPractice.ChangeMode and return read-back state.',
  'game.setPracticeBehaviour': 'Set an NPC current cultivation behaviour through NpcPractice.SetBehavourKind and return read-back state.',
  'game.changeGong': 'Change an NPC cultivation art; requires explicit confirm=true.',
  'game.promoteInnerDisciple': 'Use the native Up2Disciple workflow to promote a foundation-ready outer disciple and register them with SchoolMgr; requires confirm=true.',
  'game.learnSkillTreeNode': 'Learn a node through the native Wnd_SkillTree validation and learning path; requires confirm=true.',
  'game.beginBreakthrough': 'Begin a game-native breakthrough after preflight checks; requires explicit confirm=true.',
  'game.useItem': 'Use a native item with an NPC, including manuals where supported; requires confirm=true.',
  'game.equipItem': 'Equip a nearby or carried player item on one NPC and read back the equipment slots; requires confirm=true.',
  'game.unequipItem': 'Remove one NPC equipment slot and read back inventory; requires confirm=true.',
  'game.assignBed': 'Assign or clear one NPC bed through the native NPC setter and verify the assigned ID.',
  'game.assignPracticePlace': 'Assign or clear one NPC cultivation place through the native NPC setter and verify the assigned ID.',
  'game.huntAnimal': 'Queue the native Hunting command for an eligible animal; requires confirm=true.',
  'game.slaughterAnimal': 'Queue the native Slaughter command for an eligible animal; requires confirm=true.',
  'game.moveNpcTo': 'Queue the native MoveNpc command to have workers relocate a player NPC or animal to a target grid; requires confirm=true.',
  'game.talkToNpc': 'Queue a native TryTalk command from a player NPC to a living intelligent NPC.',
  'game.performTalkAction': 'Open the native Jianghu conversation and dispatch one currently available interaction action; requires confirm=true.',
  'game.openGodCity': 'Open the original divine-city management window for a god-cultivator and return structured state.',
  'game.createGodBuilding': 'Create a native divine-city building after native placement, condition, and cost checks; requires confirm=true.',
  'game.upgradeGodBuilding': 'Upgrade a divine-city building after native level/resource checks; requires confirm=true.',
  'game.removeGodBuilding': 'Remove a divine-city building through GodPractice; requires confirm=true.',
  'game.toggleGodBuilding': 'Open or close a divine-city building and apply/remove its native effects.',
  'game.setGodBuildingGuard': 'Assign or clear an available divine-city guard; requires confirm=true.',
  'game.refineArtifact': 'Queue the native RefiningFabao command with a selected item, label and refining mode; requires confirm=true.',
  'game.changeBodyAttitude': 'Switch an NPC to an unlocked body cultivation attitude.',
  'game.beginBodyQuenching': 'Start native body part quenching with an unlocked method and available material; requires confirm=true.',
  'game.cancelBodyQuenching': 'Cancel active quenching for a named body part.',
  'game.attackTarget': 'Set a native FightBody target and begin combat.',
  'game.stopCombat': 'Interrupt and end combat, refusing cant-stop fights unless force=true.',
  'game.castSkill': 'Cast a known native fight skill at a thing or grid and return combat read-back.',
  'game.setAutoSkills': 'Replace one NPC native automatic fight-skill list after validating known skills.',
  'game.castArtifacts': 'Cast an NPC equipped artifacts in a native artifact mode, optionally at a target.',
  'game.beginBuild': 'Enter the original native Build UI logic mode by an exact build-catalog name.',
  'game.placeBuilding': 'Enter native Build mode and dispatch a map placement at a grid with immediate read-back.',
  'game.moveBuilding': 'Enter the native move-building path for a finished building and place its destination blueprint; requires confirm=true.',
  'game.changeBuildingMaterial': 'Queue the native building material replacement job; requires confirm=true.',
  'game.dismantleBuilding': 'Queue the native dismantle command for a building; requires confirm=true.',
  'game.addProductionTask': 'Add a native recipe/task to a building production queue.',
  'game.removeProductionTask': 'Remove a production task by ID; requires confirm=true.',
  'game.createFormation': 'Create a native formation from a legal shape and ordered NPC IDs; requires confirm=true.',
  'game.cancelFormation': 'Cancel an active native formation; requires confirm=true.',
  'game.createFormationShape': 'Create and native-validate a custom formation shape from unlocked node definitions; requires confirm=true.',
  'game.removeFormationShape': 'Remove an unused custom formation shape; requires confirm=true.',
  'game.saveFormationScheme': 'Save an ordered NPC assignment for a legal formation shape; requires confirm=true.',
  'game.removeFormationScheme': 'Remove a saved formation member scheme; requires confirm=true.',
  'game.apprentice': 'Establish a native master-disciple relationship; requires confirm=true.',
  'game.teachSecret': 'Queue the native TeachHim command for a known manual/esoterica; requires confirm=true.',
  'game.createArea': 'Create a native farming/storage/etc. area at a map grid.',
  'game.addAreaGrid': 'Add one grid to an existing native area.',
  'game.removeAreaGrid': 'Remove one grid from an existing native area.',
  'game.configurePlantArea': 'Set a farming area crop and outer/inner-disciple restriction.',
  'game.configureStorageArea': 'Set storage priority, sale policy, item-rate range, and special filters.',
  'game.configureGuardArea': 'Configure a native guard area and add/remove assigned player NPC guards.',
  'game.removeArea': 'Remove an entire native area; requires confirm=true.',
  'game.openSchoolTrade': 'Open the native trade window for a school; use ui.snapshot/ui.input to finalize offers.',
  'game.confirmTrade': 'Finalize the currently selected native trade offer; requires confirm=true.',
  'game.openWorldMap': 'Open the native world-map window.',
  'game.startExplore': 'Queue the native GoMapExplore command for an unlocked place; requires confirm=true.',
  'game.recallExplorer': 'Recall an NPC from exploration through PlacesMgr; requires confirm=true.',
  'game.chooseStoryOption': 'Choose a current native story/adventure option by index; requires confirm=true.',
  'game.openOutspreadRegion': 'Dispatch an available NPC to open a native outspread region; requires confirm=true.',
  'game.setOutspreadPolicy': 'Set or clear a region policy after native unlock/condition checks; requires confirm=true.',
  'game.setOutspreadBuild': 'Build, replace, or remove one region building slot through native SetBuild; requires confirm=true.',
  'game.cancelOutspreadBuild': 'Cancel one waiting outspread construction; requires confirm=true.',
  'game.setOutspreadChief': 'Dispatch an available NPC to become a region chief; requires confirm=true.',
  'game.sendOutspreadMember': 'Dispatch an available NPC to join a region after capacity validation; requires confirm=true.',
  'game.removeOutspreadMember': 'Remove a region member through the original manager path; requires confirm=true.',
  'game.startOutspreadSupervision': 'Start regional supervision after native eligibility checks; requires confirm=true.',
  'game.collectOutspreadProducts': 'Move stored outspread products back to the player map; requires confirm=true.',
  'game.selectCommand': 'Enter an original Wnd_GameMain command-selection mode by command name.',
  'game.addThingCommand': 'Add a native ACS command to a map object by ID and return read-back evidence.',
  'game.addGridCommand': 'Add a native ACS command at a map grid and return read-back evidence.',
  'game.removeThingCommand': 'Remove a native ACS command from a map object and verify removal.',
  'map.click': 'Inject an in-game grid click through UILogicMgr, preserving the active ACS build/command mode.',
  'map.drag': 'Inject an in-game grid drag through UILogicMgr for rectangular/line designations.',
  'ui.snapshot': 'Read the current FairyGUI control tree with stable target IDs.',
  'ui.input': 'Activate a FairyGUI control by targetId or semantic selector.',
  'runtime.pause': 'Pause game time inside the game Mod.',
  'runtime.speed': 'Set game time scale inside the game Mod.',
  'runtime.nextFrame': 'Advance at least one ACS simulation frame, pause again, and return before/after evidence.',
  'runtime.advance': 'Advance a requested number of ACS simulation frames, pause again, and return evidence.',
  'runtime.runUntil': 'Run while polling structured state until a path condition matches, then pause.',
  'ui.observe': 'Capture the current game client area as a PNG image.',
  'ui.focus': 'Restore and focus the game window.',
  'ui.move': 'Move the pointer to client coordinates.',
  'ui.click': 'Click client coordinates with left, right, or middle mouse button.',
  'ui.drag': 'Drag between two client-coordinate points.',
  'ui.scroll': 'Scroll vertically at optional client coordinates.',
  'ui.key': 'Press a named key, optionally with Ctrl/Alt/Shift.',
  'ui.type': 'Type Unicode text using Win32 input events.',
  'runtime.wait': 'Wait briefly before the next observation.',
  ...HELPER_HELP,
};

export const TOOLS = [
  {
    name: 'acsmcp_call',
    description: 'Observe and operate Amazing Cultivation Simulator through structured game state, verified high-level helpers, native UI, and fallback Windows input. Start with helper.observe or helper.startingPlan.',
    inputSchema: {
      type: 'object',
      additionalProperties: false,
      required: ['method'],
      properties: {
        method: { type: 'string', enum: Object.keys(METHOD_HELP) },
        args: { type: 'object', additionalProperties: true, default: {} },
      },
    },
  },
  {
    name: 'acsmcp_methods',
    description: 'List supported ACS MCP methods and their purpose.',
    inputSchema: { type: 'object', additionalProperties: false, properties: {} },
  },
  {
    name: 'acsmcp_guide',
    description: 'Return the recommended observe-decide-act-verify loop and argument recipes for reliably playing ACS.',
    inputSchema: { type: 'object', additionalProperties: false, properties: {} },
  },
];

const PLAY_GUIDE = {
  loop: [
    'Call session.status; if disconnected, start the runtime/game and ensure the Mod is enabled.',
    'Call helper.observe or helper.startingPlan before deciding; use state.overview for the lower-level bounded snapshot. Inspect alerts and low NPC needs first.',
    'Use state.enums before passing workKind, plan, or thing type names.',
    'Prefer game.* and ui.snapshot/ui.input. Use coordinate input only when no semantic adapter exists.',
    'For multi-step UI or build flows, acquire session.sequence.begin and pass sequenceId to every mutation, then release it.',
    'After every mutation, wait briefly and read the affected state/UI again. Never infer success only from commandAccepted.',
    'Save to a new explicit name before a risky multi-step operation.',
  ],
  recipes: {
    inspectColony: [{ method: 'helper.observe', args: {} }, { method: 'helper.startingPlan', args: {} }],
    configureWork: [{ method: 'helper.ensureWork', args: { id: '<npcId>', workKind: 'Plant', priority: 3, enabled: true } }],
    configureSchedule: [{ method: 'helper.ensureSchedule', args: { entries: [{ hour: 0, plan: '<plan>' }] } }],
    configureCultivation: [{ method: 'state.enums', args: {} }, { method: 'helper.ensureCultivation', args: { id: '<npcId>', mode: '<practiceBehaviour>', behaviour: '<practiceBehaviour>' } }],
    learnCultivationNode: [{ method: 'state.skillTree', args: { id: '<npcId>' } }, { method: 'game.learnSkillTreeNode', args: { id: '<npcId>', name: '<nodeWithCanLearnTrue>', confirm: true } }, { method: 'state.spellCatalog', args: { npcId: '<npcId>' } }],
    build: [{ method: 'state.buildCatalog', args: { query: '<building>' } }, { method: 'helper.buildLayout', args: { placements: [{ name: '<catalogName>', x: '<x>', y: '<y>', stuffDefName: '<material>', direction: '<direction>' }] } }],
    reusableLayout: [{ method: 'helper.saveLayoutTemplate', args: { name: '<templateName>', placements: [{ name: '<catalogName>', x: 10, y: 10 }, { name: '<catalogName>', x: 11, y: 10 }] } }, { method: 'helper.buildLayoutTemplate', args: { name: '<templateName>', x: '<newOriginX>', y: '<newOriginY>', rotation: 0 } }],
    moveOrRemoveBuilding: [{ method: 'state.building', args: { id: '<buildingId>' } }, { method: 'game.moveBuilding', args: { id: '<buildingId>', x: '<x>', y: '<y>', confirm: true } }, { method: 'helper.dismantleAndVerify', args: { id: '<buildingId>', confirm: true } }],
    production: [{ method: 'state.building', args: { id: '<buildingId>' } }, { method: 'helper.ensureProduction', args: { id: '<buildingId>', name: '<recipeName>', loop: 1 } }],
    alchemyAndRefining: [{ method: 'state.recipes', args: { npcId: '<npcId>', kind: 'all' } }, { method: 'state.productionCatalog', args: { id: '<buildingId>' } }, { method: 'helper.ensureProduction', args: { id: '<buildingId>', name: '<recipeName>', loop: 1 } }],
    formation: [{ method: 'state.formationCatalog', args: {} }, { method: 'game.createFormation', args: { shapeId: '<shapeId>', npcIds: ['<npcId>'], confirm: true } }, { method: 'state.formations', args: {} }],
    formationCombat: [{ method: 'helper.commandFormationCombat', args: { formationId: '<formationId>', targetIds: ['<enemyId>'], policy: 'focus-fire' } }, { method: 'helper.retreatTo', args: { npcIds: ['<memberId>'], x: '<safeX>', y: '<safeY>' } }],
    authorFormation: [{ method: 'state.formationCatalog', args: {} }, { method: 'game.createFormationShape', args: { name: '<name>', nodes: [{ name: '<nodeDefinition>', x: 0, y: 0, rotation: 0 }], confirm: true } }, { method: 'game.saveFormationScheme', args: { shapeId: '<shapeId>', npcIds: ['<npcId>'], confirm: true } }],
    mentorship: [{ method: 'state.npcCultivation', args: { id: '<teacherId>' } }, { method: 'game.apprentice', args: { studentId: '<studentId>', masterId: '<teacherId>', confirm: true } }, { method: 'game.teachSecret', args: { teacherId: '<teacherId>', studentId: '<studentId>', secret: '<secretId>', confirm: true } }],
    farmingAndStorage: [{ method: 'state.areaCatalog', args: {} }, { method: 'game.createArea', args: { type: '<Plant-or-Storage-type>', x: '<x>', y: '<y>' } }, { method: 'game.configurePlantArea', args: { id: '<areaId>', crop: '<plantDefName>' } }, { method: 'state.areas', args: {} }],
    trade: [{ method: 'state.trade', args: {} }, { method: 'game.openSchoolTrade', args: { schoolId: '<schoolId>' } }, { method: 'ui.snapshot', args: { limit: 3000 } }, { method: 'ui.input', args: { targetId: '<freshTargetId>', action: 'click' } }, { method: 'state.tradeWindow', args: {} }, { method: 'game.confirmTrade', args: { confirm: true } }],
    exploration: [{ method: 'state.worldMap', args: {} }, { method: 'game.startExplore', args: { id: '<npcId>', place: '<placeName>', stay: false, confirm: true } }, { method: 'state.storyChoice', args: {} }, { method: 'game.chooseStoryOption', args: { index: '<optionIndex>', confirm: true } }],
    outspread: [{ method: 'state.outspread', args: {} }, { method: 'state.outspreadCatalog', args: { region: '<openedRegionName>' } }, { method: 'game.setOutspreadPolicy', args: { region: '<regionName>', name: '<availablePolicy>', confirm: true } }, { method: 'game.setOutspreadBuild', args: { region: '<regionName>', type: 'Region', index: 0, name: '<availableBuild>', confirm: true } }],
    outspreadStaff: [{ method: 'state.outspread', args: { name: '<regionName>' } }, { method: 'game.setOutspreadChief', args: { region: '<regionName>', npcId: '<npcId>', confirm: true } }, { method: 'game.sendOutspreadMember', args: { region: '<regionName>', npcId: '<npcId>', confirm: true } }],
    combat: [{ method: 'state.combat', args: { id: '<attackerId>' } }, { method: 'helper.engageCombat', args: { attackerId: '<attackerId>', targetId: '<targetId>' } }, { method: 'game.castSkill', args: { id: '<attackerId>', skill: '<skillName>', targetId: '<targetId>' } }],
    emergencyDisengage: [{ method: 'helper.emergencyDisengage', args: { npcIds: ['<npcId>'], force: false } }],
    breakthrough: [{ method: 'helper.prepareBreakthrough', args: { id: '<npcId>', minimumRate: 0.9 } }, { method: 'helper.prepareBreakthrough', args: { id: '<npcId>', minimumRate: 0.9, begin: true, confirm: true } }],
    artifactRefining: [{ method: 'state.item', args: { id: '<itemId>' } }, { method: 'helper.refineAndTrack', args: { npcId: '<npcId>', itemId: '<itemId>', kind: '<itemLabel>', refiningType: 0 } }],
    visitorInteraction: [{ method: 'state.interactions', args: { actorId: '<playerNpcId>', targetId: '<visitorNpcId>' } }, { method: 'game.performTalkAction', args: { actorId: '<playerNpcId>', targetId: '<visitorNpcId>', action: '<availableAction>', confirm: true } }],
    godCultivation: [{ method: 'state.godCultivation', args: { id: '<npcId>' } }, { method: 'state.godBuildingCatalog', args: { id: '<npcId>' } }, { method: 'game.createGodBuilding', args: { id: '<npcId>', name: '<unlockedBuilding>', cellId: '<cellId>', confirm: true } }, { method: 'state.godCultivation', args: { id: '<npcId>' } }],
    animalBreeding: [{ method: 'state.areas', args: {} }, { method: 'helper.moveAnimalsToBreedingArea', args: { areaId: '<breedingAreaId>', npcIds: ['<animalNpcId>'], waitForArrival: true } }, { method: 'state.npc', args: { id: '<animalNpcId>' } }],
    ensureDesignation: [{ method: 'helper.ensureThingCommand', args: { command: 'Cutoff', id: '<thingId>' } }],
    supervisedRun: [{ method: 'helper.runAndWatch', args: { conditions: [{ method: 'state.world', path: 'dayHour', operator: 'gte', value: 12 }], timeoutMs: 30000 } }],
    inspectNpc: [{ method: 'state.npcs', args: { limit: 30 } }, { method: 'state.npcProfile', args: { id: '<npcId>' } }, { method: 'state.npcCultivation', args: { id: '<npcId>' } }, { method: 'state.npcSkills', args: { id: '<npcId>' } }, { method: 'state.npcHealth', args: { id: '<npcId>' } }, { method: 'state.npcInventory', args: { id: '<npcId>' } }],
    inspectGrid: [{ method: 'state.grid', args: { x: '<x>', y: '<y>' } }],
    spiritGathering: [{ method: 'helper.planSpiritGathering', args: { x: '<seatX>', y: '<seatY>', radius: 6, element: '<optional target element>' } }, { method: 'state.buildCatalog', args: { query: '聚灵' } }, { method: 'helper.placeBuilding', args: { name: '<verified catalog name>', x: '<planned x>', y: '<planned y>' } }, { method: 'state.qiField', args: { x: '<seatX>', y: '<seatY>', radius: 6 } }],
    ominousRoom: [{ method: 'helper.planOminousRoom', args: { x: '<room center x>', y: '<room center y>', radius: 8 } }, { method: 'state.fengShui', args: { x: '<room center x>', y: '<room center y>', radius: 8 } }],
    findObjects: [{ method: 'state.things', args: { type: 'Building', query: '', playerOnly: true, limit: 100 } }],
    designateObject: [{ method: 'state.commandTypes', args: { query: 'Cutoff' } }, { method: 'game.addThingCommand', args: { command: 'CommandCutoff', id: '<thingId>', params: [] } }, { method: 'state.commands', args: { type: 'CommandCutoff' } }],
    designateGrid: [{ method: 'state.commandTypes', args: { query: 'RemoveFloor' } }, { method: 'game.addGridCommand', args: { command: 'CommandRemoveFloor', x: '<x>', y: '<y>', params: [] } }],
    mapModeAction: [{ method: 'state.input', args: {} }, { method: 'map.click', args: { x: '<x>', y: '<y>', button: 0, sequenceId: '<sequenceId>', requestId: '<uniqueRequestId>' } }, { method: 'state.grid', args: { x: '<x>', y: '<y>' } }],
    mapAreaAction: [{ method: 'state.input', args: {} }, { method: 'map.drag', args: { from: { x: '<x1>', y: '<y1>' }, to: { x: '<x2>', y: '<y2>' }, button: 0, sequenceId: '<sequenceId>', requestId: '<uniqueRequestId>' } }],
    operateWindow: [{ method: 'game.openPanel', args: { panel: 'build|produce|search|main|close|cancel' } }, { method: 'ui.snapshot', args: { limit: 3000 } }, { method: 'ui.input', args: { targetId: '<fresh targetId>', action: 'click' } }],
    sequence: [{ method: 'session.sequence.begin', args: { leaseMs: 15000 } }, { method: '<mutation>', args: { sequenceId: '<sequenceId>', requestId: '<uniqueRequestId>' } }, { method: 'session.sequence.end', args: { sequenceId: '<sequenceId>' } }],
    verify: [{ method: 'runtime.wait', args: { milliseconds: 500 } }, { method: 'state.overview', args: {} }],
  },
  safety: ['Use fresh UI target IDs after any screen change.', 'Ask before overwriting/deleting saves or taking irreversible sect/NPC actions.'],
};

function textContent(value) {
  return { type: 'text', text: JSON.stringify(value, null, 2) };
}

function numberArg(args, key, required = true) {
  const value = args[key];
  if (value === undefined && !required) return undefined;
  if (!Number.isFinite(value)) throw new Error(`${key} must be a finite number`);
  return value;
}

function boolArg(args, key, fallback = false) {
  return args[key] === undefined ? fallback : Boolean(args[key]);
}

function bridgeData(response, method) {
  if (!response?.success) throw new Error(`${method} failed: ${response?.error?.message || 'unknown bridge error'}`);
  return response.data;
}

function valueAtPath(value, dottedPath) {
  if (!dottedPath) return value;
  return dottedPath.split('.').reduce((current, part) => current == null ? undefined : current[part], value);
}

function conditionMatches(actual, operator, expected) {
  switch (operator) {
    case 'eq': return actual === expected;
    case 'ne': return actual !== expected;
    case 'gt': return actual > expected;
    case 'gte': return actual >= expected;
    case 'lt': return actual < expected;
    case 'lte': return actual <= expected;
    case 'contains': return typeof actual === 'string' ? actual.includes(String(expected)) : Array.isArray(actual) && actual.includes(expected);
    default: throw new Error('operator must be eq, ne, gt, gte, lt, lte, or contains');
  }
}

async function withActionSequence(args, deps, action) {
  let sequenceId = args.sequenceId;
  let owned = false;
  if (!sequenceId) {
    const lease = bridgeData(await deps.runtimeCall('session.sequence.begin', { leaseMs: Math.min(60_000, Math.max(15_000, args.timeoutMs || 15_000)) }), 'session.sequence.begin');
    sequenceId = lease.sequenceId;
    owned = true;
  }
  try { return await action(sequenceId); }
  finally {
    if (owned) {
      try { await deps.runtimeCall('session.sequence.end', { sequenceId }); } catch {}
    }
  }
}

async function advanceRuntime(args, deps) {
  const frames = Math.max(1, Math.min(100_000, Math.trunc(args.frames ?? 1)));
  const speed = Math.max(0.1, Math.min(10, Number(args.speed ?? 1)));
  const timeoutMs = Math.max(500, Math.min(300_000, Math.trunc(args.timeoutMs ?? 30_000)));
  return withActionSequence({ ...args, timeoutMs }, deps, async (sequenceId) => {
    const before = bridgeData(await deps.runtimeCall('state.world', {}), 'state.world');
    let after = before;
    const deadline = Date.now() + timeoutMs;
    await deps.runtimeCall('runtime.speed', { value: speed, sequenceId, requestId: `advance-play-${Date.now()}-${Math.random()}` });
    try {
      while ((after.frameCount - before.frameCount) < frames) {
        if (Date.now() >= deadline) throw new Error(`runtime.advance timed out after ${timeoutMs} ms at ${after.frameCount - before.frameCount}/${frames} frames`);
        await deps.wait(20);
        after = bridgeData(await deps.runtimeCall('state.world', {}), 'state.world');
      }
    } finally {
      await deps.runtimeCall('runtime.pause', { sequenceId, requestId: `advance-pause-${Date.now()}-${Math.random()}` });
    }
    const paused = bridgeData(await deps.runtimeCall('state.world', {}), 'state.world');
    return { success: true, data: { requestedFrames: frames, advancedFrames: paused.frameCount - before.frameCount, before, after: paused, completion: 'verified' } };
  });
}

async function callMethod(method, args, deps) {
  if (method.startsWith('helper.')) return { content: [textContent(await runHelper(method, args, deps))] };
  switch (method) {
    case 'session.status': {
      try {
        const response = await deps.runtimeCall(method, args);
        if (response?.success && Array.isArray(response.data?.methods)) response.data.methods = [...new Set([...response.data.methods, ...STATUS_METHOD_ADDITIONS])];
        return { content: [textContent(response)] };
      } catch (error) {
        const window = await deps.runWindows('status');
        return { content: [textContent({ success: true, data: { bridge: 'disconnected', window, transportError: error.message } })] };
      }
    }
    case 'session.launch': {
      const root = args.gameRoot || process.env.ACS_ROOT;
      if (!root) throw new Error('Set ACS_ROOT or pass args.gameRoot');
      const executable = path.join(root, 'Amazing Cultivation Simulator.exe');
      await access(executable);
      const child = spawn(executable, [], { cwd: root, detached: true, stdio: 'ignore', windowsHide: false });
      child.unref();
      return { content: [textContent({ ok: true, launched: executable, pid: child.pid })] };
    }
    case 'state.world':
    case 'session.sequence.begin':
    case 'session.sequence.end':
    case 'session.result':
    case 'events.poll':
    case 'state.overview':
    case 'state.map':
    case 'state.npcs':
    case 'state.npc':
    case 'state.npcProfile':
    case 'state.npcCultivation':
    case 'state.breakthrough':
    case 'state.bodyCultivation':
    case 'state.esotericaCatalog':
    case 'state.spellCatalog':
    case 'state.skillTree':
    case 'state.godCultivation':
    case 'state.godBuildingCatalog':
    case 'state.interactions':
    case 'state.npcSkills':
    case 'state.npcHealth':
    case 'state.npcInventory':
    case 'state.item':
    case 'state.refiningForecast':
    case 'state.combat':
    case 'state.buildCatalog':
    case 'state.building':
    case 'state.productionCatalog':
    case 'state.recipes':
    case 'state.formations':
    case 'state.formationCatalog':
    case 'state.areas':
    case 'state.areaCatalog':
    case 'state.trade':
    case 'state.tradeWindow':
    case 'state.worldMap':
    case 'state.outspread':
    case 'state.outspreadCatalog':
    case 'state.dlc':
    case 'state.storyChoice':
    case 'state.grid':
    case 'state.qiField':
    case 'state.fengShui':
    case 'state.population':
    case 'state.things':
    case 'state.resources':
    case 'state.school':
    case 'state.selection':
    case 'state.enums':
    case 'state.alerts':
    case 'state.commandTypes':
    case 'state.commands':
    case 'state.input':
    case 'game.focusNpc':
    case 'game.focusThing':
    case 'game.lookGrid':
    case 'game.openPanel':
    case 'game.save':
    case 'game.setWorkPriority':
    case 'game.setWorkEnabled':
    case 'game.setVipWork':
    case 'game.interruptNpcJob':
    case 'game.setSchedule':
    case 'game.setSchoolMaster':
    case 'game.setPracticeMode':
    case 'game.setPracticeBehaviour':
    case 'game.changeGong':
    case 'game.promoteInnerDisciple':
    case 'game.learnSkillTreeNode':
    case 'game.beginBreakthrough':
    case 'game.useItem':
    case 'game.equipItem':
    case 'game.unequipItem':
    case 'game.assignBed':
    case 'game.assignPracticePlace':
    case 'game.huntAnimal':
    case 'game.slaughterAnimal':
    case 'game.moveNpcTo':
    case 'game.talkToNpc':
    case 'game.performTalkAction':
    case 'game.openGodCity':
    case 'game.createGodBuilding':
    case 'game.upgradeGodBuilding':
    case 'game.removeGodBuilding':
    case 'game.toggleGodBuilding':
    case 'game.setGodBuildingGuard':
    case 'game.refineArtifact':
    case 'game.changeBodyAttitude':
    case 'game.beginBodyQuenching':
    case 'game.cancelBodyQuenching':
    case 'game.attackTarget':
    case 'game.stopCombat':
    case 'game.castSkill':
    case 'game.setAutoSkills':
    case 'game.castArtifacts':
    case 'game.beginBuild':
    case 'game.placeBuilding':
    case 'game.moveBuilding':
    case 'game.changeBuildingMaterial':
    case 'game.dismantleBuilding':
    case 'game.addProductionTask':
    case 'game.removeProductionTask':
    case 'game.createFormation':
    case 'game.cancelFormation':
    case 'game.createFormationShape':
    case 'game.removeFormationShape':
    case 'game.saveFormationScheme':
    case 'game.removeFormationScheme':
    case 'game.apprentice':
    case 'game.teachSecret':
    case 'game.createArea':
    case 'game.addAreaGrid':
    case 'game.removeAreaGrid':
    case 'game.configurePlantArea':
    case 'game.configureStorageArea':
    case 'game.configureGuardArea':
    case 'game.removeArea':
    case 'game.openSchoolTrade':
    case 'game.confirmTrade':
    case 'game.openWorldMap':
    case 'game.startExplore':
    case 'game.recallExplorer':
    case 'game.chooseStoryOption':
    case 'game.openOutspreadRegion':
    case 'game.setOutspreadPolicy':
    case 'game.setOutspreadBuild':
    case 'game.cancelOutspreadBuild':
    case 'game.setOutspreadChief':
    case 'game.sendOutspreadMember':
    case 'game.removeOutspreadMember':
    case 'game.startOutspreadSupervision':
    case 'game.collectOutspreadProducts':
    case 'game.selectCommand':
    case 'game.addThingCommand':
    case 'game.addGridCommand':
    case 'game.removeThingCommand':
    case 'map.click':
    case 'map.drag':
    case 'ui.snapshot':
    case 'ui.input':
    case 'runtime.pause':
    case 'runtime.speed':
      return { content: [textContent(await deps.runtimeCall(method, args))] };
    case 'runtime.nextFrame':
      return { content: [textContent(await advanceRuntime({ ...args, frames: 1 }, deps))] };
    case 'runtime.advance':
      return { content: [textContent(await advanceRuntime(args, deps))] };
    case 'runtime.runUntil': {
      const observeMethod = args.observeMethod || 'state.overview';
      if (!String(observeMethod).startsWith('state.')) throw new Error('observeMethod must be a state.* method');
      const operator = args.operator || 'eq';
      const timeoutMs = Math.max(500, Math.min(300_000, Math.trunc(args.timeoutMs ?? 30_000)));
      const pollMs = Math.max(20, Math.min(5000, Math.trunc(args.pollMs ?? 100)));
      const speed = Math.max(0.1, Math.min(10, Number(args.speed ?? 1)));
      const result = await withActionSequence({ ...args, timeoutMs }, deps, async (sequenceId) => {
        const deadline = Date.now() + timeoutMs;
        let observations = 0;
        let last;
        await deps.runtimeCall('runtime.speed', { value: speed, sequenceId, requestId: `until-play-${Date.now()}-${Math.random()}` });
        try {
          while (true) {
            last = bridgeData(await deps.runtimeCall(observeMethod, args.observeArgs || {}), observeMethod);
            observations++;
            const actual = valueAtPath(last, args.path || '');
            if (conditionMatches(actual, operator, args.value)) return { actual, last, observations };
            if (Date.now() >= deadline) throw new Error(`runtime.runUntil timed out: ${args.path || '<root>'} ${operator} ${JSON.stringify(args.value)}; last=${JSON.stringify(actual)}`);
            await deps.wait(pollMs);
          }
        } finally {
          await deps.runtimeCall('runtime.pause', { sequenceId, requestId: `until-pause-${Date.now()}-${Math.random()}` });
        }
      });
      return { content: [textContent({ success: true, data: { ...result, completion: 'condition-met' } })] };
    }
    case 'ui.observe': {
      const { result, base64 } = await deps.captureWindow({ IncludeBorder: boolArg(args, 'includeBorder', false) });
      return { content: [textContent(result), { type: 'image', data: base64, mimeType: 'image/png' }] };
    }
    case 'ui.focus':
      return { content: [textContent(await deps.runWindows('focus'))] };
    case 'ui.move':
      return { content: [textContent(await deps.runWindows('move', { X: numberArg(args, 'x'), Y: numberArg(args, 'y'), Focus: boolArg(args, 'focus', true) }))] };
    case 'ui.click':
      return { content: [textContent(await deps.runWindows('click', { X: numberArg(args, 'x'), Y: numberArg(args, 'y'), Button: args.button || 'left', Count: args.count ?? 1, Focus: boolArg(args, 'focus', true) }))] };
    case 'ui.drag':
      return { content: [textContent(await deps.runWindows('drag', { X: numberArg(args, 'x'), Y: numberArg(args, 'y'), ToX: numberArg(args, 'toX'), ToY: numberArg(args, 'toY'), Button: args.button || 'left', DurationMs: args.durationMs ?? 350, Focus: boolArg(args, 'focus', true) }, { timeoutMs: 20_000 }))] };
    case 'ui.scroll':
      return { content: [textContent(await deps.runWindows('scroll', { X: numberArg(args, 'x', false), Y: numberArg(args, 'y', false), Delta: args.delta ?? -120, Focus: boolArg(args, 'focus', true) }))] };
    case 'ui.key': {
      if (typeof args.key !== 'string' || !args.key.trim()) throw new Error('key must be a non-empty string');
      return { content: [textContent(await deps.runWindows('key', { Key: args.key, Ctrl: boolArg(args, 'ctrl'), Alt: boolArg(args, 'alt'), Shift: boolArg(args, 'shift'), Focus: boolArg(args, 'focus', true) }))] };
    }
    case 'ui.type': {
      if (typeof args.text !== 'string') throw new Error('text must be a string');
      return { content: [textContent(await deps.runWindows('type', { Text: args.text, IntervalMs: args.intervalMs ?? 8, Focus: boolArg(args, 'focus', true) }, { timeoutMs: 30_000 }))] };
    }
    case 'runtime.wait': {
      const milliseconds = Math.max(0, Math.min(60_000, args.milliseconds ?? 500));
      await deps.wait(milliseconds);
      return { content: [textContent({ ok: true, waitedMs: milliseconds })] };
    }
    default:
      throw new Error(`Unknown method: ${method}`);
  }
}

export function createHandler(overrides = {}) {
  const deps = {
    runWindows,
    captureWindow,
    runtimeCall,
    layoutTemplates,
    wait: (ms) => new Promise((resolve) => setTimeout(resolve, ms)),
    ...overrides,
  };
  return async function handle(message) {
    if (message.method === 'notifications/initialized' || message.method === 'notifications/cancelled') return null;
    if (message.method === 'initialize') {
      return { jsonrpc: '2.0', id: message.id, result: { protocolVersion: message.params?.protocolVersion || '2025-06-18', capabilities: { tools: { listChanged: false } }, serverInfo: SERVER_INFO } };
    }
    if (message.method === 'ping') return { jsonrpc: '2.0', id: message.id, result: {} };
    if (message.method === 'tools/list') return { jsonrpc: '2.0', id: message.id, result: { tools: TOOLS } };
    if (message.method === 'tools/call') {
      try {
        const { name, arguments: input = {} } = message.params || {};
        if (name === 'acsmcp_methods') return { jsonrpc: '2.0', id: message.id, result: { content: [textContent(METHOD_HELP)] } };
        if (name === 'acsmcp_guide') return { jsonrpc: '2.0', id: message.id, result: { content: [textContent(PLAY_GUIDE)] } };
        if (name !== 'acsmcp_call') throw new Error(`Unknown tool: ${name}`);
        if (!input.method) throw new Error('method is required');
        const result = await callMethod(input.method, input.args || {}, deps);
        return { jsonrpc: '2.0', id: message.id, result };
      } catch (error) {
        return { jsonrpc: '2.0', id: message.id, result: { isError: true, content: [textContent({ ok: false, error: error.message, details: error.details || undefined })] } };
      }
    }
    if (message.id === undefined) return null;
    return { jsonrpc: '2.0', id: message.id, error: { code: -32601, message: `Method not found: ${message.method}` } };
  };
}

export async function runStdio() {
  const handle = createHandler();
  const lines = createInterface({ input: process.stdin, crlfDelay: Infinity });
  for await (const line of lines) {
    if (!line.trim()) continue;
    let response;
    try {
      response = await handle(JSON.parse(line));
    } catch (error) {
      response = { jsonrpc: '2.0', id: null, error: { code: -32700, message: `Parse error: ${error.message}` } };
    }
    if (response) process.stdout.write(`${JSON.stringify(response)}\n`);
  }
}

const invokedPath = process.argv[1] ? path.resolve(process.argv[1]) : '';
if (invokedPath === fileURLToPath(import.meta.url)) runStdio().catch((error) => { process.stderr.write(`${error.stack || error}\n`); process.exitCode = 1; });
