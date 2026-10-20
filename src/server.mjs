import { createInterface } from 'node:readline';
import { spawn } from 'node:child_process';
import { access } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { captureWindow, runWindows } from './windows.mjs';
import { runtimeCall } from './runtime-client.mjs';

export const SERVER_INFO = { name: 'amazing-cultivation-mcp', version: '0.5.0' };

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
  'state.grid': 'Inspect an ACS map grid by key, x/y, or the current pointer: terrain, qi, five elements, temperature, fertility, beauty, light, and occupants.',
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
};

export const TOOLS = [
  {
    name: 'acsmcp_call',
    description: 'Observe and operate Amazing Cultivation Simulator through its real visible Windows UI. Start with session.status and ui.observe. Coordinates are relative to the game client area. Re-observe after every action that can change the screen.',
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
    'Call state.overview before deciding. Inspect state.alerts and low NPC needs first.',
    'Use state.enums before passing workKind, plan, or thing type names.',
    'Prefer game.* and ui.snapshot/ui.input. Use coordinate input only when no semantic adapter exists.',
    'For multi-step UI or build flows, acquire session.sequence.begin and pass sequenceId to every mutation, then release it.',
    'After every mutation, wait briefly and read the affected state/UI again. Never infer success only from commandAccepted.',
    'Save to a new explicit name before a risky multi-step operation.',
  ],
  recipes: {
    inspectNpc: [{ method: 'state.npcs', args: { limit: 30 } }, { method: 'state.npc', args: { id: '<npcId>' } }],
    inspectGrid: [{ method: 'state.grid', args: { x: '<x>', y: '<y>' } }],
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
  switch (method) {
    case 'session.status': {
      try {
        return { content: [textContent(await deps.runtimeCall(method, args))] };
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
    case 'state.grid':
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
