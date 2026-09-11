from excelcompare.core.diff import diff_cells, diff_sheets, diff_workbooks
from excelcompare.core.models import (
    CellValue,
    DiffKind,
    SheetData,
    SheetDiffKind,
    WorkbookData,
)


def test_diff_cells_unchanged():
    base = CellValue(value=42, data_type="n")
    ours = CellValue(value=42, data_type="n")
    theirs = CellValue(value=42, data_type="n")
    assert diff_cells(base, ours, theirs) == DiffKind.UNCHANGED


def test_diff_cells_ours_only():
    base = CellValue(value=42, data_type="n")
    ours = CellValue(value=99, data_type="n")
    theirs = CellValue(value=42, data_type="n")
    assert diff_cells(base, ours, theirs) == DiffKind.OURS_ONLY


def test_diff_cells_theirs_only():
    base = CellValue(value=42, data_type="n")
    ours = CellValue(value=42, data_type="n")
    theirs = CellValue(value=99, data_type="n")
    assert diff_cells(base, ours, theirs) == DiffKind.THEIRS_ONLY


def test_diff_cells_both_same():
    base = CellValue(value=42, data_type="n")
    ours = CellValue(value=99, data_type="n")
    theirs = CellValue(value=99, data_type="n")
    assert diff_cells(base, ours, theirs) == DiffKind.BOTH_SAME


def test_diff_cells_conflict():
    base = CellValue(value=42, data_type="n")
    ours = CellValue(value=100, data_type="n")
    theirs = CellValue(value=200, data_type="n")
    assert diff_cells(base, ours, theirs) == DiffKind.CONFLICT


def test_diff_cells_formula_unchanged():
    base = CellValue(value="=SUM(A1:A5)", formula="=SUM(A1:A5)", data_type="f")
    ours = CellValue(value="=SUM(A1:A5)", formula="=SUM(A1:A5)", data_type="f")
    theirs = CellValue(value="=SUM(A1:A5)", formula="=SUM(A1:A5)", data_type="f")
    assert diff_cells(base, ours, theirs) == DiffKind.UNCHANGED


def test_diff_cells_formula_conflict():
    base = CellValue(value="=SUM(A1:A5)", formula="=SUM(A1:A5)", data_type="f")
    ours = CellValue(value="=SUM(A1:A10)", formula="=SUM(A1:A10)", data_type="f")
    theirs = CellValue(value="=AVG(A1:A5)", formula="=AVG(A1:A5)", data_type="f")
    assert diff_cells(base, ours, theirs) == DiffKind.CONFLICT


def test_diff_cells_new_in_ours():
    assert diff_cells(None, CellValue(value=42, data_type="n"), None) == DiffKind.OURS_ONLY


def test_diff_cells_new_in_theirs():
    assert diff_cells(None, None, CellValue(value=42, data_type="n")) == DiffKind.THEIRS_ONLY


def test_diff_sheets_unchanged():
    sheet = SheetData(name="S1", cells={(1, 1): CellValue(value=1, data_type="n")}, max_row=1, max_col=1)
    result = diff_sheets("S1", sheet, sheet, sheet)
    assert result.kind == SheetDiffKind.UNCHANGED
    assert len(result.cells) == 0


def test_diff_sheets_added_ours():
    ours = SheetData(name="New", cells={(1, 1): CellValue(value=1, data_type="n")}, max_row=1, max_col=1)
    result = diff_sheets("New", None, ours, None)
    assert result.kind == SheetDiffKind.ADDED_OURS


def test_diff_sheets_with_conflicts():
    base = SheetData(name="S1", cells={
        (1, 1): CellValue(value=10, data_type="n"),
        (1, 2): CellValue(value=20, data_type="n"),
    }, max_row=1, max_col=2)
    ours = SheetData(name="S1", cells={
        (1, 1): CellValue(value=100, data_type="n"),
        (1, 2): CellValue(value=20, data_type="n"),
    }, max_row=1, max_col=2)
    theirs = SheetData(name="S1", cells={
        (1, 1): CellValue(value=200, data_type="n"),
        (1, 2): CellValue(value=30, data_type="n"),
    }, max_row=1, max_col=2)

    result = diff_sheets("S1", base, ours, theirs)
    assert result.kind == SheetDiffKind.MODIFIED
    assert result.conflict_count == 1
    assert result.cells[(1, 1)].kind == DiffKind.CONFLICT
    assert result.cells[(1, 2)].kind == DiffKind.THEIRS_ONLY


def test_diff_workbooks():
    base = WorkbookData(
        sheets={"S1": SheetData(name="S1", cells={(1, 1): CellValue(value=1, data_type="n")}, max_row=1, max_col=1)},
        sheet_order=["S1"],
    )
    ours = WorkbookData(
        sheets={"S1": SheetData(name="S1", cells={(1, 1): CellValue(value=2, data_type="n")}, max_row=1, max_col=1)},
        sheet_order=["S1"],
    )
    theirs = WorkbookData(
        sheets={"S1": SheetData(name="S1", cells={(1, 1): CellValue(value=3, data_type="n")}, max_row=1, max_col=1)},
        sheet_order=["S1"],
    )

    result = diff_workbooks(base, ours, theirs)
    assert result.conflict_count == 1
    assert "S1" in result.sheets
