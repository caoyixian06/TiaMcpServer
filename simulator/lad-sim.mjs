#!/usr/bin/env node
// ════════════════════════════════════════════════════════════════════
// lad-sim.mjs — 梯形图 XML/JSON 模拟器（AI 专用逻辑验证工具）
// 输入: read_lad_network 导出的网络 JSON（parts/accesses/wires，与 FlgNet XML 同构）
// 输出: 逐扫描周期变量演化 + 断言结果
// ════════════════════════════════════════════════════════════════════
import fs from 'node:fs';
import path from 'node:path';
import { pathToFileURL, fileURLToPath } from 'node:url';

// ── 时间解析: T#3S / T#500MS / T#1S500MS(多段) / S5T#1S / S5T#3.3S / S5T#2M30S / 1 / 2 等 ──
export function parseConst(raw) {
  if (raw === undefined || raw === null) return undefined;
  const s = String(raw).trim();
  // 多段累加: T#1S500MS / T#2M30S / S5T#2M30S / S5T#1H30M 等（交替 MS 必须在 S 前, 否则 500MS 被切成 500M+S）
  const multi = /^((?:T#)|(?:S5T#))(\d+(?:MS|S|M|H))+$/i.exec(s);
  if (multi) {
    let ms = 0;
    // 注意: 捕获组重复只保留最后一次, 必须对完整匹配串 multi[0] 做 matchAll
    // matchAll 必须同样带 i 标志: TIA 会导出小写单位(T#1s500ms), 大小写不一致会静默得 0
    for (const m of multi[0].matchAll(/(\d+)(MS|S|M|H)/gi)) {
      const v = parseInt(m[1], 10);
      ms += m[2].toUpperCase() === 'S' ? v * 1000
        : m[2].toUpperCase() === 'M' ? v * 60000
        : m[2].toUpperCase() === 'H' ? v * 3600000 : v;
    }
    return ms;
  }
  // S5 定时器常量: S5T#1S / S5T#500MS / S5T#1M / S5T#1H / S5T#3.3S(3秒300毫秒)
  const s5 = /^S5T#(\d+(?:\.\d+)?)(S|MS|M|H)$/i.exec(s);
  if (s5) {
    const num = parseFloat(s5[1]);
    const unit = s5[2].toUpperCase();
    const ms = unit === 'S' ? num * 1000 : unit === 'M' ? num * 60000
      : unit === 'H' ? num * 3600000 : num;
    return Math.round(ms);
  }
  const t = /^T#(\d+)(S|MS|M)$/i.exec(s);
  if (t) {
    const v = parseInt(t[1], 10);
    const unit = t[2].toUpperCase();
    return unit === 'S' ? v * 1000 : unit === 'M' ? v * 60000 : v;
  }
  // S7 进制常量: 16#FF / 2#0001_0000 / 8#777 / 前缀变体 W#16#F / DW#16#F / B#16#F / Q#16#F
  const based = /^(?:(?:DW|W|Q|B|D|I|L)#)?(\d{1,2})#([0-9A-Fa-f_]+)$/.exec(s);
  if (based) {
    const base = parseInt(based[1], 10);
    const v = parseInt(based[2].replace(/_/g, ''), base);
    if (base >= 2 && base <= 36 && !Number.isNaN(v)) return v;
  }
  if (/^-?\d+$/.test(s)) return parseInt(s, 10);
  // Real 字面量: 1.5 / -2.75（S7 的 1.0 与 Int 1 在 JS === 下相等, 语义巧合一致）
  if (/^-?\d+\.\d+$/.test(s)) return parseFloat(s);
  // 注: "0"/"1" 已被上面的整数分支截获, TRUE/FALSE 只匹配布尔字面量
  if (/^(TRUE|true)$/.test(s)) return true;
  if (/^(FALSE|false)$/.test(s)) return false;
  return s;
}

// ── 网络解析: wire 簇图 ──
export function parseNetwork(net) {
  const accessMap = new Map(); // uid -> {kind, name, value}
  for (const a of net.accesses ?? []) {
    if (a.type === 'Symbol') accessMap.set(a.uid, { kind: 'symbol', name: a.symbol });
    else accessMap.set(a.uid, { kind: 'const', value: parseConst(a.symbol) });
  }
  const parts = (net.parts ?? []).map((p) => ({
    uid: p.uid,
    name: p.name,
    negated: p.negated ?? [],
    instance: p.instance || '',
    instanceScope: p.instanceScope || '',
  }));
  const partById = new Map(parts.map((p) => [p.uid, p]));

  // wire 簇: 每个 wire 一个簇 id(数组下标)
  const wires = net.wires ?? [];
  const clusterOf = new Map(); // "uid.pin" -> wireIdx
  const wireDataRefs = [];    // wireIdx -> [{pinKey}] 数据注入端(IdentCon → pin)
  const wireHasPower = new Set(); // wireIdx 含 Powerrail
  const wireHasOpen = new Set();
  wires.forEach((w, wi) => {
    for (const ep of w.endpoints) {
      if (ep === 'Powerrail') { wireHasPower.add(wi); continue; }
      if (ep === 'Open') { wireHasOpen.add(wi); continue; }
      if (ep.startsWith('#')) {
        // IdentCon: 数据注入（access 值 → 所在簇）
        const accUid = parseInt(ep.slice(1), 10);
        (wireDataRefs[wi] ??= []).push(accUid);
        continue;
      }
      const m = /^(\d+)\.(.*)$/.exec(ep);
      if (m) {
        const uid = m[1];
        const pin = m[2];
        // Preserve exact TIA pin names first: data OUT and flow out are
        // distinct pins even though they differ only by case.
        const exactKey = `${uid}.${pin}`;
        clusterOf.set(exactKey, wi);
        const lowerKey = `${uid}.${pin.toLowerCase()}`;
        const upperKey = `${uid}.${pin.toUpperCase()}`;
        if (!clusterOf.has(lowerKey)) clusterOf.set(lowerKey, wi);
        if (!clusterOf.has(upperKey)) clusterOf.set(upperKey, wi);
      }
    }
  });

  return { title: net.title, accessMap, parts, partById, wires, clusterOf, wireDataRefs, wireHasPower, wireHasOpen, calls: net.calls ?? [] };
}

// ── 边沿触点存储位（立即更新语义，贴近 S7 真实行为: 同扫描共享位的第二个 P 触点不触发）──
export class EdgeStore {
  constructor() { this.prev = new Map(); }
  rising(bit, op) {
    const p = this.prev.get(bit) === true;
    this.prev.set(bit, op === true);
    return op === true && !p;
  }
  falling(bit, op) {
    const p = this.prev.get(bit) === true;
    this.prev.set(bit, op === true);
    return op !== true && p;
  }
}

// ── 定时器实例（TON/TOF/TP/SdCoil）──
export class TimerInst {
  constructor(pt, mode = 'ton') {
    this.in = false; this.pt = pt ?? 0; this.elapsed = 0; this.q = false; this.et = 0;
    this.mode = mode; this.prevIn = false; this.running = false; this.everOn = false;
  }
  update(dt) {
    if (this.mode === 'tof') {
      // TOF 断开延时: IN=1 → Q 立即 1 且复位计时; IN=0 → 计时, PT 内 Q 保持 1, 到 PT → Q=0。
      // S7 真实语义: IN 从未接通过时 Q 恒为 0(初态), 不得输出假脉冲。
      if (this.in) { this.everOn = true; this.elapsed = 0; this.et = 0; this.q = true; }
      else if (this.everOn) {
        this.elapsed += dt; this.et = this.elapsed;
        this.q = this.elapsed < this.pt;
      } else { this.q = false; this.et = 0; }
    } else if (this.mode === 'tp') {
      // TP 脉冲: IN 上升沿启动, PT 期间 Q=1, 结束后 Q=0(IN 保持 1 也不重启)
      const rising = this.in && !this.prevIn;
      this.prevIn = this.in;
      if (rising) { this.running = true; this.elapsed = 0; }
      if (this.running) {
        this.elapsed += dt; this.et = this.elapsed;
        this.q = this.elapsed < this.pt;
        if (this.elapsed >= this.pt) { this.running = false; this.q = false; }
      } else { this.q = false; this.et = 0; }
    } else {
      // TON 接通延时
      if (this.in) {
        this.elapsed += dt;
        this.et = this.elapsed;
        this.q = this.elapsed >= this.pt;
      } else {
        this.elapsed = 0;
        this.et = 0;
        this.q = false;
      }
    }
  }
}

// ── 模拟器主体 ──
// 声明类型 → 回绕范围（S7 整数运算按位宽回绕; DInt 超出 JS 精度场景罕见, 不回绕）
const INT_RANGES = {
  SInt: [-128, 127], USInt: [0, 255],
  Int: [-32768, 32767], UInt: [0, 65535], Word: [0, 65535],
};

export class LadSim {
  constructor(networksJson, varsDecl) {
    // 容错: PowerShell 导出的单元素数组会被展开成对象
    if (!Array.isArray(networksJson)) networksJson = [networksJson];
    this.networks = networksJson.map((net, i) => {
      const p = parseNetwork(net);
      p.netIdx = i; // TIA 的 uid 是每网络局部编号, 定时器实例 key 必须含网络索引
      return p;
    });
    this.vars = {};
    for (const [k, v] of Object.entries(varsDecl)) {
      if (k === '_comment') continue;
      const init = v.init ?? (v.type === 'Bool' ? false : 0);
      this.vars[k] = v.type === 'Bool' ? init === true : init;
    }
    this.varTypes = varsDecl;
    this.timers = new Map(); // instance -> TimerInst
    this.edge = new EdgeStore();
    this.dt = 10;          // 扫描周期 ms
    this.cycle = 0;
    this.unresolvedParts = 0; // 累计未求值 part（输入悬空诊断, 供断言）
    this.trace = [];       // 每周期变量快照（压缩记录）
    this._log = [];
  }

  get(name) { return this.vars[name]; }
  set(name, value) { this.vars[name] = value; }
  getInt(name) { return this.vars[name] ?? 0; }
  getBool(name) { return this.vars[name] === true; }

  // 数值写入: 目标声明为整数类型时按位宽回绕(贴近 S7 ADD/MUL 溢出行为);
  // Bool 目标按位拷贝语义规范化(非零即真), 否则 MOVE 1→Bool 写成数字 1, 触点 ===true 判不通
  _writeVar(nm, v) {
    const t = this.varTypes?.[nm]?.type;
    if (t === 'Bool') { this.vars[nm] = v ? true : false; return; }
    const r = INT_RANGES[t];
    if (r && typeof v === 'number' && Number.isFinite(v)) {
      const span = r[1] - r[0] + 1;
      v = (((v - r[0]) % span) + span) % span + r[0];
    }
    this.vars[nm] = v;
  }

  // 从 access uid 读值（未声明变量默认 0：真实项目变量海量，静态执行无需完整变量表）
  _accessVal(accessMap, uid) {
    const a = accessMap.get(uid);
    if (!a) return undefined;
    if (a.kind === 'const') return a.value;
    return this.vars[a.name] ?? 0;
  }
  // 从 access uid 取变量名（用于写）
  _accessName(accessMap, uid) {
    const a = accessMap.get(uid);
    return a && a.kind === 'symbol' ? a.name : null;
  }

  // 单网络求值: 簇传播迭代
  _evalNetwork(n) {
    const cluster = new Array(n.wires.length).fill(undefined);
    const { accessMap, partById, clusterOf, wireDataRefs, wireHasPower, wireHasOpen } = n;

    // 1) Powerrail 簇 = true; Open 簇不赋值(悬空)
    for (const wi of wireHasPower) cluster[wi] = true;

    // 2) IdentCon 数据注入（operand/PT/in1... 所在簇）
    for (let wi = 0; wi < n.wires.length; wi++) {
      const refs = wireDataRefs[wi];
      if (refs && refs.length) {
        for (const uid of refs) {
          const v = this._accessVal(accessMap, uid);
          if (cluster[wi] === undefined && v !== undefined) cluster[wi] = v;
        }
      }
    }

    // 3) 迭代求值各 Part
    const pinVal = (uid, pin) => {
      const wi = clusterOf.get(`${uid}.${pin}`);
      if (wi === undefined) return undefined;
      const refs = wireDataRefs[wi] ?? [];
      if (refs.length === 1) {
        const a = accessMap.get(refs[0]);
        if (a && a.kind === 'symbol') return this.vars[a.name] ?? 0;
        if (a && a.kind === 'const') return a.value;
      }
      return cluster[wi];
    };
    // fix#16: 数据引脚实时读——S7 的触点/比较/算术在求值时刻读变量值,
    // 同一网络内前面元件(如 SD 线圈)写入的变量, 后面的触点立即读到新值。
    // 仅在簇是"单一 IdentCon 数据注入"时实时读; 流信号(触点 out/线圈 in)仍走簇。
    const readPin = (uid, pin) => {
      const wi = clusterOf.get(`${uid}.${pin}`);
      if (wi === undefined) return undefined;
      const refs = wireDataRefs[wi] ?? [];
      if (refs.length === 1) {
        const a = accessMap.get(refs[0]);
        if (a && a.kind === 'symbol') return this.vars[a.name] ?? 0;
        if (a && a.kind === 'const') return a.value;
      }
      return cluster[wi];
    };
    const setPin = (uid, pin, v) => {
      const wi = clusterOf.get(`${uid}.${pin}`);
      if (wi !== undefined && cluster[wi] === undefined) cluster[wi] = v;
    };
    const writeVar = (uid) => {
      const wi = clusterOf.get(`${uid}.operand`);
      if (wi === undefined) return;
      // operand 簇上的 IdentCon access 即目标变量
      const refs = wireDataRefs[wi] ?? [];
      for (const ref of refs) {
        const nm = this._accessName(accessMap, ref);
        if (nm) return nm;
      }
      // 找不到 IdentCon 时用簇已注入值推断（写变量名=注入源名）
      return null;
    };

    let done = new Set();
    for (let iter = 0; iter < 64; iter++) {
      let progressed = false;
      for (const p of n.parts) {
        if (done.has(p.uid)) continue;
        const name = p.name.toUpperCase();
        const negOp = p.negated.includes('operand') || p.negated.includes('in1');

        if (name === 'CONTACT') {
          const inV = pinVal(p.uid, 'in');
          const opV = readPin(p.uid, 'operand');
          if (inV === undefined || opV === undefined) continue;
          const cond = opV === true;
          const out = inV && (negOp ? !cond : cond);
          setPin(p.uid, 'out', out);
          done.add(p.uid); progressed = true;
        } else if (name === 'PCONTACT' || name === 'NCONTACT') {
          const inV = pinVal(p.uid, 'pre');
          const opV = readPin(p.uid, 'operand');
          const bitNm = this._accessName(accessMap, [...(wireDataRefs[clusterOf.get(`${p.uid}.bit`)] ?? [])][0]) ?? `_edge${n.netIdx}:${p.uid}`;
          if (inV === undefined || opV === undefined) continue;
          const fire = name === 'PCONTACT' ? this.edge.rising(bitNm, opV) : this.edge.falling(bitNm, opV);
          setPin(p.uid, 'out', inV && fire);
          done.add(p.uid); progressed = true;
        } else if (name === 'NOT') {
          // S7 的 Not 盒: in 流取反 → out 流。negated 的 Not 极罕见, 当前按 TIA 导出形态处理。
          const inV = pinVal(p.uid, 'in');
          if (inV === undefined) continue;
          setPin(p.uid, 'out', !inV);
          done.add(p.uid); progressed = true;
        } else if (name === 'COIL' || name === 'SCOIL' || name === 'RCOIL') {
          const inV = pinVal(p.uid, 'in');
          if (inV === undefined) continue;
          // 取反线圈 -(/#x)-: 信号取反后再写/触发(S/R 线圈取反极罕见, 按同一取反语义)
          const sig = (inV === true) !== p.negated.includes('operand');
          const tgt = writeVar(p.uid);
          if (tgt) {
            if (name === 'COIL') this.vars[tgt] = sig;
            else if (name === 'SCOIL' && sig) this.vars[tgt] = true;
            else if (name === 'RCOIL' && sig) this.vars[tgt] = false;
          }
          done.add(p.uid); progressed = true;
        } else if (name === 'TON' || name === 'TOF' || name === 'TP') {
          const inV = pinVal(p.uid, 'IN');
          const ptV = readPin(p.uid, 'PT');
          if (inV === undefined || ptV === undefined) continue;
          if (typeof ptV !== 'number') {
            this._warnOnce(`pt:${n.netIdx}:${p.uid}`, `[warn] 网络"${n.title}" ${name}(${p.uid}) 的 PT 值 "${ptV}" 未解析为毫秒数, 定时器不会动作`);
          }
          const key = `${name.toLowerCase()}:${n.netIdx}:${p.instance || 'anon'}:${p.uid}`;
          if (!this.timers.has(key)) this.timers.set(key, new TimerInst(ptV, name.toLowerCase()));
          const t = this.timers.get(key);
          if (t.pt !== ptV) t.pt = ptV;
          t.in = inV === true;
          t.update(this.dt);
          // TIA timer outputs are data connections to IdentCon variables;
          // write Q/ET back to those variables as well as the flow cluster.
          for (const [pin, value] of [['Q', t.q], ['ET', t.et]]) {
            const wi = clusterOf.get(`${p.uid}.${pin}`);
            if (wi !== undefined) {
              for (const ref of wireDataRefs[wi] ?? []) {
                const nm = this._accessName(accessMap, ref);
                if (nm) { this._writeVar(nm, value); break; }
              }
              cluster[wi] = value;
            }
          }
          setPin(p.uid, 'Q', t.q);
          setPin(p.uid, 'ET', t.et);
          done.add(p.uid); progressed = true;
        } else if (name === 'RS' || name === 'SR') {
          // RS(复位优先)/SR(置位优先)触发器: S/R 数据输入, Q 锁存输出写变量
          const sV = readPin(p.uid, name === 'RS' ? 'S' : 'S1');
          const rV = readPin(p.uid, name === 'RS' ? 'R1' : 'R');
          const s = sV === true, r = rV === true;
          if (sV === undefined || rV === undefined) continue;
          const key = `ff:${n.netIdx}:${p.uid}`;
          let q = this._ffState?.get(key) === true;
          if (name === 'RS') { if (r) q = false; else if (s) q = true; }
          else { if (s) q = true; else if (r) q = false; }
          (this._ffState ??= new Map()).set(key, q);
          const wi = clusterOf.get(`${p.uid}.Q`);
          if (wi !== undefined) {
            for (const ref of wireDataRefs[wi] ?? []) {
              const nm = this._accessName(accessMap, ref);
              if (nm) { this.vars[nm] = q; break; }
            }
            cluster[wi] = q; // 盒对盒直连数据线
          }
          done.add(p.uid); progressed = true;
        } else if (name === 'CTU' || name === 'CTD') {
          // 计数器: CU/CD 上升沿计数, R/LD 复位/装载, CV 写变量, Q 达到阈值输出
          const trigPin = name === 'CTU' ? 'CU' : 'CD';
          const trigV = readPin(p.uid, trigPin);
          const rV = readPin(p.uid, name === 'CTU' ? 'R' : 'LD');
          const pvV = readPin(p.uid, 'PV') ?? 0;
          if (trigV === undefined || rV === undefined) continue;
          const key = `ct:${n.netIdx}:${p.instance || 'anon'}:${p.uid}`;
          const st = (this._ctState ??= new Map()).get(key) ?? { cv: 0, prev: false };
          const rising = trigV === true && !st.prev;
          st.prev = trigV === true;
          if (name === 'CTU') {
            // S7 IEC 计数器 CV 是 Int(-32768..32767), 达上限停止计数(S5 计数器的 999 是 BCD 上限, 不适用于 IEC CTU)
            if (rV === true) st.cv = 0;
            else if (rising) st.cv = Math.min(st.cv + 1, 32767);
          } else {
            if (rV === true) st.cv = pvV;
            else if (rising) st.cv = Math.max(st.cv - 1, -32768);
          }
          this._ctState.set(key, st);
          const cvw = clusterOf.get(`${p.uid}.CV`);
          if (cvw !== undefined) {
            for (const ref of wireDataRefs[cvw] ?? []) {
              const nm = this._accessName(accessMap, ref);
              if (nm) { this._writeVar(nm, st.cv); break; }
            }
            cluster[cvw] = st.cv; // 盒对盒直连数据线
          }
          setPin(p.uid, 'Q', name === 'CTU' ? st.cv >= pvV : st.cv <= 0);
          done.add(p.uid); progressed = true;
        } else if (name === 'SD') {
          // S5 接通延时定时器盒(与 SdCoil 线圈形态不同): s=使能流, tv=时间值, r=复位, timer=背景名, q=流输出
          const sV = pinVal(p.uid, 's');
          const rV = readPin(p.uid, 'r');
          const tvV = readPin(p.uid, 'tv') ?? 0;
          if (sV === undefined) continue;
          if (typeof tvV !== 'number') {
            this._warnOnce(`sdtv:${n.netIdx}:${p.uid}`, `[warn] 网络"${n.title}" Sd(${p.uid}) 的 tv "${tvV}" 未解析为毫秒数`);
          }
          const tw = clusterOf.get(`${p.uid}.timer`);
          let timerNm = null;
          if (tw !== undefined) {
            for (const ref of wireDataRefs[tw] ?? []) {
              const nm = this._accessName(accessMap, ref);
              if (nm) { timerNm = nm; break; }
            }
          }
          const key = `s5box:${n.netIdx}:${timerNm ?? p.uid}`;
          let t = this.timers.get(key);
          if (!t) { t = new TimerInst(tvV, 'ton'); this.timers.set(key, t); }
          if (t.pt !== tvV) t.pt = tvV;
          if (rV === true) { t.in = false; t.elapsed = 0; t.et = 0; t.q = false; }
          else { t.in = sV === true; t.update(this.dt); }
          setPin(p.uid, 'q', t.q);
          setPin(p.uid, 'rt', t.et);
          done.add(p.uid); progressed = true;
        } else if (name === 'PBOX' || name === 'NBOX') {
          // 边沿检测盒: in 流上升沿(下降沿) → out 脉冲; bit=存储位(存上一扫描的流值)
          const inV = pinVal(p.uid, 'in');
          if (inV === undefined) continue;
          const bw = clusterOf.get(`${p.uid}.bit`);
          let bitNm = null;
          if (bw !== undefined) {
            for (const ref of wireDataRefs[bw] ?? []) {
              const nm = this._accessName(accessMap, ref);
              if (nm) { bitNm = nm; break; }
            }
          }
          const key = bitNm ?? `_boxedge${n.netIdx}:${p.uid}`;
          const fire = name === 'PBOX' ? this.edge.rising(key, inV) : this.edge.falling(key, inV);
          setPin(p.uid, 'out', fire);
          done.add(p.uid); progressed = true;
        } else if (name === 'CU' || name === 'CD') {
          // S5 计数器盒: cu/cd 计数输入(流上升沿), r 复位, s 置位, pv 预置, counter 背景, cv 写变量(BCD 0-999)
          const trigV = pinVal(p.uid, name === 'CU' ? 'cu' : 'cd');
          const rV = readPin(p.uid, 'r');
          if (trigV === undefined) continue;
          const cw = clusterOf.get(`${p.uid}.counter`);
          let cNm = null;
          if (cw !== undefined) {
            for (const ref of wireDataRefs[cw] ?? []) {
              const nm = this._accessName(accessMap, ref);
              if (nm) { cNm = nm; break; }
            }
          }
          const key = `s5ct:${n.netIdx}:${cNm ?? p.uid}`;
          const st = (this._ctState ??= new Map()).get(key) ?? { cv: 0, prev: false };
          const rising = trigV === true && !st.prev;
          st.prev = trigV === true;
          if (rV === true) st.cv = 0;
          else if (rising) st.cv = name === 'CU' ? Math.min(st.cv + 1, 999) : Math.max(st.cv - 1, 0);
          this._ctState.set(key, st);
          const cvw = clusterOf.get(`${p.uid}.cv`);
          if (cvw !== undefined) {
            for (const ref of wireDataRefs[cvw] ?? []) {
              const nm = this._accessName(accessMap, ref);
              if (nm) { this._writeVar(nm, st.cv); break; }
            }
            cluster[cvw] = st.cv;
          }
          done.add(p.uid); progressed = true;
        } else if (name === 'CUCOIL') {
          // CU 计数线圈: in 上升沿 → operand 变量自增(BCD 0-999, S5 计数器语义)
          const inV = pinVal(p.uid, 'in');
          if (inV === undefined) continue;
          const ow = clusterOf.get(`${p.uid}.operand`);
          let nm = null;
          if (ow !== undefined) {
            for (const ref of wireDataRefs[ow] ?? []) {
              const tgt = this._accessName(accessMap, ref);
              if (tgt) { nm = tgt; break; }
            }
          }
          const key = `cucoil:${n.netIdx}:${p.uid}`;
          const st = (this._ctState ??= new Map()).get(key) ?? { cv: 0, prev: false };
          const rising = inV === true && !st.prev;
          st.prev = inV === true;
          if (rising) st.cv = Math.min((st.cv || 0) + 1, 999);
          this._ctState.set(key, st);
          if (nm) { this._writeVar(nm, st.cv); }
          done.add(p.uid); progressed = true;
        } else if (name === 'INC') {
          // INC 自增盒: en=1 → operand 变量 +1(每扫描执行)
          const enV = pinVal(p.uid, 'en');
          if (enV === undefined) continue;
          if (enV === true) {
            const ow = clusterOf.get(`${p.uid}.operand`);
            if (ow !== undefined) {
              for (const ref of wireDataRefs[ow] ?? []) {
                const nm = this._accessName(accessMap, ref);
                if (nm) { this._writeVar(nm, (this.vars[nm] ?? 0) + 1); break; }
              }
            }
          }
          done.add(p.uid); progressed = true;
        } else if (name === 'SE') {
          // S5 扩展脉冲定时器盒(同 Sd 盒结构, 语义=TP 脉冲: s 上升沿启动, q 脉冲 tv 时长, 与 s 无关)
          const sV = pinVal(p.uid, 's');
          const rV = readPin(p.uid, 'r');
          const tvV = readPin(p.uid, 'tv') ?? 0;
          if (sV === undefined) continue;
          if (typeof tvV !== 'number') {
            this._warnOnce(`setv:${n.netIdx}:${p.uid}`, `[warn] 网络"${n.title}" Se(${p.uid}) 的 tv "${tvV}" 未解析为毫秒数`);
          }
          const tw = clusterOf.get(`${p.uid}.timer`);
          let timerNm = null;
          if (tw !== undefined) {
            for (const ref of wireDataRefs[tw] ?? []) {
              const nm = this._accessName(accessMap, ref);
              if (nm) { timerNm = nm; break; }
            }
          }
          const key = `s5box:${n.netIdx}:${timerNm ?? p.uid}`;
          let t = this.timers.get(key);
          if (!t) { t = new TimerInst(tvV, 'tp'); this.timers.set(key, t); }
          if (t.pt !== tvV) t.pt = tvV;
          if (rV === true) { t.in = false; t.running = false; t.q = false; t.elapsed = 0; t.et = 0; }
          else { t.in = sV === true; t.update(this.dt); }
          setPin(p.uid, 'q', t.q);
          setPin(p.uid, 'rt', t.et);
          done.add(p.uid); progressed = true;
        } else if (name === 'O') {
          // O 门（并联汇合）: in1..inN 任一为真 → out
          let card = 0;
          for (const key of clusterOf.keys()) {
            const dot = key.indexOf('.');
            if (dot < 0) continue;
            const puid = key.slice(0, dot);
            const pin = key.slice(dot + 1);
            const m = /^in(\d+)$/.exec(pin);
            if (puid === String(p.uid) && m) card = Math.max(card, parseInt(m[1], 10));
          }
          if (card === 0) { done.add(p.uid); continue; }
          let any = false; let ready = true;
          for (let i = 1; i <= card; i++) {
            let v = pinVal(p.uid, `in${i}`);
            if (v === undefined) { ready = false; break; }
            if (p.negated.includes(`in${i}`)) v = !(v === true); // 并联支路取反
            if (v === true) any = true;
          }
          if (!ready) continue;
          setPin(p.uid, 'out', any);
          done.add(p.uid); progressed = true;
        } else if (['EQ', 'NE', 'GT', 'LT', 'GE', 'LE'].includes(name)) {
          const inV = pinVal(p.uid, 'pre');
          const a = readPin(p.uid, 'in1');
          const b = readPin(p.uid, 'in2');
          if (inV === undefined || a === undefined || b === undefined) continue;
          const av = typeof a === 'number' ? a : parseConst(a);
          const bv = typeof b === 'number' ? b : parseConst(b);
          const aNum = typeof av === 'number' ? av : Number(av);
          const bNum = typeof bv === 'number' ? bv : Number(bv);
          if (typeof av === 'string' || typeof bv === 'string') {
            this._warnOnce(`cmpstr:${n.netIdx}:${p.uid}`, `[warn] 网络"${n.title}" ${name}(${p.uid}) 比较输入含非数值字面量(${JSON.stringify(typeof av === 'string' ? av : bv)}), 恒按不等处理`);
          }
          let r = false;
          switch (name) {
            case 'EQ': r = aNum === bNum; break;
            case 'NE': r = aNum !== bNum; break;
            case 'GT': r = aNum > bNum; break;
            case 'LT': r = aNum < bNum; break;
            case 'GE': r = aNum >= bNum; break;
            case 'LE': r = aNum <= bNum; break;
          }
          const result = inV === true && r;
          // Comparison boxes expose a data output (OUT) to an IdentCon and a
          // separate flow output (out) to the next ladder element.
          const dataOutWi = clusterOf.get(`${p.uid}.OUT`);
          if (dataOutWi !== undefined) {
            for (const ref of wireDataRefs[dataOutWi] ?? []) {
              const nm = this._accessName(accessMap, ref);
              if (nm) { this._writeVar(nm, result); break; }
            }
            cluster[dataOutWi] = result;
          }
          const outWi = clusterOf.get(`${p.uid}.out`);
          if (outWi !== undefined) {
            for (const ref of wireDataRefs[outWi] ?? []) {
              const nm = this._accessName(accessMap, ref);
              if (nm) { this._writeVar(nm, result); break; }
            }
            cluster[outWi] = result;
          }
          setPin(p.uid, 'out', result);
          done.add(p.uid); progressed = true;
        } else if (['ADD', 'SUB', 'MUL', 'DIV'].includes(name)) {
          // 算术指令: en/in1/in2 → out(数据输出, 写变量), eno(除零时=0)。TIA 整数除法取整。
          const enV = pinVal(p.uid, 'en');
          const a = readPin(p.uid, 'in1') ?? 0;
          const b = readPin(p.uid, 'in2') ?? 0;
          if (enV === undefined) continue;
          if (typeof a === 'string' || typeof b === 'string') {
            this._warnOnce(`arithstr:${n.netIdx}:${p.uid}`, `[warn] 网络"${n.title}" ${name}(${p.uid}) 算术输入含非数值字面量(${JSON.stringify(typeof a === 'string' ? a : b)}), 按字面量比较规则结果将失真`);
          }
          const divZero = name === 'DIV' && b === 0;
          let r = 0;
          switch (name) {
            case 'ADD': r = a + b; break;
            case 'SUB': r = a - b; break;
            case 'MUL': r = a * b; break;
            case 'DIV': r = divZero ? 0 : Math.trunc(a / b); break;
          }
          const ok = enV === true && !divZero;
          if (ok) {
            // fix#17: 盒对盒直连数据线——out 是数据输出, 除写变量外还强制写簇,
            // 供下游比较/算术盒的 in1/in2 直接读取(无 IdentCon 中转的 NameCon 数据线)
            const wi = clusterOf.get(`${p.uid}.out`);
            if (wi !== undefined) {
              for (const ref of wireDataRefs[wi] ?? []) {
                const nm = this._accessName(accessMap, ref);
                if (nm) { this._writeVar(nm, r); break; }
              }
              cluster[wi] = r; // 覆盖注入阶段的旧值, 下游同网络读到新值
            }
          }
          setPin(p.uid, 'eno', ok);
          done.add(p.uid); progressed = true;
        } else if (['SDCOIL', 'SD', 'COILSD'].includes(name)) {
          // S5 风格接通延时线圈: in 持续 value 时间后 operand 置位, in 断开立即复位
          const inV = pinVal(p.uid, 'in');
          const valV = readPin(p.uid, 'value') ?? 0;
          if (inV === undefined) continue;
          if (typeof valV !== 'number') {
            this._warnOnce(`sd:${n.netIdx}:${p.uid}`, `[warn] 网络"${n.title}" SdCoil(${p.uid}) 的 value "${valV}" 未解析为毫秒数, 延时线圈不会动作`);
          }
          const key = `sdcoil:${n.netIdx}:${p.uid}`;
          if (!this.timers.has(key)) this.timers.set(key, new TimerInst(valV));
          const t = this.timers.get(key);
          if (t.pt !== valV) t.pt = valV;
          t.in = inV === true;
          t.update(this.dt);
          const wi = clusterOf.get(`${p.uid}.operand`);
          if (wi !== undefined) {
            for (const ref of wireDataRefs[wi] ?? []) {
              const nm = this._accessName(accessMap, ref);
              if (nm) { this.vars[nm] = t.q; break; }
            }
            cluster[wi] = t.q; // 盒对盒直连: 供下游数据线读取
          }
          done.add(p.uid); progressed = true;
        } else if (name === 'MOVE' || name === 'MOV') {
          const enV = pinVal(p.uid, 'en');
          const inV = readPin(p.uid, 'in') ?? 0;  // IN 悬空按 0 处理, 避免卡死整条链
          if (enV === undefined) continue;
          if (enV === true) {
            // TIA MOVE exports its destination pin as OUT; older simulator
            // JSON used out1. Accept both spellings for the same data output.
            const wi = clusterOf.get(`${p.uid}.OUT`) ?? clusterOf.get(`${p.uid}.out1`);
            if (wi !== undefined) {
              for (const ref of wireDataRefs[wi] ?? []) {
                const nm = this._accessName(accessMap, ref);
                if (nm) { this._writeVar(nm, inV); break; }
              }
              cluster[wi] = inV;
            }
          }
          setPin(p.uid, 'eno', enV);
          done.add(p.uid); progressed = true;
        } else if (name === 'RD_SYS_T') {
          // SFB1 读系统时间: en=1 → RET_VAL=0, OUT=模拟时钟节拍(每扫描 en=1 时 +1, 近似秒计数)
          const enV = pinVal(p.uid, 'en');
          if (enV === undefined) continue;
          const key = `rdsyst:${n.netIdx}:${p.uid}`;
          let tick = (this._rdtState ??= new Map()).get(key) ?? 0;
          if (enV === true) tick = (tick + 1) & 0x7fffffff;
          (this._rdtState ??= new Map()).set(key, tick);
          const applyPin = (pin, val) => {
            const wi = clusterOf.get(`${p.uid}.${pin}`);
            if (wi !== undefined) {
              for (const ref of wireDataRefs[wi] ?? []) {
                const nm = this._accessName(accessMap, ref);
                if (nm) { this._writeVar(nm, val); break; }
              }
              cluster[wi] = val;
            }
          };
          applyPin('RET_VAL', 0);
          applyPin('OUT', tick);
          done.add(p.uid); progressed = true;
        } else if (name === 'BLKMOV') {
          // SFC20 块移动: en=1 → SRCBLK 值标量拷贝到 DSTBLK(字节级区拷贝近似), RET_VAL=0
          const enV = pinVal(p.uid, 'en');
          if (enV === undefined) continue;
          const readRef = (pin) => {
            const wi = clusterOf.get(`${p.uid}.${pin}`);
            if (wi === undefined) return undefined;
            for (const ref of wireDataRefs[wi] ?? []) {
              const nm = this._accessName(accessMap, ref);
              if (nm) return this.vars[nm];
            }
            return cluster[wi];
          };
          const writeRef = (pin, val) => {
            const wi = clusterOf.get(`${p.uid}.${pin}`);
            if (wi !== undefined) {
              for (const ref of wireDataRefs[wi] ?? []) {
                const nm = this._accessName(accessMap, ref);
                if (nm) { this._writeVar(nm, val); break; }
              }
              cluster[wi] = val;
            }
          };
          this._warnOnce(`blkmov:${n.netIdx}:${p.uid}`, `[approx] BLKMOV 按标量拷贝近似(字节级区拷贝未模拟), 网络"${n.title}"`);
          if (enV === true) {
            const src = readRef('SRCBLK');
            if (src !== undefined) writeRef('DSTBLK', src);
          }
          writeRef('RET_VAL', 0);
          done.add(p.uid); progressed = true;
        } else if (name === 'PUT') {
          // SFB15 发送通信: 无对端站, 本地数据不动; DONE=REQ 且 en 直通, ERROR=0, STATUS=0
          const enV = pinVal(p.uid, 'en');
          if (enV === undefined) continue;
          const reqV = readPin(p.uid, 'REQ');
          if (reqV === undefined) continue;
          this._warnOnce(`put:${n.netIdx}:${p.uid}`, `[approx] PUT 通信未模拟(无对端站), DONE 直通近似, 网络"${n.title}"`);
          const applyPin = (pin, val) => {
            const wi = clusterOf.get(`${p.uid}.${pin}`);
            if (wi !== undefined) {
              for (const ref of wireDataRefs[wi] ?? []) {
                const nm = this._accessName(accessMap, ref);
                if (nm) { this._writeVar(nm, val); break; }
              }
              cluster[wi] = val;
            }
          };
          applyPin('DONE', enV === true && reqV === true);
          applyPin('ERROR', false);
          applyPin('STATUS', 0);
          done.add(p.uid); progressed = true;
        } else if (name === 'GET') {
          // SFB14 接收通信: 无对端站, RD 区保持原值; NDR=en 直通, ERROR=0, STATUS=0
          const enV = pinVal(p.uid, 'en');
          if (enV === undefined) continue;
          const reqV = readPin(p.uid, 'REQ');
          if (reqV === undefined) continue;
          this._warnOnce(`get:${n.netIdx}:${p.uid}`, `[approx] GET 通信未模拟(无对端站), NDR 直通近似, 网络"${n.title}"`);
          const applyPin = (pin, val) => {
            const wi = clusterOf.get(`${p.uid}.${pin}`);
            if (wi !== undefined) {
              for (const ref of wireDataRefs[wi] ?? []) {
                const nm = this._accessName(accessMap, ref);
                if (nm) { this._writeVar(nm, val); break; }
              }
              cluster[wi] = val;
            }
          };
          applyPin('NDR', enV === true && reqV === true);
          applyPin('ERROR', false);
          applyPin('STATUS', 0);
          done.add(p.uid); progressed = true;
        } else {
          // 未知元件: 记录但不阻塞
          if (!this._warned?.has(name)) {
            (this._warned ??= new Set()).add(name);
            this._log.push(`[warn] 未实现元件: ${name}（网络 ${n.title}）`);
          }
          done.add(p.uid); progressed = true;
        }
      }
      // FB/FC 调用: 不内联执行, 按 eno=en 直通近似(下游链不断), 每实例告警一次
      for (const c of n.calls ?? []) {
        const ck = `call:${c.uid}`;
        if (done.has(ck)) continue;
        const wi = clusterOf.get(`${c.uid}.en`);
        const enV = cluster[wi];
        if (enV === undefined) continue;
        this._warnOnce(`call:${n.netIdx}:${c.uid}`, `[warn] 网络"${n.title}" 调用 ${c.calleeName} 未内联执行, 下游按 eno=en 直通近似`);
        const ewi = clusterOf.get(`${c.uid}.eno`);
        if (ewi !== undefined && cluster[ewi] === undefined) cluster[ewi] = enV;
        done.add(ck); progressed = true;
      }
      if (!progressed) break;
    }
    // 4) 未求值 part 诊断（计数暴露给 Checker, 纳入断言防假 PASS）
    for (const p of n.parts) {
      if (!done.has(p.uid)) {
        this.unresolvedParts++; // 计数仍逐周期累计(供断言), 仅日志去重
        this._warnOnce(`unres:${n.netIdx}:${p.uid}`, `[warn] 网络"${n.title}" part ${p.name}(${p.uid}) 未求值(输入悬空?)`);
      }
    }
  }

  // 周期性告警按 key 去重, 避免逐扫描刷屏
  _warnOnce(key, msg) {
    (this._warned ??= new Set());
    if (this._warned.has(key)) return;
    this._warned.add(key);
    this._log.push(msg);
  }

  // 推进一个扫描周期; inputs: {name: value} 在本周期求值前写入
  step(inputs = {}) {
    for (const [k, v] of Object.entries(inputs)) this.vars[k] = v;
    for (const n of this.networks) this._evalNetwork(n);
    this.cycle++;
    if (this._traceEnabled) this._snap(); // step/advance 统一在此快照
  }

  // 推进 n 周期; 事件表: [{at: 周期偏移, set: {name: value}}]（at 越界事件被忽略）
  advance(n, events = []) {
    const evByAt = new Map();
    for (const e of events) {
      if (!Number.isInteger(e.at) || e.at < 0 || e.at >= n) continue;
      evByAt.set(e.at, { ...(evByAt.get(e.at) ?? {}), ...e.set });
    }
    for (let i = 0; i < n; i++) {
      this.step(evByAt.get(i) ?? {});
    }
  }

  enableTrace(keys = null) { this._traceEnabled = true; this.trace = []; this._traceKeys = keys; }
  _snap() {
    // 压缩: 仅记录变化点
    const keys = this._traceKeys ?? Object.keys(this.vars);
    const row = { c: this.cycle, d: {} };
    const prev = this.trace.length ? this.trace[this.trace.length - 1].d : null;
    for (const k of keys) {
      const v = this.vars[k];
      if (!prev || prev[k] !== v) row.d[k] = v;
    }
    if (Object.keys(row.d).length) this.trace.push(row);
  }

  logs() { return this._log; }
}

// ── 断言辅助 ──
export class Checker {
  constructor(sim) { this.sim = sim; this.results = []; }
  expect(cond, name, detail = '') {
    this.results.push({ name, pass: !!cond, detail });
    if (!cond) console.log(`  ✗ FAIL: ${name}${detail ? '  [' + detail + ']' : ''}`);
  }
  eq(name, expected, label) {
    const actual = this.sim.get(name);
    const pass = Object.is(actual, expected);
    this.expect(pass, `${label ?? name} == ${expected}`, `实际=${JSON.stringify(actual)}`);
  }
  bool(name, expected, label) { this.eq(name, expected === true, label); }
  // 断言无未求值 part（输入悬空会掩盖假 PASS, 场景结尾必须调用）
  assertNoUnresolved() {
    const n = this.sim.unresolvedParts;
    this.expect(n === 0, '无未求值元件', `累计未求值=${n}`);
  }
  summary() {
    const pass = this.results.filter((r) => r.pass).length;
    const fail = this.results.length - pass;
    console.log(`\n══════ 断言汇总: ${this.results.length} 项, PASS ${pass}, FAIL ${fail} ══════`);
    for (const r of this.results) if (!r.pass) console.log(`  ✗ ${r.name}  ${r.detail}`);
    return fail === 0;
  }
}

// ── 主入口: node lad-sim.mjs <网络json> <变量json> <场景mjs> ──
export async function main() {
  const [, , netsFile, varsFile, sceneFile] = process.argv;
  if (!netsFile || !varsFile || !sceneFile) {
    console.log('用法: node lad-sim.mjs <网络json> <变量json> <场景mjs>');
    return 1;
  }
  const nets = JSON.parse(fs.readFileSync(netsFile, 'utf8').replace(/^\uFEFF/, ''));
  const varsDecl = JSON.parse(fs.readFileSync(varsFile, 'utf8').replace(/^\uFEFF/, ''));
  const sim = new LadSim(nets, varsDecl);
  const scene = await import(pathToFileURL(path.resolve(sceneFile)).href);
  // 约定: scene.run 返回 false(如直接返回 ck.summary()) → 退出码 1, 便于脚本判 FAIL
  const ok = await scene.run(sim, { Checker });
  for (const l of sim.logs()) console.log(l);
  return ok === false ? 1 : 0;
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main().then((c) => process.exit(c)).catch((e) => { console.error(e); process.exit(2); });
}
