#!/usr/bin/env node
// extract-more.mjs — 递归抽取：从 Archive 中按符号名定位定义，插入 LegacyBridge 的 partial 类内
// 用法: node tools/extract-more.mjs Sym1 Sym2 ...
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const BRIDGE = path.join(ROOT, 'Services', 'LegacyBridge.cs');
const ARCHIVE_DIRS = ['Archive/Services', 'Archive/Tools'];

const symbols = process.argv.slice(2);
if (!symbols.length) { console.error('用法: node tools/extract-more.mjs Sym1 Sym2 ...'); process.exit(2); }

let bridge = fs.readFileSync(BRIDGE, 'utf8');

function findDefinition(sym) {
  const re = new RegExp('\\b(private|internal|public)\\s+(?:sealed\\s+)?(?:async\\s+)?(?:static\\s+)?[^=;\\n]{0,300}?\\b' + sym + '\\s*(\\(|<)');
  for (const dir of ARCHIVE_DIRS) {
    const full = path.join(ROOT, dir);
    for (const f of fs.readdirSync(full).filter((x) => x.endsWith('.cs'))) {
      const text = fs.readFileSync(path.join(full, f), 'utf8');
      const m = re.exec(text);
      if (m) return { text, file: path.join(dir, f), m };
    }
  }
  return null;
}

function sliceMember(text, m, sym) {
  let s = text.lastIndexOf('\n', m.index) + 1;
  for (;;) {
    const prev = s > 0 ? text.lastIndexOf('\n', s - 2) + 1 : 0;
    const line = text.slice(prev, s).trim();
    if (prev < s && (line.startsWith('///') || line.startsWith('//') || line.startsWith('['))) s = prev; else break;
  }
  const bi = text.indexOf('{', m.index);
  const arrow = text.indexOf('=>', m.index);
  let end;
  if (arrow !== -1 && (bi === -1 || arrow < bi)) {
    end = text.indexOf(';', arrow);
    if (end === -1) throw new Error(`${sym}: 表达式体缺分号`);
    end += 1;
  } else {
    let depth = 0, i = bi;
    for (; i < text.length; i++) {
      if (text[i] === '{') depth++;
      else if (text[i] === '}') { depth--; if (depth === 0) { end = i + 1; break; } }
    }
    if (end === undefined) throw new Error(`${sym}: 花括号不配平`);
  }
  return text.slice(s, end).trimEnd();
}

let added = 0, skipped = 0, failed = [];
for (const sym of symbols) {
  // 已存在“定义”（带访问修饰符）则跳过；仅出现调用点不算
  const defRe = new RegExp('\\b(private|internal|public)\\s+(?:sealed\\s+)?(?:async\\s+)?(?:static\\s+)?[^=;\\n]{0,300}?\\b' + sym + '\\s*(\\(|<)');
  if (defRe.test(bridge)) { skipped++; continue; }
  const hit = findDefinition(sym);
  if (!hit) { failed.push(sym); continue; }
  try {
    const member = sliceMember(hit.text, hit.m, sym);
    const close = bridge.lastIndexOf('}');
    bridge = bridge.slice(0, close) + '\n' + member + '\n' + bridge.slice(close);
    added++;
    console.log('✓ ' + sym + ' ← ' + hit.file);
  } catch (e) {
    failed.push(sym + ' (' + e.message + ')');
  }
}

fs.writeFileSync(BRIDGE, bridge, 'utf8');
console.log(`\n新增 ${added}，已存在跳过 ${skipped}，失败 ${failed.length}${failed.length ? ': ' + failed.join(', ') : ''}`);
process.exit(failed.length ? 1 : 0);
