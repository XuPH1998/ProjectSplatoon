from excelcompare.core.diff import diff_workbooks
from excelcompare.core.merge import auto_merge, build_merged_workbook, is_fully_resolved, unresolved_count
from excelcompare.core.models import (
    CellResolution,
    CellValue,
    DiffKind,
    ResolutionChoice,
    SheetData,
    WorkbookData,
)


def _make_workbooks():
    base = WorkbookData(
        sheets={"S1": SheetData(name="S1", cells={
            (1, 1): CellValue(value=10, data_type="n"),
            (2, 1): CellValue(value=20, data_type="n"),
            (3, 1): CellValue(value=30, data_type="n"),
        }, max_row=3, max_col=1)},
        sheet_order=["S1"],
    )
    ours = WorkbookData(
        sheets={"S1": SheetData(name="S1", cells={
            (1, 1): CellValue(value=100, data_type="n"),
            (2, 1): CellValue(value=20, data_type="n"),
            (3, 1): CellValue(value=33, data_type="n"),
        }, max_row=3, max_col=1)},
        sheet_order=["S1"],
    )
    theirs = WorkbookData(
        sheets={"S1": SheetData(name="S1", cells={
            (1, 1): CellValue(value=200, data_type="n"),
            (2, 1): CellValue(value=25, data_type="n"),
            (3, 1): CellValue(value=33, data_type="n"),
        }, max_row=3, max_col=1)},
        sheet_order=["S1"],
    )
    return base, ours, theirs


def test_auto_merge_resolves_non_conflicts():
    base, ours, theirs = _make_workbooks()
    diff = diff_workbooks(base, ours, theirs)
    resolutions = auto_merge(diff)

    assert ("S1", 2, 1) in resolutions
    assert resolutions[("S1", 2, 1)].choice == ResolutionChoice.ACCEPT_THEIRS

    assert ("S1", 3, 1) in resolutions
    assert resolutions[("S1", 3, 1)].choice == ResolutionChoice.ACCEPT_OURS

    assert ("S1", 1, 1) not in resolutions


def test_is_fully_resolved_false():
    base, ours, theirs = _make_workbooks()
    diff = diff_workbooks(base, ours, theirs)
    resolutions = auto_merge(diff)
    assert not is_fully_resolved(diff, resolutions)


def test_is_fully_resolved_true():
    base, ours, theirs = _make_workbooks()
    diff = diff_workbooks(base, ours, theirs)
    resolutions = auto_merge(diff)
    resolutions[("S1", 1, 1)] = CellResolution(
        sheet="S1", row=1, col=1, choice=ResolutionChoice.ACCEPT_OURS,
    )
    assert is_fully_resolved(diff, resolutions)


def test_unresolved_count():
    base, ours, theirs = _make_workbooks()
    diff = diff_workbooks(base, ours, theirs)
    resolutions = auto_merge(diff)
    assert unresolved_count(diff, resolutions) == 1

    resolutions[("S1", 1, 1)] = CellResolution(
        sheet="S1", row=1, col=1, choice=ResolutionChoice.ACCEPT_THEIRS,
    )
    assert unresolved_count(diff, resolutions) == 0


def test_build_merged_workbook():
    base, ours, theirs = _make_workbooks()
    diff = diff_workbooks(base, ours, theirs)
    resolutions = auto_merge(diff)
    resolutions[("S1", 1, 1)] = CellResolution(
        sheet="S1", row=1, col=1, choice=ResolutionChoice.ACCEPT_OURS,
    )

    merged = build_merged_workbook(diff, resolutions, base, ours, theirs)
    assert merged.sheets["S1"].cells[(1, 1)].value == 100
    assert merged.sheets["S1"].cells[(2, 1)].value == 25
    assert merged.sheets["S1"].cells[(3, 1)].value == 33
