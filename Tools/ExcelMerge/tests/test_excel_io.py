from tests.conftest import create_xlsx
from excelcompare.core.excel_io import load_workbook, save_workbook


def test_load_workbook_basic(tmp_dir):
    path = tmp_dir / "test.xlsx"
    create_xlsx(path, {"Sheet1": [[1, 2, 3], [4, 5, 6]]})

    wb = load_workbook(path)
    assert "Sheet1" in wb.sheets
    assert wb.sheets["Sheet1"].cells[(1, 1)].value == 1
    assert wb.sheets["Sheet1"].cells[(2, 3)].value == 6


def test_load_workbook_formula(tmp_dir):
    path = tmp_dir / "formula.xlsx"
    create_xlsx(path, {"Sheet1": [["=SUM(B1:B5)", 10, 20]]})

    wb = load_workbook(path)
    cell = wb.sheets["Sheet1"].cells[(1, 1)]
    assert cell.formula == "=SUM(B1:B5)"
    assert cell.data_type == "f"


def test_save_and_reload(tmp_dir):
    path = tmp_dir / "test.xlsx"
    create_xlsx(path, {"S1": [[1, 2], [3, 4]], "S2": [["hello"]]})

    wb = load_workbook(path)
    out_path = tmp_dir / "out.xlsx"
    save_workbook(wb, out_path)

    wb2 = load_workbook(out_path)
    assert wb2.sheets["S1"].cells[(1, 1)].value == 1
    assert wb2.sheets["S2"].cells[(1, 1)].value == "hello"
    assert wb2.sheet_order == ["S1", "S2"]


def test_multiple_sheets(tmp_dir):
    path = tmp_dir / "multi.xlsx"
    create_xlsx(path, {
        "Data": [[1, 2], [3, 4]],
        "Config": [["key", "value"], ["timeout", 30]],
    })

    wb = load_workbook(path)
    assert len(wb.sheets) == 2
    assert wb.sheet_order == ["Data", "Config"]
