from __future__ import annotations

import subprocess
import sys
from pathlib import Path


_LFS_POINTER_PREFIX = b"version https://git-lfs.github.com/spec/v1\n"


def _looks_like_lfs_pointer(data: bytes) -> bool:
    return (
        data.startswith(_LFS_POINTER_PREFIX)
        and b"\noid sha256:" in data
        and b"\nsize " in data
    )


def _smudge_lfs_pointer(pointer: bytes) -> bytes:
    result = subprocess.run(
        ["git", "lfs", "smudge"],
        input=pointer,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
    )
    if result.returncode != 0:
        sys.stderr.write(result.stderr.decode(errors="replace"))
        raise RuntimeError("Git LFS could not restore the Excel file. Try running `git lfs pull`.")
    if _looks_like_lfs_pointer(result.stdout):
        raise RuntimeError("Git LFS returned a pointer instead of the Excel file. Try running `git lfs pull`.")
    return result.stdout


def export_git_stage(stage_path: str, output_path: str | Path) -> bool:
    result = subprocess.run(
        ["git", "show", stage_path],
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
    )
    if result.returncode != 0:
        return False

    data = result.stdout
    if _looks_like_lfs_pointer(data):
        data = _smudge_lfs_pointer(data)

    Path(output_path).write_bytes(data)
    return True


def create_empty_workbook(output_path: str | Path) -> None:
    import openpyxl

    workbook = openpyxl.Workbook()
    workbook.save(str(output_path))
    workbook.close()


def main() -> int:
    if len(sys.argv) == 3 and sys.argv[1] == "--empty":
        create_empty_workbook(sys.argv[2])
        return 0

    if len(sys.argv) != 3:
        print("Usage: python -m excelcompare.git.export_stage <stage-path> <output-path>", file=sys.stderr)
        print("   or: python -m excelcompare.git.export_stage --empty <output-path>", file=sys.stderr)
        return 2

    try:
        return 0 if export_git_stage(sys.argv[1], sys.argv[2]) else 1
    except Exception as exc:
        print(f"ExcelCompare: {exc}", file=sys.stderr)
        return 3


if __name__ == "__main__":
    raise SystemExit(main())
