"""AI Terrain Workflow command-line client; requires the unity_bridge Python package."""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

from unity_bridge.client import UnityClient


def load_json(path: str) -> dict:
    with Path(path).open("r", encoding="utf-8") as stream:
        return json.load(stream)


def call(args, command: str, **payload):
    with UnityClient(args.host, args.port, args.timeout) as client:
        result = client.call(command, **payload)
    print(json.dumps(result, ensure_ascii=False, indent=2))
    return 0 if not isinstance(result, dict) or result.get("valid", True) else 2


def export_manifest(args) -> int:
    with UnityClient(args.host, args.port, args.timeout) as client:
        result = client.call("workflow.export", path=args.project)
    manifest = result.get("json") if isinstance(result, dict) else None
    if not isinstance(manifest, str) or not manifest:
        raise RuntimeError("bridge 未返回 manifest JSON")
    output = Path(args.output)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(manifest + "\n", encoding="utf-8")
    print(json.dumps({"operation": "export", "projectPath": result.get("projectPath"), "output": str(output)}, ensure_ascii=False, indent=2))
    return 0


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(prog="workflow-bridge")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=None,
                        help="bridge port; omitted to read unity-python-bridge configuration")
    parser.add_argument("--timeout", type=float, default=300.0)
    sub = parser.add_subparsers(dest="action", required=True)

    configure = sub.add_parser("configure")
    configure.add_argument("manifest")
    configure.add_argument("--project", default="")

    export = sub.add_parser("export", help="读取当前 Unity 工作流配置并写为完整 manifest JSON")
    export.add_argument("output")
    export.add_argument("--project", required=True)

    run = sub.add_parser("run")
    run.add_argument("manifest")
    run.add_argument("--project", default="")

    args = parser.parse_args(argv)
    if args.action == "configure":
        return call(args, "workflow.configure", path=args.project,
                    message=json.dumps(load_json(args.manifest), ensure_ascii=False))
    if args.action == "export":
        return export_manifest(args)
    if args.action == "run":
        return call(args, "workflow.run", path=args.project,
                    message=json.dumps(load_json(args.manifest), ensure_ascii=False))
    parser.error("unknown action")
    return 2


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as exc:  # UnityClient errors should be concise in shell usage.
        print(f"workflow-bridge: {exc}", file=sys.stderr)
        raise SystemExit(1)
