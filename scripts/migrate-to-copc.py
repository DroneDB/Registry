#!/usr/bin/env python3
"""Migrate EPT datasets to COPC format using PDAL."""

import argparse
import json
import shutil
import subprocess
import sys
import tempfile
from concurrent.futures import ThreadPoolExecutor, as_completed
from pathlib import Path
from typing import List


def build_pdal_cmd(pipeline_file: str, use_docker: bool, root_dir: Path) -> List[str]:
    if use_docker:
        abs_root = str(root_dir.resolve())
        pipe_dir = str(Path(pipeline_file).parent)
        return [
            "docker", "run", "--rm",
            "-v", f"{abs_root}:{abs_root}",
            "-v", f"{pipe_dir}:{pipe_dir}",
            "pdal/pdal", "pdal", "pipeline", pipeline_file,
        ]
    return ["pdal", "pipeline", pipeline_file]


def convert_one(
    index: int,
    total: int,
    ept_json: Path,
    *,
    keep_ept: bool,
    use_docker: bool,
    root_dir: Path,
    converted_log: Path,
    failed_log: Path,
) -> bool:
    prefix = f"[{index}/{total}]"
    ept_dir = ept_json.parent
    copc_dir = ept_dir.parent / "copc"
    out_file = copc_dir / "cloud.copc.laz"

    if out_file.exists():
        print(f"{prefix} Skipping (COPC already exists): {out_file}")
        with converted_log.open("a") as f:
            f.write(f"{ept_json}\n")
        return True

    copc_dir.mkdir(parents=True, exist_ok=True)

    print(f"{prefix} Converting:")
    print(f"  input : {ept_json}")
    print(f"  output: {out_file}")

    pipeline = [
        {"type": "readers.ept", "filename": str(ept_json.resolve())},
        {
            "type": "writers.copc",
            "filename": str(out_file.resolve()),
            "forward": "all",
            "extra_dims": "all",
        },
    ]

    with tempfile.NamedTemporaryFile(
        mode="w", suffix=".json", delete=False
    ) as tmp:
        json.dump(pipeline, tmp, indent=2)
        pipeline_file = tmp.name

    try:
        cmd = build_pdal_cmd(pipeline_file, use_docker, root_dir)
        result = subprocess.run(cmd, check=False)
        success = result.returncode == 0
    finally:
        Path(pipeline_file).unlink(missing_ok=True)

    if success:
        print(f"{prefix} Conversion completed: {out_file}")
        with converted_log.open("a") as f:
            f.write(f"{ept_json}\n")

        if keep_ept:
            print(f"{prefix} Keeping EPT directory: {ept_dir}")
        else:
            print(f"{prefix} Deleting EPT directory: {ept_dir}")
            shutil.rmtree(ept_dir)
            print(f"{prefix} OK: deleted {ept_dir}")
    else:
        shutil.rmtree(copc_dir, ignore_errors=True)
        print(f"{prefix} ERROR: conversion failed for {ept_json}", file=sys.stderr)
        with failed_log.open("a") as f:
            f.write(f"{ept_json}\n")

    return success


def find_ept_files(root_dir: Path) -> List[Path]:
    return [
        p for p in root_dir.rglob("ept.json")
        if p.parent.name == "ept"
    ]


def main() -> None:
    parser = argparse.ArgumentParser(
        description="Migrate EPT datasets to COPC format."
    )
    parser.add_argument("root_folder", type=Path, help="Root folder to scan")
    parser.add_argument(
        "--keep-ept", action="store_true", help="Keep EPT directories after conversion"
    )
    parser.add_argument(
        "--docker", action="store_true", help="Run PDAL via Docker"
    )
    parser.add_argument(
        "--parallel", type=int, default=1, metavar="N",
        help="Number of parallel conversions (default: 1)"
    )
    args = parser.parse_args()

    root_dir: Path = args.root_folder
    if not root_dir.is_dir():
        parser.error(f"'{root_dir}' is not a valid directory")

    if args.parallel < 1:
        parser.error("--parallel must be a positive integer")

    tool = "docker" if args.docker else "pdal"
    if not shutil.which(tool):
        print(f"Error: '{tool}' was not found in PATH", file=sys.stderr)
        sys.exit(1)

    ept_files = find_ept_files(root_dir)
    total = len(ept_files)

    if total == 0:
        print(f"No EPT datasets found in '{root_dir}'.")
        sys.exit(0)

    converted_log = root_dir / "converted.txt"
    failed_log = root_dir / "failed.txt"
    converted_log.write_text("")
    failed_log.write_text("")

    print(f"Found {total} EPT dataset(s) to migrate (parallel: {args.parallel}).")

    kwargs = dict(
        keep_ept=args.keep_ept,
        use_docker=args.docker,
        root_dir=root_dir,
        converted_log=converted_log,
        failed_log=failed_log,
    )

    with ThreadPoolExecutor(max_workers=args.parallel) as executor:
        futures = {
            executor.submit(convert_one, i + 1, total, ept, **kwargs): ept
            for i, ept in enumerate(ept_files)
        }
        results = [future.result() for future in as_completed(futures)]

    converted = sum(results)
    failed = total - converted

    print(f"\nMigration complete: {converted} converted, {failed} failed.")
    if converted:
        print(f"  Converted list : {converted_log}")
    if failed:
        print(f"  Failed list    : {failed_log}")
        sys.exit(1)


if __name__ == "__main__":
    main()
