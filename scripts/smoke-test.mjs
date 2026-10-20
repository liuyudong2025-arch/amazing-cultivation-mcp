import { runtimeCall } from '../src/runtime-client.mjs';

const checks = [];

async function check(name, method, args = {}) {
  try {
    const result = await runtimeCall(method, args, 15_000);
    checks.push({ name, ok: true });
    return result;
  } catch (error) {
    checks.push({ name, ok: false, error: error.message });
    return null;
  }
}

const status = await check('bridge', 'session.status');
if (!status) {
  console.error(JSON.stringify({ ok: false, hint: '启动 Runtime，并在游戏 Mod 管理器启用 Amazing Cultivation MCP 后重启游戏。', checks }, null, 2));
  process.exitCode = 1;
} else {
  await check('uiTree', 'ui.snapshot', { limit: 3000 });
  await check('enums', 'state.enums');
  const overview = await check('worldOverview', 'state.overview', { npcLimit: 30, resourceLimit: 100, alertLimit: 50 });
  if (overview) await check('alerts', 'state.alerts', { limit: 50 });
  const failed = checks.filter((item) => !item.ok);
  console.log(JSON.stringify({
    ok: failed.length === 0,
    mode: overview ? 'world-loaded' : 'main-menu',
    hint: overview ? '游戏内桥接和自主玩法所需的核心读写通路可用。' : '桥接已连接；载入一个存档后再次运行可验证世界状态。',
    checks,
  }, null, 2));
  if (failed.some((item) => item.name !== 'worldOverview' && item.name !== 'alerts')) process.exitCode = 1;
}
