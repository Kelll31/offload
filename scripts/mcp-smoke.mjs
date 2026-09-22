#!/usr/bin/env node
// Дымовой тест MCP-сервера Offload без зависимостей: JSON-RPC 2.0 поверх stdio,
// как это делают Claude Code, Cursor и другие IDE.
//
// Использование:
//   node scripts/mcp-smoke.mjs <путь к Offload.exe> [имя_инструмента '<json аргументов>'] ...
// Примеры:
//   node scripts/mcp-smoke.mjs .\publish\Offload.exe
//   node scripts/mcp-smoke.mjs .\publish\Offload.exe local_status '{}'
//   node scripts/mcp-smoke.mjs .\publish\Offload.exe local_ask_files '{"paths":["src"],"question":"Кратко опиши архитектуру"}'
import { spawn } from 'node:child_process';
import { createInterface } from 'node:readline';

const [exe, ...rest] = process.argv.slice(2);
if (!exe) {
  console.error('Укажите путь к Offload.exe');
  process.exit(2);
}
const calls = [];
for (let i = 0; i < rest.length; i += 2) calls.push([rest[i], JSON.parse(rest[i + 1] ?? '{}')]);

const child = spawn(exe, ['--mcp'], { stdio: ['pipe', 'pipe', 'pipe'], cwd: process.cwd() });
child.stderr.on('data', d => process.stderr.write(`[stderr] ${d}`));
child.on('exit', c => console.error(`[сервер завершился с кодом ${c}]`));

let nextId = 1;
const pending = new Map();
const rl = createInterface({ input: child.stdout });
rl.on('line', line => {
  if (!line.trim()) return;
  let msg;
  try { msg = JSON.parse(line); } catch { console.error('[не JSON в stdout!]', line); return; }
  if (msg.id !== undefined && pending.has(msg.id)) {
    const { resolve } = pending.get(msg.id);
    pending.delete(msg.id);
    resolve(msg);
  } else if (msg.method === 'notifications/progress') {
    console.error(`[прогресс] ${msg.params.progress}/${msg.params.total ?? '?'} ${msg.params.message ?? ''}`);
  } else if (msg.method === 'notifications/message') {
    console.error(`[лог ${msg.params.level}] ${JSON.stringify(msg.params.data)}`);
  } else if (msg.method === 'roots/list' && msg.id !== undefined) {
    child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id: msg.id, result: { roots: [{ uri: 'file:///' + process.cwd().replace(/\\/g, '/'), name: 'cwd' }] } }) + '\n');
  } else {
    console.error('[уведомление]', line.slice(0, 500));
  }
});

function request(method, params, timeoutMs = 30 * 60 * 1000) {
  const id = nextId++;
  const payload = { jsonrpc: '2.0', id, method, params };
  child.stdin.write(JSON.stringify(payload) + '\n');
  return new Promise((resolve, reject) => {
    pending.set(id, { resolve });
    setTimeout(() => { if (pending.has(id)) { pending.delete(id); reject(new Error(`таймаут ${method}`)); } }, timeoutMs);
  });
}
const notify = (method, params) => child.stdin.write(JSON.stringify({ jsonrpc: '2.0', method, params }) + '\n');

try {
  const t0 = Date.now();
  const init = await request('initialize', {
    protocolVersion: '2025-06-18',
    capabilities: { roots: { listChanged: false } },
    clientInfo: { name: 'offload-smoke', version: '1.0.0' },
  }, 60000);
  console.log(`initialize (${Date.now() - t0} мс):`, JSON.stringify(init.result?.serverInfo), 'protocol', init.result?.protocolVersion);
  if (init.result?.instructions) console.log('instructions:\n' + init.result.instructions + '\n');
  notify('notifications/initialized', {});

  const tools = await request('tools/list', {}, 60000);
  for (const t of tools.result?.tools ?? []) {
    console.log(`- ${t.name}: ${(t.description ?? '').split('\n')[0].slice(0, 120)}`);
    console.log(`    params: ${Object.keys(t.inputSchema?.properties ?? {}).join(', ')}; annotations: ${JSON.stringify(t.annotations ?? {})}`);
  }

  for (const [name, args] of calls) {
    const t1 = Date.now();
    console.log(`\n=== ${name} ${JSON.stringify(args)}`);
    const r = await request('tools/call', { name, arguments: args, _meta: { progressToken: `p${nextId}` } });
    if (r.error) console.log('ОШИБКА JSON-RPC:', JSON.stringify(r.error));
    else {
      console.log(`isError=${r.result.isError ?? false} (${Date.now() - t1} мс)`);
      for (const c of r.result.content ?? []) console.log(c.type === 'text' ? c.text : JSON.stringify(c));
      if (r.result.structuredContent) console.log('structuredContent:', JSON.stringify(r.result.structuredContent).slice(0, 2000));
    }
  }
} catch (e) {
  console.error('Сбой:', e.message);
  process.exitCode = 1;
} finally {
  child.stdin.end();
  setTimeout(() => child.kill(), 2000).unref();
}
