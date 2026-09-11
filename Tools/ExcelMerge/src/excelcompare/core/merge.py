from __future__ import annotations

from excelcompare.core.models import (
    CellResolution,
    DiffKind,
    ResolutionChoice,
    WorkbookData,
    WorkbookDiff,
)
from pathlib import Path

from excelcompare.core.excel_io import apply_resolutions, save_merged_xlsx


def auto_merge(diff: WorkbookDiff) -> dict[tuple[str, int, int], CellResolution]:
    resolutions: dict[tuple[str, int, int], CellResolution] = {}
    for sheet_name, sheet_diff in diff.sheets.items():
        for (row, col), cell_diff in sheet_diff.cells.items():
            if cell_diff.kind == DiffKind.BOTH_SAME:
                resolutions[(sheet_name, row, col)] = CellResolution(
                    sheet=sheet_name, row=row, col=col,
                    choice=ResolutionChoice.ACCEPT_OURS,
                )
    return resolutions


def is_fully_resolved(
    diff: WorkbookDiff,
    resolutions: dict[tuple[str, int, int], CellResolution],
) -> bool:
    for sheet_name, sheet_diff in diff.sheets.items():
        for (row, col), cell_diff in sheet_diff.cells.items():
            if cell_diff.kind in (DiffKind.UNCHANGED, DiffKind.BOTH_SAME,
                                  DiffKind.OURS_ONLY, DiffKind.THEIRS_ONLY):
                continue
            if (sheet_name, row, col) not in resolutions:
                return False
    return True


def unresolved_count(
    diff: WorkbookDiff,
    resolutions: dict[tuple[str, int, int], CellResolution],
) -> int:
    count = 0
    for sheet_name, sheet_diff in diff.sheets.items():
        for (row, col), cell_diff in sheet_diff.cells.items():
            if cell_diff.kind in (DiffKind.UNCHANGED, DiffKind.BOTH_SAME,
                                  DiffKind.OURS_ONLY, DiffKind.THEIRS_ONLY):
                continue
            if (sheet_name, row, col) not in resolutions:
                count += 1
    return count


def build_merged_workbook(
    diff: WorkbookDiff,
    resolutions: dict[tuple[str, int, int], CellResolution],
    base: WorkbookData,
    ours: WorkbookData,
    theirs: WorkbookData,
) -> WorkbookData:
    return apply_resolutions(diff, resolutions, base, ours, theirs)


def save_merged_with_styles(
    diff: WorkbookDiff,
    resolutions: dict[tuple[str, int, int], CellResolution],
    ours_path: str | Path,
    theirs_path: str | Path,
    output_path: str | Path,
    theirs: WorkbookData,
    keep_both_edits: dict | None = None,
) -> None:
    """Save merged result preserving all styles from ours file."""
    save_merged_xlsx(ours_path, theirs_path, output_path, diff, resolutions, theirs,
                     keep_both_edits=keep_both_edits or {})
