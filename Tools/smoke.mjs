#!/usr/bin/env node
// smoke.mjs — 重构回归冒烟测试
// 用法: node tools/smoke.mjs <TiaMcpServer.exe路径> [TIA进程PID]
// 流程: initialize → tools/list(计数) → attach → 工程信息 → list_blocks →
//       read_lad_network(升降控制 网1) → compile_plc → 汇总退出码
import { spawn, execFileSync } from 'node:child_process';
import readline from 'node:readline';

const exe = process.argv[2];
let tiaPid = parseInt(process.argv[3] ?? '0', 10);
if (!tiaPid) {
  const out = execFileSync('powershell', ['-NoProfile', '-Command',
    "(Get-CimInstance Win32_Process -Filter \"Name='Siemens.Automation.Portal.exe'\").ProcessId | Select-Object -First 1"], { encoding: 'utf8' });
  tiaPid = parseInt(out.trim(), 10);
}
if (!exe || !tiaPid) { console.error('用法: node tools/smoke.mjs <TiaMcpServer.exe> [tiaPid]'); process.exit(2); }

const env = { ...process.env, TIA_PORTAL_DIR: 'C:\\Program Files\\Siemens\\Automation\\Portal V17', TIA_PORTAL_VERSION: 'V17', TIA_MCP_DEBUG: '0' };
const p = spawn(exe, [], { stdio: ['pipe', 'pipe', 'pipe'], env });
p.stderr.on('data', () => { });
const rl = readline.createInterface({ input: p.stdout });
const pending = new Map();
let nextId = 1;
rl.on('line', (line) => {
  try {
    const msg = JSON.parse(line);
    if (msg.id !== undefined && pending.has(msg.id)) { pending.get(msg.id)(msg); pending.delete(msg.id); }
  } catch { }
});
function call(method, params) {
  return new Promise((resolve) => {
    const id = nextId++;
    pending.set(id, resolve);
    p.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n');
  });
}
async function tool(name, args, timeoutMs = 180000) {
  const r = await Promise.race([
    call('tools/call', { name, arguments: args ?? {} }),
    new Promise((res) => setTimeout(() => res({ timeout: true }), timeoutMs)),
  ]);
  if (r.timeout) return { __timeout: true };
  const t = r?.result?.content?.[0]?.text;
  try { return JSON.parse(t); } catch { return { __raw: String(t).slice(0, 200) }; }
}
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const results = [];
const check = (name, pass, extra = '') => {
  results.push({ name, pass: !!pass });
  console.log(`${pass ? '✓' : '✗'} ${name}${extra ? ' — ' + extra : ''}`);
};

await call('initialize', { protocolVersion: '2024-11-05', capabilities: {}, clientInfo: { name: 'smoke', version: '1.0' } });
p.stdin.write(JSON.stringify({ jsonrpc: '2.0', method: 'notifications/initialized', params: {} }) + '\n');
await sleep(500);

const tlMsg = await call('tools/list', {});
const toolCount = tlMsg?.result?.tools?.length ?? 0;
check('tools/list', toolCount > 0, `工具数 ${toolCount}`);

const at = await tool('attach_to_process', { processId: tiaPid }, 300000);
check('attach_to_process', at.success === true, at.projectName ?? at.error ?? '');

const gi = await tool('get_project_info');
check('get_project_info', !!gi.name, gi.name ?? gi.error ?? '');

const lb = await tool('list_blocks');
check('list_blocks', Array.isArray(lb.blocks), `块数 ${lb.blocks?.length ?? '?'}`);

const rn = await tool('read_lad_network', { blockName: '升降控制', networkNumber: 1, plcName: 'PLC_1' });
check('read_lad_network 网1', Array.isArray(rn.parts) && rn.parts.length > 0, `元件 ${rn.parts?.length ?? 0}`);

// 模拟门禁自检：一个常开触点+线圈的最小网络必须 PASS（不写工程，纯验证管线）
const sim = await tool('verify_lad_simulation', {
  networkJson: JSON.stringify({ title: '冒烟_门禁自检', steps: [{ tag: '一层限位', type: 'contact' }, { tag: '门输出', type: 'coil' }] }),
});
check('verify_lad_simulation 门禁', sim.simulationPass === true, `网络 ${sim.networks ?? '?'} 元件 ${sim.parts ?? '?'}`);

const cp = await tool('compile_plc', {}, 600000);
check('compile_plc', cp.errorCount === 0, `错误 ${cp.errorCount ?? '?'} 警告 ${cp.warningCount ?? '?'}`);

const allPass = results.every((r) => r.pass);
console.log(`\n冒烟结果: ${results.filter((r) => r.pass).length}/${results.length} 通过`);
console.log(`SMOKE_JSON:${JSON.stringify({ pass: allPass, toolCount, project: gi.name ?? '' })}`);
p.stdin.end();
await sleep(1000);
try { p.kill(); } catch { }
process.exit(allPass ? 0 : 1);
