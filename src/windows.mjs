import { execFile } from 'node:child_process';
import { mkdtemp, readFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { promisify } from 'node:util';

const execFileAsync = promisify(execFile);
const here = path.dirname(fileURLToPath(import.meta.url));
const scriptPath = path.resolve(here, '..', 'scripts', 'win32.ps1');

function psArgs(action, args = {}) {
  const values = ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', scriptPath, '-Action', action];
  for (const [key, value] of Object.entries(args)) {
    if (value === undefined || value === null) continue;
    values.push(`-${key}`, typeof value === 'boolean' ? (value ? '1' : '0') : String(value));
  }
  return values;
}

export async function runWindows(action, args = {}, options = {}) {
  try {
    const { stdout, stderr } = await execFileAsync(
      'powershell.exe',
      psArgs(action, args),
      { windowsHide: true, timeout: options.timeoutMs ?? 15_000, maxBuffer: 4 * 1024 * 1024 },
    );
    const text = stdout.trim();
    if (!text) throw new Error(stderr.trim() || `Windows helper returned no result for ${action}`);
    const result = JSON.parse(text);
    if (!result.ok) throw Object.assign(new Error(result.error || `Windows action failed: ${action}`), { details: result });
    return result;
  } catch (error) {
    const returned = typeof error.stdout === 'string' ? error.stdout.trim() : '';
    if (returned) {
      try {
        const result = JSON.parse(returned);
        throw Object.assign(new Error(result.error || `Windows action failed: ${action}`), { details: result });
      } catch (parseError) {
        if (parseError.details) throw parseError;
      }
    }
    if (error.killed) throw new Error(`Windows action timed out: ${action}`);
    throw error;
  }
}

export async function captureWindow(args = {}) {
  const dir = await mkdtemp(path.join(tmpdir(), 'acsmcp-'));
  const outputPath = path.join(dir, 'capture.png');
  try {
    const result = await runWindows('capture', { ...args, OutputPath: outputPath }, { timeoutMs: 20_000 });
    const data = await readFile(outputPath);
    return { result, base64: data.toString('base64') };
  } finally {
    await rm(dir, { recursive: true, force: true });
  }
}
