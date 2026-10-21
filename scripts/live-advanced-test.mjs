import { runtimeCall } from '../src/runtime-client.mjs';

const checks = [];
const data = (response) => {
  if (!response?.success) throw new Error(response?.error?.message || 'Bridge call failed');
  return response.data;
};
const check = (name, ok, details = {}) => {
  checks.push({ name, ok: Boolean(ok), ...details });
  if (!ok) throw new Error(`Check failed: ${name}`);
};

async function confirmationGuard(method, args) {
  try {
    await runtimeCall(method, args);
    return false;
  } catch (error) {
    return error.details?.error?.code === 'CONFIRMATION_REQUIRED';
  }
}

try {
  const status = data(await runtimeCall('session.status'));
  check('v1.0 bridge methods loaded', status.methods.includes('state.formations') && status.methods.includes('game.chooseStoryOption') && status.methods.includes('state.qiField') && status.methods.includes('state.fengShui'), { methodCount: status.methods.length });

  const npcs = data(await runtimeCall('state.npcs', { limit: 10 }));
  check('NPC available for contextual reads', npcs.total > 0, { count: npcs.total });
  const npcId = npcs.items[0].id;
  const npcKey = npcs.items[0].key;

  const qiField = data(await runtimeCall('state.qiField', { key: npcKey, radius: 2, top: 5 }));
  const fengShui = data(await runtimeCall('state.fengShui', { key: npcKey, radius: 12 }));
  check('qi field and feng shui readable', qiField.count > 0 && Array.isArray(qiField.points) && Array.isArray(fengShui.buildings) && Array.isArray(fengShui.rooms), { sampled: qiField.count, buildings: fengShui.buildingCount, rooms: fengShui.roomCount });

  const buildCatalog = data(await runtimeCall('state.buildCatalog', {}));
  check('build catalog fixed', buildCatalog.count > 0, { count: buildCatalog.count, schoolUnlockLevel: buildCatalog.schoolUnlockLevel });

  const recipes = data(await runtimeCall('state.recipes', { npcId, kind: 'all' }));
  check('alchemy/artifact recipes readable', recipes.count > 0, { count: recipes.count });

  const formationCatalog = data(await runtimeCall('state.formationCatalog', {}));
  const formations = data(await runtimeCall('state.formations', {}));
  check('formation catalogs readable', Array.isArray(formationCatalog.shapes) && Array.isArray(formations.items), { shapes: formationCatalog.shapeCount, active: formations.count });

  const areaCatalog = data(await runtimeCall('state.areaCatalog', {}));
  const areas = data(await runtimeCall('state.areas', {}));
  check('agriculture/storage areas readable', areaCatalog.count > 0 && Array.isArray(areas.items), { types: areaCatalog.count, areas: areas.count });

  const trade = data(await runtimeCall('state.trade', {}));
  const tradeWindow = data(await runtimeCall('state.tradeWindow', {}));
  check('trade/diplomacy state readable', Array.isArray(trade.schools) && typeof tradeWindow.available === 'boolean', { schools: trade.schoolCount, windowOpen: tradeWindow.available });

  const worldMap = data(await runtimeCall('state.worldMap', {}));
  const storyChoice = data(await runtimeCall('state.storyChoice', {}));
  check('world travel/event state readable', worldMap.placeCount > 0 && typeof storyChoice.available === 'boolean', { places: worldMap.placeCount, explorations: worldMap.explorationCount, choiceOpen: storyChoice.available });

  const dlc = data(await runtimeCall('state.dlc', {}));
  check('official DLC state readable', Array.isArray(dlc.items) && dlc.items.some((item) => item.name === 'Panda') && dlc.items.some((item) => item.name === 'WuDang'), { dlc: dlc.items });

  const outspread = data(await runtimeCall('state.outspread', {}));
  const outspreadCatalog = data(await runtimeCall('state.outspreadCatalog', {}));
  check('outspread state and catalog readable', Array.isArray(outspread.regions) && Array.isArray(outspreadCatalog.regions) && Array.isArray(outspreadCatalog.builds) && Array.isArray(outspreadCatalog.policies), { opened: outspread.count, regionDefinitions: outspreadCatalog.regions.length, builds: outspreadCatalog.builds.length, policies: outspreadCatalog.policies.length });

  check('formation mutation protected', await confirmationGuard('game.createFormation', { shapeId: 1, npcIds: [npcId] }));
  check('apprenticeship protected', await confirmationGuard('game.apprentice', { studentId: npcId, masterId: npcId }));
  check('teaching protected', await confirmationGuard('game.teachSecret', { teacherId: npcId, studentId: npcId, secret: 'test' }));
  check('exploration protected', await confirmationGuard('game.startExplore', { id: npcId, place: 'test' }));
  check('trade settlement protected', await confirmationGuard('game.confirmTrade', {}));
  check('story choice protected', await confirmationGuard('game.chooseStoryOption', { index: 0 }));
  check('outspread opening protected', await confirmationGuard('game.openOutspreadRegion', { region: 'test', npcId }));
  check('outspread policy protected', await confirmationGuard('game.setOutspreadPolicy', { region: 'test', name: 'test' }));
  check('outspread building protected', await confirmationGuard('game.setOutspreadBuild', { region: 'test', type: 'Region', index: 0, name: 'test' }));
  check('outspread member removal protected', await confirmationGuard('game.removeOutspreadMember', { region: 'test', memberId: 1 }));
} catch (error) {
  checks.push({ name: 'advanced live suite', ok: false, error: error.message });
} finally {
  try { await runtimeCall('runtime.pause', { requestId: `advanced-live-final-pause-${Date.now()}` }); } catch {}
}

const ok = checks.every((item) => item.ok);
console.log(JSON.stringify({ ok, checks, cleanup: { paused: true, mutationsPerformed: false } }, null, 2));
if (!ok) process.exitCode = 1;
