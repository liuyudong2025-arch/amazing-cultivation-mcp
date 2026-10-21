import assert from 'node:assert/strict';
import test from 'node:test';
import { HELPER_HELP, runHelper } from '../agent/helpers/index.mjs';

const immediate = async () => {};

test('helper catalog exposes the first autonomous-play layer', () => {
  for (const name of ['helper.observe', 'helper.readAllNpcs', 'helper.inspectNpc', 'helper.cultivationRoster', 'helper.ensureWork', 'helper.ensureSchedule', 'helper.ensureCultivation', 'helper.placeBuilding', 'helper.ensureProduction', 'helper.engageCombat', 'helper.ensureThingCommand', 'helper.runAndWatch', 'helper.startingPlan']) {
    assert.equal(typeof HELPER_HELP[name], 'string', name);
  }
});

test('placeBuilding verifies a newly created map object', async () => {
  let placed = false;
  const result = await runHelper('helper.placeBuilding', { name: 'Building_Test', key: 12 }, {
    wait: immediate,
    runtimeCall: async (method) => {
      if (method === 'session.sequence.begin') return { success: true, data: { sequenceId: 'lease' } };
      if (method === 'state.grid') return { success: true, data: { key: 12, things: placed ? [{ id: 2, defName: 'Building_Test' }] : [{ id: 1 }] } };
      if (method === 'game.placeBuilding') { placed = true; return { success: true, data: { commandAccepted: true } }; }
      if (method === 'session.sequence.end') return { success: true, data: {} };
      throw new Error(`Unexpected ${method}`);
    },
  });
  assert.equal(result.data.created[0].id, 2);
  assert.equal(result.data.completion, 'verified');
});

test('ensureProduction adds and verifies one production task', async () => {
  let tasks = [];
  const result = await runHelper('helper.ensureProduction', { id: 20, name: 'Meal' }, {
    wait: immediate,
    runtimeCall: async (method) => {
      if (method === 'session.sequence.begin') return { success: true, data: { sequenceId: 'lease' } };
      if (method === 'state.building') return { success: true, data: { productionTasks: tasks } };
      if (method === 'game.addProductionTask') { tasks = [{ id: 3, name: 'Meal' }]; return { success: true, data: { commandAccepted: true } }; }
      if (method === 'session.sequence.end') return { success: true, data: {} };
      throw new Error(`Unexpected ${method}`);
    },
  });
  assert.equal(result.data.task.name, 'Meal');
  assert.equal(result.data.changed, true);
});

test('engageCombat verifies native fighting target state', async () => {
  let fighting = false;
  const result = await runHelper('helper.engageCombat', { attackerId: 1, targetId: 2 }, {
    wait: immediate,
    runtimeCall: async (method) => {
      if (method === 'session.sequence.begin') return { success: true, data: { sequenceId: 'lease' } };
      if (method === 'state.combat') return { success: true, data: { fighting, targetId: fighting ? 2 : 0 } };
      if (method === 'game.attackTarget') { fighting = true; return { success: true, data: { commandAccepted: true } }; }
      if (method === 'session.sequence.end') return { success: true, data: {} };
      throw new Error(`Unexpected ${method}`);
    },
  });
  assert.equal(result.data.combat.targetId, 2);
  assert.equal(result.data.completion, 'verified');
});

test('ensureCultivation changes only mismatched cultivation settings and verifies', async () => {
  let mode = 'Normal';
  const calls = [];
  const result = await runHelper('helper.ensureCultivation', { id: 3, mode: 'Practice' }, {
    wait: immediate,
    runtimeCall: async (method, args) => {
      calls.push({ method, args });
      if (method === 'session.sequence.begin') return { success: true, data: { sequenceId: 'lease' } };
      if (method === 'state.npcCultivation') return { success: true, data: { cultivation: { available: true, practiceMode: mode, behaviour: 'Normal' } } };
      if (method === 'game.setPracticeMode') { mode = args.mode; return { success: true, data: { commandAccepted: true } }; }
      if (method === 'session.sequence.end') return { success: true, data: {} };
      throw new Error(`Unexpected ${method}`);
    },
  });
  assert.equal(result.data.changed, true);
  assert.equal(result.data.after.practiceMode, 'Practice');
  assert.deepEqual(result.data.actions, ['mode']);
});

test('inspectNpc combines every cultivation-domain view and derives warnings', async () => {
  const result = await runHelper('helper.inspectNpc', { id: 9 }, {
    wait: immediate,
    runtimeCall: async (method) => {
      const data = {
        'state.npc': { id: 9, name: 'Test', hp: 40, maxHp: 100 },
        'state.npcProfile': { profile: { physique: 5 } },
        'state.npcCultivation': { cultivation: { touchingNeck: true, currentNeck: { name: 'Neck' } } },
        'state.npcSkills': { skills: { items: [] } },
        'state.npcHealth': { health: { dying: false, injuryCount: 1 } },
        'state.npcInventory': { inventory: { equipped: [], carried: [] } },
      }[method];
      if (!data) throw new Error(`Unexpected ${method}`);
      return { success: true, data };
    },
  });
  assert.equal(result.data.profile.physique, 5);
  assert.deepEqual(result.data.warnings.map((item) => item.kind), ['low-hp', 'injuries', 'bottleneck']);
});

test('readAllNpcs follows pagination and reports consistency', async () => {
  const calls = [];
  const result = await runHelper('helper.readAllNpcs', { pageSize: 2 }, {
    wait: immediate,
    runtimeCall: async (method, args) => {
      calls.push({ method, args });
      const page = args.offset === 0
        ? { items: [{ id: 1 }, { id: 2 }], total: 3, nextOffset: 2 }
        : { items: [{ id: 3 }], total: 3, nextOffset: null };
      return { success: true, meta: { sessionId: 's', worldEpoch: 4, frame: 10 + args.offset }, data: page };
    },
  });
  assert.deepEqual(result.data.items.map((item) => item.id), [1, 2, 3]);
  assert.equal(result.data.consistency.sameWorld, true);
  assert.equal(result.data.consistency.mixedFrames, true);
  assert.deepEqual(calls.map((call) => call.args.offset), [0, 2]);
});

test('ensureWork changes only mismatches and verifies state', async () => {
  let priority = 2;
  const calls = [];
  const result = await runHelper('helper.ensureWork', { id: 7, workKind: 'Plant', priority: 3, enabled: true }, {
    wait: immediate,
    runtimeCall: async (method, args) => {
      calls.push({ method, args });
      if (method === 'session.sequence.begin') return { success: true, data: { sequenceId: 'lease' } };
      if (method === 'state.npc') return { success: true, data: { id: 7, workSettings: [{ workKind: 'Plant', priority, banned: false }] } };
      if (method === 'game.setWorkPriority') { priority = args.priority; return { success: true, data: { commandAccepted: true } }; }
      if (method === 'session.sequence.end') return { success: true, data: {} };
      throw new Error(`Unexpected ${method}`);
    },
  });
  assert.equal(result.data.changed, true);
  assert.deepEqual(result.data.actions, ['priority']);
  assert.equal(result.data.after.priority, 3);
  assert.equal(calls.some((call) => call.method === 'game.setWorkEnabled'), false);
  assert.equal(calls.at(-1).method, 'session.sequence.end');
});

test('ensureSchedule updates only different hours', async () => {
  const schedule = Array.from({ length: 24 }, (_, hour) => ({ hour, plan: 'Work' }));
  const mutations = [];
  const result = await runHelper('helper.ensureSchedule', { entries: [{ hour: 1, plan: 'Sleep' }, { hour: 2, plan: 'Work' }] }, {
    wait: immediate,
    runtimeCall: async (method, args) => {
      if (method === 'session.sequence.begin') return { success: true, data: { sequenceId: 'lease' } };
      if (method === 'state.school') return { success: true, data: { schedule } };
      if (method === 'game.setSchedule') { schedule[args.hour] = { hour: args.hour, plan: args.plan }; mutations.push(args.hour); return { success: true, data: { commandAccepted: true } }; }
      if (method === 'session.sequence.end') return { success: true, data: {} };
      throw new Error(`Unexpected ${method}`);
    },
  });
  assert.deepEqual(mutations, [1]);
  assert.equal(result.data.completion, 'verified');
});

test('ensureThingCommand adds once and verifies active command', async () => {
  let active = false;
  let adds = 0;
  const result = await runHelper('helper.ensureThingCommand', { command: 'Cutoff', id: 42 }, {
    wait: immediate,
    runtimeCall: async (method) => {
      if (method === 'session.sequence.begin') return { success: true, data: { sequenceId: 'lease' } };
      if (method === 'state.commands') return { success: true, data: { items: active ? [{ id: 100, ownerThingId: 42, valid: true, finished: false }] : [] } };
      if (method === 'game.addThingCommand') { active = true; adds++; return { success: true, data: { commandAccepted: true, created: true } }; }
      if (method === 'session.sequence.end') return { success: true, data: {} };
      throw new Error(`Unexpected ${method}`);
    },
  });
  assert.equal(adds, 1);
  assert.equal(result.data.changed, true);
  assert.equal(result.data.commandState.ownerThingId, 42);
});

test('runAndWatch stops on a structured condition and always pauses', async () => {
  let hour = 1;
  const calls = [];
  const result = await runHelper('helper.runAndWatch', {
    conditions: [{ method: 'state.world', path: 'dayHour', operator: 'gte', value: 2 }],
    pollMs: 20,
    timeoutMs: 1000,
  }, {
    wait: async () => { hour = 2; },
    runtimeCall: async (method, args) => {
      calls.push({ method, args });
      if (method === 'session.sequence.begin') return { success: true, data: { sequenceId: 'lease' } };
      if (method === 'state.world') return { success: true, data: { dayHour: hour, paused: calls.some((call) => call.method === 'runtime.pause') } };
      return { success: true, data: {} };
    },
  });
  assert.equal(result.data.stopReason, 'condition-met');
  assert.ok(calls.some((call) => call.method === 'runtime.pause'));
  assert.equal(calls.at(-1).method, 'session.sequence.end');
});

test('startingPlan prioritizes observed alerts and survival needs without mutation', async () => {
  const result = await runHelper('helper.startingPlan', {}, {
    wait: immediate,
    runtimeCall: async (method) => {
      if (method === 'state.overview') return { success: true, data: {
        world: { paused: true }, map: {}, school: {}, resources: { food: 10, items: [], total: 0, nextOffset: null },
        npcs: { items: [{ id: 1, name: '甲', hp: 50, maxHp: 100, currentJob: { name: 'JobFree' }, needs: [{ type: 'Food', min: -100, max: 160, realValue: -80 }] }], total: 1, nextOffset: null },
        alerts: { items: [{ description: '危险' }] }, selection: { items: [] },
      } };
      if (method === 'state.input') return { success: true, data: { ready: true } };
      if (method === 'state.commands') return { success: true, data: { items: [], truncated: false } };
      throw new Error(`Unexpected mutation or call: ${method}`);
    },
  });
  assert.equal(result.data.mutationPerformed, false);
  assert.equal(result.data.recommendations[0].kind, 'alerts');
  assert.ok(result.data.recommendations.some((item) => item.kind === 'needs'));
  assert.ok(result.data.recommendations.some((item) => item.kind === 'food'));
});

test('planSpiritGathering combines native qi, feng shui, and build candidates without mutation', async () => {
  const calls = [];
  const result = await runHelper('helper.planSpiritGathering', { x: 10, y: 20, radius: 2, element: 'Water' }, {
    wait: immediate,
    runtimeCall: async (method, args) => {
      calls.push({ method, args });
      if (method === 'state.qiField') return { success: true, data: {
        center: { key: 99, x: 10, y: 20 }, ling: { min: 1, max: 8, average: 4 }, elementTotals: [1, 2, 3, 4, 5],
        points: [
          { key: 99, x: 10, y: 20, distance: 0, ling: 4, things: [{ id: 1 }] },
          { key: 100, x: 11, y: 20, distance: 1, ling: 8, things: [], elements: [1], elementProportions: [1] },
        ],
      } };
      if (method === 'state.fengShui') return { success: true, data: { ranks: { Auspicious: 1 }, buildings: [], rooms: [] } };
      if (method === 'state.buildCatalog') return { success: true, data: { items: args.query === '聚灵' ? [{ key: 'a', name: 'Gatherer' }] : [] } };
      throw new Error(`Unexpected ${method}`);
    },
  });
  assert.equal(result.data.mutationPerformed, false);
  assert.equal(result.data.openPlacementCandidates[0].key, 100);
  assert.equal(result.data.buildCandidates.length, 1);
  assert.ok(calls.every((call) => call.method.startsWith('state.')));
});

test('planOminousRoom reports when the target is not yet enclosed', async () => {
  const result = await runHelper('helper.planOminousRoom', { x: 3, y: 4 }, {
    wait: immediate,
    runtimeCall: async (method) => {
      if (method === 'state.qiField') return { success: true, data: { center: { key: 34 }, ling: { average: 2 } } };
      if (method === 'state.fengShui') return { success: true, data: { rooms: [], ranks: {}, buildings: [] } };
      throw new Error(`Unexpected ${method}`);
    },
  });
  assert.equal(result.data.containingRooms.length, 0);
  assert.equal(result.data.unresolved.length, 1);
  assert.equal(result.data.target.desiredRank, '大凶');
});

test('buildRectangle reports exact completed cells when a later placement fails', async () => {
  const calls = [];
  let placed = false;
  const result = await runHelper('helper.buildRectangle', { name: 'Wall', x: 2, y: 3, width: 2, height: 1 }, {
    wait: immediate,
    runtimeCall: async (method, args) => {
      calls.push({ method, args });
      if (method === 'session.sequence.begin') return { success: true, data: { sequenceId: 'lease' } };
      if (method === 'session.sequence.end') return { success: true, data: {} };
      if (method === 'state.grid') return { success: true, data: { key: args.x + 100, x: args.x, y: args.y, things: args.x === 2 && placed ? [{ id: 77, type: 'Building' }] : [] } };
      if (method === 'game.placeBuilding') {
        if (args.x === 3) return { success: false, error: { message: 'blocked' } };
        placed = true;
        return { success: true, data: { commandAccepted: true } };
      }
      throw new Error(`Unexpected ${method}`);
    },
  });
  assert.equal(result.data.completion, 'partial');
  assert.equal(result.data.completed.length, 1);
  assert.deepEqual(result.data.failed, { x: 3, y: 3, error: 'game.placeBuilding failed: blocked' });
  assert.equal(calls.at(-1).method, 'session.sequence.end');
});

test('buildLayout places heterogeneous cells and preserves their per-cell options', async () => {
  const placed = new Map();
  const result = await runHelper('helper.buildLayout', { placements: [
    { name: 'Wall', x: 1, y: 2, stuffDefName: 'Wood' },
    { name: 'Door', x: 2, y: 2, direction: 3 },
  ] }, {
    wait: immediate,
    runtimeCall: async (method, args) => {
      if (method === 'session.sequence.begin') return { success: true, data: { sequenceId: args.sequenceId || 'lease' } };
      if (method === 'session.sequence.end') return { success: true, data: {} };
      if (method === 'state.grid') return { success: true, data: { key: args.x * 10 + args.y, x: args.x, y: args.y, things: placed.get(`${args.x}:${args.y}`) || [] } };
      if (method === 'game.placeBuilding') {
        placed.set(`${args.x}:${args.y}`, [{ id: 100 + placed.size, type: 'Building', defName: args.name }]);
        return { success: true, data: { commandAccepted: true, material: args.stuffDefName, direction: args.direction } };
      }
      throw new Error(`Unexpected ${method}`);
    },
  });
  assert.equal(result.data.completion, 'verified');
  assert.equal(result.data.completed.length, 2);
  assert.equal(result.data.completed[1].name, 'Door');
});

test('prepareBreakthrough returns a native-factor readiness report without mutating by default', async () => {
  const calls = [];
  const result = await runHelper('helper.prepareBreakthrough', { id: 7, minimumRate: 0.8 }, {
    wait: immediate,
    runtimeCall: async (method) => {
      calls.push(method);
      if (method === 'state.breakthrough') return { success: true, data: { id: 7, key: 77, conditionMet: true, successRate: 0.9, factors: { season: 1.2, qi: 0.8 } } };
      if (method === 'state.npcInventory') return { success: true, data: { inventory: { equipped: [], carried: [] } } };
      if (method === 'state.grid') return { success: true, data: { key: 77 } };
      if (method === 'state.qiField') return { success: true, data: { ling: { average: 5 } } };
      if (method === 'state.fengShui') return { success: true, data: { ranks: { Auspicious: 1 } } };
      throw new Error(`Unexpected mutation ${method}`);
    },
  });
  assert.equal(result.data.ready, true);
  assert.equal(result.data.mutationPerformed, false);
  assert.equal(result.data.weakestFactors[0].name, 'qi');
  assert.ok(calls.every((method) => method.startsWith('state.')));
});

test('emergencyDisengage pauses, stops combat, cancels affected formations, and verifies', async () => {
  let fighting = true;
  let formations = [{ id: 9, npcIds: [7, 8] }];
  const result = await runHelper('helper.emergencyDisengage', { npcIds: [7] }, {
    wait: immediate,
    runtimeCall: async (method, args) => {
      if (method === 'session.sequence.begin') return { success: true, data: { sequenceId: 'lease' } };
      if (method === 'session.sequence.end' || method === 'runtime.pause') return { success: true, data: {} };
      if (method === 'state.combat') return { success: true, data: { id: args.id, fighting } };
      if (method === 'game.stopCombat') { fighting = false; return { success: true, data: { commandAccepted: true } }; }
      if (method === 'state.formations') return { success: true, data: { items: formations } };
      if (method === 'game.cancelFormation') { formations = formations.filter((item) => item.id !== args.id); return { success: true, data: { removed: true } }; }
      throw new Error(`Unexpected ${method}`);
    },
  });
  assert.equal(result.data.completion, 'verified');
  assert.deepEqual(result.data.stillFighting, []);
  assert.deepEqual(result.data.stillFormed, []);
});

test('moveAnimalsToBreedingArea uses native move jobs and can return after queueing', async () => {
  const moves = [];
  const result = await runHelper('helper.moveAnimalsToBreedingArea', { areaId: 5, animalIds: [11, 12], waitForArrival: false }, {
    wait: immediate,
    runtimeCall: async (method, args) => {
      if (method === 'session.sequence.begin') return { success: true, data: { sequenceId: 'lease' } };
      if (method === 'session.sequence.end') return { success: true, data: {} };
      if (method === 'state.areas') return { success: true, data: { items: [{ id: 5, grids: [101, 102], breeding: { npcIds: [] } }] } };
      if (method === 'state.npc') return { success: true, data: { id: args.id, animal: { breedingAreaId: null } } };
      if (method === 'game.moveNpcTo') { moves.push({ id: args.id, key: args.key }); return { success: true, data: { commandAccepted: true } }; }
      throw new Error(`Unexpected ${method}`);
    },
  });
  assert.equal(result.data.completion, 'queued');
  assert.deepEqual(moves, [{ id: 11, key: 101 }, { id: 12, key: 102 }]);
});

test('layout templates persist relative coordinates and build at a rotated origin', async () => {
  let saved;
  const store = {
    file: 'memory://layouts',
    list: async () => saved ? [saved] : [],
    save: async (template) => { saved = template; },
    remove: async () => true,
  };
  const savedResult = await runHelper('helper.saveLayoutTemplate', {
    name: 'qi-room', originX: 10, originY: 20,
    placements: [{ name: 'Wall', x: 10, y: 20 }, { name: 'Door', x: 12, y: 20, direction: 1 }],
  }, { layoutTemplates: store });
  assert.deepEqual(savedResult.data.template.placements.map(({ x, y }) => ({ x, y })), [{ x: 0, y: 0 }, { x: 2, y: 0 }]);
  const placed = [];
  const deps = {
    layoutTemplates: store,
    wait: immediate,
    runtimeCall: async (method, args) => {
      if (method === 'session.sequence.begin') return { success: true, data: { sequenceId: 'lease' } };
      if (method === 'session.sequence.end') return { success: true, data: {} };
      if (method === 'state.grid') return { success: true, data: { key: args.x * 1000 + args.y, x: args.x, y: args.y, things: placed.filter((item) => item.x === args.x && item.y === args.y).map((item) => ({ id: item.id, type: 'Building' })) } };
      if (method === 'game.placeBuilding') { placed.push({ id: placed.length + 1, x: args.x, y: args.y }); return { success: true, data: { commandAccepted: true } }; }
      throw new Error(`Unexpected ${method}`);
    },
  };
  const built = await runHelper('helper.buildLayoutTemplate', { name: 'qi-room', x: 100, y: 200, rotation: 90 }, deps);
  assert.deepEqual(placed.map(({ x, y }) => ({ x, y })), [{ x: 100, y: 200 }, { x: 100, y: 202 }]);
  assert.equal(built.data.completion, 'verified');
});

test('refineAndTrack inspects new artifacts and reports numeric property deltas', async () => {
  let inventoryRead = 0;
  let commandRead = 0;
  const result = await runHelper('helper.refineAndTrack', { npcId: 1, itemId: 10, refiningType: 0, kind: 'Weapon' }, {
    wait: immediate,
    runtimeCall: async (method, args) => {
      if (method === 'session.sequence.begin') return { success: true, data: { sequenceId: 'lease' } };
      if (method === 'session.sequence.end' || method === 'runtime.speed' || method === 'runtime.pause') return { success: true, data: {} };
      if (method === 'game.refineArtifact') return { success: true, data: { command: { id: 77 } } };
      if (method === 'state.refiningForecast') return { success: true, data: { successRate: 0.8, quality: { minimum: 3, maximum: 5 } } };
      if (method === 'state.commands') return { success: true, data: { items: commandRead++ === 0 ? [{ id: 77, ownerThingId: 1 }] : [] } };
      if (method === 'state.npcInventory') {
        inventoryRead++;
        return { success: true, data: { inventory: { equipped: [], carried: inventoryRead === 1 ? [{ id: 10 }] : [{ id: 20 }] } } };
      }
      if (method === 'state.item' && args.id === 10) return { success: inventoryRead < 2, data: { item: { id: 10, quality: 2 }, artifact: { properties: [{ name: 'Power', value: 5 }] } } };
      if (method === 'state.item' && args.id === 20) return { success: true, data: { item: { id: 20, name: 'Sword', quality: 4 }, artifact: { kind: 'Sword', properties: [{ name: 'Power', value: 8 }, { name: 'Speed', value: 3 }] } } };
      throw new Error(`Unexpected ${method}`);
    },
  });
  assert.equal(result.data.outputItems[0].item.id, 20);
  assert.equal(result.data.forecast.successRate, 0.8);
  assert.equal(result.data.comparisons[0].properties[0].delta, 3);
  assert.equal(result.data.comparisons[0].improvedPropertyCount, 1);
});

test('commandFormationCombat applies round-robin targets and verifies every member', async () => {
  const targets = new Map();
  const result = await runHelper('helper.commandFormationCombat', { formationId: 9, targetIds: [50, 51], policy: 'round-robin' }, {
    wait: immediate,
    runtimeCall: async (method, args) => {
      if (method === 'session.sequence.begin') return { success: true, data: { sequenceId: 'lease' } };
      if (method === 'session.sequence.end') return { success: true, data: {} };
      if (method === 'state.formations') return { success: true, data: { items: [{ id: 9, npcIds: [1, 2, 3] }] } };
      if (method === 'game.attackTarget') { targets.set(args.attackerId, args.targetId); return { success: true, data: { commandAccepted: true } }; }
      if (method === 'state.combat') return { success: true, data: { id: args.id, fighting: true, targetId: targets.get(args.id) } };
      throw new Error(`Unexpected ${method}`);
    },
  });
  assert.deepEqual(result.data.orders.map((item) => item.targetId), [50, 51, 50]);
  assert.equal(result.data.completion, 'verified');
});
