#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
ObjectPref 命令行工具 —— 以 JSON 文件保存 / 读取 key-value 字符串信息。

数据文件默认位于本工具上一级的 ObjectPref 目录（即 <Utils>/ObjectPref/objectpref.json），
保存为 UTF-8 编码的 JSON 对象 {"key": "value", ...}。

JSON 格式保证：
  1) 使用标准库 json 模块序列化（不手写拼接字符串），写入缩进 + ensure_ascii=False，可读且正确；
  2) 原子写入：先写同目录临时文件并 fsync，再 os.replace 覆盖，写入中断不会损坏原文件；
  3) 读取时校验：文件缺失视为空；内容非法（非 JSON / 非对象）时明确报错，不静默覆盖。

用法（在任意目录均可运行，默认数据文件路径按本脚本位置解析）:
    python objectpref.py set <key> <value> [--overwrite] [--file <路径>]
    python objectpref.py get <key> [--file <路径>]
    python objectpref.py list [--file <路径>]

说明:
    set     录入 / 更新；若 <key> 已存在且未显式加 --overwrite，则报错退出（不覆盖）
    get     读取 <key> 对应的 value，输出到 stdout；key 不存在时报错退出
    list    列出当前数据文件中全部 key-value（按 key 排序）

退出码: 0 = 成功；1 = 参数/业务错误（如重复 key 未覆盖、key 不存在、JSON 损坏）
"""

import argparse
import json
import os
import sys
import tempfile
from pathlib import Path

# 默认数据文件：本脚本所在目录的上一级目录下的 ObjectPref/objectpref.json
# （py 文件放 Utils/，数据文件放 Utils/ObjectPref/）
DEFAULT_DATA_FILE = Path(__file__).resolve().parent / "ObjectPref" / "objectpref.json"


def load_data(data_file: Path) -> dict:
    """读取数据文件为 dict。

    文件不存在 → 返回空 dict；
    文件内容非法（非合法 JSON / 顶层不是对象）→ 打印错误并退出（退出码 1），不破坏原文件。
    """
    if not data_file.exists():
        return {}
    try:
        with open(data_file, "r", encoding="utf-8") as f:
            raw = f.read()
    except OSError as e:
        sys.stderr.write(f"错误: 无法读取数据文件 {data_file}: {e}\n")
        sys.exit(1)

    try:
        data = json.loads(raw)
    except json.JSONDecodeError as e:
        sys.stderr.write(
            f"错误: 数据文件 {data_file} 不是合法 JSON（第 {e.lineno} 行第 {e.colno} 列: {e.msg}）。\n"
            f"请人工修复该文件或删除后重试，工具不会静默覆盖损坏数据。\n"
        )
        sys.exit(1)

    if not isinstance(data, dict):
        sys.stderr.write(f"错误: 数据文件 {data_file} 顶层必须是 JSON 对象 {{key: value}}。\n")
        sys.exit(1)
    return data


def save_data(data_file: Path, data: dict) -> None:
    """原子写入：先写同目录临时文件，fsync 落盘后 os.replace 替换原文件。

    即使中途异常退出，原文件也保持完整，避免 JSON 被截断损坏。
    """
    data_file.parent.mkdir(parents=True, exist_ok=True)
    fd, tmp_path = tempfile.mkstemp(prefix=".objectpref-", suffix=".tmp", dir=str(data_file.parent))
    try:
        with os.fdopen(fd, "w", encoding="utf-8", newline="\n") as f:
            json.dump(data, f, ensure_ascii=False, indent=2)
            f.write("\n")
            f.flush()
            os.fsync(f.fileno())
        os.replace(tmp_path, data_file)
    except BaseException:
        try:
            os.unlink(tmp_path)
        except OSError:
            pass
        raise


def cmd_set(args) -> int:
    key = args.key
    if not key or key != key.strip():
        sys.stderr.write("错误: key 不能为空或全为空白字符\n")
        return 1

    data = load_data(args.file)
    existed = key in data
    if existed and not args.overwrite:
        sys.stderr.write(
            f"错误: key '{key}' 已存在（当前 value: {data[key]!r}）。\n"
            f"如需覆盖，请显式添加 --overwrite 参数。\n"
        )
        return 1

    data[key] = args.value
    save_data(args.file, data)
    verb = "更新" if existed else "录入"
    print(f"ObjectPref: {verb}成功 key='{key}' value='{args.value}' -> {args.file}")
    return 0


def cmd_get(args) -> int:
    data = load_data(args.file)
    if args.key not in data:
        sys.stderr.write(f"错误: key '{args.key}' 不存在（可用 list 子命令查看全部 key）\n")
        return 1
    print(data[args.key])
    return 0


def cmd_list(args) -> int:
    data = load_data(args.file)
    if not data:
        print("(空，暂无数据)")
        return 0
    for k in sorted(data.keys()):
        print(f"{k} = {data[k]}")
    return 0


FILE_HELP = "数据文件路径（默认: {0}）".format(DEFAULT_DATA_FILE)


def add_file_arg(p: argparse.ArgumentParser, *, is_main: bool = False) -> None:
    """给解析器注册 --file 参数。

    主解析器用 default=DEFAULT_DATA_FILE；子命令用 default=argparse.SUPPRESS
    （未提供时不写入 namespace，避免子解析器用默认值覆盖主解析器已解析的 --file）。
    因此 --file 既可写在子命令前（--file X set ...），也可写在子命令后（set ... --file X）。
    """
    if is_main:
        p.add_argument("--file", type=Path, default=DEFAULT_DATA_FILE, help=FILE_HELP)
    else:
        p.add_argument("--file", type=Path, default=argparse.SUPPRESS, help=FILE_HELP)


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="objectpref",
        description="ObjectPref 工具：以 JSON 文件保存/读取 key-value 字符串信息。"
                    "默认数据文件: {0}".format(DEFAULT_DATA_FILE),
    )
    add_file_arg(parser, is_main=True)
    sub = parser.add_subparsers(dest="command", required=True)

    p_set = sub.add_parser("set", help="录入/更新 key-value（key 已存在时必须加 --overwrite）")
    add_file_arg(p_set)
    p_set.add_argument("key", help="键（非空字符串）")
    p_set.add_argument("value", help="值（字符串，可为空）")
    p_set.add_argument("--overwrite", action="store_true", help="key 已存在时允许覆盖")
    p_set.set_defaults(func=cmd_set)

    p_get = sub.add_parser("get", help="读取 key 对应的 value 并输出")
    add_file_arg(p_get)
    p_get.add_argument("key", help="键")
    p_get.set_defaults(func=cmd_get)

    p_list = sub.add_parser("list", help="列出全部 key-value")
    add_file_arg(p_list)
    p_list.set_defaults(func=cmd_list)

    return parser


def main(argv=None) -> int:
    args = build_parser().parse_args(argv)
    return args.func(args)


if __name__ == "__main__":
    sys.exit(main())
