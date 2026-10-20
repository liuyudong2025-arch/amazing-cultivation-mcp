import assert from 'node:assert/strict';
import test from 'node:test';
import { createHandler, TOOLS } from '../src/server.mjs';

test('initialize and tools/list expose a valid MCP surface', async () => {
  const handle = createHandler();
  const init = await handle({ jsonrpc: '2.0', id: 1, method: 'initialize', params: { protocolVersion: '2025-06-18' } });
  assert.equal(init.result.serverInfo.name, 'amazing-cultivation-mcp');
  assert.equal(init.result.protocolVersion, '2025-06-18');
  const list = await handle({ jsonrpc: '2.0', id: 2, method: 'tools/list' });
  assert.deepEqual(list.result.tools, TOOLS);
});

test('play guide exposes an observe-act-verify loop', async () => {
  const handle = createHandler();
  const response = await handle({ jsonrpc: '2.0', id: 'guide', method: 'tools/call', params: { name: 'acsmcp_guide', arguments: {} } });
  const guide = JSON.parse(response.result.content[0].text);
  assert.ok(guide.loop.some((step) => step.includes('state.overview')));
  assert.ok(guide.loop.some((step) => step.includes('affected state')));
});

test('status delegates to the in-game runtime', async () => {
  const calls = [];
  const handle = createHandler({ runtimeCall: async (...args) => { calls.push(args); return { success: true, data: { bridge: 'connected' } }; } });
  const response = await handle({ jsonrpc: '2.0', id: 3, method: 'tools/call', params: { name: 'acsmcp_call', arguments: { method: 'session.status', args: {} } } });
  assert.equal(response.result.isError, undefined);
  assert.equal(JSON.parse(response.result.content[0].text).data.bridge, 'connected');
  assert.equal(calls[0][0], 'session.status');
});

test('invalid calls become MCP tool errors', async () => {
  const handle = createHandler();
  const response = await handle({ jsonrpc: '2.0', id: 4, method: 'tools/call', params: { name: 'acsmcp_call', arguments: { method: 'ui.click', args: {} } } });
  assert.equal(response.result.isError, true);
  assert.match(response.result.content[0].text, /x must be a finite number/);
});

test('runtime.wait is bounded and testable', async () => {
  let waited = -1;
  const handle = createHandler({ wait: async (ms) => { waited = ms; } });
  await handle({ jsonrpc: '2.0', id: 5, method: 'tools/call', params: { name: 'acsmcp_call', arguments: { method: 'runtime.wait', args: { milliseconds: 90_000 } } } });
  assert.equal(waited, 60_000);
});

test('structured state calls delegate to the in-game runtime', async () => {
  let called;
  const handle = createHandler({ runtimeCall: async (method, args) => { called = { method, args }; return { success: true, data: { size: 300 } }; } });
  const response = await handle({ jsonrpc: '2.0', id: 6, method: 'tools/call', params: { name: 'acsmcp_call', arguments: { method: 'state.map', args: {} } } });
  assert.equal(JSON.parse(response.result.content[0].text).data.size, 300);
  assert.equal(called.method, 'state.map');
});

test('ACS-specific grid and NPC focus methods delegate to the Mod bridge', async () => {
  const calls = [];
  const handle = createHandler({ runtimeCall: async (method, args) => { calls.push({ method, args }); return { success: true }; } });
  for (const [method, args] of [['state.grid', { x: 12, y: 34 }], ['game.focusNpc', { id: 42 }]]) {
    const response = await handle({ jsonrpc: '2.0', id: method, method: 'tools/call', params: { name: 'acsmcp_call', arguments: { method, args } } });
    assert.equal(response.result.isError, undefined);
  }
  assert.deepEqual(calls, [
    { method: 'state.grid', args: { x: 12, y: 34 } },
    { method: 'game.focusNpc', args: { id: 42 } },
  ]);
});

test('all in-game adapter methods are exposed and routed', async () => {
  const routed = [];
  const handle = createHandler({ runtimeCall: async (method) => { routed.push(method); return { success: true }; } });
  const methods = TOOLS[0].inputSchema.properties.method.enum.filter((method) =>
    method.startsWith('state.') || method.startsWith('game.') || method.startsWith('map.') || method === 'runtime.pause' || method === 'runtime.speed' || method === 'ui.snapshot' || method === 'ui.input');
  for (const method of methods) {
    const response = await handle({ jsonrpc: '2.0', id: method, method: 'tools/call', params: { name: 'acsmcp_call', arguments: { method, args: {} } } });
    assert.equal(response.result.isError, undefined, method);
  }
  assert.deepEqual(routed, methods);
});

test('runtime.advance uses a sequence, verifies frame progress, and pauses', async () => {
  let frame = 100;
  const calls = [];
  const handle = createHandler({
    wait: async () => { frame += 2; },
    runtimeCall: async (method, args) => {
      calls.push({ method, args });
      if (method === 'session.sequence.begin') return { success: true, data: { sequenceId: 'lease-1' } };
      if (method === 'state.world') return { success: true, data: { frameCount: frame, paused: method === 'runtime.pause' } };
      return { success: true, data: {} };
    },
  });
  const response = await handle({ jsonrpc: '2.0', id: 9, method: 'tools/call', params: { name: 'acsmcp_call', arguments: { method: 'runtime.advance', args: { frames: 3 } } } });
  const result = JSON.parse(response.result.content[0].text);
  assert.ok(result.data.advancedFrames >= 3);
  assert.ok(calls.some((call) => call.method === 'runtime.speed' && call.args.sequenceId === 'lease-1'));
  assert.ok(calls.some((call) => call.method === 'runtime.pause' && call.args.sequenceId === 'lease-1'));
  assert.equal(calls.at(-1).method, 'session.sequence.end');
});
