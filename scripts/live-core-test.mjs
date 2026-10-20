import { runtimeCall } from '../src/runtime-client.mjs';

const report = { ok: false, checks: [], cleanup: [] };
let sequenceId = null;

function data(response) {
  if (!response?.success) throw new Error(response?.error?.message || 'Bridge call failed');
  return response.data;
}

function check(name, ok, details = {}) {
  report.checks.push({ name, ok: Boolean(ok), ...details });
  if (!ok) throw new Error(`Check failed: ${name}`);
}

const call = (method, args = {}, timeout = 15_000) => runtimeCall(method, args, timeout);
const unique = (label) => `live-${label}-${Date.now()}-${Math.random().toString(16).slice(2)}`;

try {
  const status = data(await call('session.status'));
  check('bridge connected', status.bridge === 'connected' && status.gameLoaded && status.mapLoaded);

  const input = data(await call('state.input'));
  check('map input ready', input.ready && !input.popupOpen && !input.modalOpen, { mode: input.mode });

  const commandTypes = data(await call('state.commandTypes'));
  check('native command types discoverable', commandTypes.count > 0 && commandTypes.items.some((item) => item.key === 'Cutoff'), { count: commandTypes.count });

  const npcs = data(await call('state.npcs', { limit: 30 }));
  check('NPC state readable', npcs.total > 0, { count: npcs.total, names: npcs.items.map((npc) => npc.name) });

  const eventsBefore = data(await call('events.poll', { after: 0, limit: 1 }));
  const lease = data(await call('session.sequence.begin', { leaseMs: 45_000 }));
  sequenceId = lease.sequenceId;
  check('sequence lease acquired', Boolean(sequenceId));

  const pauseId = unique('pause');
  data(await call('runtime.pause', { sequenceId, requestId: pauseId }));
  const paused = data(await call('state.world'));
  check('pause verified', paused.paused === true, { frame: paused.frameCount });

  const selectionBefore = data(await call('state.selection'));
  const selectedIds = new Set(selectionBefore.items.map((item) => item.id));
  const target = npcs.items.find((npc) => !selectedIds.has(npc.id)) || selectionBefore.items[0] || npcs.items[0];
  check('click target available', Number.isInteger(target?.key), { target: target?.name, key: target?.key });

  const clickId = unique('click');
  const click1 = await call('map.click', { key: target.key, button: 0, sequenceId, requestId: clickId });
  const click2 = await call('map.click', { key: target.key, button: 0, sequenceId, requestId: clickId });
  const selectionAfter = data(await call('state.selection'));
  check('map click selected a target', data(click1).inputProcessed && selectionAfter.count > 0, { selected: selectionAfter.items.map((item) => item.name) });
  check('requestId replayed', click2.meta?.replayed === true, { requestId: clickId });

  const dragId = unique('drag');
  const drag = data(await call('map.drag', {
    from: { key: target.key }, to: { key: target.key }, button: 0, sequenceId, requestId: dragId,
  }));
  check('map drag dispatched', drag.inputProcessed && drag.fromKey === target.key && drag.toKey === target.key);

  const activeCutoff = data(await call('state.commands', { type: 'Cutoff', limit: 2000 }));
  const trees = data(await call('state.things', { query: '银杏树', limit: 50 }));
  const tree = trees.items.find((item) => !activeCutoff.items.some((command) => command.ownerThingId === item.id));
  if (tree) {
    const addId = unique('add-command');
    const added = data(await call('game.addThingCommand', { command: 'Cutoff', id: tree.id, sequenceId, requestId: addId }));
    const afterAdd = data(await call('state.commands', { type: 'Cutoff', limit: 2000 }));
    check('native thing command added', added.commandAccepted && afterAdd.items.some((command) => command.ownerThingId === tree.id), { thing: tree.name, thingId: tree.id });

    if (added.created) {
      const removeId = unique('remove-command');
      const removed = data(await call('game.removeThingCommand', { command: 'Cutoff', id: tree.id, sequenceId, requestId: removeId }));
      let afterRemove = data(await call('state.commands', { type: 'Cutoff', limit: 2000 }));
      const removeDeadline = Date.now() + 2_000;
      while (afterRemove.items.some((command) => command.ownerThingId === tree.id) && Date.now() < removeDeadline) {
        await new Promise((resolve) => setTimeout(resolve, 50));
        afterRemove = data(await call('state.commands', { type: 'Cutoff', limit: 2000 }));
      }
      check('native thing command rolled back', removed.commandAccepted && !afterRemove.items.some((command) => command.ownerThingId === tree.id));
      report.cleanup.push({ removedTestCommand: true, thingId: tree.id });
    }

    const cached = data(await call('session.result', { requestId: addId }));
    check('mutation result cache readable', cached.found === true, { requestId: addId });
  } else {
    report.checks.push({ name: 'native thing command add/remove', ok: true, skipped: 'No uncommanded 银杏树 found' });
  }

  const beforeAdvance = data(await call('state.world'));
  data(await call('runtime.speed', { value: 1, sequenceId, requestId: unique('play') }));
  let afterAdvance = beforeAdvance;
  const deadline = Date.now() + 3_000;
  while (afterAdvance.frameCount <= beforeAdvance.frameCount && Date.now() < deadline) {
    await new Promise((resolve) => setTimeout(resolve, 50));
    afterAdvance = data(await call('state.world'));
  }
  data(await call('runtime.pause', { sequenceId, requestId: unique('repause') }));
  const finalWorld = data(await call('state.world'));
  check('frame advance works', afterAdvance.frameCount > beforeAdvance.frameCount, { before: beforeAdvance.frameCount, after: afterAdvance.frameCount });
  check('game returned to pause', finalWorld.paused === true);
  report.cleanup.push({ paused: true });

  const eventsAfter = data(await call('events.poll', { after: eventsBefore.latest, limit: 100 }));
  check('event journal recorded mutations', eventsAfter.count > 0, { count: eventsAfter.count, latest: eventsAfter.latest });

  report.ok = report.checks.every((item) => item.ok);
} catch (error) {
  report.error = error.message;
} finally {
  try {
    if (sequenceId) data(await call('runtime.pause', { sequenceId, requestId: unique('final-pause') }));
  } catch (error) {
    report.cleanup.push({ paused: false, error: error.message });
  }
  try {
    if (sequenceId) data(await call('session.sequence.end', { sequenceId }));
    report.cleanup.push({ sequenceReleased: Boolean(sequenceId) });
  } catch (error) {
    report.cleanup.push({ sequenceReleased: false, error: error.message });
  }
}

console.log(JSON.stringify(report, null, 2));
if (!report.ok) process.exitCode = 1;
