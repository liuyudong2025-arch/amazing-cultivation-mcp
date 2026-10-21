export const HELPER_HELP = {
  'helper.observe': 'Read a stable colony decision snapshot with normalized risks, idle NPCs, alerts, input readiness, and coverage.',
  'helper.readAllNpcs': 'Read every active player NPC across pages and report session/world/frame consistency.',
  'helper.queryAllThings': 'Read every matching map object across pages with a caller-selected safety limit.',
  'helper.readAllResources': 'Read the complete sect warehouse inventory across pages.',
  'helper.inspectNpc': 'Read a complete NPC dossier: work/needs, attributes, cultivation, skills, health, equipment, and carried items.',
  'helper.cultivationRoster': 'Read cultivation summaries for every active inner disciple, or every active player NPC when requested.',
  'helper.ensureWork': 'Ensure one NPC work kind has the requested priority/enabled state, changing only mismatches and verifying the result.',
  'helper.ensureSchedule': 'Ensure one or more hours of the sect schedule have the requested plans and verify all changes.',
  'helper.ensureCultivation': 'Ensure one NPC cultivation mode and/or current cultivation behaviour, changing only mismatches and verifying both.',
  'helper.placeBuilding': 'Enter the native build mode, place one building/blueprint at a grid, and verify a new map object appears.',
  'helper.buildRectangle': 'Place and verify a bounded wall/floor/building rectangle one grid at a time; stop and report partial progress on a rejected cell.',
  'helper.buildLayout': 'Place and verify up to 128 heterogeneous building cells with per-cell definition, material, and direction.',
  'helper.buildArea': 'Drag-place a native area build such as a roof and verify the resulting covered room grids.',
  'helper.listLayoutTemplates': 'List persistent named building layouts with dimensions and relative placements.',
  'helper.saveLayoutTemplate': 'Save 1-128 building placements as a named origin-relative reusable layout.',
  'helper.deleteLayoutTemplate': 'Delete a named persistent building layout; requires confirm=true.',
  'helper.buildLayoutTemplate': 'Place and verify a saved layout at a new origin, optionally rotated by 90-degree steps.',
  'helper.dismantleAndVerify': 'Queue native building dismantling and optionally run time until the building is removed.',
  'helper.ensureProduction': 'Ensure a production recipe/task exists on a building and verify the production queue.',
  'helper.engageCombat': 'Order one NPC to attack a target and verify its native FightBody target/fighting state.',
  'helper.emergencyDisengage': 'Pause immediately, stop selected NPC fights, cancel formations containing them, and verify combat/formation state.',
  'helper.commandFormationCombat': 'Order every member of an active formation to attack using focus-fire or round-robin target policy and verify combat targets.',
  'helper.retreatTo': 'Stop combat, cancel affected formations, move NPCs to a compact destination grid pattern, and optionally verify arrival.',
  'helper.ensureThingCommand': 'Idempotently ensure a native command exists on a map object and poll until visible.',
  'helper.ensureNoThingCommand': 'Idempotently remove a native command from a map object and poll until absent.',
  'helper.selectNpc': 'Select an NPC through native map input, optionally focus its information panel, and verify selection.',
  'helper.runAndWatch': 'Run game time while polling multiple structured conditions, always pausing and releasing the sequence on exit.',
  'helper.planSpiritGathering': 'Inspect native qi, five-element, occupants, feng-shui, and relevant build entries around a seat, then return evidence-based placement candidates without building anything.',
  'helper.planOminousRoom': 'Inspect the native room/aura and nearby building feng-shui state and return a read-only plan for deliberately reaching a very inauspicious room.',
  'helper.prepareBreakthrough': 'Combine native breakthrough factors, cultivation, inventory, grid, qi, and feng-shui into a readiness report; optionally begin when a minimum rate is met.',
  'helper.refineAndTrack': 'Queue native artifact refinement, track completion, inspect output artifacts, and compare every numeric artifact property with the input.',
  'helper.moveAnimalsToBreedingArea': 'Queue native MoveNpc jobs to relocate animals into a breeding area and optionally verify native area membership.',
  'helper.startingPlan': 'Inspect the current colony and return prioritized, evidence-linked opening recommendations without mutating the game.',
};

function bridgeData(response, method) {
  if (!response?.success) throw new Error(`${method} failed: ${response?.error?.message || 'unknown bridge error'}`);
  return response.data;
}

function requestId(prefix) {
  return `helper-${prefix}-${Date.now()}-${Math.random().toString(16).slice(2)}`;
}

function clampInteger(value, fallback, min, max) {
  const number = value === undefined ? fallback : Math.trunc(Number(value));
  if (!Number.isFinite(number)) throw new Error('Expected a finite number');
  return Math.max(min, Math.min(max, number));
}

function valueAtPath(value, dottedPath) {
  if (!dottedPath) return value;
  return String(dottedPath).split('.').reduce((current, part) => current == null ? undefined : current[part], value);
}

function conditionMatches(actual, operator, expected) {
  switch (operator || 'eq') {
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

async function withSequence(args, deps, action) {
  let sequenceId = args.sequenceId;
  let owned = false;
  if (!sequenceId) {
    const leaseMs = clampInteger(args.leaseMs, 30_000, 5_000, 60_000);
    sequenceId = bridgeData(await deps.runtimeCall('session.sequence.begin', { leaseMs }), 'session.sequence.begin').sequenceId;
    owned = true;
  }
  try {
    return await action(sequenceId);
  } finally {
    if (owned && sequenceId) {
      try { await deps.runtimeCall('session.sequence.end', { sequenceId }); } catch {}
    }
  }
}

async function readAll(method, args, deps, { pageSize, maxItems }) {
  let offset = 0;
  let firstMeta;
  let lastMeta;
  let summary = {};
  const items = [];
  const pages = [];
  while (items.length < maxItems) {
    const limit = Math.min(pageSize, maxItems - items.length);
    const response = await deps.runtimeCall(method, { ...args, offset, limit });
    const page = bridgeData(response, method);
    firstMeta ||= response.meta || null;
    lastMeta = response.meta || lastMeta || null;
    const pageItems = Array.isArray(page.items) ? page.items : [];
    items.push(...pageItems);
    pages.push({ offset, count: pageItems.length, frame: response.meta?.frame ?? null });
    summary = Object.fromEntries(Object.entries(page).filter(([key]) => !['items', 'offset', 'limit', 'nextOffset'].includes(key)));
    if (page.nextOffset === null || page.nextOffset === undefined || pageItems.length === 0) break;
    offset = page.nextOffset;
  }
  const truncated = items.length >= maxItems && Number(summary.total ?? items.length) > items.length;
  return {
    items,
    ...summary,
    count: items.length,
    truncated,
    pages,
    consistency: {
      sameSession: !firstMeta || !lastMeta || firstMeta.sessionId === lastMeta.sessionId,
      sameWorld: !firstMeta || !lastMeta || firstMeta.worldEpoch === lastMeta.worldEpoch,
      firstFrame: firstMeta?.frame ?? null,
      lastFrame: lastMeta?.frame ?? null,
      mixedFrames: Boolean(firstMeta && lastMeta && firstMeta.frame !== lastMeta.frame),
    },
  };
}

async function poll(deps, observe, predicate, timeoutMs = 2_000, pollMs = 50) {
  const deadline = Date.now() + timeoutMs;
  let value;
  do {
    value = await observe();
    if (predicate(value)) return value;
    if (Date.now() >= deadline) return value;
    await deps.wait(pollMs);
  } while (true);
}

function normalizeNeed(need) {
  const span = Number(need.max) - Number(need.min);
  if (!Number.isFinite(span) || span <= 0) return null;
  return (Number(need.realValue ?? need.value) - Number(need.min)) / span;
}

async function inspectColony(args, deps) {
  const overview = bridgeData(await deps.runtimeCall('state.overview', {
    npcLimit: clampInteger(args.npcLimit, 100, 1, 100),
    resourceLimit: clampInteger(args.resourceLimit, 100, 1, 1000),
    alertLimit: clampInteger(args.alertLimit, 100, 1, 500),
  }), 'state.overview');
  if (overview.npcs?.nextOffset != null) {
    overview.npcs = await readAll('state.npcs', {}, deps, { pageSize: 100, maxItems: clampInteger(args.maxNpcs, 5_000, 1, 50_000) });
  }
  if (overview.resources?.nextOffset != null) {
    overview.resources = await readAll('state.resources', {}, deps, { pageSize: 500, maxItems: clampInteger(args.maxResources, 10_000, 1, 100_000) });
  }
  const input = bridgeData(await deps.runtimeCall('state.input', {}), 'state.input');
  const commands = bridgeData(await deps.runtimeCall('state.commands', { limit: 500 }), 'state.commands');
  const needsThreshold = Math.max(0, Math.min(1, Number(args.needsThreshold ?? 0.2)));
  const survivalNeeds = new Set(['Food', 'Water', 'Rest', 'Fun', 'MindState']);
  const criticalNeeds = [];
  const wounded = [];
  const idleNpcs = [];
  for (const npc of overview.npcs?.items || []) {
    if (npc.maxHp > 0 && npc.hp / npc.maxHp < 0.8) wounded.push({ id: npc.id, name: npc.name, hp: npc.hp, maxHp: npc.maxHp });
    if (npc.currentJob?.name === 'JobFree') idleNpcs.push({ id: npc.id, name: npc.name, key: npc.key });
    for (const need of npc.needs || []) {
      if (!survivalNeeds.has(need.type)) continue;
      const normalized = normalizeNeed(need);
      if (normalized !== null && normalized <= needsThreshold) criticalNeeds.push({ npcId: npc.id, npcName: npc.name, type: need.type, value: need.realValue ?? need.value, normalized });
    }
  }
  return {
    ...overview,
    input,
    commands,
    risks: { alerts: overview.alerts?.items || [], criticalNeeds, wounded },
    opportunities: { idleNpcs },
    coverage: {
      npcs: { returned: overview.npcs?.items?.length || 0, total: overview.npcs?.total ?? null, complete: overview.npcs?.nextOffset == null },
      resources: { returned: overview.resources?.items?.length || 0, total: overview.resources?.total ?? null, complete: overview.resources?.nextOffset == null },
      commandsTruncated: Boolean(commands.truncated),
    },
  };
}

async function inspectNpc(args, deps) {
  const id = Number(args.id);
  if (!Number.isInteger(id)) throw new Error('id is required');
  const methods = ['state.npc', 'state.npcProfile', 'state.npcCultivation', 'state.npcSkills', 'state.npcHealth', 'state.npcInventory'];
  const responses = await Promise.all(methods.map((method) => deps.runtimeCall(method, { id })));
  const values = Object.fromEntries(methods.map((method, index) => [method, bridgeData(responses[index], method)]));
  const base = values['state.npc'];
  const cultivation = values['state.npcCultivation'].cultivation;
  const health = values['state.npcHealth'].health;
  const warnings = [];
  if (base.maxHp > 0 && base.hp / base.maxHp < 0.8) warnings.push({ kind: 'low-hp', hp: base.hp, maxHp: base.maxHp });
  if (health?.dying) warnings.push({ kind: 'dying' });
  if (health?.injuryCount) warnings.push({ kind: 'injuries', count: health.injuryCount });
  if (cultivation?.touchingNeck) warnings.push({ kind: 'bottleneck', neck: cultivation.currentNeck });
  return {
    id,
    name: base.name,
    base,
    profile: values['state.npcProfile'].profile,
    cultivation,
    skills: values['state.npcSkills'].skills,
    health,
    inventory: values['state.npcInventory'].inventory,
    warnings,
  };
}

async function cultivationRoster(args, deps) {
  const npcs = await readAll('state.npcs', {}, deps, { pageSize: 100, maxItems: clampInteger(args.maxItems, 200, 1, 500) });
  const selected = (npcs.items || []).filter((npc) => args.includeAll || npc.disciple);
  const items = [];
  for (const npc of selected) {
    const response = await deps.runtimeCall('state.npcCultivation', { id: npc.id });
    items.push({ id: npc.id, name: npc.name, rank: npc.rank, ...bridgeData(response, 'state.npcCultivation').cultivation });
  }
  return { items, count: items.length, sourceNpcCount: npcs.count, includeAll: Boolean(args.includeAll), consistency: npcs.consistency };
}

async function ensureWork(args, deps) {
  const id = Number(args.id);
  const workKind = String(args.workKind || '').trim();
  if (!Number.isInteger(id) || !workKind) throw new Error('id and workKind are required');
  if (args.priority === undefined && args.enabled === undefined && !args.vip) throw new Error('Pass priority, enabled, or vip');
  if (args.priority !== undefined && (!Number.isInteger(args.priority) || args.priority < 0 || args.priority > 9)) throw new Error('priority must be an integer from 0 to 9');
  return withSequence(args, deps, async (sequenceId) => {
    const before = bridgeData(await deps.runtimeCall('state.npc', { id }), 'state.npc');
    const prior = before.workSettings?.find((item) => item.workKind === workKind);
    if (!prior) throw new Error(`NPC ${id} has no work setting ${workKind}`);
    const actions = [];
    if (args.priority !== undefined && prior.priority !== args.priority) {
      await deps.runtimeCall('game.setWorkPriority', { id, workKind, priority: args.priority, sequenceId, requestId: requestId('work-priority') });
      actions.push('priority');
    }
    if (args.enabled !== undefined && (!prior.banned) !== Boolean(args.enabled)) {
      await deps.runtimeCall('game.setWorkEnabled', { id, workKind, enabled: Boolean(args.enabled), sequenceId, requestId: requestId('work-enabled') });
      actions.push('enabled');
    }
    if (args.vip) {
      const vip = bridgeData(await deps.runtimeCall('game.setVipWork', { id, workKind, sequenceId, requestId: requestId('vip-work') }), 'game.setVipWork');
      if (!vip.commandAccepted) throw new Error(`The game rejected VIP work ${workKind} for NPC ${id}`);
      actions.push('vip');
    }
    const after = bridgeData(await deps.runtimeCall('state.npc', { id }), 'state.npc');
    const current = after.workSettings?.find((item) => item.workKind === workKind);
    const priorityOk = args.priority === undefined || current?.priority === args.priority;
    const enabledOk = args.enabled === undefined || (!current?.banned) === Boolean(args.enabled);
    if (!priorityOk || !enabledOk) throw new Error(`Work verification failed for NPC ${id}/${workKind}`);
    return { npcId: id, workKind, changed: actions.length > 0, actions, before: prior, after: current, completion: 'verified' };
  });
}

async function ensureSchedule(args, deps) {
  const entries = Array.isArray(args.entries) ? args.entries : [{ hour: args.hour, plan: args.plan }];
  if (!entries.length) throw new Error('At least one schedule entry is required');
  const normalized = entries.map((entry) => {
    const hour = Number(entry.hour);
    const plan = String(entry.plan || '').trim();
    if (!Number.isInteger(hour) || hour < 0 || hour > 23 || !plan) throw new Error('Each schedule entry needs hour 0-23 and plan');
    return { hour, plan };
  });
  return withSequence(args, deps, async (sequenceId) => {
    const before = bridgeData(await deps.runtimeCall('state.school', {}), 'state.school');
    const prior = new Map((before.schedule || []).map((item) => [item.hour, item.plan]));
    const changed = [];
    for (const entry of normalized) {
      if (prior.get(entry.hour) === entry.plan) continue;
      await deps.runtimeCall('game.setSchedule', { ...entry, sequenceId, requestId: requestId('schedule') });
      changed.push(entry);
    }
    const after = bridgeData(await deps.runtimeCall('state.school', {}), 'state.school');
    const actual = new Map((after.schedule || []).map((item) => [item.hour, item.plan]));
    const failed = normalized.filter((entry) => actual.get(entry.hour) !== entry.plan);
    if (failed.length) throw new Error(`Schedule verification failed for hours: ${failed.map((item) => item.hour).join(', ')}`);
    return { changed: changed.length > 0, changes: changed, schedule: after.schedule, completion: 'verified' };
  });
}

async function ensureCultivation(args, deps) {
  const id = Number(args.id);
  const mode = args.mode === undefined ? undefined : String(args.mode).trim();
  const behaviour = args.behaviour === undefined ? undefined : String(args.behaviour).trim();
  if (!Number.isInteger(id) || (!mode && !behaviour)) throw new Error('id and mode and/or behaviour are required');
  return withSequence(args, deps, async (sequenceId) => {
    const before = bridgeData(await deps.runtimeCall('state.npcCultivation', { id }), 'state.npcCultivation').cultivation;
    if (!before?.available) throw new Error(`NPC ${id} has no cultivation data`);
    const actions = [];
    if (mode && before.practiceMode !== mode) {
      await deps.runtimeCall('game.setPracticeMode', { id, mode, sequenceId, requestId: requestId('practice-mode') });
      actions.push('mode');
    }
    if (behaviour && before.behaviour !== behaviour) {
      await deps.runtimeCall('game.setPracticeBehaviour', { id, behaviour, sequenceId, requestId: requestId('practice-behaviour') });
      actions.push('behaviour');
    }
    const after = bridgeData(await deps.runtimeCall('state.npcCultivation', { id }), 'state.npcCultivation').cultivation;
    if ((mode && after.practiceMode !== mode) || (behaviour && after.behaviour !== behaviour)) throw new Error(`Cultivation verification failed for NPC ${id}`);
    return { npcId: id, changed: actions.length > 0, actions, before: { practiceMode: before.practiceMode, behaviour: before.behaviour }, after: { practiceMode: after.practiceMode, behaviour: after.behaviour }, completion: 'verified' };
  });
}

async function placeBuilding(args, deps) {
  const name = String(args.name || '').trim();
  if (!name) throw new Error('name is required; use state.buildCatalog first');
  const selector = args.key !== undefined ? { key: args.key } : { x: args.x, y: args.y };
  if (selector.key === undefined && (!Number.isInteger(selector.x) || !Number.isInteger(selector.y))) throw new Error('Pass key or integer x/y');
  return withSequence(args, deps, async (sequenceId) => {
    const before = bridgeData(await deps.runtimeCall('state.grid', selector), 'state.grid');
    const priorIds = new Set((before.things || []).map((thing) => thing.id));
    const mutation = bridgeData(await deps.runtimeCall('game.placeBuilding', { ...selector, name, stuffDefName: args.stuffDefName, direction: args.direction, sequenceId, requestId: args.requestId || requestId('place-building') }), 'game.placeBuilding');
    const after = await poll(deps, async () => bridgeData(await deps.runtimeCall('state.grid', selector), 'state.grid'), (grid) =>
      (grid.things || []).some((thing) => !priorIds.has(thing.id) && (!args.expectedDefName || thing.defName === args.expectedDefName)),
      clampInteger(args.timeoutMs, 3_000, 100, 30_000), 50);
    const created = (after.things || []).filter((thing) => !priorIds.has(thing.id));
    if (!created.some((thing) => !args.expectedDefName || thing.defName === args.expectedDefName)) throw new Error(`Building placement ${name} was dispatched but no new matching map object appeared`);
    return { name, grid: { key: after.key, x: after.x, y: after.y }, created, mutation, completion: 'verified' };
  });
}

async function buildRectangle(args, deps) {
  const x = Number(args.x);
  const y = Number(args.y);
  const width = Number(args.width);
  const height = Number(args.height);
  const name = String(args.name || '').trim();
  if (![x, y, width, height].every(Number.isInteger) || !name || width < 1 || height < 1 || width * height > 64) {
    throw new Error('name, integer x/y and positive width/height with at most 64 cells are required');
  }
  const cells = [];
  for (let dy = 0; dy < height; dy++) for (let dx = 0; dx < width; dx++) cells.push({ x: x + dx, y: y + dy });
  return withSequence({ ...args, leaseMs: 60_000 }, deps, async (sequenceId) => {
    const completed = [];
    const skipped = [];
    let renewedAt = Date.now();
    for (const cell of cells) {
      if (Date.now() - renewedAt > 10_000) {
        await deps.runtimeCall('session.sequence.begin', { sequenceId, leaseMs: 60_000 });
        renewedAt = Date.now();
      }
      if (args.skipOccupied) {
        const before = bridgeData(await deps.runtimeCall('state.grid', cell), 'state.grid');
        if ((before.things || []).some((thing) => thing.type === 'Building')) { skipped.push({ ...cell, reason: 'building-present' }); continue; }
      }
      try {
        const result = await placeBuilding({ ...args, ...cell, sequenceId, requestId: requestId('rectangle-cell') }, deps);
        completed.push({ ...cell, created: result.created });
      } catch (error) {
        return { name, requested: cells.length, completed, skipped, failed: { ...cell, error: error.message }, completion: 'partial' };
      }
    }
    return { name, requested: cells.length, completed, skipped, failed: null, completion: 'verified' };
  });
}

async function buildLayout(args, deps) {
  if (!Array.isArray(args.placements) || args.placements.length < 1 || args.placements.length > 128) throw new Error('placements must contain 1-128 entries');
  const placements = args.placements.map((entry, index) => {
    const name = String(entry?.name || args.name || '').trim();
    const x = Number(entry?.x);
    const y = Number(entry?.y);
    if (!name || !Number.isInteger(x) || !Number.isInteger(y)) throw new Error(`placement ${index} needs name and integer x/y`);
    return { ...entry, name, x, y };
  });
  return withSequence({ ...args, leaseMs: 60_000 }, deps, async (sequenceId) => {
    const completed = [];
    const skipped = [];
    let renewedAt = Date.now();
    for (let index = 0; index < placements.length; index++) {
      const placement = placements[index];
      if (Date.now() - renewedAt > 10_000) {
        await deps.runtimeCall('session.sequence.begin', { sequenceId, leaseMs: 60_000 });
        renewedAt = Date.now();
      }
      if (args.skipOccupied || placement.skipOccupied) {
        const before = bridgeData(await deps.runtimeCall('state.grid', { x: placement.x, y: placement.y }), 'state.grid');
        if ((before.things || []).some((thing) => thing.type === 'Building')) { skipped.push({ index, x: placement.x, y: placement.y, reason: 'building-present' }); continue; }
      }
      try {
        const result = await placeBuilding({ ...placement, sequenceId, requestId: requestId('layout-cell'), timeoutMs: placement.timeoutMs ?? args.timeoutMs }, deps);
        completed.push({ index, name: placement.name, x: placement.x, y: placement.y, created: result.created });
      } catch (error) {
        return { requested: placements.length, completed, skipped, failed: { index, name: placement.name, x: placement.x, y: placement.y, error: error.message }, completion: 'partial' };
      }
    }
    return { requested: placements.length, completed, skipped, failed: null, completion: 'verified' };
  });
}

async function buildArea(args, deps) {
  const name = String(args.name || '').trim();
  const from = args.from || { x: args.x, y: args.y };
  const to = args.to || { x: args.toX, y: args.toY };
  if (!name || !Number.isInteger(from?.x) || !Number.isInteger(from?.y) || !Number.isInteger(to?.x) || !Number.isInteger(to?.y)) {
    throw new Error('name and integer from/to coordinates are required');
  }
  const minX = Math.min(from.x, to.x);
  const maxX = Math.max(from.x, to.x);
  const minY = Math.min(from.y, to.y);
  const maxY = Math.max(from.y, to.y);
  const keys = [];
  for (let y = minY; y <= maxY; y++) for (let x = minX; x <= maxX; x++) keys.push(y * 128 + x);
  if (keys.length > 256) throw new Error('buildArea supports at most 256 grids');
  return withSequence(args, deps, async (sequenceId) => {
    const center = { x: Math.floor((minX + maxX) / 2), y: Math.floor((minY + maxY) / 2) };
    const radius = Math.max(maxX - minX, maxY - minY) + 2;
    const before = bridgeData(await deps.runtimeCall('state.fengShui', { ...center, radius, limit: 1000 }), 'state.fengShui');
    const beforeRooms = new Set((before.rooms || []).map((entry) => entry.area?.id).filter((id) => id !== undefined));
    const build = bridgeData(await deps.runtimeCall('game.beginBuild', { name, stuffDefName: args.stuffDefName, direction: args.direction, sequenceId, requestId: args.requestId || requestId('build-area-mode') }), 'game.beginBuild');
    const drag = bridgeData(await deps.runtimeCall('map.drag', { from, to, button: 0, sequenceId, requestId: requestId('build-area-drag') }), 'map.drag');
    const after = await poll(deps, async () => bridgeData(await deps.runtimeCall('state.fengShui', { ...center, radius, limit: 1000 }), 'state.fengShui'), (state) =>
      (state.rooms || []).some((entry) => keys.every((key) => (entry.area?.grids || []).includes(key))),
      clampInteger(args.timeoutMs, 5_000, 100, 30_000), 50);
    const room = (after.rooms || []).find((entry) => keys.every((key) => (entry.area?.grids || []).includes(key))) || null;
    if (!room) throw new Error(`Area build ${name} was dispatched but no covering room was observed`);
    return { name, from, to, gridCount: keys.length, build, drag, room, roomCreated: !beforeRooms.has(room.area?.id), completion: 'verified' };
  });
}

function requireTemplateStore(deps) {
  if (!deps.layoutTemplates) throw new Error('Layout template storage is unavailable');
  return deps.layoutTemplates;
}

function templateName(value) {
  const name = String(value || '').trim();
  if (!name || name.length > 80 || /[\u0000-\u001f]/.test(name)) throw new Error('template name must contain 1-80 printable characters');
  return name;
}

function normalizeTemplatePlacements(args) {
  if (!Array.isArray(args.placements) || args.placements.length < 1 || args.placements.length > 128) throw new Error('placements must contain 1-128 entries');
  const absolute = args.placements.map((entry, index) => {
    const name = String(entry?.name || '').trim();
    const x = Number(entry?.x);
    const y = Number(entry?.y);
    if (!name || !Number.isInteger(x) || !Number.isInteger(y)) throw new Error(`placement ${index} needs name and integer x/y`);
    return { name, x, y, ...(entry.stuffDefName === undefined ? {} : { stuffDefName: entry.stuffDefName }), ...(entry.direction === undefined ? {} : { direction: entry.direction }), ...(entry.expectedDefName === undefined ? {} : { expectedDefName: entry.expectedDefName }) };
  });
  const originX = args.originX === undefined ? Math.min(...absolute.map((item) => item.x)) : Number(args.originX);
  const originY = args.originY === undefined ? Math.min(...absolute.map((item) => item.y)) : Number(args.originY);
  if (!Number.isInteger(originX) || !Number.isInteger(originY)) throw new Error('originX and originY must be integers');
  return absolute.map((item) => ({ ...item, x: item.x - originX, y: item.y - originY }));
}

function templateSummary(template) {
  const xs = template.placements.map((item) => item.x);
  const ys = template.placements.map((item) => item.y);
  return { ...template, bounds: { minX: Math.min(...xs), maxX: Math.max(...xs), minY: Math.min(...ys), maxY: Math.max(...ys), width: Math.max(...xs) - Math.min(...xs) + 1, height: Math.max(...ys) - Math.min(...ys) + 1 } };
}

async function listLayoutTemplates(args, deps) {
  const query = String(args.query || '').trim().toLocaleLowerCase();
  const templates = (await requireTemplateStore(deps).list()).filter((item) => !query || item.name.toLocaleLowerCase().includes(query));
  return { items: templates.map(templateSummary), count: templates.length, storage: requireTemplateStore(deps).file || null };
}

async function saveLayoutTemplate(args, deps) {
  const name = templateName(args.name);
  const template = {
    version: 1,
    name,
    description: String(args.description || '').trim().slice(0, 500),
    placements: normalizeTemplatePlacements(args),
    updatedAt: new Date().toISOString(),
  };
  await requireTemplateStore(deps).save(template, { overwrite: args.overwrite === true });
  return { template: templateSummary(template), completion: 'saved' };
}

async function deleteLayoutTemplate(args, deps) {
  const name = templateName(args.name);
  if (args.confirm !== true) throw new Error('confirm=true is required to delete a layout template');
  const removed = await requireTemplateStore(deps).remove(name);
  return { name, removed, completion: removed ? 'deleted' : 'not-found' };
}

function rotatePlacement(placement, rotation) {
  if (rotation === 90) return { ...placement, x: -placement.y, y: placement.x };
  if (rotation === 180) return { ...placement, x: -placement.x, y: -placement.y };
  if (rotation === 270) return { ...placement, x: placement.y, y: -placement.x };
  return { ...placement };
}

async function buildLayoutTemplate(args, deps) {
  const name = templateName(args.name);
  const originX = Number(args.x);
  const originY = Number(args.y);
  const rotation = Number(args.rotation ?? 0);
  if (!Number.isInteger(originX) || !Number.isInteger(originY)) throw new Error('integer x and y origin are required');
  if (![0, 90, 180, 270].includes(rotation)) throw new Error('rotation must be 0, 90, 180, or 270');
  const template = (await requireTemplateStore(deps).list()).find((item) => item.name === name);
  if (!template) throw new Error(`Layout template not found: ${name}`);
  const placements = template.placements.map((item) => {
    const rotated = rotatePlacement(item, rotation);
    return { ...rotated, x: originX + rotated.x, y: originY + rotated.y };
  });
  const result = await buildLayout({ ...args, placements }, deps);
  return { template: name, origin: { x: originX, y: originY }, rotation, ...result };
}

async function dismantleAndVerify(args, deps) {
  const id = Number(args.id);
  if (!Number.isInteger(id)) throw new Error('building id is required');
  return withSequence({ ...args, leaseMs: 60_000 }, deps, async (sequenceId) => {
    const before = bridgeData(await deps.runtimeCall('state.building', { id }), 'state.building');
    const mutation = bridgeData(await deps.runtimeCall('game.dismantleBuilding', { id, confirm: true, sequenceId, requestId: args.requestId || requestId('dismantle') }), 'game.dismantleBuilding');
    if (args.waitForRemoval === false) return { id, before, mutation, completion: 'queued' };
    const timeoutMs = clampInteger(args.timeoutMs, 60_000, 100, 300_000);
    const started = Date.now();
    let renewedAt = started;
    if (args.runSpeed !== 0) await deps.runtimeCall('runtime.speed', { value: Number(args.runSpeed ?? 3), sequenceId });
    try {
      while (Date.now() - started <= timeoutMs) {
        const response = await deps.runtimeCall('state.building', { id });
        if (!response?.success && ['THING_NOT_FOUND', 'BUILDING_NOT_FOUND'].includes(response?.error?.code)) return { id, before, mutation, removed: true, elapsedMs: Date.now() - started, completion: 'verified' };
        if (Date.now() - renewedAt > 10_000) { await deps.runtimeCall('session.sequence.begin', { sequenceId, leaseMs: 60_000 }); renewedAt = Date.now(); }
        await deps.wait(100);
      }
    } finally {
      if (args.runSpeed !== 0) await deps.runtimeCall('runtime.pause', { sequenceId });
    }
    return { id, before, mutation, removed: false, elapsedMs: Date.now() - started, completion: 'timeout' };
  });
}

async function prepareBreakthrough(args, deps) {
  const id = Number(args.id);
  if (!Number.isInteger(id)) throw new Error('NPC id is required');
  const breakthrough = bridgeData(await deps.runtimeCall('state.breakthrough', { id, failureCount: args.failureCount }), 'state.breakthrough');
  const [inventory, grid, qi, fengShui] = await Promise.all([
    deps.runtimeCall('state.npcInventory', { id }).then((value) => bridgeData(value, 'state.npcInventory')),
    deps.runtimeCall('state.grid', { key: breakthrough.key }).then((value) => bridgeData(value, 'state.grid')),
    deps.runtimeCall('state.qiField', { key: breakthrough.key, radius: clampInteger(args.radius, 3, 0, 8) }).then((value) => bridgeData(value, 'state.qiField')),
    deps.runtimeCall('state.fengShui', { key: breakthrough.key, radius: clampInteger(args.radius, 3, 0, 8) }).then((value) => bridgeData(value, 'state.fengShui')),
  ]);
  const rate = Number(breakthrough.successRate);
  const minimumRate = Number(args.minimumRate ?? 0);
  const ready = breakthrough.conditionMet === true && Number.isFinite(rate) && rate >= minimumRate;
  const factors = Object.entries(breakthrough.factors || {}).map(([name, value]) => ({ name, value: Number(value) })).filter((entry) => Number.isFinite(entry.value)).sort((a, b) => a.value - b.value);
  const report = { npcId: id, ready, minimumRate, breakthrough, weakestFactors: factors.slice(0, 5), inventory: inventory.inventory, grid, qi: qi.ling, fengShui, mutationPerformed: false };
  if (!args.begin) return { ...report, completion: 'readiness-report' };
  if (!args.confirm) throw new Error('confirm=true is required when begin=true');
  if (!ready) throw new Error(`Breakthrough preflight failed: conditionMet=${breakthrough.conditionMet}, successRate=${breakthrough.successRate}, minimumRate=${minimumRate}`);
  const mutation = bridgeData(await deps.runtimeCall('game.beginBreakthrough', { id, confirm: true, sequenceId: args.sequenceId, requestId: args.requestId || requestId('breakthrough') }), 'game.beginBreakthrough');
  return { ...report, mutation, mutationPerformed: true, completion: 'dispatched' };
}

async function refineAndTrack(args, deps) {
  const npcId = Number(args.npcId);
  const itemId = Number(args.itemId);
  if (!Number.isInteger(npcId) || !Number.isInteger(itemId) || !Number.isInteger(Number(args.refiningType))) throw new Error('npcId, itemId, and integer refiningType are required');
  return withSequence({ ...args, leaseMs: 60_000 }, deps, async (sequenceId) => {
    const beforeItem = bridgeData(await deps.runtimeCall('state.item', { id: itemId }), 'state.item');
    const forecast = bridgeData(await deps.runtimeCall('state.refiningForecast', { npcId, itemId, buildingId: args.buildingId }), 'state.refiningForecast');
    const beforeInventory = bridgeData(await deps.runtimeCall('state.npcInventory', { id: npcId }), 'state.npcInventory');
    const mutation = bridgeData(await deps.runtimeCall('game.refineArtifact', { ...args, npcId, itemId, refiningType: Number(args.refiningType), confirm: true, sequenceId, requestId: args.requestId || requestId('refine') }), 'game.refineArtifact');
    if (args.waitForCompletion === false) return { npcId, itemId, beforeItem, forecast, beforeInventory: beforeInventory.inventory, mutation, completion: 'queued' };
    const commandId = mutation.command?.id;
    const timeoutMs = clampInteger(args.timeoutMs, 120_000, 100, 300_000);
    const started = Date.now();
    let renewedAt = started;
    let seen = false;
    if (args.runSpeed !== 0) await deps.runtimeCall('runtime.speed', { value: Number(args.runSpeed ?? 3), sequenceId });
    try {
      while (Date.now() - started <= timeoutMs) {
        const commands = bridgeData(await deps.runtimeCall('state.commands', { type: 'RefiningFabao', limit: 2000 }), 'state.commands');
        const current = (commands.items || []).find((command) => command.id === commandId || command.ownerThingId === npcId);
        if (current) seen = true;
        if (seen && !current) break;
        if (Date.now() - renewedAt > 10_000) { await deps.runtimeCall('session.sequence.begin', { sequenceId, leaseMs: 60_000 }); renewedAt = Date.now(); }
        await deps.wait(100);
      }
    } finally {
      if (args.runSpeed !== 0) await deps.runtimeCall('runtime.pause', { sequenceId });
    }
    const afterInventory = bridgeData(await deps.runtimeCall('state.npcInventory', { id: npcId }), 'state.npcInventory');
    const beforeIds = new Set([...(beforeInventory.inventory?.equipped || []), ...(beforeInventory.inventory?.carried || [])].map((item) => item.id));
    const afterItems = [...(afterInventory.inventory?.equipped || []), ...(afterInventory.inventory?.carried || [])];
    const createdOrReceived = afterItems.filter((item) => !beforeIds.has(item.id));
    const outputItems = [];
    for (const item of createdOrReceived) {
      const state = await deps.runtimeCall('state.item', { id: item.id });
      if (state?.success) outputItems.push(state.data);
    }
    const inputProperties = new Map((beforeItem.artifact?.properties || []).map((property) => [property.name, Number(property.value)]));
    const comparisons = outputItems.map((output) => {
      const properties = (output.artifact?.properties || []).map((property) => {
        const before = inputProperties.get(property.name);
        const after = Number(property.value);
        return { name: property.name, before: Number.isFinite(before) ? before : null, after: Number.isFinite(after) ? after : property.value, delta: Number.isFinite(before) && Number.isFinite(after) ? after - before : null };
      });
      return { id: output.item?.id, name: output.item?.name, quality: output.item?.quality, artifactKind: output.artifact?.kind || null, properties, improvedPropertyCount: properties.filter((item) => item.delta > 0).length, reducedPropertyCount: properties.filter((item) => item.delta < 0).length };
    });
    const inputState = await deps.runtimeCall('state.item', { id: itemId });
    return { npcId, itemId, beforeItem, forecast, beforeInventory: beforeInventory.inventory, afterInventory: afterInventory.inventory, inputAfter: inputState?.success ? inputState.data : null, inputConsumed: !inputState?.success, createdOrReceived, outputItems, comparisons, mutation, commandObserved: seen, elapsedMs: Date.now() - started, completion: seen ? 'tracked' : 'timeout-before-command-observed' };
  });
}

async function moveAnimalsToBreedingArea(args, deps) {
  const areaId = Number(args.areaId);
  const animalIds = [...new Set((args.animalIds || args.npcIds || []).map(Number).filter(Number.isInteger))];
  if (!Number.isInteger(areaId) || !animalIds.length) throw new Error('areaId and at least one animal id are required');
  return withSequence({ ...args, leaseMs: 60_000 }, deps, async (sequenceId) => {
    const areas = bridgeData(await deps.runtimeCall('state.areas', { id: areaId }), 'state.areas');
    const area = areas.items?.[0];
    if (!area?.breeding || !Array.isArray(area.grids) || !area.grids.length) throw new Error(`Area ${areaId} is not a non-empty breeding area`);
    const queued = [];
    for (let index = 0; index < animalIds.length; index++) {
      const id = animalIds[index];
      const npc = bridgeData(await deps.runtimeCall('state.npc', { id }), 'state.npc');
      if (!npc.animal) throw new Error(`NPC ${id} is not an animal`);
      if (npc.animal.breedingAreaId === areaId) { queued.push({ id, alreadyInside: true }); continue; }
      const targetKey = area.grids[index % area.grids.length];
      const mutation = bridgeData(await deps.runtimeCall('game.moveNpcTo', { id, key: targetKey, confirm: true, sequenceId, requestId: requestId('move-animal') }), 'game.moveNpcTo');
      queued.push({ id, targetKey, mutation, alreadyInside: false });
    }
    if (args.waitForArrival === false) return { areaId, queued, completion: 'queued' };
    const timeoutMs = clampInteger(args.timeoutMs, 120_000, 100, 300_000);
    const started = Date.now();
    let renewedAt = started;
    if (args.runSpeed !== 0) await deps.runtimeCall('runtime.speed', { value: Number(args.runSpeed ?? 3), sequenceId });
    try {
      while (Date.now() - started <= timeoutMs) {
        const arrived = [];
        for (const id of animalIds) {
          const npc = bridgeData(await deps.runtimeCall('state.npc', { id }), 'state.npc');
          if (npc.animal?.breedingAreaId === areaId) arrived.push(id);
        }
        if (arrived.length === animalIds.length) return { areaId, queued, arrived, elapsedMs: Date.now() - started, completion: 'verified' };
        if (Date.now() - renewedAt > 10_000) { await deps.runtimeCall('session.sequence.begin', { sequenceId, leaseMs: 60_000 }); renewedAt = Date.now(); }
        await deps.wait(100);
      }
    } finally {
      if (args.runSpeed !== 0) await deps.runtimeCall('runtime.pause', { sequenceId });
    }
    const final = [];
    for (const id of animalIds) final.push(bridgeData(await deps.runtimeCall('state.npc', { id }), 'state.npc'));
    return { areaId, queued, arrived: final.filter((npc) => npc.animal?.breedingAreaId === areaId).map((npc) => npc.id), elapsedMs: Date.now() - started, completion: 'timeout' };
  });
}

async function ensureProduction(args, deps) {
  const id = Number(args.id);
  const name = String(args.name || '').trim();
  if (!Number.isInteger(id) || !name) throw new Error('building id and recipe/task name are required');
  return withSequence(args, deps, async (sequenceId) => {
    const before = bridgeData(await deps.runtimeCall('state.building', { id }), 'state.building');
    const match = (before.productionTasks || []).find((task) => task.name === name || task.productDefName === name);
    if (match) return { buildingId: id, changed: false, task: match, completion: 'already-satisfied' };
    const mutation = bridgeData(await deps.runtimeCall('game.addProductionTask', { id, name, loop: args.loop, sequenceId, requestId: args.requestId || requestId('production') }), 'game.addProductionTask');
    const after = await poll(deps, async () => bridgeData(await deps.runtimeCall('state.building', { id }), 'state.building'), (building) =>
      (building.productionTasks || []).some((task) => task.name === name || task.productDefName === name),
      clampInteger(args.timeoutMs, 3_000, 100, 30_000), 50);
    const task = (after.productionTasks || []).find((item) => item.name === name || item.productDefName === name);
    if (!task) throw new Error(`Production task ${name} was accepted but not found in the queue`);
    return { buildingId: id, changed: true, task, mutation, completion: 'verified' };
  });
}

async function engageCombat(args, deps) {
  const attackerId = Number(args.attackerId);
  const targetId = Number(args.targetId);
  if (!Number.isInteger(attackerId) || !Number.isInteger(targetId)) throw new Error('attackerId and targetId are required');
  return withSequence(args, deps, async (sequenceId) => {
    const before = bridgeData(await deps.runtimeCall('state.combat', { id: attackerId }), 'state.combat');
    if (before.fighting && before.targetId === targetId) return { attackerId, targetId, changed: false, combat: before, completion: 'already-satisfied' };
    const mutation = bridgeData(await deps.runtimeCall('game.attackTarget', { attackerId, targetId, cantStop: Boolean(args.cantStop), sequenceId, requestId: args.requestId || requestId('attack') }), 'game.attackTarget');
    const after = await poll(deps, async () => bridgeData(await deps.runtimeCall('state.combat', { id: attackerId }), 'state.combat'), (combat) => combat.fighting && combat.targetId === targetId, clampInteger(args.timeoutMs, 2_000, 100, 30_000), 50);
    if (!after.fighting || after.targetId !== targetId) throw new Error(`Combat target verification failed for ${attackerId} -> ${targetId}`);
    return { attackerId, targetId, changed: true, mutation, combat: after, completion: 'verified' };
  });
}

async function emergencyDisengage(args, deps) {
  const npcIds = [...new Set((args.npcIds || []).map(Number).filter(Number.isInteger))];
  if (!npcIds.length) throw new Error('npcIds must contain at least one NPC id');
  return withSequence(args, deps, async (sequenceId) => {
    await deps.runtimeCall('runtime.pause', { sequenceId });
    const formations = bridgeData(await deps.runtimeCall('state.formations', {}), 'state.formations');
    const affected = (formations.items || []).filter((formation) => (formation.npcIds || []).some((id) => npcIds.includes(id)));
    const stopped = [];
    for (const id of npcIds) {
      const combat = bridgeData(await deps.runtimeCall('state.combat', { id }), 'state.combat');
      if (!combat.fighting) continue;
      const result = await deps.runtimeCall('game.stopCombat', { id, force: Boolean(args.force), sequenceId, requestId: requestId('disengage') });
      stopped.push({ id, result: bridgeData(result, 'game.stopCombat') });
    }
    const cancelled = [];
    for (const formation of affected) {
      const result = await deps.runtimeCall('game.cancelFormation', { id: formation.id, confirm: true, sequenceId, requestId: requestId('cancel-formation') });
      cancelled.push({ id: formation.id, result: bridgeData(result, 'game.cancelFormation') });
    }
    const afterCombat = [];
    for (const id of npcIds) afterCombat.push(bridgeData(await deps.runtimeCall('state.combat', { id }), 'state.combat'));
    const afterFormations = bridgeData(await deps.runtimeCall('state.formations', {}), 'state.formations');
    const stillFighting = afterCombat.filter((combat) => combat.fighting).map((combat) => combat.id);
    const stillFormed = (afterFormations.items || []).filter((formation) => (formation.npcIds || []).some((id) => npcIds.includes(id))).map((formation) => formation.id);
    return { npcIds, stopped, cancelled, stillFighting, stillFormed, completion: stillFighting.length || stillFormed.length ? 'partial' : 'verified' };
  });
}

async function commandFormationCombat(args, deps) {
  const formationId = Number(args.formationId);
  const targetIds = [...new Set((args.targetIds || []).map(Number).filter(Number.isInteger))];
  const policy = args.policy || 'focus-fire';
  if (!Number.isInteger(formationId) || !targetIds.length) throw new Error('formationId and targetIds are required');
  if (!['focus-fire', 'round-robin'].includes(policy)) throw new Error('policy must be focus-fire or round-robin');
  return withSequence(args, deps, async (sequenceId) => {
    const formations = bridgeData(await deps.runtimeCall('state.formations', {}), 'state.formations');
    const formation = (formations.items || []).find((item) => item.id === formationId);
    if (!formation || !(formation.npcIds || []).length) throw new Error(`Active formation ${formationId} was not found or has no members`);
    const orders = [];
    for (let index = 0; index < formation.npcIds.length; index++) {
      const attackerId = formation.npcIds[index];
      const targetId = targetIds[policy === 'focus-fire' ? 0 : index % targetIds.length];
      const mutation = bridgeData(await deps.runtimeCall('game.attackTarget', { attackerId, targetId, sequenceId, requestId: requestId('formation-attack') }), 'game.attackTarget');
      orders.push({ attackerId, targetId, mutation });
    }
    const combat = [];
    for (const order of orders) combat.push(bridgeData(await deps.runtimeCall('state.combat', { id: order.attackerId }), 'state.combat'));
    const failed = orders.filter((order, index) => !combat[index].fighting || combat[index].targetId !== order.targetId);
    return { formationId, policy, orders, combat, failed, completion: failed.length ? 'partial' : 'verified' };
  });
}

async function retreatTo(args, deps) {
  const npcIds = [...new Set((args.npcIds || []).map(Number).filter(Number.isInteger))];
  const x = Number(args.x);
  const y = Number(args.y);
  if (!npcIds.length || !Number.isInteger(x) || !Number.isInteger(y)) throw new Error('npcIds and integer x/y are required');
  return withSequence({ ...args, leaseMs: 60_000 }, deps, async (sequenceId) => {
    const disengage = await emergencyDisengage({ npcIds, force: Boolean(args.force), sequenceId }, deps);
    const destinations = npcIds.map((id, index) => ({ id, x: x + (index % 3) - 1, y: y + Math.floor(index / 3) }));
    const orders = [];
    for (const destination of destinations) {
      const mutation = bridgeData(await deps.runtimeCall('game.moveNpcTo', { ...destination, confirm: true, sequenceId, requestId: requestId('retreat') }), 'game.moveNpcTo');
      orders.push({ ...destination, mutation });
    }
    if (args.waitForArrival === false) return { disengage, orders, completion: 'queued' };
    const timeoutMs = clampInteger(args.timeoutMs, 120_000, 100, 300_000);
    const started = Date.now();
    let renewedAt = started;
    if (args.runSpeed !== 0) await deps.runtimeCall('runtime.speed', { value: Number(args.runSpeed ?? 3), sequenceId });
    try {
      while (Date.now() - started <= timeoutMs) {
        const arrived = [];
        for (const destination of destinations) {
          const npc = bridgeData(await deps.runtimeCall('state.npc', { id: destination.id }), 'state.npc');
          if (Math.round(npc.position?.x) === destination.x && Math.round(npc.position?.z) === destination.y) arrived.push(destination.id);
        }
        if (arrived.length === destinations.length) return { disengage, orders, arrived, elapsedMs: Date.now() - started, completion: 'verified' };
        if (Date.now() - renewedAt > 10_000) { await deps.runtimeCall('session.sequence.begin', { sequenceId, leaseMs: 60_000 }); renewedAt = Date.now(); }
        await deps.wait(100);
      }
    } finally {
      if (args.runSpeed !== 0) await deps.runtimeCall('runtime.pause', { sequenceId });
    }
    return { disengage, orders, arrived: [], elapsedMs: Date.now() - started, completion: 'timeout' };
  });
}

async function commandState(command, id, deps) {
  const state = bridgeData(await deps.runtimeCall('state.commands', { type: command, limit: 2000 }), 'state.commands');
  return { state, match: state.items?.find((item) => item.ownerThingId === id && item.valid && !item.finished) || null };
}

async function ensureThingCommand(args, deps, present) {
  const command = String(args.command || '').trim();
  const id = Number(args.id);
  if (!command || !Number.isInteger(id)) throw new Error('command and integer id are required');
  return withSequence(args, deps, async (sequenceId) => {
    const before = await commandState(command, id, deps);
    if (Boolean(before.match) === present) return { command, thingId: id, changed: false, commandState: before.match, completion: 'already-satisfied' };
    const method = present ? 'game.addThingCommand' : 'game.removeThingCommand';
    const mutation = bridgeData(await deps.runtimeCall(method, { command, id, params: args.params || [], sequenceId, requestId: args.requestId || requestId(present ? 'add-command' : 'remove-command') }), method);
    const timeoutMs = clampInteger(args.timeoutMs, 3_000, 100, 30_000);
    const after = await poll(deps, () => commandState(command, id, deps), (value) => Boolean(value.match) === present, timeoutMs, 50);
    if (Boolean(after.match) !== present) throw new Error(`${method} was accepted but verification timed out for ${command}/${id}`);
    return { command, thingId: id, changed: true, mutation, commandState: after.match, completion: 'verified' };
  });
}

async function selectNpc(args, deps) {
  const id = Number(args.id);
  if (!Number.isInteger(id)) throw new Error('id is required');
  return withSequence(args, deps, async (sequenceId) => {
    const npc = bridgeData(await deps.runtimeCall('state.npc', { id }), 'state.npc');
    let selected = bridgeData(await deps.runtimeCall('state.selection', {}), 'state.selection');
    let changed = false;
    if (!selected.items?.some((item) => item.id === id)) {
      await deps.runtimeCall('map.click', { key: npc.key, button: 0, sequenceId, requestId: args.requestId || requestId('select-npc') });
      changed = true;
      selected = await poll(deps, async () => bridgeData(await deps.runtimeCall('state.selection', {}), 'state.selection'), (value) => value.items?.some((item) => item.id === id), clampInteger(args.timeoutMs, 2_000, 100, 30_000), 50);
    }
    if (!selected.items?.some((item) => item.id === id)) throw new Error(`NPC ${id} was not selected`);
    if (args.focus) await deps.runtimeCall('game.focusNpc', { id, sequenceId, requestId: requestId('focus-npc') });
    return { npcId: id, name: npc.name, changed, focused: Boolean(args.focus), selection: selected.items, completion: 'verified' };
  });
}

async function runAndWatch(args, deps) {
  const conditions = Array.isArray(args.conditions) ? args.conditions : [];
  if (!conditions.length) throw new Error('conditions must contain at least one structured stop condition');
  const mode = args.mode || 'any';
  if (!['any', 'all'].includes(mode)) throw new Error('mode must be any or all');
  const timeoutMs = clampInteger(args.timeoutMs, 30_000, 500, 300_000);
  const pollMs = clampInteger(args.pollMs, 250, 20, 5_000);
  const speed = Math.max(0.1, Math.min(10, Number(args.speed ?? 1)));
  return withSequence({ ...args, leaseMs: Math.min(60_000, timeoutMs + 5_000) }, deps, async (sequenceId) => {
    const started = Date.now();
    let renewAt = started + 10_000;
    const observations = [];
    let stopReason = 'timeout';
    await deps.runtimeCall('runtime.speed', { value: speed, sequenceId, requestId: requestId('watch-play') });
    try {
      while (Date.now() - started < timeoutMs) {
        if (Date.now() >= renewAt) {
          await deps.runtimeCall('session.sequence.begin', { sequenceId, leaseMs: Math.min(60_000, timeoutMs + 5_000) });
          renewAt = Date.now() + 10_000;
        }
        const results = [];
        for (const condition of conditions) {
          const method = String(condition.method || 'state.overview');
          if (!method.startsWith('state.')) throw new Error('runAndWatch condition methods must be state.*');
          const observed = bridgeData(await deps.runtimeCall(method, condition.args || {}), method);
          const actual = valueAtPath(observed, condition.path || '');
          results.push({ method, path: condition.path || '', operator: condition.operator || 'eq', expected: condition.value, actual, matched: conditionMatches(actual, condition.operator, condition.value) });
        }
        observations.push({ elapsedMs: Date.now() - started, results });
        const matched = mode === 'all' ? results.every((item) => item.matched) : results.some((item) => item.matched);
        if (matched) { stopReason = 'condition-met'; break; }
        await deps.wait(pollMs);
      }
    } finally {
      await deps.runtimeCall('runtime.pause', { sequenceId, requestId: requestId('watch-pause') });
    }
    const world = bridgeData(await deps.runtimeCall('state.world', {}), 'state.world');
    return { stopReason, elapsedMs: Date.now() - started, observations, finalWorld: world, completion: world.paused ? 'paused' : 'pause-unverified' };
  });
}

async function catalogSearch(queries, deps) {
  const seen = new Set();
  const items = [];
  for (const query of queries) {
    const result = bridgeData(await deps.runtimeCall('state.buildCatalog', { query }), 'state.buildCatalog');
    for (const item of result.items || []) {
      const key = item.key || `${item.name}/${item.objectName}`;
      if (seen.has(key)) continue;
      seen.add(key);
      items.push(item);
    }
  }
  return items;
}

async function planSpiritGathering(args, deps) {
  const x = Number(args.x);
  const y = Number(args.y);
  if (!Number.isInteger(x) || !Number.isInteger(y)) throw new Error('integer x and y for the cultivation seat are required');
  const radius = clampInteger(args.radius, 6, 1, 12);
  const qi = bridgeData(await deps.runtimeCall('state.qiField', { x, y, radius, top: 30 }), 'state.qiField');
  const fengShui = bridgeData(await deps.runtimeCall('state.fengShui', { x, y, radius, limit: 1000 }), 'state.fengShui');
  const catalog = await catalogSearch(['聚灵', '灵气', '蒲团'], deps);
  const center = (qi.points || []).find((point) => point.x === x && point.y === y) || null;
  const openPositions = (qi.points || [])
    .filter((point) => point.distance > 0 && point.distance <= radius && (!point.things || point.things.length === 0))
    .sort((a, b) => Number(b.ling) - Number(a.ling) || Number(a.distance) - Number(b.distance))
    .slice(0, 40)
    .map((point) => ({ key: point.key, x: point.x, y: point.y, distance: point.distance, ling: point.ling, elements: point.elements, elementProportions: point.elementProportions }));
  return {
    mutationPerformed: false,
    seat: center,
    requestedElement: args.element ?? null,
    qiSummary: qi.ling,
    elementTotals: qi.elementTotals,
    openPlacementCandidates: openPositions,
    nearbyFengShui: { ranks: fengShui.ranks, buildings: fengShui.buildings, rooms: fengShui.rooms },
    buildCandidates: catalog,
    guidance: [
      'Treat the selected x/y as the cultivation seat and keep it walkable.',
      'Choose gathering objects from buildCandidates by their native description/material; do not infer gathering range from the object name alone.',
      'Place candidates on openPlacementCandidates within the item’s stated effective range, then re-read state.qiField at the seat.',
      'Match the seat, cultivation law, material five-element values, temperature, and room feng-shui using the returned native values.',
    ],
    completion: 'read-only-plan',
  };
}

async function planOminousRoom(args, deps) {
  const x = Number(args.x);
  const y = Number(args.y);
  if (!Number.isInteger(x) || !Number.isInteger(y)) throw new Error('integer x and y inside the intended room are required');
  const radius = clampInteger(args.radius, 8, 1, 20);
  const qi = bridgeData(await deps.runtimeCall('state.qiField', { x, y, radius, top: 20 }), 'state.qiField');
  const fengShui = bridgeData(await deps.runtimeCall('state.fengShui', { x, y, radius, limit: 2000 }), 'state.fengShui');
  const centerKey = qi.center?.key;
  const containingRooms = (fengShui.rooms || []).filter((room) => room.area?.grids?.includes(centerKey));
  const targetRoom = containingRooms.find((entry) => entry.room) || null;
  const targetRank = String(args.desiredRank || '大凶');
  const buildings = [...(fengShui.buildings || [])].sort((a, b) => {
    const av = Number(a.summary?.fengShuiValue);
    const bv = Number(b.summary?.fengShuiValue);
    return (Number.isFinite(av) ? av : Infinity) - (Number.isFinite(bv) ? bv : Infinity);
  });
  return {
    mutationPerformed: false,
    target: { x, y, key: centerKey, desiredRank: targetRank },
    containingRooms,
    currentRoom: targetRoom?.room || null,
    targetReached: targetRoom?.room?.fengShui === targetRank,
    nativeRankCounts: fengShui.ranks,
    lowestNearbyBuildings: buildings.slice(0, 30),
    qiSummary: qi.ling,
    guidance: [
      'First make the target grid belong to a closed room; AreaRoom data in containingRooms is the authoritative enclosure check.',
      'Use nearby building fengShui/fengShuiValue read-back to identify which room fixtures are pushing the native result downward.',
      'After every wall, door, material, or fixture change, re-read state.fengShui; stop only when the game reports the requested rank.',
      'A deliberately very inauspicious room can harm occupants; keep it separate from normal bedrooms, workshops, and cultivation rooms.',
    ],
    unresolved: targetRoom ? [] : ['No AreaRoom currently contains the target grid; build/close the room before its final rank can be verified.'],
    completion: 'read-only-plan',
  };
}

function startingRecommendations(inspection) {
  const recommendations = [];
  if (inspection.risks.alerts.length) recommendations.push({ priority: 100, kind: 'alerts', reason: `${inspection.risks.alerts.length} active alerts`, evidence: inspection.risks.alerts.slice(0, 5) });
  if (inspection.risks.criticalNeeds.length) recommendations.push({ priority: 90, kind: 'needs', reason: `${inspection.risks.criticalNeeds.length} critical survival needs`, evidence: inspection.risks.criticalNeeds });
  if (inspection.risks.wounded.length) recommendations.push({ priority: 80, kind: 'health', reason: `${inspection.risks.wounded.length} wounded NPCs`, evidence: inspection.risks.wounded });
  const food = Number(inspection.resources?.food ?? 0);
  if (food < 50) recommendations.push({ priority: 70, kind: 'food', reason: `Usable food is low (${food})`, suggestedWorkKinds: ['Plant', 'Cooking', 'Hunting'] });
  if (inspection.opportunities.idleNpcs.length) recommendations.push({ priority: 40, kind: 'idle', reason: `${inspection.opportunities.idleNpcs.length} idle NPCs`, evidence: inspection.opportunities.idleNpcs });
  if (!recommendations.length) recommendations.push({ priority: 10, kind: 'develop', reason: 'No immediate survival risk detected; inspect construction, cultivation, and production goals.' });
  return recommendations.sort((a, b) => b.priority - a.priority);
}

export async function runHelper(method, args = {}, deps) {
  switch (method) {
    case 'helper.observe':
      return { success: true, data: await inspectColony(args, deps) };
    case 'helper.readAllNpcs':
      return { success: true, data: await readAll('state.npcs', {}, deps, { pageSize: clampInteger(args.pageSize, 100, 1, 500), maxItems: clampInteger(args.maxItems, 5_000, 1, 50_000) }) };
    case 'helper.queryAllThings': {
      const { pageSize, maxItems, ...query } = args;
      return { success: true, data: await readAll('state.things', query, deps, { pageSize: clampInteger(pageSize, 500, 1, 1000), maxItems: clampInteger(maxItems, 20_000, 1, 100_000) }) };
    }
    case 'helper.readAllResources': {
      const { pageSize, maxItems, ...query } = args;
      return { success: true, data: await readAll('state.resources', query, deps, { pageSize: clampInteger(pageSize, 500, 1, 1000), maxItems: clampInteger(maxItems, 10_000, 1, 100_000) }) };
    }
    case 'helper.inspectNpc':
      return { success: true, data: await inspectNpc(args, deps) };
    case 'helper.cultivationRoster':
      return { success: true, data: await cultivationRoster(args, deps) };
    case 'helper.ensureWork':
      return { success: true, data: await ensureWork(args, deps) };
    case 'helper.ensureSchedule':
      return { success: true, data: await ensureSchedule(args, deps) };
    case 'helper.ensureCultivation':
      return { success: true, data: await ensureCultivation(args, deps) };
    case 'helper.placeBuilding':
      return { success: true, data: await placeBuilding(args, deps) };
    case 'helper.buildRectangle':
      return { success: true, data: await buildRectangle(args, deps) };
    case 'helper.buildLayout':
      return { success: true, data: await buildLayout(args, deps) };
    case 'helper.buildArea':
      return { success: true, data: await buildArea(args, deps) };
    case 'helper.listLayoutTemplates':
      return { success: true, data: await listLayoutTemplates(args, deps) };
    case 'helper.saveLayoutTemplate':
      return { success: true, data: await saveLayoutTemplate(args, deps) };
    case 'helper.deleteLayoutTemplate':
      return { success: true, data: await deleteLayoutTemplate(args, deps) };
    case 'helper.buildLayoutTemplate':
      return { success: true, data: await buildLayoutTemplate(args, deps) };
    case 'helper.dismantleAndVerify':
      return { success: true, data: await dismantleAndVerify(args, deps) };
    case 'helper.ensureProduction':
      return { success: true, data: await ensureProduction(args, deps) };
    case 'helper.engageCombat':
      return { success: true, data: await engageCombat(args, deps) };
    case 'helper.emergencyDisengage':
      return { success: true, data: await emergencyDisengage(args, deps) };
    case 'helper.commandFormationCombat':
      return { success: true, data: await commandFormationCombat(args, deps) };
    case 'helper.retreatTo':
      return { success: true, data: await retreatTo(args, deps) };
    case 'helper.ensureThingCommand':
      return { success: true, data: await ensureThingCommand(args, deps, true) };
    case 'helper.ensureNoThingCommand':
      return { success: true, data: await ensureThingCommand(args, deps, false) };
    case 'helper.selectNpc':
      return { success: true, data: await selectNpc(args, deps) };
    case 'helper.runAndWatch':
      return { success: true, data: await runAndWatch(args, deps) };
    case 'helper.planSpiritGathering':
      return { success: true, data: await planSpiritGathering(args, deps) };
    case 'helper.planOminousRoom':
      return { success: true, data: await planOminousRoom(args, deps) };
    case 'helper.prepareBreakthrough':
      return { success: true, data: await prepareBreakthrough(args, deps) };
    case 'helper.refineAndTrack':
      return { success: true, data: await refineAndTrack(args, deps) };
    case 'helper.moveAnimalsToBreedingArea':
      return { success: true, data: await moveAnimalsToBreedingArea(args, deps) };
    case 'helper.startingPlan': {
      const inspection = await inspectColony(args, deps);
      return { success: true, data: { inspection, recommendations: startingRecommendations(inspection), mutationPerformed: false } };
    }
    default:
      throw new Error(`Unknown helper: ${method}`);
  }
}
