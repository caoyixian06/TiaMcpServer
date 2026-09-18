#!/usr/bin/env node
// extract-helpers.mjs — 从 Archive 归档文件中按符号抽取方法体到 LegacyBridge.cs（一次性工具）
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const jobs = [
  ['Archive/Services/DiagnosticsService.cs', 'DownloadToDeviceEnhanced'],
  ['Archive/Services/DiagnosticsService.cs', 'ExploreProviderMethods'],
  ['Archive/Services/DiagnosticsService.cs', 'ReadOnlineVariables'],
  ['Archive/Services/DiagnosticsService.cs', 'WriteOnlineVariable'],
  ['Archive/Services/CatalogService.cs', 'FindTypeIdentifierByOrder'],
  ['Archive/Services/InstructionTemplateService.cs', 'GetInstructionLibrary'],
  ['Archive/Services/AdvancedFeaturesService.cs', 'ResolveFeatureType'],
  ['Archive/Services/AdvancedFeaturesService.cs', 'ResolvePlc'],
  ['Archive/Services/AdvancedFeaturesService.cs', 'TryGetService'],
  ['Archive/Services/QualityGuardService.cs', 'IsLadConstant'],
  ['Archive/Services/QualityGuardService.cs', 'TryGetExistingHmiCanvasSize'],
  ['Archive/Services/QualityGuardService.cs', 'TryReadSuccess'],
  ['Archive/Services/QualityGuardService.cs', 'ValidateBlockInstanceCompatibility'],
  ['Archive/Services/QualityGuardService.cs', 'ValidateHmiScreenSpecJson'],
  ['Archive/Services/QualityGuardService.cs', 'ValidateLadNetworkDefinition'],
  ['Archive/Services/QualityGuardService.cs', 'ValidateLadNetworkJson'],
  ['Archive/Services/QualityGuardService.cs', 'ValidateLadProgramJson'],
];

function extract(text, sym, file) {
  const re = new RegExp('\\b(private|internal|public)\\s+(?:async\\s+)?(?:static\\s+)?[^=;\\n]{0,300}?\\b' + sym + '\\s*(\\(|<)');
  const m = re.exec(text);
  if (!m) throw new Error(`未找到定义: ${sym} @ ${file}`);
  let s = text.lastIndexOf('\n', m.index) + 1;
  // 回退吞掉紧邻的注释/特性行
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
    if (end === -1) throw new Error(`表达式体缺分号: ${sym}`);
    end += 1;
  } else {
    let depth = 0, i = bi;
    for (; i < text.length; i++) {
      if (text[i] === '{') depth++;
      else if (text[i] === '}') { depth--; if (depth === 0) { end = i + 1; break; } }
    }
    if (end === undefined) throw new Error(`花括号不配平: ${sym}`);
  }
  return text.slice(s, end).trimEnd();
}

const parts = [];
for (const [f, sym] of jobs) {
  const text = fs.readFileSync(path.join(ROOT, f), 'utf8');
  try {
    parts.push(extract(text, sym, f));
    console.log('✓ ' + sym);
  } catch (e) {
    console.error('✗ ' + sym + ': ' + e.message);
    process.exitCode = 1;
  }
}

const out = `using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.SW;

namespace TiaMcpServer;

/// <summary>
/// 阶段1裁剪桥接：从已归档文件中搬入、但保留域仍在调用的成员。
/// 全部为 PortalService 的 partial 成员，逐字保留原实现（含 private 可跨 partial 访问）。
/// </summary>
public partial class PortalService
{
${parts.join('\n\n')}
}
`;
fs.writeFileSync(path.join(ROOT, 'Services', 'LegacyBridge.cs'), out, 'utf8');
console.log('\n写入 Services/LegacyBridge.cs');
