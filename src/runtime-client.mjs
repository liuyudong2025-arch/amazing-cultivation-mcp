import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');

export async function runtimeCall(method, args = {}, timeoutMs = 30_000) {
  const configPath = process.env.ACSMCP_CONFIG || path.join(root, 'work', 'runtime.json');
  const config = JSON.parse(await readFile(configPath, 'utf8'));
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), timeoutMs + 2_000);
  try {
    const response = await fetch(`${config.url}/call`, {
      method: 'POST',
      headers: { authorization: `Bearer ${config.token}`, 'content-type': 'application/json' },
      body: JSON.stringify({ method, args, timeoutMs }),
      signal: controller.signal,
    });
    const result = await response.json();
    if (!response.ok || result.success === false) {
      const error = new Error(result.error?.message || `Runtime HTTP ${response.status}`);
      error.details = result;
      throw error;
    }
    return result;
  } finally {
    clearTimeout(timer);
  }
}
