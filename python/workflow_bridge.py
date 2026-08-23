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


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(prog="workflow-bridge")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=None,
                        help="bridge port; omitted to read unity-python-bridge configuration")
    parser.add_argument("--timeout", type=float, default=300.0)
    sub = parser.add_subparsers(dest="action", required=True)

    create = sub.add_parser("project-create")
    create.add_argument("name")
    create.add_argument("--resolution", type=int, default=512)

    configure = sub.add_parser("configure")
    configure.add_argument("manifest")
    configure.add_argument("--project", default="")

    prefab = sub.add_parser("prefab-build")
    prefab.add_argument("path")
    prefab.add_argument("--billboard-mode", default="None",
                        choices=["None", "CrossPlanes", "FaceCamera", "YawOnly"])
    prefab.add_argument("--two-point-height", action="store_true")

    bounds = sub.add_parser("prefab-update-bounds")
    bounds.add_argument("--force", action="store_true")
    sub.add_parser("prefab-update-billboards")

    area = sub.add_parser("area-rebuild")
    area.add_argument("project")
    area.add_argument("operations", help='JSON file: {"operations": [...]}')

    bake = sub.add_parser("bake")
    bake.add_argument("project")

    validate = sub.add_parser("validate")
    validate.add_argument("project")

    build = sub.add_parser("build")
    build.add_argument("project")
    build.add_argument("--terrain", default="")
    build.add_argument("--through", default="FixedPointEdit",
                       choices=["HeightEdit", "TextureEdit", "ScatterEdit", "PropEdit", "FixedPointEdit"])

    run = sub.add_parser("run")
    run.add_argument("manifest")
    run.add_argument("--project", default="")

    args = parser.parse_args(argv)
    if args.action == "project-create":
        return call(args, "workflow.project.create", name=args.name, width=args.resolution)
    if args.action == "configure":
        return call(args, "workflow.configure", path=args.project,
                    message=json.dumps(load_json(args.manifest), ensure_ascii=False))
    if args.action == "prefab-build":
        return call(args, "workflow.prefab.build", path=args.path,
                    type=args.billboard_mode, placed=args.two_point_height)
    if args.action == "prefab-update-bounds":
        return call(args, "workflow.prefab.update_bounds", active=args.force)
    if args.action == "prefab-update-billboards":
        return call(args, "workflow.prefab.update_billboards")
    if args.action == "area-rebuild":
        return call(args, "workflow.area.rebuild", path=args.project,
                    message=json.dumps(load_json(args.operations), ensure_ascii=False))
    if args.action == "bake":
        return call(args, "workflow.bake", path=args.project)
    if args.action == "validate":
        return call(args, "workflow.validate", path=args.project)
    if args.action == "build":
        return call(args, "workflow.build", path=args.project,
                    terrain=args.terrain, type=args.through)
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
