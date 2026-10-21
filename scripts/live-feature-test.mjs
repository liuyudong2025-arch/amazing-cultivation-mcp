import { runtimeCall } from '../src/runtime-client.mjs';

const report = { ok: false, checks: [], cleanup: [] };
const data = (response) => {
  if (!response?.success) throw new Error(`${response?.error?.code || 'BRIDGE_ERROR'}: ${response?.error?.message || 'unknown error'}`);
  return response.data;
};
const check = (name, ok, details = {}) => {
  report.checks.push({ name, ok: Boolean(ok), ...details });
  if (!ok) throw new Error(`Check failed: ${name}`);
};

let sequenceId;
let workRestore;
let scheduleRestore;
try {
  const lease = data(await runtimeCall('session.sequence.begin', { leaseMs: 60_000 }));
  sequenceId = lease.sequenceId;
  data(await runtimeCall('runtime.pause', { sequenceId, requestId: `feature-pause-${Date.now()}` }));

  const npcs = data(await runtimeCall('state.npcs', { limit: 20 }));
  const npc = npcs.items[0];
  check('scenario NPC available', Boolean(npc), { count: npcs.total, npc: npc?.name });

  const tree = data(await runtimeCall('state.skillTree', { id: npc.id }));
  check('native cultivation tree readable', Array.isArray(tree.nodes) && Array.isArray(tree.learned), { gong: tree.gong, nodes: tree.count, active: tree.active });

  const things = data(await runtimeCall('state.things', { type: 'Item', playerOnly: true, limit: 100 }));
  if (things.items.length) {
    const item = things.items[0];
    const forecast = data(await runtimeCall('state.refiningForecast', { npcId: npc.id, itemId: item.id }));
    check('native refining formula executes', Number.isFinite(forecast.successRate) && Number.isFinite(forecast.quality.minimum) && Number.isFinite(forecast.quality.maximum), { item: item.name, successRate: forecast.successRate, quality: forecast.quality, lingInheritance: forecast.lingInheritance });
  } else {
    report.checks.push({ name: 'native refining formula executes', ok: true, skipped: 'No player item exists in this save' });
  }

  const work = npc.workSettings.find((item) => item.workKind !== 'None' && Number.isInteger(item.priority));
  if (work) {
    const changedPriority = work.priority === 9 ? 8 : work.priority + 1;
    workRestore = { id: npc.id, workKind: work.workKind, priority: work.priority };
    data(await runtimeCall('game.setWorkPriority', { id: npc.id, workKind: work.workKind, priority: changedPriority, sequenceId, requestId: `feature-work-change-${Date.now()}` }));
    const changed = data(await runtimeCall('state.npc', { id: npc.id })).workSettings.find((item) => item.workKind === work.workKind);
    check('work priority mutation verified', changed?.priority === changedPriority, { workKind: work.workKind, before: work.priority, changed: changed?.priority });
    data(await runtimeCall('game.setWorkPriority', { ...workRestore, sequenceId, requestId: `feature-work-restore-${Date.now()}` }));
    const restored = data(await runtimeCall('state.npc', { id: npc.id })).workSettings.find((item) => item.workKind === work.workKind);
    check('work priority restored', restored?.priority === work.priority, { restored: restored?.priority });
    workRestore = null;
  }

  const school = data(await runtimeCall('state.school', {}));
  const hour = school.schedule[0];
  const enums = data(await runtimeCall('state.enums', {}));
  const replacement = enums.timePlans.find((plan) => !['None', 'Count', hour.plan].includes(plan));
  if (!replacement) throw new Error('No alternate valid schedule plan is available');
  scheduleRestore = { hour: hour.hour, plan: hour.plan };
  data(await runtimeCall('game.setSchedule', { hour: hour.hour, plan: replacement, sequenceId, requestId: `feature-schedule-change-${Date.now()}` }));
  let changedSchool = data(await runtimeCall('state.school', {}));
  check('schedule mutation verified', changedSchool.schedule.find((item) => item.hour === hour.hour)?.plan === replacement, { hour: hour.hour, before: hour.plan, changed: replacement });
  data(await runtimeCall('game.setSchedule', { ...scheduleRestore, sequenceId, requestId: `feature-schedule-restore-${Date.now()}` }));
  changedSchool = data(await runtimeCall('state.school', {}));
  check('schedule restored', changedSchool.schedule.find((item) => item.hour === hour.hour)?.plan === hour.plan, { restored: hour.plan });
  scheduleRestore = null;
  report.ok = report.checks.every((item) => item.ok);
} catch (error) {
  report.error = error.message;
} finally {
  try { if (workRestore) data(await runtimeCall('game.setWorkPriority', { ...workRestore, sequenceId, requestId: `feature-final-work-restore-${Date.now()}` })); } catch (error) { report.cleanup.push({ workRestored: false, error: error.message }); }
  try { if (scheduleRestore) { data(await runtimeCall('game.setSchedule', { ...scheduleRestore, sequenceId, requestId: `feature-final-schedule-restore-${Date.now()}` })); scheduleRestore = null; } } catch (error) { report.cleanup.push({ scheduleRestored: false, error: error.message }); }
  try { if (sequenceId) data(await runtimeCall('runtime.pause', { sequenceId, requestId: `feature-final-pause-${Date.now()}` })); } catch {}
  try { if (sequenceId) data(await runtimeCall('session.sequence.end', { sequenceId })); } catch {}
  report.cleanup.push({ workRestored: !workRestore, scheduleRestored: !scheduleRestore, paused: true, sequenceReleased: Boolean(sequenceId) });
}

console.log(JSON.stringify(report, null, 2));
if (!report.ok) process.exitCode = 1;
