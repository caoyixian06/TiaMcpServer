#!/usr/bin/env python3
"""V4.4.4 HMI control-orchestrator source/sample regression.

Runs without Windows/TIA. It validates that the implementation is driven by the
shared control/action catalogs and that the supported XML structures exist in the
user-provided formal HMI exports. Runtime import/compile remains a separate test.
"""
from __future__ import annotations
import argparse, json, re
from collections import Counter
from datetime import datetime, timezone
from pathlib import Path
import xml.etree.ElementTree as ET


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8-sig")


def local(tag: str) -> str:
    return tag.rsplit("}", 1)[-1]


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[2])
    ap.add_argument("--samples", type=Path, required=True)
    ap.add_argument("--json-out", type=Path, default=Path("V4.4.4_HMI控件专项回归.json"))
    args = ap.parse_args()
    root, samples = args.root.resolve(), args.samples.resolve()
    src = root / "src"

    builder = read(src / "Xml" / "HmiScreenXmlBuilder.cs")
    setup = read(src / "Services" / "HmiSetupService.cs")
    quality = read(src / "Services" / "QualityGuardService.cs")
    orchestration = read(src / "Services" / "XmlOrchestrationService.cs")
    controls = read(src / "Services" / "HmiControlCatalog.cs")
    actions = read(src / "Services" / "HmiActionCatalog.cs")
    tools = read(src / "Tools" / "XmlOrchestrationTools.cs")
    filter_cfg = json.loads(read(src / "tools_filter.json"))

    xml_files = sorted(p for p in samples.rglob("*.xml") if p.is_file())
    parse_errors: list[str] = []
    counts: Counter[str] = Counter()
    screen_files = 0
    for path in xml_files:
        try:
            tree = ET.parse(path)
        except Exception as exc:
            parse_errors.append(f"{path}: {exc}")
            continue
        has_screen = False
        for node in tree.getroot().iter():
            name = local(node.tag)
            if name.startswith("Hmi.Screen.") or name.startswith("Hmi.Dynamic."):
                counts[name] += 1
            if name == "Hmi.Screen.Screen":
                has_screen = True
        screen_files += int(has_screen)

    tests: list[dict[str, object]] = []
    def check(name: str, passed: bool, evidence: str) -> None:
        tests.append({"name": name, "passed": bool(passed), "evidence": evidence})

    expected_samples = {
        "Hmi.Screen.Line": 1555,
        "Hmi.Screen.Group": 610,
        "Hmi.Screen.SymbolicIOField": 168,
        "Hmi.Screen.GraphicView": 77,
        "Hmi.Dynamic.VisibilityAnimation": 114,
        "Hmi.Dynamic.SingleBitVisibilityAnimation": 76,
        "Hmi.Dynamic.ObjectEnablingAnimation": 6,
    }
    check("all uploaded XML samples parse", not parse_errors and len(xml_files) == 41,
          f"parsed={len(xml_files) - len(parse_errors)}/{len(xml_files)}, screen_documents={screen_files}")
    for name, expected in expected_samples.items():
        check(f"formal sample coverage: {name}", counts.get(name, 0) == expected,
              f"observed {counts.get(name, 0)} nodes in the uploaded corpus")

    methods = {
        "line": "CreateLine",
        "group": "CreateGroup",
        "symboliciofield": "CreateSymbolicIoField",
        "graphicview": "CreateGraphicView",
    }
    for control, method in methods.items():
        routed = (f'case "{control}"' in setup) if control != "group" else ("groupSpecs.Add(item)" in setup and "builder.CreateGroup" in setup)
        check(f"builder supports {control}", method in builder and routed,
              f"{method} exists and CreateHmiScreenFromSpec routes the canonical type to it")

    for animation in ("CreateVisibilityAnimation", "CreateSingleBitVisibilityAnimation", "CreateObjectEnablingAnimation"):
        check(f"builder supports {animation}", animation in builder and "ApplyAnimations" in setup,
              "animation XML builder exists and the create path applies normalized animations")

    for control in methods:
        check(f"shared catalog exposes {control}", f'"{control}"' in controls,
              "compile/validate/apply/create use HmiControlCatalog instead of independent allowlists")

    check("validate/create share the screen-spec rule set",
          "ValidateHmiScreenSpecJson(spec.ToString" in setup and "normalizedItems" in setup,
          "CreateHmiScreenFromSpec validates first and builds only normalized items")
    check("validate_hmi_ir delegates to the same rule set",
          "ValidateHmiScreenSpecJson" in orchestration and "ValidateHmiIr" in orchestration,
          "IR validation converts to formal screen spec and calls PortalService.ValidateHmiScreenSpecJson")
    check("button aliases normalize before XML generation",
          "CanonicalBehavior" in actions and "HmiActionCatalog.CanonicalBehavior" in setup,
          "momentary/set/reset/toggle/navigate aliases cannot pass validation then fail in the builder")
    check("event functions use one signature catalog",
          "HmiActionCatalog.Validate(action)" in builder and "HmiActionCatalog.Validate" in quality,
          "builder and validator share trigger/function/parameter rules")
    check("layers and groups use two-stage assembly",
          "SortedDictionary<int, List<XmlElement>>" in setup and "groupSpecs" in setup and "layerItems" in builder,
          "normal controls are placed by layer before groups consume named members")
    check("dependencies are reported before import",
          all(x in quality for x in ("tagDependencies", "pictureDependencies", "textListDependencies", "screenDependencies")),
          "validation returns tags/pictures/text-lists/screens required by the specification")
    check("opaque controls remain fail-closed",
          "SymbolLibrary" in orchestration and "opaque OcxState" in orchestration and "CreateSymbolLibrary" not in builder,
          "the server reports SymbolLibrary as template-only instead of fabricating OcxState")
    check("dedicated HMI capability tool is published",
          'RegisterTool("get_hmi_ir_capabilities"' in tools and "get_hmi_ir_capabilities" in filter_cfg.get("tools", []),
          "clients can discover exact control/animation/event contracts without reading source")
    check("HMI schema and release version updated",
          'schemaVersion = "hmi-ir/1.2"' in orchestration and 'version = "4.4.4-hmi-controls"' in orchestration,
          "capability, compile, validate and apply responses identify the upgraded contract")
    check("duplicate-name validation preserves full error output",
          "!byName.ContainsKey(normalizedName)" in quality and ".ToDictionary(x => x[\"name\"]" not in quality,
          "duplicate names are recorded as validation errors without a secondary ToDictionary exception")

    failures = [t for t in tests if not t["passed"]]
    result = {
        "version": "4.4.4-hmi-controls",
        "generated_at_utc": datetime.now(timezone.utc).isoformat(),
        "success": not failures,
        "test_type": "source-contract + uploaded-formal-XML regression; no TIA runtime",
        "source": {"csharp_files": len(list(src.rglob("*.cs")))},
        "samples": {
            "xml_files": len(xml_files), "screen_documents": screen_files,
            "parse_errors": parse_errors, "selected_counts": {k: counts.get(k, 0) for k in expected_samples},
        },
        "tests": tests,
        "failures": failures,
        "runtime_status": {
            "dotnet_framework_build": "not_run",
            "tia_openness_import": "not_run",
            "hmi_screen_compile": "not_run",
            "reason": "Current environment has no Windows/.NET Framework/TIA Portal/Siemens Openness toolchain.",
        },
    }
    rendered = json.dumps(result, ensure_ascii=False, indent=2)
    print(rendered)
    out = args.json_out if args.json_out.is_absolute() else root / args.json_out
    out.write_text(rendered + "\n", encoding="utf-8")
    return 0 if not failures else 1


if __name__ == "__main__":
    raise SystemExit(main())
