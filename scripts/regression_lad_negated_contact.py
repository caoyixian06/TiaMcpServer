#!/usr/bin/env python3
"""Regression checks for LAD negated-contact XML generation."""
from __future__ import annotations

import argparse
import json
import os
import pathlib
import subprocess
import tempfile
import time
import xml.etree.ElementTree as ET


def rpc(proc: subprocess.Popen[str], request_id: int, method: str, params: dict) -> dict:
    message = {"jsonrpc": "2.0", "id": request_id, "method": method, "params": params}
    proc.stdin.write(json.dumps(message, ensure_ascii=False) + "\n")
    proc.stdin.flush()
    line = proc.stdout.readline()
    if not line:
        raise RuntimeError(proc.stderr.read())
    return json.loads(line)


def tool(proc: subprocess.Popen[str], request_id: int, name: str, arguments: dict) -> dict:
    return rpc(proc, request_id, "tools/call", {"name": name, "arguments": arguments})


def text_result(response: dict) -> dict:
    content = response.get("result", {}).get("content", [])
    if not content:
        raise RuntimeError(json.dumps(response, ensure_ascii=False))
    return json.loads(content[0]["text"])


def generate_and_count(proc: subprocess.Popen[str], request_id: int, output: pathlib.Path, contact: dict) -> tuple[int, int]:
    network = {
        "title": "常闭触点回归",
        "variables": [
            {"name": "A", "datatype": "Bool", "section": "Static"},
            {"name": "B", "datatype": "Bool", "section": "Static"},
            {"name": "Y", "datatype": "Bool", "section": "Static"},
        ],
        "rung": [
            {"contact": {"contact": "A"}},
            {"contact": contact},
            {"coil": {"coil": "Y"}},
        ],
    }
    response = tool(proc, request_id, "generate_lad_block", {
        "blockType": "FB",
        "name": "NegatedContactRegression",
        "programmingLanguage": "LAD",
        "filePath": str(output),
        "ladNetworkJson": json.dumps(network, ensure_ascii=False),
    })
    job = text_result(response)["job_id"]
    for poll_id in range(request_id + 1, request_id + 30):
        time.sleep(0.05)
        status = text_result(tool(proc, poll_id, "get_job_status", {"job_id": job}))
        if status["job"]["State"] in (2, 3):
            if status["job"]["State"] != 2:
                raise RuntimeError(status["job"].get("Error", "generate_lad_block failed"))
            break
    if not output.exists():
        raise RuntimeError(f"generator did not create {output}")
    root = ET.parse(output).getroot()
    negated = sum(1 for element in root.iter() if element.tag.split("}")[-1] == "Negated")
    units = sum(1 for element in root.iter() if element.tag.split("}")[-1].endswith("CompileUnit"))
    return units, negated


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--exe", type=pathlib.Path, required=True)
    parser.add_argument("--tia-dir", default=r"C:\Program Files\Siemens\Automation\Portal V17")
    args = parser.parse_args()

    data_root = pathlib.Path(tempfile.mkdtemp(prefix="tia-mcp-negated-")).resolve()
    output_dir = pathlib.Path(tempfile.mkdtemp(prefix="tia-mcp-negated-xml-")).resolve()
    env = os.environ.copy()
    env.update({
        "TIA_PORTAL_VERSION": "V17",
        "TIA_PORTAL_DIR": args.tia_dir,
        "TIA_MCP_DATA_DIR": str(data_root),
    })
    proc = subprocess.Popen(
        [str(args.exe), "--profile=all", "--client=lad-negated-regression"],
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        encoding="utf-8",
        errors="replace",
        env=env,
        bufsize=1,
    )
    try:
        rpc(proc, 1, "initialize", {
            "protocolVersion": "2025-11-25",
            "clientInfo": {"name": "lad-negated-regression", "version": "1"},
        })
        units_alias, negated_alias = generate_and_count(
            proc, 2, output_dir / "alias.xml", {"contact": "B", "type": "NegatedContact"}
        )
        units_bool, negated_bool = generate_and_count(
            proc, 40, output_dir / "bool.xml", {"contact": "B", "negated": True}
        )
        bad = {
            "title": "非法触点类型",
            "variables": [
                {"name": "A", "datatype": "Bool", "section": "Static"},
                {"name": "Y", "datatype": "Bool", "section": "Static"},
            ],
            "rung": [
                {"contact": {"contact": "A", "type": "UnknownContactType"}},
                {"coil": {"coil": "Y"}},
            ],
        }
        invalid = text_result(tool(proc, 80, "validate_lad_network", {
            "networkJson": json.dumps(bad, ensure_ascii=False),
        }))
        if units_alias != 1 or units_bool != 1 or negated_alias != 1 or negated_bool != 1:
            raise RuntimeError(f"unexpected XML counts: alias={units_alias}/{negated_alias}, bool={units_bool}/{negated_bool}")
        if invalid.get("valid") is not False:
            raise RuntimeError("unknown contact type was not rejected")
        print(json.dumps({
            "pass": True,
            "alias": {"compileUnits": units_alias, "negated": negated_alias},
            "boolean": {"compileUnits": units_bool, "negated": negated_bool},
            "unknownTypeRejected": True,
        }, ensure_ascii=False))
        return 0
    finally:
        try:
            proc.stdin.close()
        except Exception:
            pass
        proc.terminate()
        try:
            proc.wait(timeout=5)
        except subprocess.TimeoutExpired:
            proc.kill()


if __name__ == "__main__":
    raise SystemExit(main())
