#!/usr/bin/env python3
"""V4.4.3 IEC instance / shared validation source-contract regression.

This test is intentionally independent of TIA Portal. It verifies the five hotfix
contracts at source level and records that a Windows/TIA compile was not executed.
"""
from __future__ import annotations

import argparse
import json
from datetime import datetime, timezone
from pathlib import Path


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8-sig")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", default=".")
    parser.add_argument("--json-out", default="V4.4.3_IEC共享校验专项回归.json")
    args = parser.parse_args()

    root = Path(args.root).resolve()
    src = root / "src"
    orch = read(src / "Services" / "XmlOrchestrationService.cs")
    plc = read(src / "Services" / "PlcService.cs")
    quality = read(src / "Services" / "QualityGuardService.cs")
    catalog = read(src / "Services" / "LadInstructionCatalog.cs")
    shared = read(src / "Services" / "LadProgramValidationService.cs")
    flg = read(src / "Xml" / "FlgNetBuilder.cs")
    block = read(src / "Xml" / "BlockXmlBuilder.cs")
    tools = read(src / "Tools" / "PlcTools.cs")

    tests: list[dict[str, object]] = []

    def check(name: str, condition: bool, evidence: str) -> None:
        tests.append({"name": name, "passed": bool(condition), "evidence": evidence})

    box_case_start = orch.find('case "box":')
    call_case_start = orch.find('case "call":', box_case_start)
    box_case = orch[box_case_start:call_case_start]
    decl = box_case.find('var instanceScope = (step["instanceScope"]')
    use = box_case.find('["instanceScope"] = instanceScope')
    check(
        "instanceScope declaration precedes use",
        box_case_start >= 0 and decl >= 0 and use > decl,
        "ConvertStep(box) declares and validates instanceScope before serializing it.",
    )
    check(
        "add_lad_box exposes and forwards instanceScope",
        '["instanceScope"] = StrProp' in tools
        and "GetStringArg(args, \"instanceScope\")" in tools
        and "Dictionary<string, string>? pinBindings, string? instanceScope = null, string? plcName = null" in plc,
        "Tool schema, tool call and service signature use the same argument order.",
    )

    batch_start = plc.find("public string AddLadBoxBatch")
    batch_end = plc.find("public string GetInstructionDetails", batch_start)
    batch = plc[batch_start:batch_end]
    check(
        "add_lad_box_batch routes through compiling batch API",
        "List<LadNetworkDef>" in batch
        and "JsonConvert.SerializeObject(networks)" in batch
        and "compileAfter: true" in batch
        and "compileAfter: false" not in batch,
        "Strongly typed networks are passed to AddLadNetworksBatch with compileAfter=true.",
    )

    check(
        "IEC instances are batch-unique",
        "usedInstances.TryGetValue" in shared
        and "for (var suffix = 1; suffix < 100000; suffix++)" in shared
        and "ExtractLadInstanceNames(originalBlockXml)" in plc
        and "GetDefaultInstanceName" not in flg
        and "IEC_Timer_0_DB_1" not in flg
        and "IEC_Counter_0_DB" not in flg,
        "Allocator checks current batch, declared variables and original-block instances; builder no longer guesses defaults.",
    )
    check(
        "post-normalization declaration collision check",
        shared.count("ValidateVariableDeclarations(networks, reservedVars, errors);") >= 2,
        "Auto-added Static IEC members are rechecked against original and cross-network declarations.",
    )

    mappings = {
        "TON": 'case "TON": case "TONR": return "TON_TIME";',
        "TOF": 'case "TOF": return "TOF_TIME";',
        "TP": 'case "TP": return "TP_TIME";',
        "CTU": 'case "CTU": return "CTU_" +',
        "CTD": 'case "CTD": return "CTD_" +',
        "CTUD": 'case "CTUD": return "CTUD_" +',
        "R_TRIG": 'case "R_TRIG": return "R_TRIG";',
        "F_TRIG": 'case "F_TRIG": return "F_TRIG";',
    }
    check(
        "IEC datatype mapping is instruction-specific",
        all(token in catalog for token in mappings.values())
        and '"CTU_INT" or "CTD_INT" or "CTUD_INT" => "IEC_COUNTER"' not in block
        and 'originalType.StartsWith("CTU_"' in block
        and 'originalType.StartsWith("CTD_"' in block
        and 'originalType.StartsWith("CTUD_"' in block,
        "Timers, counters and edge triggers retain their exact local instance datatypes.",
    )
    check(
        "counter value datatype is integer-only",
        'return ToCanonicalScalar(normalized);' in catalog
        and all(x in catalog for x in ('case "SINT"', 'case "INT"', 'case "DINT"', 'case "LINT"')),
        "CTU/CTD/CTUD value_type is normalized to supported integer scalar types.",
    )

    validate_start = orch.find("public string ValidateLadIr")
    apply_start = orch.find("public string ApplyLadIr")
    validate_body = orch[validate_start:apply_start]
    apply_end = orch.find("private static JObject? ConvertNetwork", apply_start)
    apply_body = orch[apply_start:apply_end]
    check(
        "validate_lad_ir uses shared rules",
        "PortalService.PrepareLadProgram" in validate_body,
        "Validation normalizes and validates legacy networks through PrepareLadProgram.",
    )
    check(
        "apply_lad_ir invokes validate_lad_ir",
        "var compiledText = ValidateLadIr(irJson);" in apply_body
        and "AddLadNetworksBatch" in apply_body,
        "Apply cannot bypass the public IR validation path and the project write path reuses PrepareLadProgram.",
    )
    check(
        "legacy validate tools use shared rules",
        quality.count("PrepareLadProgram(networks, null, null") >= 2,
        "validate_lad_network_json and validate_lad_program_json share the same normalizer/validator.",
    )
    check(
        "all LAD write paths use shared rules",
        plc.count("PrepareLadProgram(") >= 4,
        "Single append, batch append, block generation and network update call PrepareLadProgram.",
    )
    check(
        "builder fails closed on missing IEC instance",
        "IEC 指令 {box.box} 缺少唯一 instance" in flg
        and "IEC 指令 {sc.inst} 缺少唯一 instance" in flg,
        "XML generation throws instead of silently assigning a shared instance.",
    )

    failures = [t for t in tests if not t["passed"]]
    result = {
        "version": "4.4.4-hmi-controls",
        "generated_at_utc": datetime.now(timezone.utc).isoformat(),
        "success": not failures,
        "test_type": "source-contract regression; no TIA Portal runtime",
        "tests": tests,
        "failures": failures,
        "compile_status": {
            "dotnet_framework_build": "not_run",
            "tia_openness_import_compile": "not_run",
            "reason": "Current environment has no Windows/.NET Framework/TIA Portal/Siemens Openness toolchain.",
        },
    }
    out = root / args.json_out
    out.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(result, ensure_ascii=False, indent=2))
    return 0 if not failures else 1


if __name__ == "__main__":
    raise SystemExit(main())
