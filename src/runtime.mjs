import crypto from 'node:crypto';
import fs from 'node:fs';
import http from 'node:http';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { acceptWebSocket } from './websocket-server.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const workDir = process.env.ACSMCP_WORK_DIR || path.join(root, 'work');
const localDir = process.env.ACSMCP_LOCAL_DIR || path.join(process.env.LOCALAPPDATA || os.homedir(), 'AmazingCultivationMCP');
const runtimePort = Number(process.env.ACSMCP_PORT || 18741);
const bridgePort = Number(process.env.ACSMCP_BRIDGE_PORT || 18742);
const runtimeToken = crypto.randomBytes(24).toString('hex');
const bridgeToken = crypto.randomBytes(24).toString('hex');
const debugEnabled = process.env.ACSMCP_DEBUG === '1';
const debug = (message) => { if (debugEnabled) process.stderr.write(`[acsmcp-runtime] ${message}\n`); };

fs.mkdirSync(workDir, { recursive: true });
fs.mkdirSync(localDir, { recursive: true });
fs.writeFileSync(path.join(localDir, 'bridge.json'), JSON.stringify({
  wsUrl: `ws://127.0.0.1:${bridgePort}/bridge`, bridgeToken, runtimePid: process.pid, writtenAtUtc: new Date().toISOString(),
}, null, 2));

let game = null;
let nextId = 1;
const pending = new Map();

function failPending(message) {
  for (const entry of pending.values()) { clearTimeout(entry.timer); entry.reject(new Error(message)); }
  pending.clear();
}

function callGame(method, args, timeoutMs = 30_000) {
  if (!game || game.closed) return Promise.reject(new Error('Game Mod bridge is not connected'));
  const id = String(nextId++);
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => { pending.delete(id); reject(new Error(`Game call timed out: ${method}`)); }, Math.max(100, Math.min(300_000, timeoutMs)));
    pending.set(id, { resolve, reject, timer });
    debug(`send id=${id} method=${method}`);
    game.send({ id, method, args: args || {} });
  });
}

const server = http.createServer(async (request, response) => {
  response.setHeader('content-type', 'application/json; charset=utf-8');
  const send = (status, value) => { response.writeHead(status); response.end(JSON.stringify(value)); };
  if (request.url === '/health' && request.method === 'GET') {
    send(200, { ready: true, gameConnected: Boolean(game && !game.closed), pid: process.pid }); return;
  }
  if (request.headers.authorization !== `Bearer ${runtimeToken}`) {
    send(401, { success: false, error: { code: 'UNAUTHORIZED', message: 'Missing or invalid runtime token' } }); return;
  }
  if (request.url !== '/call' || request.method !== 'POST') { send(404, { success: false }); return; }
  try {
    const chunks = []; let size = 0;
    for await (const chunk of request) { size += chunk.length; if (size > 4 * 1024 * 1024) throw new Error('Request exceeds 4 MiB'); chunks.push(chunk); }
    const call = JSON.parse(Buffer.concat(chunks).toString('utf8'));
    const result = await callGame(call.method, call.args || {}, call.timeoutMs);
    send(200, result);
  } catch (error) {
    send(502, { success: false, error: { code: 'TRANSPORT_ERROR', message: error.message, outcomeMayHaveChanged: true } });
  }
});

const bridgeServer = http.createServer((request, response) => {
  response.writeHead(404); response.end();
});

bridgeServer.on('upgrade', (request, socket, head) => {
  const peer = acceptWebSocket(request, socket, head, bridgeToken);
  if (!peer) return;
  debug(`bridge connected from ${socket.remoteAddress}`);
  if (game && !game.closed) game.close();
  game = peer;
  peer.onMessage = (text) => {
    debug(`receive ${text.length} characters`);
    let response;
    try { response = JSON.parse(text); }
    catch (error) {
      debug(`invalid JSON: ${error.message}; payload=${text.slice(0, 1500)}`);
      return;
    }
    const entry = pending.get(String(response.id));
    if (!entry) { debug(`unmatched response id=${response.id}`); return; }
    pending.delete(String(response.id)); clearTimeout(entry.timer); entry.resolve(response);
  };
  peer.onClose = () => { if (game === peer) game = null; failPending('Game Mod bridge disconnected'); };
});

bridgeServer.listen(bridgePort, '127.0.0.1');

server.listen(runtimePort, '127.0.0.1', () => {
  const runtimeConfig = { url: `http://127.0.0.1:${runtimePort}`, token: runtimeToken, pid: process.pid, bridgePort, startedAtUtc: new Date().toISOString() };
  fs.writeFileSync(path.join(workDir, 'runtime.json'), JSON.stringify(runtimeConfig, null, 2));
  process.stdout.write(`Amazing Cultivation MCP runtime listening on 127.0.0.1:${runtimePort}\n`);
});

process.on('SIGTERM', () => { failPending('Runtime stopped'); game?.close(); bridgeServer.close(); server.close(); });
