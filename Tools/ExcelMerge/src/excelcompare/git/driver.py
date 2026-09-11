from __future__ import annotations

import sys
from pathlib import Path


def main():
    args = sys.argv[1:]

    label = None
    positional = []
    i = 0
    while i < len(args):
        if args[i] == "-L" and i + 1 < len(args):
            label = args[i + 1]
            i += 2
        else:
            positional.append(args[i])
            i += 1

    if len(positional) < 3:
        print("Usage: excelcompare-driver <base> <ours> <theirs> [-L <label>]", file=sys.stderr)
        sys.exit(2)

    base_path = str(Path(positional[0]).resolve())
    ours_path = str(Path(positional[1]).resolve())
    theirs_path = str(Path(positional[2]).resolve())

    if label:
        print(f"ExcelCompare: merging {label}", file=sys.stderr)

    from excelcompare.core.diff import diff_workbooks
    from excelcompare.core.excel_io import load_workbook
    from excelcompare.core.merge import auto_merge, save_merged_with_styles, unresolved_count

    try:
        base_wb = load_workbook(base_path)
        ours_wb = load_workbook(ours_path)
        theirs_wb = load_workbook(theirs_path)
    except Exception as e:
        print(f"ExcelCompare: failed to load workbooks: {e}", file=sys.stderr)
        sys.exit(2)

    wb_diff = diff_workbooks(base_wb, ours_wb, theirs_wb)
    resolutions = auto_merge(wb_diff)

    remaining = unresolved_count(wb_diff, resolutions)

    if remaining == 0:
        save_merged_with_styles(wb_diff, resolutions, ours_path, theirs_path, ours_path, theirs_wb)
        print(f"ExcelCompare: auto-merged successfully ({len(resolutions)} changes)", file=sys.stderr)
        sys.exit(0)

    print(f"ExcelCompare: {remaining} conflict(s) require manual resolution", file=sys.stderr)

    from excelcompare.web.app import run_merge_server

    exit_code = run_merge_server(
        diff=wb_diff,
        resolutions=resolutions,
        base=base_wb,
        ours=ours_wb,
        theirs=theirs_wb,
        output_path=ours_path,
        is_git_driver=True,
        ours_path=ours_path,
        theirs_path=theirs_path,
    )
    sys.exit(exit_code)


if __name__ == "__main__":
    main()
