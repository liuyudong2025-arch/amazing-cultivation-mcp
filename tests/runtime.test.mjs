import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { readFile, rm } from 'node:fs/promises';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');

async function retry(action, timeoutMs = 5000) {
  const deadline = Date.now() + timeoutMs;
  let prior;
  while (Date.now() < deadline) {
    try { return await action(); } catch (error) { prior = error; await new Promise((resolve) => setTimeout(resolve, 50)); }
  }
  throw prior || new Error('Timed out');
}

test('runtime relays authenticated HTTP calls to the WebSocket Mod bridge', async () => {
  const base = 22000 + (process.pid % 1000) * 2;
  const testLocalDir = path.join(root, 'work', `test-local-${process.pid}`);
  const testWorkDir = path.join(root, 'work', `test-work-${process.pid}`);
  const runtime = spawn(process.execPath, [path.join(root, 'src', 'runtime.mjs')], {
    cwd: root,
    env: { ...process.env, ACSMCP_PORT: String(base), ACSMCP_BRIDGE_PORT: String(base + 1), ACSMCP_LOCAL_DIR: testLocalDir, ACSMCP_WORK_DIR: testWorkDir },
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  let game;
  let stderr = '';
  runtime.stderr.on('data', (chunk) => { stderr += chunk.toString(); });
  try {
    const bridgeConfig = await retry(async () => {
      const value = JSON.parse(await readFile(path.join(testLocalDir, 'bridge.json'), 'utf8'));
      if (value.runtimePid !== runtime.pid) throw new Error('Waiting for this runtime config');
      return value;
    });
    const runtimeConfig = await retry(async () => {
      const value = JSON.parse(await readFile(path.join(testWorkDir, 'runtime.json'), 'utf8'));
      if (value.pid !== runtime.pid) throw new Error('Waiting for this runtime config');
      return value;
    });
    game = new WebSocket(`${bridgeConfig.wsUrl}?token=${encodeURIComponent(bridgeConfig.bridgeToken)}`);
    await new Promise((resolve, reject) => { game.addEventListener('open', resolve, { once: true }); game.addEventListener('error', reject, { once: true }); });
    game.addEventListener('message', (event) => {
      const call = JSON.parse(event.data);
      game.send(JSON.stringify({ id: call.id, success: true, meta: { frame: 7 }, data: { echoedMethod: call.method } }));
    });
    const response = await fetch(`${runtimeConfig.url}/call`, {
      method: 'POST',
      headers: { authorization: `Bearer ${runtimeConfig.token}`, 'content-type': 'application/json' },
      body: JSON.stringify({ method: 'session.status', args: {} }),
    });
    const result = await response.json();
    assert.equal(response.status, 200);
    assert.equal(result.data.echoedMethod, 'session.status');
    assert.equal(result.meta.frame, 7);
    game.close();
  } finally {
    try { game?.close(); } catch {}
    if (runtime.exitCode === null) {
      const exited = once(runtime, 'exit');
      runtime.kill('SIGTERM');
      await Promise.race([exited, new Promise((resolve) => setTimeout(resolve, 2000))]);
      if (runtime.exitCode === null) runtime.kill('SIGKILL');
    }
    await rm(testLocalDir, { recursive: true, force: true });
    await rm(testWorkDir, { recursive: true, force: true });
    assert.equal(stderr, '');
  }
});
