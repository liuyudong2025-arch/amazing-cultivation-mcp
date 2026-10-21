import { runHelper } from '../agent/helpers/index.mjs';
import { runtimeCall } from '../src/runtime-client.mjs';

const deps = { runtimeCall, wait: (ms) => new Promise((resolve) => setTimeout(resolve, ms)) };
const checks = [];
let testCommand;

function check(name, ok, details = {}) {
  checks.push({ name, ok: Boolean(ok), ...details });
  if (!ok) throw new Error(`Check failed: ${name}`);
}

try {
  const observed = (await runHelper('helper.observe', {}, deps)).data;
  check('helper.observe', observed.input.ready && observed.npcs.total > 0, {
    npcs: observed.npcs.total,
    alerts: observed.risks.alerts.length,
    criticalNeeds: observed.risks.criticalNeeds.length,
  });

  const plan = (await runHelper('helper.startingPlan', {}, deps)).data;
  check('helper.startingPlan', plan.mutationPerformed === false && plan.recommendations.length > 0, {
    recommendations: plan.recommendations.map((item) => item.kind),
  });

  const allNpcs = (await runHelper('helper.readAllNpcs', {}, deps)).data;
  check('helper.readAllNpcs', allNpcs.count === observed.npcs.total && allNpcs.consistency.sameWorld, { count: allNpcs.count });

  const npc = allNpcs.items[0];
  const dossier = (await runHelper('helper.inspectNpc', { id: npc.id }, deps)).data;
  check('helper.inspectNpc', dossier.id === npc.id && dossier.profile && dossier.skills && dossier.health && dossier.inventory, {
    npc: npc.name,
    cultivationAvailable: dossier.cultivation?.available,
    skillCount: dossier.skills?.count,
    injuryCount: dossier.health?.injuryCount,
  });

  const roster = (await runHelper('helper.cultivationRoster', {}, deps)).data;
  check('helper.cultivationRoster', roster.items.every((item) => item.available), { count: roster.count });

  const allResources = (await runHelper('helper.readAllResources', {}, deps)).data;
  check('helper.readAllResources', !allResources.truncated && allResources.consistency.sameWorld, { count: allResources.count });

  const safeWork = npc.workSettings.find((item) => item.priority >= 0);
  const work = (await runHelper('helper.ensureWork', {
    id: npc.id,
    workKind: safeWork.workKind,
    priority: safeWork.priority,
    enabled: !safeWork.banned,
  }, deps)).data;
  check('helper.ensureWork idempotent', work.changed === false && work.completion === 'verified', { npc: npc.name, workKind: safeWork.workKind });

  const scheduleEntry = observed.school.schedule[0];
  const schedule = (await runHelper('helper.ensureSchedule', { hour: scheduleEntry.hour, plan: scheduleEntry.plan }, deps)).data;
  check('helper.ensureSchedule idempotent', schedule.changed === false && schedule.completion === 'verified', scheduleEntry);

  const selected = (await runHelper('helper.selectNpc', { id: npc.id }, deps)).data;
  check('helper.selectNpc', selected.selection.some((item) => item.id === npc.id), { npc: npc.name, changed: selected.changed });

  const trees = (await runHelper('helper.queryAllThings', { query: '银杏树', maxItems: 100 }, deps)).data;
  const active = (await runtimeCall('state.commands', { type: 'Cutoff', limit: 2000 })).data.items;
  const tree = trees.items.find((item) => !active.some((command) => command.ownerThingId === item.id));
  if (tree) {
    testCommand = { command: 'Cutoff', id: tree.id };
    const added = (await runHelper('helper.ensureThingCommand', testCommand, deps)).data;
    check('helper.ensureThingCommand', added.changed && added.commandState?.ownerThingId === tree.id, { thingId: tree.id });
    const removed = (await runHelper('helper.ensureNoThingCommand', testCommand, deps)).data;
    testCommand = null;
    check('helper.ensureNoThingCommand', removed.changed && removed.commandState === null, { thingId: tree.id });
  } else {
    checks.push({ name: 'command ensure/remove', ok: true, skipped: 'No uncommanded 银杏树 found' });
  }

  const beforeWorld = (await runtimeCall('state.world', {})).data;
  const watched = (await runHelper('helper.runAndWatch', {
    conditions: [{ method: 'state.world', path: 'frameCount', operator: 'gt', value: beforeWorld.frameCount }],
    timeoutMs: 5_000,
    pollMs: 50,
  }, deps)).data;
  check('helper.runAndWatch', watched.stopReason === 'condition-met' && watched.finalWorld.paused, {
    beforeFrame: beforeWorld.frameCount,
    afterFrame: watched.finalWorld.frameCount,
  });
} catch (error) {
  checks.push({ name: 'live helper suite', ok: false, error: error.message });
} finally {
  if (testCommand) {
    try { await runHelper('helper.ensureNoThingCommand', testCommand, deps); } catch (error) { checks.push({ name: 'cleanup test command', ok: false, error: error.message }); }
  }
  try { await runtimeCall('runtime.pause', { requestId: `helper-live-final-pause-${Date.now()}` }); } catch (error) { checks.push({ name: 'cleanup pause', ok: false, error: error.message }); }
}

const ok = checks.every((item) => item.ok);
console.log(JSON.stringify({ ok, checks, cleanup: { testCommandRemoved: !testCommand, paused: true } }, null, 2));
if (!ok) process.exitCode = 1;
