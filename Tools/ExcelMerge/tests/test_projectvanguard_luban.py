from excelcompare.core.diff import detect_key_col, diff_sheets
from excelcompare.core.models import CellValue, DiffKind, SheetData


def _cv(value):
    return CellValue(value=value, data_type="s" if isinstance(value, str) else "n")


def _sheet(rows):
    cells = {}
    for row_idx, row in enumerate(rows, start=1):
        for col_idx, value in enumerate(row, start=1):
            if value is not None:
                cells[(row_idx, col_idx)] = _cv(value)
    return SheetData(
        name="Sheet1",
        cells=cells,
        max_row=len(rows),
        max_col=max(len(row) for row in rows),
    )


def test_luban_id_header_is_preferred_over_second_column_fallback():
    base = _sheet([
        ["##var", "name", "ID"],
        ["##type", "string", "int"],
        ["##", "desc", "desc"],
        [None, "Alpha", 1001],
        [None, "Beta", 1002],
    ])
    ours = _sheet([
        ["##var", "name", "ID"],
        ["##type", "string", "int"],
        ["##", "desc", "desc"],
        [None, "Alpha renamed", 1001],
        [None, "Beta", 1002],
    ])
    theirs = _sheet([
        ["##var", "name", "ID"],
        ["##type", "string", "int"],
        ["##", "desc", "desc"],
        [None, "Alpha", 1001],
        [None, "Beta renamed", 1002],
    ])

    key_col, header_count = detect_key_col(base, ours, theirs)

    assert key_col == 3
    assert header_count == 3


def test_luban_header_rows_are_skipped_for_row_alignment():
    base = _sheet([
        ["##var", "id", "name"],
        ["##type", "int", "string"],
        ["##", "desc", "desc"],
        [None, 1001, "Alpha"],
        [None, 1002, "Beta"],
    ])
    ours = _sheet([
        ["##var", "id", "name"],
        ["##type", "int", "string"],
        ["##", "desc", "desc"],
        [None, 1001, "Alpha ours"],
        [None, 1002, "Beta"],
    ])
    theirs = _sheet([
        ["##var", "id", "name"],
        ["##type", "int", "string"],
        ["##", "desc", "desc"],
        [None, 1001, "Alpha"],
        [None, 1002, "Beta theirs"],
    ])

    result = diff_sheets("Sheet1", base, ours, theirs)

    assert result.key_col == 2
    assert result.header_count == 3
    assert result.cells[(4, 3)].kind == DiffKind.OURS_ONLY
    assert result.cells[(5, 3)].kind == DiffKind.THEIRS_ONLY
