#!/usr/bin/env node
// simcheck.mjs — LAD 写入前模拟门禁
// 用法: node simcheck.mjs <块级XML文件> [扫描周期数=50]
// 输出: 单行 JSON {pass, networks, parts, warnings[], unimplemented[], unresolvedCount}
// 判定: 无"未实现元件"且无"未求值"即 PASS（告警不阻塞，但如实上报）
import fs from 'node:fs';
import { convert } from './xml2net.mjs';
import { LadSim } from './lad-sim.mjs';

const file = process.argv[2];
const cycles = parseInt(process.argv[3] ?? '50', 10) || 50;
if (!file) { console.log(JSON.stringify({ pass: false, error: '用法: node simcheck.mjs <块XML> [周期数]' })); process.exit(2); }

try {
  const xml = fs.readFileSync(file, 'utf8');
  const nets = convert(xml);
  if (!Array.isArray(nets) || nets.length === 0) {
    console.log(JSON.stringify({ pass: false, error: 'XML 中未解析出任何网络（CompileUnit/NetworkSource/FlgNet 结构缺失?）', networks: 0 }));
    process.exit(0);
  }
  const parts = nets.reduce((a, n) => a + (n.parts ?? []).length, 0);
  const s = new LadSim(nets, {});
  s.advance(cycles);
  const logs = s.logs();
  const unimplemented = logs.filter((l) => l.includes('未实现元件'));
  const unresolved = logs.filter((l) => l.includes('未求值'));
  const warnings = logs.filter((l) => l.includes('[warn]') || l.includes('[approx]'));
  const pass = unimplemented.length === 0 && unresolved.length === 0;
  console.log(JSON.stringify({
    pass,
    networks: nets.length,
    parts,
    cycles,
    unresolvedCount: s.unresolvedParts ?? 0,
    unimplemented: [...new Set(unimplemented.map((l) => (l.match(/未实现元件:\s*([^\s（(]+)/) ?? [])[1] ?? '?'))],
    warnings: warnings.slice(0, 10),
    warningCount: warnings.length,
  }));
} catch (e) {
  console.log(JSON.stringify({ pass: false, error: String(e && e.message || e) }));
}
