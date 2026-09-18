#!/usr/bin/env node
// ════════════════════════════════════════════════════════════════════
// xml2net.mjs — TIA Openness 导出块 XML(SW.Blocks) → read_lad_network 同构 JSON
// 用法: node xml2net.mjs <块.xml> [输出.json]  (默认输出到同名 .json)
// 输出与 run_lad_json.ps1(read_lad_network 工具) 完全同构, 可直接喂 lad-sim.mjs
// ════════════════════════════════════════════════════════════════════
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

// ── XML 实体解码（标题/符号中的 &amp; &lt; &gt; &quot; &apos; 及数字实体）──
function decodeEntities(s) {
  return s
    .replace(/&lt;/g, '<').replace(/&gt;/g, '>')
    .replace(/&quot;/g, '"').replace(/&apos;/g, "'")
    .replace(/&#(\d+);/g, (_, d) => String.fromCodePoint(parseInt(d, 10)))
    .replace(/&#x([0-9a-fA-F]+);/g, (_, h) => String.fromCodePoint(parseInt(h, 16)))
    .replace(/&amp;/g, '&');
}

// ── 迷你 XML 解析器（仅支持规整的元素/属性/文本, 处理命名空间前缀与自闭合）──
function parseXml(xml) {
  let i = 0;
  const n = xml.length;
  function skipWs() { while (i < n && /\s/.test(xml[i])) i++; }
  function localName(tag) { return tag.split(':').pop(); }
  function parseNode() {
    skipWs();
    if (xml.startsWith('<?', i)) { // 处理声明/注释
      const end = xml.indexOf('>', i);
      i = end + 1;
      return null;
    }
    if (xml.startsWith('<!--', i)) {
      const end = xml.indexOf('-->', i);
      i = end + 3;
      return null;
    }
    if (xml[i] !== '<') { // 文本（解码 XML 实体）
      const end = xml.indexOf('<', i);
      const t = decodeEntities(xml.slice(i, end).trim());
      i = end;
      return t || null;
    }
    const tagEnd = xml.indexOf('>', i);
    const tag = xml.slice(i + 1, tagEnd);
    if (tag.startsWith('/')) { i = tagEnd + 1; return null; } // 意外闭合
    if (tag.endsWith('/')) { // 自闭合
      const name = localName(tag.slice(0, -1).trim().split(/\s/)[0]);
      const attrs = {};
      const m = tag.slice(0, -1).trim().match(/^[^\s]+\s+([\s\S]*)$/);
      if (m) for (const am of m[1].matchAll(/([:\w-]+)="([^"]*)"/g)) attrs[am[1].split(':').pop()] = decodeEntities(am[2]);
      i = tagEnd + 1;
      return { name, attrs, children: [], text: '' };
    }
    const head = tag.trim().split(/\s/);
    const name = localName(head[0]);
    const attrs = {};
    const m = tag.trim().match(/^[^\s]+\s+([\s\S]*)$/);
    if (m) for (const am of m[1].matchAll(/([:\w-]+)="([^"]*)"/g)) attrs[am[1].split(':').pop()] = decodeEntities(am[2]);
    i = tagEnd + 1;
    const node = { name, attrs, children: [], text: '' };
    while (i < n) {
      const next = xml.indexOf('<', i);
      if (next < 0) break;
      if (xml.startsWith('</', next)) {
        // 闭合前先收集 i..next 之间的文本（纯文本元素如 ConstantValue 在此处收文本; 实体必须解码）
        const txt = decodeEntities(xml.slice(i, next).trim());
        if (txt) node.text += txt;
        const closeEnd = xml.indexOf('>', next);
        const closeName = localName(xml.slice(next + 2, closeEnd).trim());
        if (closeName === name) { i = closeEnd + 1; break; }
        i = closeEnd + 1;
        continue;
      }
      const child = parseNode();
      if (child === null) continue;
      if (typeof child === 'string') node.text += child;
      else node.children.push(child);
    }
    return node;
  }
  let root = null;
  while (i < n && root === null) root = parseNode();
  return root;
}

function findNodes(root, name) {
  const out = [];
  (function walk(nd) {
    if (!nd || typeof nd === 'string') return;
    if (nd.name === name || nd.name.endsWith('.' + name)) out.push(nd);
    for (const c of nd.children ?? []) walk(c);
  })(root);
  return out;
}

// ── FlgNet → 网络 JSON ──
function parseFlgNet(flg, idx) {
  const partsContainer = (flg.children ?? []).find((c) => c.name === 'Parts');
  const wiresContainer = (flg.children ?? []).find((c) => c.name === 'Wires');
  const accesses = [];
  const parts = [];
  const calls = [];
  const accessMap = new Map();
  for (const c of partsContainer?.children ?? []) {
    if (c.name === 'Access') {
      const uid = parseInt(c.attrs.UId ?? '0', 10);
      const scope = c.attrs.Scope ?? '';
      const symEl = (c.children ?? []).find((x) => x.name === 'Symbol');
      const constEl = (c.children ?? []).find((x) => x.name === 'Constant');
      let type = 'Symbol', symbol = '';
      if (symEl) {
        const comps = (symEl.children ?? []).filter((x) => x.name === 'Component').map((x) => x.attrs.Name ?? '');
        symbol = comps.join('.');
      } else if (constEl) {
        type = 'Constant';
        const cv = (constEl.children ?? []).find((x) => x.name === 'ConstantValue');
        symbol = cv?.text ?? '';
      }
      accesses.push({ uid, scope, type, symbol });
      accessMap.set(uid, symbol);
    } else if (c.name === 'Part') {
      const negated = (c.children ?? []).filter((x) => x.name === 'Negated').map((x) => x.attrs.Name ?? '');
      const instEl = (c.children ?? []).find((x) => x.name === 'Instance');
      let instance = '', instanceScope = '';
      if (instEl) {
        instanceScope = instEl.attrs.Scope ?? '';
        instance = (instEl.children ?? []).filter((x) => x.name === 'Component').map((x) => x.attrs.Name ?? '').join('.');
      }
      const templates = {};
      for (const tv of (c.children ?? []).filter((x) => x.name === 'TemplateValue')) {
        templates[tv.attrs.Name ?? ''] = tv.text;
      }
      parts.push({
        uid: parseInt(c.attrs.UId ?? '0', 10),
        name: c.attrs.Name ?? '',
        version: c.attrs.Version ?? '',
        disabledEno: c.attrs.DisabledENO ?? '',
        negated,
        instance,
        instanceScope,
        templates,
      });
    } else if (c.name === 'Call') {
      const ci = (c.children ?? []).find((x) => x.name === 'CallInfo');
      const instEl = (ci?.children ?? []).find((x) => x.name === 'Instance');
      let instance = '', instanceScope = '';
      if (instEl) {
        instanceScope = instEl.attrs.Scope ?? '';
        instance = (instEl.children ?? []).filter((x) => x.name === 'Component').map((x) => x.attrs.Name ?? '').join('.');
      }
      const parameters = [];
      for (const pm of (ci?.children ?? []).filter((x) => x.name === 'Parameter')) {
        const pa = (pm.children ?? []).find((x) => x.name === 'Access');
        let value = '';
        if (pa) value = accessMap.get(parseInt(pa.attrs.UId ?? '0', 10)) ?? '';
        parameters.push({ name: pm.attrs.Name ?? '', section: pm.attrs.Section ?? '', type: pm.attrs.Type ?? '', value });
      }
      calls.push({
        uid: parseInt(c.attrs.UId ?? '0', 10),
        calleeName: ci?.attrs.Name ?? '',
        blockType: ci?.attrs.BlockType ?? '',
        instance,
        instanceScope,
        parameters,
      });
    }
  }
  const wires = [];
  for (const w of wiresContainer?.children ?? []) {
    if (w.name !== 'Wire') continue;
    const endpoints = [];
    for (const nd of w.children ?? []) {
      if (nd.name === 'Powerrail') endpoints.push('Powerrail');
      else if (nd.name === 'NameCon') endpoints.push(`${nd.attrs.UId}.${nd.attrs.Name}`);
      else if (nd.name === 'IdentCon') endpoints.push(`#${nd.attrs.UId}`);
      else if (nd.name === 'OpenCon') endpoints.push('Open');
      else endpoints.push(nd.name);
    }
    wires.push({ endpoints });
  }
  return { networkNumber: idx, title: '', comment: '', parts, accesses, calls, wires };
}

function convert(xmlText) {
  const root = parseXml(xmlText.replace(/^\uFEFF/, ''));
  const cus = findNodes(root, 'CompileUnit');
  const nets = [];
  let idx = 0;
  for (const cu of cus) {
    const netSources = findNodes(cu, 'NetworkSource');
    const flg = netSources.map((ns) => (ns.children ?? []).find((c) => c.name === 'FlgNet')).find(Boolean);
    // 标题
    let title = '';
    for (const ml of findNodes(cu, 'MultilingualText')) {
      if (ml.attrs.CompositionName !== 'Title') continue;
      const item = findNodes(ml, 'MultilingualTextItem')[0];
      const t = findNodes(item ?? ml, 'Text')[0];
      title = t?.text ?? '';
      if (title) break;
    }
    idx++;
    if (!flg) { nets.push({ networkNumber: idx, title, parts: [], accesses: [], calls: [], wires: [] }); continue; }
    const net = parseFlgNet(flg, idx);
    net.title = title;
    nets.push(net);
  }
  return nets;
}

export { convert, parseXml };

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const [, , inFile, outFile] = process.argv;
  if (!inFile) { console.log('用法: node xml2net.mjs <块.xml> [输出.json]'); process.exit(1); }
  const xmlText = fs.readFileSync(inFile, 'utf8');
  const nets = convert(xmlText);
  const out = outFile ?? path.join(path.dirname(inFile), path.basename(inFile, path.extname(inFile)) + '.json');
  fs.writeFileSync(out, JSON.stringify(nets, null, 1), 'utf8');
  console.log(`网络 ${nets.length} 个 → ${out}`);
}
