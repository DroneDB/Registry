#!/usr/bin/env python3
"""Migrate EPT datasets to COPC format using PDAL.

Requires: pip install psutil
"""

import argparse
import json
import shutil
import subprocess
import sys
import threading
import time
from collections import deque
from concurrent.futures import FIRST_COMPLETED, ThreadPoolExecutor, wait
from pathlib import Path
from typing import List, Optional

import psutil

# Global registry of subprocesses currently running, used for hard-stop cleanup
_active_procs: List[subprocess.Popen] = []
_procs_lock = threading.Lock()


def free_ram_pct() -> float:
    """Return the percentage of RAM currently available."""
    vm = psutil.virtual_memory()
    return vm.available / vm.total * 100


def build_pdal_cmd(pdal_args: List[str], use_docker: bool, root_dir: Path) -> List[str]:
    """Build a pdal command, optionally wrapping it in a Docker call.

    The Docker variant mounts only root_dir (same host path inside the container),
    so all input/output paths must be inside root_dir.
    """
    if use_docker:
        abs_root = str(root_dir.resolve())
        return [
            "docker", "run", "--rm",
            "-v", f"{abs_root}:{abs_root}",
            "pdal/pdal", "pdal",
        ] + pdal_args
    return ["pdal"] + pdal_args


def get_ept_num_points(ept_json: Path) -> Optional[int]:
    """Read the total point count directly from ept.json (no pdal call needed)."""
    try:
        data = json.loads(ept_json.read_text())
        pts = data.get("points")
        return int(pts) if pts is not None else None
    except Exception:
        return None


def get_pdal_num_points(file: Path, use_docker: bool, root_dir: Path) -> Optional[int]:
    """Run ``pdal info --summary`` and return num_points, or None on failure."""
    cmd = build_pdal_cmd(["info", "--summary", str(file.resolve())], use_docker, root_dir)
    try:
        result = subprocess.run(cmd, capture_output=True, text=True, timeout=120)
        if result.returncode != 0:
            return None
        data = json.loads(result.stdout)
        summary = data.get("summary", {})
        # num_points is a direct field in modern PDAL; older versions nest it under metadata
        pts = summary.get("num_points")
        if pts is None:
            pts = summary.get("metadata", {}).get("num_points")
        return int(pts) if pts is not None else None
    except Exception:
        return None


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
    log_lock: threading.Lock,
    max_points: Optional[int] = None,
) -> str:
    """Convert one EPT dataset to COPC. Returns 'converted', 'skipped', or 'failed'."""
    prefix = f"[{index}/{total}]"
    ept_dir = ept_json.parent
    copc_dir = ept_dir.parent / "copc"
    out_file = copc_dir / "cloud.copc.laz"
    sentinel_file = copc_dir / "cloud.copc.laz.ok"

    ept_points = get_ept_num_points(ept_json)

    if max_points is not None and ept_points is not None and ept_points > max_points:
        print(f"{prefix} Skipping (too many points: {ept_points} > {max_points}): {ept_json}")
        return "skipped"

    if out_file.exists():
        # Fast path: sentinel was written by a previous successful run — skip entirely
        if sentinel_file.exists():
            print(f"{prefix} Skipping (already validated): {out_file}")
            with log_lock:
                with converted_log.open("a") as f:
                    f.write(f"{ept_json}\n")
            return "skipped"

        # Sentinel missing: file exists but was never confirmed valid — validate now
        print(f"{prefix} COPC exists but not yet validated, checking: {out_file}")
        copc_points = get_pdal_num_points(out_file, use_docker, root_dir)
        if (ept_points is not None
                and copc_points is not None
                and copc_points == ept_points):
            sentinel_file.write_text(f"points={copc_points}\n")
            print(f"{prefix} Skipping (COPC valid, {copc_points} points): {out_file}")
            with log_lock:
                with converted_log.open("a") as f:
                    f.write(f"{ept_json}\n")
            return "skipped"
        print(
            f"{prefix} COPC is incomplete or corrupt "
            f"(EPT: {ept_points}, COPC: {copc_points}), re-converting...",
            file=sys.stderr,
        )
        out_file.unlink(missing_ok=True)
        sentinel_file.unlink(missing_ok=True)

    # Track whether we created copc_dir so we know how to clean up on failure
    copc_dir_created = not copc_dir.exists()
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

    # Write the pipeline file inside copc_dir so it's under root_dir and
    # therefore always accessible inside the Docker volume mount.
    pipeline_file = copc_dir / "_pipeline.json"
    pipeline_file.write_text(json.dumps(pipeline, indent=2))

    try:
        cmd = build_pdal_cmd(["pipeline", str(pipeline_file.resolve())], use_docker, root_dir)
        proc = subprocess.Popen(cmd)
        with _procs_lock:
            _active_procs.append(proc)
        try:
            proc.wait()
        finally:
            with _procs_lock:
                _active_procs.remove(proc)
        success = proc.returncode == 0
    finally:
        pipeline_file.unlink(missing_ok=True)

    if success:
        # Validate the output against the source point count
        copc_points = get_pdal_num_points(out_file, use_docker, root_dir)
        if ept_points is not None and copc_points is not None and copc_points != ept_points:
            print(
                f"{prefix} ERROR: point count mismatch "
                f"(EPT: {ept_points}, COPC: {copc_points})",
                file=sys.stderr,
            )
            success = False
        elif copc_points is None:
            # pdal returned 0 and file exists; keep as successful but warn
            print(
                f"{prefix} WARNING: could not verify COPC output via pdal info",
                file=sys.stderr,
            )

    if success:
        copc_points_str = str(copc_points) if copc_points is not None else "unknown"
        print(f"{prefix} Conversion completed: {out_file} ({copc_points_str} points)")
        sentinel_file.write_text(f"points={copc_points_str}\n")
        with log_lock:
            with converted_log.open("a") as f:
                f.write(f"{ept_json}\n")
        if keep_ept:
            print(f"{prefix} Keeping EPT directory: {ept_dir}")
        else:
            print(f"{prefix} Deleting EPT directory: {ept_dir}")
            shutil.rmtree(ept_dir)
            print(f"{prefix} OK: deleted {ept_dir}")
        return "converted"
    else:
        # Clean up partial output without touching any pre-existing content
        sentinel_file.unlink(missing_ok=True)
        if copc_dir_created:
            shutil.rmtree(copc_dir, ignore_errors=True)
        else:
            out_file.unlink(missing_ok=True)
        print(f"{prefix} ERROR: conversion failed for {ept_json}", file=sys.stderr)
        with log_lock:
            with failed_log.open("a") as f:
                f.write(f"{ept_json}\n")
        return "failed"


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
    parser.add_argument(
        "--min-free-ram", type=float, default=20.0, metavar="PCT",
        help="Minimum free RAM %% before starting a new job (default: 20)"
    )
    parser.add_argument(
        "--max-points", type=int, default=None, metavar="N",
        help="Skip point clouds with more than N points (default: no limit)"
    )
    args = parser.parse_args()

    root_dir: Path = args.root_folder
    if not root_dir.is_dir():
        parser.error(f"'{root_dir}' is not a valid directory")

    if args.parallel < 1:
        parser.error("--parallel must be a positive integer")

    if not (0.0 < args.min_free_ram < 100.0):
        parser.error("--min-free-ram must be between 0 and 100")

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

    log_lock = threading.Lock()

    kwargs = dict(
        keep_ept=args.keep_ept,
        use_docker=args.docker,
        root_dir=root_dir,
        converted_log=converted_log,
        failed_log=failed_log,
        log_lock=log_lock,
        max_points=args.max_points,
    )

    POLL_INTERVAL = 5  # seconds between RAM re-checks when under pressure

    pending = deque((i + 1, ept) for i, ept in enumerate(ept_files))
    active_futures: dict = {}
    results: List[str] = []
    ram_warned = False
    interrupted = False
    not_started = 0

    try:
        with ThreadPoolExecutor(max_workers=args.parallel) as executor:
            while pending or active_futures:
                # Fill open slots as long as RAM is above threshold
                while pending and len(active_futures) < args.parallel:
                    pct = free_ram_pct()
                    if pct < args.min_free_ram:
                        if not ram_warned:
                            print(
                                f"  RAM pressure ({pct:.1f}% free < {args.min_free_ram}% threshold), "
                                "waiting for memory to free up..."
                            )
                            ram_warned = True
                        break
                    ram_warned = False
                    idx, ept = pending.popleft()
                    f = executor.submit(convert_one, idx, total, ept, **kwargs)
                    active_futures[f] = ept

                if not active_futures:
                    # No jobs running; RAM still low — wait for OS to free memory
                    time.sleep(POLL_INTERVAL)
                    continue

                # Wait for at least one job to finish, with a timeout so we can
                # re-check RAM even when no jobs complete on their own
                done, _ = wait(
                    list(active_futures.keys()),
                    return_when=FIRST_COMPLETED,
                    timeout=POLL_INTERVAL,
                )
                for f in done:
                    try:
                        results.append(f.result())
                    except Exception as exc:
                        print(f"  Unexpected error in worker: {exc}", file=sys.stderr)
                        results.append("failed")
                    del active_futures[f]

    except KeyboardInterrupt:
        interrupted = True
        not_started = len(pending)
        print(
            f"\n  Interrupted — waiting for {len(active_futures)} running job(s) to finish "
            f"({not_started} job(s) not started). Press CTRL+C again to force quit and clean up."
        )
        pending.clear()
        # Collect results from in-flight futures; track which ones are still pending
        # so a second CTRL+C can skip them without double-counting.
        remaining_futures = list(active_futures.keys())
        processed = set()
        try:
            for f in remaining_futures:
                try:
                    results.append(f.result())
                except Exception:
                    results.append("failed")
                processed.add(id(f))
        except KeyboardInterrupt:
            print("\n  Force quitting — terminating all running processes and removing partial files...")
            with _procs_lock:
                for proc in _active_procs:
                    proc.terminate()
            # Wait for threads to unblock; they will clean up partial COPC files themselves
            for f in remaining_futures:
                if id(f) not in processed:
                    try:
                        results.append(f.result(timeout=30))
                    except Exception:
                        results.append("failed")
            print("  Done.")

    converted = sum(1 for r in results if r == "converted")
    skipped = sum(1 for r in results if r == "skipped")
    failed = sum(1 for r in results if r == "failed")

    if interrupted:
        print(
            f"\nMigration interrupted: {converted} converted, {skipped} already valid "
            f"(skipped), {failed} failed, {not_started} not started."
        )
    else:
        print(f"\nMigration complete: {converted} converted, {skipped} already valid (skipped), {failed} failed.")
    if converted or skipped:
        print(f"  Converted list : {converted_log}")
    if failed:
        print(f"  Failed list    : {failed_log}")
    if interrupted:
        sys.exit(130)
    if failed:
        sys.exit(1)


if __name__ == "__main__":
    main()
