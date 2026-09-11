from __future__ import annotations

import shutil
import subprocess
import sys
from pathlib import Path


def install_git_driver(is_global: bool = False) -> None:
    driver_path = shutil.which("excelcompare-driver")
    if not driver_path:
        driver_path = "excelcompare-driver"

    scope = "--global" if is_global else "--local"

    commands = [
        ["git", "config", scope, "merge.excelcompare.name", "ExcelCompare visual merge driver"],
        ["git", "config", scope, "merge.excelcompare.driver", f"{driver_path} %O %A %B -L %P"],
        ["git", "config", scope, "merge.excelcompare.recursive", "binary"],
    ]

    for cmd in commands:
        result = subprocess.run(cmd, capture_output=True, text=True)
        if result.returncode != 0:
            print(f"Failed: {' '.join(cmd)}", file=sys.stderr)
            print(result.stderr, file=sys.stderr)
            return

    print(f"Git merge driver configured ({scope}).")

    gitattributes_line = "*.xlsx merge=excelcompare"

    if is_global:
        global_attrs = Path.home() / ".config" / "git" / "attributes"
        _ensure_gitattributes(global_attrs, gitattributes_line)
        print(f"Updated {global_attrs}")
    else:
        local_attrs = Path(".gitattributes")
        _ensure_gitattributes(local_attrs, gitattributes_line)
        print(f"Updated {local_attrs}")

    print("\nDone! Excel files (.xlsx) will now use ExcelCompare for merging.")
    print("Test with: git merge <branch-with-xlsx-changes>")


def _ensure_gitattributes(path: Path, line: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)

    if path.exists():
        content = path.read_text(encoding="utf-8")
        if line in content:
            return
        if not content.endswith("\n"):
            content += "\n"
        content += line + "\n"
    else:
        content = line + "\n"

    path.write_text(content, encoding="utf-8")
