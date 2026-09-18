#!/usr/bin/env python3
"""V4.4.4 HMI-controls regression checks.

Runs without TIA/.NET. It validates release configuration and source invariants and,
when sample folders are supplied, derives XML corpus statistics dynamically.
"""
from __future__ import annotations
import argparse
import json
import re
import sys
from collections import Counter
from pathlib import Path
import xml.etree.ElementTree as ET

CORE_TOOLS = {
    "get_xml_ir_capabilities", "compile_lad_ir", "validate_lad_ir",
    "get_lad_reference_profile", "apply_lad_ir", "compile_hmi_ir",
    "validate_hmi_ir", "apply_hmi_ir", "get_hmi_ir_capabilities",
}


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8-sig", errors="strict")


def strip_csharp(text: str) -> tuple[str, str]:
    out: list[str] = []
    i, state = 0, "code"
    while i < len(text):
        c = text[i]
        d = text[i + 1] if i + 1 < len(text) else ""
        if state == "code":
            if c == "/" and d == "/": state = "line"; out += [" ", " "]; i += 2; continue
            if c == "/" and d == "*": state = "block"; out += [" ", " "]; i += 2; continue
            if c in "$@" and d in "$@" and i + 2 < len(text) and text[i + 2] == '"':
                state = "vstr" if "@" in (c + d) else "str"; out += [" "] * 3; i += 3; continue
            if c == "@" and d == '"': state = "vstr"; out += [" ", " "]; i += 2; continue
            if c == "$" and d == '"': state = "str"; out += [" ", " "]; i += 2; continue
            if c == '"': state = "str"; out.append(" "); i += 1; continue
            if c == "'": state = "char"; out.append(" "); i += 1; continue
            out.append(c); i += 1; continue
        if state == "line":
            out.append("\n" if c == "\n" else " ")
            if c == "\n": state = "code"
            i += 1; continue
        if state == "block":
            if c == "*" and d == "/": state = "code"; out += [" ", " "]; i += 2
            else: out.append("\n" if c == "\n" else " "); i += 1
            continue
        if state == "str":
            if c == "\\": out += [" ", " "] if i + 1 < len(text) else [" "]; i += 2
            elif c == '"': state = "code"; out.append(" "); i += 1
            else: out.append("\n" if c == "\n" else " "); i += 1
            continue
        if state == "vstr":
            if c == '"' and d == '"': out += [" ", " "]; i += 2
            elif c == '"': state = "code"; out.append(" "); i += 1
            else: out.append("\n" if c == "\n" else " "); i += 1
            continue
        if state == "char":
            if c == "\\": out += [" ", " "] if i + 1 < len(text) else [" "]; i += 2
            elif c == "'": state = "code"; out.append(" "); i += 1
            else: out.append("\n" if c == "\n" else " "); i += 1
    return "".join(out), state


def xml_local(tag: str) -> str:
    return tag.rsplit("}", 1)[-1]


def scan_xml(folder: Path) -> dict:
    files = [p for p in folder.rglob("*") if p.is_file() and p.suffix.lower() == ".xml"]
    errors: list[str] = []
    part_counts: Counter[str] = Counter()
    hmi_functions: Counter[str] = Counter()
    for path in files:
        try:
            root = ET.parse(path).getroot()
        except Exception as exc:
            errors.append(f"{path}: {exc}")
            continue
        for node in root.iter():
            if xml_local(node.tag) == "Part" and node.get("Name"):
                part_counts[node.get("Name", "")] += 1
            if xml_local(node.tag) == "Hmi.Event.FunctionListEntry":
                for child in node.iter():
                    if xml_local(child.tag) == "Name" and child.text:
                        hmi_functions[child.text] += 1
                        break
    return {
        "files": len(files), "parse_errors": errors,
        "part_counts": dict(part_counts), "hmi_functions": dict(hmi_functions),
    }


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[2])
    ap.add_argument("--plc-samples", type=Path)
    ap.add_argument("--hmi-samples", type=Path)
    ap.add_argument("--json-out", type=Path)
    args = ap.parse_args()
    root = args.root.resolve()
    src = root / "src"
    failures: list[str] = []
    warnings: list[str] = []

    def require(condition: bool, message: str) -> None:
        if not condition: failures.append(message)

    # Release/version consistency.
    project = read(src / "TiaMcpServer.csproj")
    server = read(src / "McpServer.cs")
    require('<Version>4.4.4</Version>' in project, "csproj Version is not 4.4.4")
    require('<InformationalVersion>4.4.4-hmi-controls</InformationalVersion>' in project, "csproj informational version mismatch")
    require('Version = "4.4.4-hmi-controls"' in server, "server version mismatch")
    require(server.count("ValidateCoreToolExposure();") >= 2, "startup/profile-switch core-tool exposure self-check missing")
    require("复合工具 {name} 未接入原子级门禁" in server, "unsafe composite tools are still exposed")

    # Default whitelist must expose the whole versioned facade.
    for cfg_path in (root / "tools_filter.json", src / "tools_filter.json"):
        cfg = json.loads(read(cfg_path))
        exposed = set(cfg.get("tools", [])) if cfg.get("mode", "whitelist").lower() == "whitelist" else CORE_TOOLS
        require(CORE_TOOLS <= exposed, f"{cfg_path.name} hides core tools: {sorted(CORE_TOOLS - exposed)}")

    block = read(src / "Xml" / "BlockXmlBuilder.cs")
    plc = read(src / "Services" / "PlcService.cs")
    flg = read(src / "Xml" / "FlgNetBuilder.cs")
    orch = read(src / "Services" / "XmlOrchestrationService.cs")
    quality = read(src / "Services" / "QualityGuardService.cs")
    hmi = read(src / "Xml" / "HmiScreenXmlBuilder.cs")
    risk = read(src / "Runtime" / "ToolRiskPolicy.cs")

    # Critical source invariants from the audit.
    require("XDocument.Parse" in block and "FilterInterfaceSections" in block, "DOM section filtering missing")
    require("Regex.Match" not in block and "Regex.Matches" not in block, "BlockXmlBuilder still regex-parses XML")
    parse_start = plc.find("private static List<LadVariableDef> ParseInterfaceSectionsXml")
    parse_method = plc[parse_start:parse_start + 4500] if parse_start >= 0 else ""
    require(parse_start >= 0 and "XDocument.Parse" in parse_method and "Regex." not in parse_method, "interface fallback parser still uses regex")
    require("PatchExistingLadBlockXml" in block and "PatchExistingLadBlockXml" in plc, "minimal original-block DOM patch path missing")
    require("ConnectTriggerCondition" in flg and "explicitTrigger" in flg, "explicit TON/CTU trigger semantics not compiled")
    require('name + "_Auto"' not in orch, "fixed shared IEC instance default remains")
    require('"CTU_INT" or "CTD_INT" or "CTUD_INT" => "IEC_COUNTER"' not in block,
            "CTU/CTD/CTUD instance types are still collapsed to IEC_COUNTER")
    catalog = read(src / "Services" / "LadInstructionCatalog.cs")
    shared_validation = read(src / "Services" / "LadProgramValidationService.cs")
    require('case "CTU": return "CTU_"' in catalog and 'case "CTD": return "CTD_"' in catalog,
            "CTU/CTD instruction-specific instance mapping missing")
    require('case "R_TRIG": return "R_TRIG"' in catalog and 'case "F_TRIG": return "F_TRIG"' in catalog,
            "trigger instance datatype mapping missing")
    require("PrepareLadProgram" in shared_validation and "NormalizeBackgroundCall" in shared_validation,
            "shared validate/apply LAD rule set missing")
    require("PortalService.PrepareLadProgram" in orch and "var compiledText = ValidateLadIr(irJson)" in orch,
            "validate_lad_ir/apply_lad_ir do not share the same validation path")
    require('var instanceScope = (step["instanceScope"]' in orch,
            "box instanceScope is referenced before declaration")
    require("AddLadBoxBatch(blockName, new List<LadBoxDef>" in plc
            and "compileAfter: true" in plc[plc.find("public string AddLadBoxBatch"):plc.find("public string GetInstructionDetails")],
            "add_lad_box_batch still has parameter/compileAfter conflict")
    require("IEC 指令 {box.box} 缺少唯一 instance" in flg
            and "IEC 指令 {sc.inst} 缺少唯一 instance" in flg,
            "FlgNetBuilder still guesses a shared IEC instance")
    require("ValidateLadProgramConflicts" in orch or "ValidateProgramConflicts" in shared_validation,
            "cross-network writer/instance validation missing")
    require("LAD_COMPILE_REQUIRED" in orch, "apply_lad_ir can still bypass compile")
    require("CALL_PIN_DATATYPE_REQUIRED" in orch and "CALL_INOUT_UNSUPPORTED" in orch, "safe call-pin contract missing")
    require('string.Equals(call.instanceScope, "global"' in flg, "call instance scope still inferred from name")
    require("InferPinType" not in flg and "必须显式提供 datatype" in flg, "legacy call pins still infer datatype")
    require("中调用 FB 必须使用 instanceScope='global'" in quality, "OB/FC FB-call instance guard missing")
    require('Name = "Screen name"' in hmi and 'Name = "Object number"' in hmi, "ActivateScreen signature is not sample-compatible")
    hmi_action_catalog = read(src / "Services" / "HmiActionCatalog.cs")
    hmi_control_catalog = read(src / "Services" / "HmiControlCatalog.cs")
    hmi_setup = read(src / "Services" / "HmiSetupService.cs")
    require("HmiActionCatalog.Validate(action)" in hmi and "CanonicalFunction" in hmi_action_catalog,
            "HMI builder does not use the shared function allowlist/signature validator")
    require('function == "SetTag"' in hmi_action_catalog and 'Name = "Value"' in hmi_action_catalog,
            "SetTag two-parameter support missing")
    require("CanonicalBehavior" in hmi_action_catalog and "拒绝静默降级" in hmi,
            "unknown HMI behavior is not fail-closed")
    for control_method in ("CreateLine", "CreateGroup", "CreateSymbolicIoField", "CreateGraphicView"):
        require(control_method in hmi, f"HMI builder missing {control_method}")
    require(all(name in hmi_control_catalog for name in ("line", "group", "symboliciofield", "graphicview")),
            "shared HMI control catalog is incomplete")
    require(all(name in hmi for name in ("VisibilityAnimation", "SingleBitVisibilityAnimation", "ObjectEnablingAnimation")),
            "verified HMI animation builders are incomplete")
    require("ValidateHmiScreenSpecJson" in hmi_setup and "BuildHmiScreenItemFromSpec" in hmi_setup,
            "create_hmi_screen_from_spec does not share the formal validation/build path")
    require("GetHmiCapabilities" in orch and 'schemaVersion = "hmi-ir/1.2"' in orch,
            "HMI capability facade/schema version missing")
    require("conditionalSetter" in risk and "UnsafeCompositeNames" in risk, "risk-policy exception fixes missing")
    require("写操作返回值缺少布尔型 success" in server, "write-result fail-closed handling missing")
    require("ProjectPath" in plc[plc.find("LadCacheKey"):plc.find("LadCacheKey") + 500], "LAD cache key lacks project identity")
    for marker, span, label in (
        ("private static string MergeMemberIntoInterfaceSection", 7000, "member merge/replace/remove"),
        ("public string UpdateNetwork", 9000, "network update/delete"),
    ):
        pos = plc.find(marker)
        snippet = plc[pos:pos + span] if pos >= 0 else ""
        require(pos >= 0 and "XDocument.Parse" in snippet and "Regex." not in snippet,
                f"{label} XML mutation still relies on regex or DOM path is missing")
    multi_pos = plc.find("public string AddMultiInstanceMember")
    multi_snippet = plc[multi_pos:multi_pos + 4500] if multi_pos >= 0 else ""
    require(multi_pos >= 0 and "MergeMemberIntoInterfaceSection" in multi_snippet and "Regex." not in multi_snippet,
            "multi-instance member XML mutation does not use the shared DOM helper")
    publish = read(root / "一键发布.ps1")
    build_script = root / "src" / "scripts" / "构建V4.4.4.ps1"
    require("构建V4.4.4.ps1" in publish and build_script.exists(), "one-click publisher references a missing build script")

    # Lightweight C# lexical balance check for every source file.
    for path in src.rglob("*.cs"):
        text = read(path)
        stripped, state = strip_csharp(text)
        require(state == "code", f"{path.relative_to(root)} has unterminated {state}")
        for left, right, label in (("{", "}", "brace"), ("(", ")", "parenthesis"), ("[", "]", "bracket")):
            depth = 0
            for ch in stripped:
                if ch == left: depth += 1
                elif ch == right: depth -= 1
                if depth < 0: break
            require(depth == 0, f"{path.relative_to(root)} unbalanced {label}s")

    # Tool registration duplicates are reported, not hidden.
    registrations: Counter[str] = Counter()
    pattern = re.compile(r'RegisterTool\(\s*"([^"]+)"')
    for path in src.rglob("*.cs"):
        registrations.update(pattern.findall(read(path)))
    duplicates = {k: v for k, v in registrations.items() if v > 1}
    require(not duplicates, "duplicate tool registrations remain: " + json.dumps(duplicates, ensure_ascii=False, sort_keys=True))

    sample_results = {}
    if args.plc_samples and args.plc_samples.exists():
        sample_results["plc"] = scan_xml(args.plc_samples)
        require(not sample_results["plc"]["parse_errors"], "PLC sample XML parse failures found")
    if args.hmi_samples and args.hmi_samples.exists():
        sample_results["hmi"] = scan_xml(args.hmi_samples)
        require(not sample_results["hmi"]["parse_errors"], "HMI sample XML parse failures found")
        funcs = sample_results["hmi"]["hmi_functions"]
        if funcs:
            require(funcs.get("ActivateScreen", 0) > 0, "HMI sample corpus has no ActivateScreen reference")

    result = {
        "version": "4.4.4-hmi-controls",
        "success": not failures,
        "csharp_files": len(list(src.rglob("*.cs"))),
        "tool_registrations": sum(registrations.values()),
        "unique_tool_names": len(registrations),
        "duplicate_tool_names": duplicates,
        "failures": failures,
        "warnings": warnings,
        "samples": sample_results,
    }
    rendered = json.dumps(result, ensure_ascii=False, indent=2)
    print(rendered)
    if args.json_out:
        args.json_out.parent.mkdir(parents=True, exist_ok=True)
        args.json_out.write_text(rendered + "\n", encoding="utf-8")
    return 0 if not failures else 1


if __name__ == "__main__":
    raise SystemExit(main())
