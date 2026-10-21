import { mkdir, readFile, rename, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';

const FORMAT_VERSION = 1;

function defaultFile() {
  const base = process.env.ACS_MCP_DATA_DIR
    || (process.platform === 'win32' && process.env.LOCALAPPDATA
      ? path.join(process.env.LOCALAPPDATA, 'AmazingCultivationMCP')
      : path.join(os.homedir(), '.amazing-cultivation-mcp'));
  return path.join(base, 'layout-templates.json');
}

async function readDocument(file) {
  try {
    const parsed = JSON.parse(await readFile(file, 'utf8'));
    if (parsed?.version !== FORMAT_VERSION || !Array.isArray(parsed.templates)) {
      throw new Error(`Unsupported layout-template format in ${file}`);
    }
    return parsed;
  } catch (error) {
    if (error?.code === 'ENOENT') return { version: FORMAT_VERSION, templates: [] };
    throw error;
  }
}

async function writeDocument(file, document) {
  await mkdir(path.dirname(file), { recursive: true });
  const temporary = `${file}.${process.pid}.tmp`;
  await writeFile(temporary, `${JSON.stringify(document, null, 2)}\n`, 'utf8');
  await rename(temporary, file);
}

export function createLayoutTemplateStore(file = defaultFile()) {
  return {
    file,
    async list() {
      return (await readDocument(file)).templates;
    },
    async save(template, { overwrite = false } = {}) {
      const document = await readDocument(file);
      const index = document.templates.findIndex((item) => item.name === template.name);
      if (index >= 0 && !overwrite) throw new Error(`Layout template already exists: ${template.name}`);
      if (index >= 0) document.templates[index] = template;
      else document.templates.push(template);
      document.templates.sort((a, b) => a.name.localeCompare(b.name));
      await writeDocument(file, document);
      return template;
    },
    async remove(name) {
      const document = await readDocument(file);
      const before = document.templates.length;
      document.templates = document.templates.filter((item) => item.name !== name);
      if (document.templates.length === before) return false;
      await writeDocument(file, document);
      return true;
    },
  };
}
