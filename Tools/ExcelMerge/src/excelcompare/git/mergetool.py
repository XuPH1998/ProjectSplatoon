"""Git mergetool entry point.

Called by `git mergetool` with env vars:
  $BASE   - common ancestor
  $LOCAL  - ours (current branch)
  $REMOTE - theirs (branch being merged)
  $MERGED - output file (write result here)

Can also be called directly:
  excelcompare-mergetool <base> <local> <remote> <merged>
"""
from __future__ import annotations

import os
import sys
from pathlib import Path


def main():
    if len(sys.argv) >= 5:
        base_path = str(Path(sys.argv[1]).resolve())
        local_path = str(Path(sys.argv[2]).resolve())
        remote_path = str(Path(sys.argv[3]).resolve())
        merged_path = str(Path(sys.argv[4]).resolve())
    else:
        base_path = os.environ.get("BASE", "")
        local_path = os.environ.get("LOCAL", "")
        remote_path = os.environ.get("REMOTE", "")
        merged_path = os.environ.get("MERGED", "")

    if not all([base_path, local_path, remote_path, merged_path]):
        print("Usage: excelcompare-mergetool <base> <local> <remote> <merged>", file=sys.stderr)
        print("  Or set BASE, LOCAL, REMOTE, MERGED env vars", file=sys.stderr)
        sys.exit(2)

    base_path = str(Path(base_path).resolve())
    local_path = str(Path(local_path).resolve())
    remote_path = str(Path(remote_path).resolve())
    merged_path = str(Path(merged_path).resolve())

    print(f"ExcelCompare: resolving {Path(merged_path).name}", file=sys.stderr)

    from excelcompare.core.diff import diff_workbooks
    from excelcompare.core.excel_io import load_workbook
    from excelcompare.core.merge import auto_merge, save_merged_with_styles, unresolved_count

    try:
        base_wb = load_workbook(base_path)
        local_wb = load_workbook(local_path)
        remote_wb = load_workbook(remote_path)
    except Exception as e:
        print(f"ExcelCompare: failed to load workbooks: {e}", file=sys.stderr)
        sys.exit(2)

    wb_diff = diff_workbooks(base_wb, local_wb, remote_wb)
    resolutions = auto_merge(wb_diff)
    remaining = unresolved_count(wb_diff, resolutions)

    total_changes = sum(len(sd.cells) for sd in wb_diff.sheets.values())

    if total_changes == 0:
        save_merged_with_styles(wb_diff, resolutions, local_path, remote_path, merged_path, remote_wb)
        print(f"ExcelCompare: no changes in {Path(merged_path).name}", file=sys.stderr)
        sys.exit(0)

    print(f"ExcelCompare: {total_changes} change(s), {remaining} conflict(s) in {Path(merged_path).name}", file=sys.stderr)

    from excelcompare.web.app import run_merge_server

    exit_code = run_merge_server(
        diff=wb_diff,
        resolutions=resolutions,
        base=base_wb,
        ours=local_wb,
        theirs=remote_wb,
        output_path=merged_path,
        is_git_driver=True,
        ours_path=local_path,
        theirs_path=remote_path,
    )
    sys.exit(exit_code)


if __name__ == "__main__":
    main()
